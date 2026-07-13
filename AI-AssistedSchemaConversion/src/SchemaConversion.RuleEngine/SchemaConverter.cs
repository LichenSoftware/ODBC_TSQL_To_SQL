using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server schema objects to PostgreSQL CREATE SCHEMA IF NOT EXISTS statements.
/// Applies Schema_Mapping_Table from ConversionContext.SchemaMappings.
/// </summary>
public sealed class SchemaConverter : IRuleBasedConverter
{
    private readonly ILogger<SchemaConverter> _logger;

    public SchemaConverter(ILogger<SchemaConverter> logger)
    {
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting schema {Name}", obj.Name);

        var targetSchema = MapSchema(obj.Name, context.SchemaMappings);

        var ddl = $"CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(targetSchema)};";

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Schema,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0
        };
    }

    private static string MapSchema(string sourceSchema, IReadOnlyDictionary<string, string> schemaMappings)
    {
        if (schemaMappings.TryGetValue(sourceSchema, out var mapped))
        {
            return mapped;
        }

        return sourceSchema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            ? "public"
            : sourceSchema;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return identifier.ToLowerInvariant();
        }
        return $"\"{identifier}\"";
    }
}
