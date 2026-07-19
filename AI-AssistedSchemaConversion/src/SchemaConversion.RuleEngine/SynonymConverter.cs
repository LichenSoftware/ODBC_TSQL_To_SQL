using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server synonyms to PostgreSQL views.
/// Generates CREATE VIEW that SELECT * FROM the synonym target,
/// applying schema mapping to the target reference.
/// </summary>
public sealed class SynonymConverter : IRuleBasedConverter
{
    private readonly ILogger<SynonymConverter> _logger;

    public SynonymConverter(ILogger<SynonymConverter> logger)
    {
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting synonym {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for synonym {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Synonym,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new CreateSynonymVisitor();
        fragment.Accept(visitor);

        if (visitor.Synonym is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Synonym,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "No CREATE SYNONYM statement found in source definition."
            };
        }

        return ConvertSynonym(visitor.Synonym, obj, context);
    }

    private ConversionResult ConvertSynonym(CreateSynonymStatement createSynonym, SchemaObject obj, ConversionContext context)
    {
        var compatibilityNotes = new List<CompatibilityNote>();
        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var synonymName = obj.Name;

        // Extract the target object reference
        var forName = createSynonym.ForName;
        if (forName is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Synonym,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "Synonym has no target (FOR) object reference."
            };
        }

        // Build the target reference with schema mapping
        var targetObjectSchema = forName.SchemaIdentifier?.Value ?? "dbo";
        var targetObjectName = forName.BaseIdentifier?.Value ?? "unknown";
        var mappedTargetSchema = MapSchema(targetObjectSchema, context.SchemaMappings);

        // Handle cross-database/server references
        var serverIdentifier = forName.ServerIdentifier?.Value;
        var databaseIdentifier = forName.DatabaseIdentifier?.Value;

        if (serverIdentifier is not null || databaseIdentifier is not null)
        {
            compatibilityNotes.Add(new CompatibilityNote
            {
                Category = "CrossDatabase",
                Description = $"Synonym '{synonymName}' references a cross-database or linked server object " +
                              $"({serverIdentifier ?? ""}.{databaseIdentifier ?? ""}.{targetObjectSchema}.{targetObjectName}). " +
                              "The generated view references only the schema.object portion. " +
                              "Cross-database access requires dblink or foreign data wrappers in PostgreSQL."
            });
        }

        var qualifiedTarget = $"{mappedTargetSchema}.{QuoteIdentifier(targetObjectName)}";

        // PostgreSQL doesn't have synonyms — create a view instead
        var ddl = $"CREATE OR REPLACE VIEW {targetSchema}.{QuoteIdentifier(synonymName)} AS\n" +
                  $"SELECT * FROM {qualifiedTarget};";

        compatibilityNotes.Add(new CompatibilityNote
        {
            Category = "Synonym",
            Description = $"SQL Server synonym '{synonymName}' converted to a PostgreSQL view. " +
                          "This preserves read access but DML operations through the view may " +
                          "require INSTEAD OF triggers if the target is not a simple table."
        });

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Synonym,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private static string MapSchema(string sourceSchema, IReadOnlyDictionary<string, string> schemaMappings)
    {
        if (schemaMappings.TryGetValue(sourceSchema, out var mapped))
        {
            return mapped;
        }

        return sourceSchema;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return identifier.ToLowerInvariant();
        }
        return $"\"{identifier}\"";
    }

    private sealed class CreateSynonymVisitor : TSqlFragmentVisitor
    {
        public CreateSynonymStatement? Synonym { get; private set; }

        public override void Visit(CreateSynonymStatement node)
        {
            Synonym ??= node;
        }
    }
}
