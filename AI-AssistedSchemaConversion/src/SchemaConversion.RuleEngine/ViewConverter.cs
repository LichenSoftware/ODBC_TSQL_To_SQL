using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server CREATE VIEW statements to PostgreSQL equivalents.
/// Translates the SELECT body via ExpressionTranslator, preserves column aliases,
/// handles WITH CHECK OPTION, omits SCHEMABINDING with a note, and falls back
/// to AI on untranslatable expressions.
/// </summary>
public sealed class ViewConverter : IRuleBasedConverter
{
    private readonly ExpressionTranslator _expressionTranslator;
    private readonly ILogger<ViewConverter> _logger;

    public ViewConverter(
        ExpressionTranslator expressionTranslator,
        ILogger<ViewConverter> logger)
    {
        _expressionTranslator = expressionTranslator;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting view {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for view {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.View,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new CreateViewVisitor();
        fragment.Accept(visitor);

        if (visitor.View is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.View,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "No CREATE VIEW statement found in source definition."
            };
        }

        return ConvertView(visitor.View, obj, context);
    }

    private ConversionResult ConvertView(CreateViewStatement createView, SchemaObject obj, ConversionContext context)
    {
        var reviewFlags = new List<ManualReviewFlag>();
        var compatibilityNotes = new List<CompatibilityNote>();
        var needsAiFallback = false;

        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var viewName = obj.Name;

        // Check for SCHEMABINDING
        if (createView.ViewOptions is not null)
        {
            foreach (var option in createView.ViewOptions)
            {
                if (option.OptionKind == ViewOptionKind.SchemaBinding)
                {
                    compatibilityNotes.Add(new CompatibilityNote
                    {
                        Category = "SchemaBinding",
                        Description = $"View '{viewName}': WITH SCHEMABINDING was removed. " +
                                      "PostgreSQL does not have an equivalent; consider using " +
                                      "dependency tracking or schema-level permissions instead."
                    });
                }
            }
        }

        // Extract column aliases from the view's column list
        var columnList = string.Empty;
        if (createView.Columns is not null && createView.Columns.Count > 0)
        {
            var columns = createView.Columns.Select(c => QuoteIdentifier(c.Value));
            columnList = $" ({string.Join(", ", columns)})";
        }

        // Extract the SELECT body
        var selectBody = GetFragmentText(createView.SelectStatement);
        if (string.IsNullOrWhiteSpace(selectBody))
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.View,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "View has no SELECT statement body."
            };
        }

        // Apply schema mapping to object references in the SELECT body
        selectBody = ApplySchemaMapping(selectBody, context.SchemaMappings);

        // Attempt translation of the SELECT body
        var translationResult = _expressionTranslator.TranslateSelect(selectBody);

        string translatedSelect;
        if (translationResult.IsSuccess)
        {
            translatedSelect = translationResult.TranslatedExpression!;
        }
        else
        {
            // ExpressionTranslator returns CannotTranslate — mark for AI fallback
            needsAiFallback = true;
            reviewFlags.Add(new ManualReviewFlag
            {
                Reason = $"View SELECT body cannot be fully translated: {translationResult.CannotTranslateReason}",
                CodeSection = selectBody,
                SuggestedAlternative = "AI-assisted translation recommended"
            });
            // Use original SELECT as fallback (best-effort)
            translatedSelect = selectBody;
        }

        // Build the CREATE VIEW DDL
        var withCheckOption = createView.WithCheckOption ? "\n    WITH CHECK OPTION" : string.Empty;

        var ddl = $"CREATE OR REPLACE VIEW {targetSchema}.{QuoteIdentifier(viewName)}{columnList} AS\n" +
                  $"{translatedSelect}{withCheckOption};";

        var status = needsAiFallback ? ConversionStatus.Flagged : ConversionStatus.Converted;

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.View,
            Status = status,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = needsAiFallback ? 0.5 : 1.0,
            ReviewFlags = reviewFlags,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private static string ApplySchemaMapping(string sql, IReadOnlyDictionary<string, string> schemaMappings)
    {
        var result = sql;
        foreach (var (source, target) in schemaMappings)
        {
            // Replace schema-qualified references: [schema]. or schema.
            result = result.Replace($"[{source}].", $"{target}.", StringComparison.OrdinalIgnoreCase);
            result = result.Replace($"{source}.", $"{target}.", StringComparison.OrdinalIgnoreCase);
        }

        // Remove remaining square brackets (T-SQL quoting → PostgreSQL uses double quotes if needed)
        result = result.Replace("[", "").Replace("]", "");

        return result;
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

    private sealed class CreateViewVisitor : TSqlFragmentVisitor
    {
        public CreateViewStatement? View { get; private set; }

        public override void Visit(CreateViewStatement node)
        {
            View ??= node;
        }
    }
}
