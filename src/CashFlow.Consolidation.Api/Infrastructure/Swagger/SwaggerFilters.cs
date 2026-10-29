using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace CashFlow.Consolidation.Api.Infrastructure.Swagger;

/// <summary>
/// DateOnly é serializado como "YYYY-MM-DD" (ISO 8601); sem este mapeamento o
/// Swashbuckle 6.x o descreve como objeto {year, month, day}.
/// </summary>
public sealed class DateOnlySchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (context.Type == typeof(DateOnly) || context.Type == typeof(DateOnly?))
        {
            schema.Type = "string";
            schema.Format = "date";
            schema.Example = new OpenApiString("2026-10-29");
            schema.Properties?.Clear();
            schema.AdditionalPropertiesAllowed = false;
        }
    }
}

/// <summary>Documenta o header X-Correlation-Id em todos os endpoints.</summary>
public sealed class CorrelationIdOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Parameters ??= new List<OpenApiParameter>();
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = CorrelationIdMiddleware.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Rastreia a requisição por todo o sistema. Se ausente, um valor é gerado e devolvido no response.",
            Schema = new OpenApiSchema { Type = "string" }
        });
    }
}
