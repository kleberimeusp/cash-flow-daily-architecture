using CashFlow.Consolidation.Api.Application;
using CashFlow.Consolidation.Api.Infrastructure;
using CashFlow.Consolidation.Api.Infrastructure.Swagger;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CashFlow Consolidation API",
        Version = "v1",
        Description = """
            Saldo diário consolidado por comerciante — projeção derivada do Ledger,
            integralmente reconstruível (nunca é fonte da verdade).

            - Consulta é **cache-first** (Redis, TTL 30s) — ADR-006.
            - Consolidação é **eventualmente consistente** (janela de centenas de ms).
            - Dia sem movimentação retorna zeros com `200`, não `404`.
            - Autenticação JWT não está configurada nesta entrega: em desenvolvimento,
              usa-se o header `X-Merchant-Id` ou o comerciante de teste
              `a1b2c3d4-e5f6-7890-abcd-ef1234567890` (RN-06).
            """
    });
    c.EnableAnnotations();
    c.SchemaFilter<DateOnlySchemaFilter>();
    c.OperationFilter<CorrelationIdOperationFilter>();
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);

var consolidationConnection = builder.Configuration.GetConnectionString("ConsolidationDb")
    ?? throw new InvalidOperationException("ConnectionStrings:ConsolidationDb não configurada.");
var rabbit = builder.Configuration.GetSection("RabbitMq");
var redisConnection = builder.Configuration["Redis"] ?? "localhost:6379";

builder.Services.AddDbContext<ConsolidationDbContext>(o => o.UseNpgsql(consolidationConnection));
builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisConnection);
builder.Services.AddScoped<IMerchantContext, HttpMerchantContext>();
builder.Services.AddScoped<DailyBalanceRepository>();
builder.Services.AddScoped<BalanceQueryService>();
builder.Services.AddSingleton<ConsolidationMetrics>();
builder.Services.AddHostedService<QueueDepthService>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<EntryConsumer>();

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(rabbit["Host"] ?? "localhost", h =>
        {
            h.Username(rabbit["Username"] ?? "guest");
            h.Password(rabbit["Password"] ?? "guest");
        });

        cfg.ReceiveEndpoint("cashflow.entry.registered", e =>
        {
            // RNF-02/RNF-03: prefetch 32 + 4 consumidores concorrentes ≈ 8,3 msg/s
            // por consumidor — folga para o pico de 50 msg/s com backpressure.
            e.PrefetchCount = 32;
            e.UseConcurrencyLimit(4);

            // Filtro externo: exceção que esgotou o retry vira mensagem na DLQ.
            e.UseFilter(new EntryRegisteredDlqFilter(
                ctx.GetRequiredService<ILogger<EntryRegisteredDlqFilter>>(),
                ctx.GetRequiredService<ConsolidationMetrics>()));

            e.UseMessageRetry(r => r.Exponential(
                retryLimit: 5,
                minInterval: TimeSpan.FromMilliseconds(200),
                maxInterval: TimeSpan.FromSeconds(30),
                intervalDelta: TimeSpan.FromMilliseconds(800)));

            e.ConfigureConsumer<EntryConsumer>(ctx);
        });
    });
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CashFlow.Consolidation.Api"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddNpgsql()
         .AddSource("MassTransit");
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
            t.AddOtlpExporter();
    })
    .WithMetrics(m => m
        .AddRuntimeInstrumentation()
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddMeter("CashFlow.Consolidation")
        .AddPrometheusExporter());

builder.Services.AddHealthChecks()
    .AddNpgSql(consolidationConnection, name: "postgres")
    .AddRabbitMQ(
        rabbitConnectionString: $"amqp://{rabbit["Username"] ?? "guest"}:{rabbit["Password"] ?? "guest"}@{rabbit["Host"] ?? "localhost"}:5672",
        name: "rabbitmq")
    .AddRedis(redisConnection, name: "redis");

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseMiddleware<CorrelationIdMiddleware>();
app.MapPrometheusScrapingEndpoint();
app.MapHealthChecks("/health");

var balances = app.MapGroup("/api/v1/balances").WithTags("Balances");

balances.MapGet("/{date}", async Task<IResult> (DateOnly date, BalanceQueryService service, CancellationToken ct) =>
        Results.Ok(await service.GetDailyAsync(date, ct)))
    .WithName("GetDailyBalance")
    .WithSummary("Consolidado de um dia")
    .WithDescription("Cache-first (Redis, TTL 30s). Dia sem movimentação retorna zeros com 200 — ausência de lançamento é um fato de negócio válido.")
    .Produces<BalanceResponse>();

balances.MapGet("/", async Task<IResult> (
        DateOnly? from, DateOnly? to, BalanceQueryService service, TimeProvider timeProvider, CancellationToken ct) =>
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var f = from ?? today.AddDays(-30);
        var t = to ?? today;
        if (f > t)
            return Results.BadRequest(new { errors = Errors("from", "A data inicial não pode ser maior que a data final.") });
        if (t.DayNumber - f.DayNumber > 366)
            return Results.BadRequest(new { errors = Errors("to", "O intervalo máximo de consulta é de 366 dias.") });

        return Results.Ok(await service.GetRangeAsync(f, t, ct));
    })
    .WithName("GetBalanceRange")
    .WithSummary("Consolidado por intervalo")
    .WithDescription("Retorna array ordenado por data. Defaults: `from` = hoje − 30 dias, `to` = hoje. Intervalo máximo de 366 dias.")
    .Produces<IEnumerable<BalanceResponse>>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

// Migrations na subida: em Docker é o que prepara o banco antes de servir tráfego.
if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<ConsolidationDbContext>().Database.MigrateAsync();
}

app.Run();

static Dictionary<string, string[]> Errors(string field, string message)
    => new() { [field] = new[] { message } };

public partial class Program;
