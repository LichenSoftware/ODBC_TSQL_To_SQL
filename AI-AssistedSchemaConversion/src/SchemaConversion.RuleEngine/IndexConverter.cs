using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server index definitions to PostgreSQL equivalents.
/// Handles standard indexes, unique indexes, filtered/partial indexes,
/// clustered indexes (with compatibility note), and INCLUDE columns.
/// </summary>
public sealed class IndexConverter : IRuleBasedConverter
{
    private readonly ExpressionTranslator _expressionTranslator;
    private readonly ILogger<IndexConverter> _logger;

    public IndexConverter(
        ExpressionTranslator expressionTranslator,
        ILogger<IndexConverter> logger)
    {
        _expressionTranslator = expressionTranslator;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting index {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for index {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Index,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new CreateIndexVisitor();
        fragment.Accept(visitor);

        if (visitor.CreateIndex is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Index,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "No CREATE INDEX statement found in source definition."
            };
        }

        return ConvertIndex(visitor.CreateIndex, obj, context);
    }

    private ConversionResult ConvertIndex(CreateIndexStatement createIndex, SchemaObject obj, ConversionContext context)
    {
        var reviewFlags = new List<ManualReviewFlag>();
        var compatibilityNotes = new List<CompatibilityNote>();
        var needsAiFallback = false;

        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var indexName = createIndex.Name?.Value ?? obj.Name;

        // Determine target table name with schema
        var tableSchema = createIndex.OnName?.SchemaIdentifier?.Value ?? obj.SchemaName;
        var tableName = createIndex.OnName?.BaseIdentifier?.Value ?? "unknown";
        var mappedTableSchema = MapSchema(tableSchema, context.SchemaMappings);

        // Check if unique
        var isUnique = createIndex.Unique;

        // Check if clustered
        var isClustered = createIndex.Clustered.HasValue && createIndex.Clustered.Value;

        if (isClustered)
        {
            compatibilityNotes.Add(new CompatibilityNote
            {
                Category = "Clustering",
                Description = $"Index '{indexName}' was a clustered index in SQL Server. " +
                              "PostgreSQL does not support clustered indexes that maintain physical row ordering. " +
                              "A standard B-tree index has been created instead. Consider using CLUSTER command for one-time physical reordering."
            });
        }

        // Build column list
        var columns = createIndex.Columns
            .Select(c =>
            {
                var colName = c.Column?.MultiPartIdentifier?.Identifiers.Last().Value ?? "unknown";
                var sortOrder = c.SortOrder == SortOrder.Descending ? " DESC" : "";
                return $"{QuoteIdentifier(colName)}{sortOrder}";
            })
            .ToList();

        // Build INCLUDE columns
        var includeColumns = new List<string>();
        if (createIndex.IncludeColumns is not null && createIndex.IncludeColumns.Count > 0)
        {
            includeColumns = createIndex.IncludeColumns
                .Select(c => QuoteIdentifier(c.MultiPartIdentifier?.Identifiers.Last().Value ?? "unknown"))
                .ToList();
        }

        // Build WHERE clause for filtered/partial indexes
        string? whereClause = null;
        if (createIndex.FilterPredicate is not null)
        {
            var filterExpr = GetFragmentText(createIndex.FilterPredicate);
            var translationResult = _expressionTranslator.Translate(filterExpr);

            if (translationResult.IsSuccess)
            {
                whereClause = translationResult.TranslatedExpression;
            }
            else
            {
                needsAiFallback = true;
                reviewFlags.Add(new ManualReviewFlag
                {
                    Reason = $"Filtered index WHERE clause cannot be translated: {translationResult.CannotTranslateReason}",
                    CodeSection = filterExpr,
                    SuggestedAlternative = "AI-assisted translation recommended"
                });
                // Use original expression as fallback
                whereClause = filterExpr;
            }
        }

        // Build the CREATE INDEX DDL
        var uniqueClause = isUnique ? "UNIQUE " : "";
        var ddl = $"CREATE {uniqueClause}INDEX {QuoteIdentifier(indexName)}\n" +
                  $"    ON {mappedTableSchema}.{QuoteIdentifier(tableName)} ({string.Join(", ", columns)})";

        if (includeColumns.Count > 0)
        {
            ddl += $"\n    INCLUDE ({string.Join(", ", includeColumns)})";
        }

        if (whereClause is not null)
        {
            ddl += $"\n    WHERE {whereClause}";
        }

        ddl += ";";

        var status = needsAiFallback ? ConversionStatus.Flagged : ConversionStatus.Converted;

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Index,
            Status = status,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = needsAiFallback ? 0.6 : 1.0,
            ReviewFlags = reviewFlags,
            CompatibilityNotes = compatibilityNotes
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

    private static string GetFragmentText(TSqlFragment? fragment)
    {
        if (fragment is null) return string.Empty;

        var tokens = new List<string>();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            if (i >= 0 && fragment.ScriptTokenStream is not null && i < fragment.ScriptTokenStream.Count)
            {
                tokens.Add(fragment.ScriptTokenStream[i].Text);
            }
        }
        return string.Join("", tokens).Trim();
    }

    private sealed class CreateIndexVisitor : TSqlFragmentVisitor
    {
        public CreateIndexStatement? CreateIndex { get; private set; }

        public override void Visit(CreateIndexStatement node)
        {
            CreateIndex ??= node;
        }
    }
}
