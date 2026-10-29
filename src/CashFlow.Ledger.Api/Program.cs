using CashFlow.Ledger.Api.Application;
using CashFlow.Ledger.Api.Infrastructure;
using CashFlow.Ledger.Api.Infrastructure.Swagger;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Swashbuckle.AspNetCore.Annotations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "CashFlow Ledger API",
        Version = "v1",
        Description = """
            Controle de lançamentos (débitos e créditos) — fonte da verdade do sistema.

            - Lançamentos são **append-only**; estorno é um novo lançamento de sinal oposto (RN-01).
            - Positivo = crédito, negativo = débito (RN-03).
            - `Idempotency-Key` é obrigatório na criação (RN-05).
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

var ledgerConnection = builder.Configuration.GetConnectionString("LedgerDb")
    ?? throw new InvalidOperationException("ConnectionStrings:LedgerDb não configurada.");
var rabbit = builder.Configuration.GetSection("RabbitMq");

builder.Services.AddDbContext<LedgerDbContext>(o => o.UseNpgsql(ledgerConnection));
builder.Services.AddScoped<IMerchantContext, HttpMerchantContext>();
builder.Services.AddScoped<EntryService>();
builder.Services.AddSingleton<LedgerMetrics>();
builder.Services.AddHostedService<OutboxRelay>();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((_, cfg) =>
    {
        cfg.Host(rabbit["Host"] ?? "localhost", h =>
        {
            h.Username(rabbit["Username"] ?? "guest");
            h.Password(rabbit["Password"] ?? "guest");
        });
    });
});

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("CashFlow.Ledger.Api"))
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
        .AddMeter("CashFlow.Ledger")
        .AddPrometheusExporter());

builder.Services.AddHealthChecks()
    .AddNpgSql(ledgerConnection, name: "postgres")
    .AddRabbitMQ(
        rabbitConnectionString: $"amqp://{rabbit["Username"] ?? "guest"}:{rabbit["Password"] ?? "guest"}@{rabbit["Host"] ?? "localhost"}:5672",
        name: "rabbitmq");

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseMiddleware<CorrelationIdMiddleware>();
app.MapPrometheusScrapingEndpoint();
app.MapHealthChecks("/health");

var entries = app.MapGroup("/api/v1/entries").WithTags("Entries");

entries.MapPost("/", async Task<IResult> (
        CreateEntryRequest? request, HttpContext http, EntryService service, CancellationToken ct) =>
    {
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        var result = await service.CreateAsync(request, idempotencyKey, http.GetCorrelationId(), ct);
        return result switch
        {
            CreateEntryResult.Created c => Results.Created($"/api/v1/entries/{c.Entry.Id}", EntryResponse.From(c.Entry)),
            CreateEntryResult.Duplicate d => Results.Ok(EntryResponse.From(d.Entry)),
            CreateEntryResult.Invalid i => Results.BadRequest(new { errors = i.Errors }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    })
    .WithName("CreateEntry")
    .WithSummary("Registrar lançamento")
    .WithDescription("Cria um lançamento (crédito se `amount` > 0, débito se < 0). O evento `EntryRegistered` é gravado no outbox na mesma transação e publicado de forma assíncrona.")
    .WithMetadata(new SwaggerOperationFilterAttribute(typeof(IdempotencyKeyOperationFilter)))
    .Produces<EntryResponse>(StatusCodes.Status201Created)
    .Produces<EntryResponse>(StatusCodes.Status200OK)
    .ProducesProblem(StatusCodes.Status400BadRequest);

entries.MapGet("/{id:guid}", async Task<IResult> (Guid id, EntryService service, CancellationToken ct) =>
    {
        var entry = await service.GetByIdAsync(id, ct);
        return entry is null ? Results.NotFound() : Results.Ok(EntryResponse.From(entry));
    })
    .WithName("GetEntry")
    .WithSummary("Consultar lançamento por id")
    .WithDescription("Retorna 404 se o lançamento não existir **ou pertencer a outro comerciante** (filtro por tenant na query, anti-IDOR).")
    .Produces<EntryResponse>()
    .Produces(StatusCodes.Status404NotFound);

entries.MapGet("/", async Task<IResult> (
        DateOnly? from, DateOnly? to, EntryService service, TimeProvider timeProvider, CancellationToken ct) =>
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var f = from ?? today.AddDays(-30);
        var t = to ?? today;
        if (f > t)
            return Results.BadRequest(new { errors = Errors("from", "A data inicial não pode ser maior que a data final.") });
        if (t.DayNumber - f.DayNumber > 366)
            return Results.BadRequest(new { errors = Errors("to", "O intervalo máximo de consulta é de 366 dias.") });

        var list = await service.ListAsync(f, t, ct);
        return Results.Ok(list.Select(EntryResponse.From));
    })
    .WithName("ListEntries")
    .WithSummary("Listar lançamentos por período")
    .WithDescription("Defaults: `from` = hoje − 30 dias, `to` = hoje. Intervalo máximo de 366 dias.")
    .Produces<IEnumerable<EntryResponse>>()
    .ProducesProblem(StatusCodes.Status400BadRequest);

entries.MapPost("/{id:guid}/reverse", async Task<IResult> (
        Guid id, HttpContext http, EntryService service, CancellationToken ct) =>
    {
        var idempotencyKey = http.Request.Headers["Idempotency-Key"].FirstOrDefault();
        var result = await service.ReverseAsync(id, idempotencyKey, http.GetCorrelationId(), ct);
        return result switch
        {
            ReverseEntryResult.Created c => Results.Created($"/api/v1/entries/{c.Entry.Id}", EntryResponse.From(c.Entry)),
            ReverseEntryResult.Duplicate d => Results.Ok(EntryResponse.From(d.Entry)),
            ReverseEntryResult.NotFound => Results.NotFound(),
            ReverseEntryResult.Conflict c => Results.Conflict(new { errors = Errors("entry", c.Message) }),
            ReverseEntryResult.Invalid i => Results.BadRequest(new { errors = i.Errors }),
            _ => Results.StatusCode(StatusCodes.Status500InternalServerError)
        };
    })
    .WithName("ReverseEntry")
    .WithSummary("Estornar lançamento")
    .WithDescription("Cria um novo lançamento de sinal oposto na mesma data de competência; o original permanece intacto (RN-01/RN-02). 409 se o lançamento já for um estorno.")
    .WithMetadata(new SwaggerOperationFilterAttribute(typeof(IdempotencyKeyOperationFilter)))
    .Produces<EntryResponse>(StatusCodes.Status201Created)
    .Produces<EntryResponse>(StatusCodes.Status200OK)
    .Produces(StatusCodes.Status404NotFound)
    .Produces(StatusCodes.Status409Conflict);

// Migrations na subida: em Docker é o que prepara o banco antes de servir tráfego.
if (!EF.IsDesignTime)
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<LedgerDbContext>().Database.MigrateAsync();
}

app.Run();

static Dictionary<string, string[]> Errors(string field, string message)
    => new() { [field] = new[] { message } };

public partial class Program;
