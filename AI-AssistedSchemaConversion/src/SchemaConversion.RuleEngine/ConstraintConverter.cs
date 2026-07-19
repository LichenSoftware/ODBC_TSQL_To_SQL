using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server constraint definitions (PRIMARY KEY, FOREIGN KEY, UNIQUE, CHECK)
/// to PostgreSQL equivalents. Handles referential actions, schema mapping, and expression translation.
/// </summary>
public sealed class ConstraintConverter : IRuleBasedConverter
{
    private readonly ExpressionTranslator _expressionTranslator;
    private readonly ILogger<ConstraintConverter> _logger;

    public ConstraintConverter(
        ExpressionTranslator expressionTranslator,
        ILogger<ConstraintConverter> logger)
    {
        _expressionTranslator = expressionTranslator;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting constraint {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for constraint {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Constraint,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new AlterTableVisitor();
        fragment.Accept(visitor);

        if (visitor.AlterTable is not null)
        {
            return ConvertAlterTableConstraint(visitor.AlterTable, obj, context);
        }

        // Try to find constraint inside CREATE TABLE
        var createTableVisitor = new CreateTableConstraintVisitor();
        fragment.Accept(createTableVisitor);

        if (createTableVisitor.Constraint is not null)
        {
            return ConvertStandaloneConstraint(createTableVisitor.Constraint, obj, context);
        }

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Constraint,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.RuleBased,
            ErrorMessage = "No constraint definition found in source."
        };
    }

    private ConversionResult ConvertAlterTableConstraint(
        AlterTableAddTableElementStatement alterTable,
        SchemaObject obj,
        ConversionContext context)
    {
        var reviewFlags = new List<ManualReviewFlag>();
        var compatibilityNotes = new List<CompatibilityNote>();
        var needsAiFallback = false;

        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var tableName = alterTable.SchemaObjectName?.BaseIdentifier?.Value ?? obj.Name;

        var constraintDdls = new List<string>();

        if (alterTable.Definition?.TableConstraints is not null)
        {
            foreach (var constraint in alterTable.Definition.TableConstraints)
            {
                var ddl = ConvertConstraintDefinition(constraint, tableName, targetSchema, context, reviewFlags, compatibilityNotes, ref needsAiFallback);
                if (ddl is not null)
                {
                    constraintDdls.Add(ddl);
                }
            }
        }

        if (constraintDdls.Count == 0)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Constraint,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "No constraint definitions found in ALTER TABLE statement."
            };
        }

        var ddlResult = string.Join("\n\n", constraintDdls.Select(d =>
            $"ALTER TABLE {targetSchema}.{QuoteIdentifier(tableName)} ADD\n    {d};"));

        var status = needsAiFallback ? ConversionStatus.Flagged : ConversionStatus.Converted;

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Constraint,
            Status = status,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddlResult,
            ConfidenceScore = needsAiFallback ? 0.6 : 1.0,
            ReviewFlags = reviewFlags,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private ConversionResult ConvertStandaloneConstraint(
        ConstraintDefinition constraint,
        SchemaObject obj,
        ConversionContext context)
    {
        var reviewFlags = new List<ManualReviewFlag>();
        var compatibilityNotes = new List<CompatibilityNote>();
        var needsAiFallback = false;

        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);

        var ddl = ConvertConstraintDefinition(constraint, obj.Name, targetSchema, context, reviewFlags, compatibilityNotes, ref needsAiFallback);

        if (ddl is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Constraint,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "Unsupported constraint type."
            };
        }

        var status = needsAiFallback ? ConversionStatus.Flagged : ConversionStatus.Converted;

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Constraint,
            Status = status,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = needsAiFallback ? 0.6 : 1.0,
            ReviewFlags = reviewFlags,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private string? ConvertConstraintDefinition(
        ConstraintDefinition constraint,
        string tableName,
        string targetSchema,
        ConversionContext context,
        List<ManualReviewFlag> reviewFlags,
        List<CompatibilityNote> compatibilityNotes,
        ref bool needsAiFallback)
    {
        return constraint switch
        {
            UniqueConstraintDefinition unique => ConvertPrimaryKeyOrUnique(unique),
            ForeignKeyConstraintDefinition fk => ConvertForeignKey(fk, context),
            CheckConstraintDefinition check => ConvertCheck(check, reviewFlags, ref needsAiFallback),
            _ => null
        };
    }

    private static string ConvertPrimaryKeyOrUnique(UniqueConstraintDefinition unique)
    {
        var constraintName = unique.ConstraintIdentifier?.Value;
        var columns = unique.Columns
            .Select(c =>
            {
                var colName = c.Column?.MultiPartIdentifier?.Identifiers.Last().Value ?? "unknown";
                var sortOrder = c.SortOrder == SortOrder.Descending ? " DESC" : "";
                return $"{QuoteIdentifier(colName)}{sortOrder}";
            })
            .ToList();

        var type = unique.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
        var nameClause = constraintName is not null
            ? $"CONSTRAINT {QuoteIdentifier(constraintName)} "
            : "";

        return $"{nameClause}{type} ({string.Join(", ", columns)})";
    }

    private static string ConvertForeignKey(ForeignKeyConstraintDefinition fk, ConversionContext context)
    {
        var constraintName = fk.ConstraintIdentifier?.Value;
        var nameClause = constraintName is not null
            ? $"CONSTRAINT {QuoteIdentifier(constraintName)} "
            : "";

        var columns = fk.Columns
            .Select(c => QuoteIdentifier(c.Value))
            .ToList();

        var refSchema = fk.ReferenceTableName?.SchemaIdentifier?.Value ?? "dbo";
        var refTable = fk.ReferenceTableName?.BaseIdentifier?.Value ?? "unknown";
        var mappedRefSchema = MapSchema(refSchema, context.SchemaMappings);

        var refColumns = fk.ReferencedTableColumns
            .Select(c => QuoteIdentifier(c.Value))
            .ToList();

        var onDelete = MapReferentialAction(fk.DeleteAction);
        var onUpdate = MapReferentialAction(fk.UpdateAction);

        return $"{nameClause}FOREIGN KEY ({string.Join(", ", columns)}) " +
               $"REFERENCES {mappedRefSchema}.{QuoteIdentifier(refTable)} ({string.Join(", ", refColumns)}) " +
               $"ON DELETE {onDelete} ON UPDATE {onUpdate}";
    }

    private string? ConvertCheck(
        CheckConstraintDefinition check,
        List<ManualReviewFlag> reviewFlags,
        ref bool needsAiFallback)
    {
        var constraintName = check.ConstraintIdentifier?.Value;
        var nameClause = constraintName is not null
            ? $"CONSTRAINT {QuoteIdentifier(constraintName)} "
            : "";

        var sourceExpr = GetFragmentText(check.CheckCondition);
        var result = _expressionTranslator.Translate(sourceExpr);

        if (result.IsSuccess)
        {
            return $"{nameClause}CHECK ({result.TranslatedExpression})";
        }

        // Cannot translate — mark for AI fallback
        needsAiFallback = true;
        reviewFlags.Add(new ManualReviewFlag
        {
            Reason = $"CHECK constraint expression cannot be translated: {result.CannotTranslateReason}",
            CodeSection = sourceExpr,
            SuggestedAlternative = "AI-assisted translation recommended"
        });

        return $"{nameClause}CHECK ({sourceExpr}) /* requires AI review */";
    }

    private static string MapReferentialAction(DeleteUpdateAction action)
    {
        return action switch
        {
            DeleteUpdateAction.Cascade => "CASCADE",
            DeleteUpdateAction.SetNull => "SET NULL",
            DeleteUpdateAction.SetDefault => "SET DEFAULT",
            DeleteUpdateAction.NoAction => "NO ACTION",
            _ => "NO ACTION"
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

    private sealed class AlterTableVisitor : TSqlFragmentVisitor
    {
        public AlterTableAddTableElementStatement? AlterTable { get; private set; }

        public override void Visit(AlterTableAddTableElementStatement node)
        {
            AlterTable ??= node;
        }
    }

    private sealed class CreateTableConstraintVisitor : TSqlFragmentVisitor
    {
        public ConstraintDefinition? Constraint { get; private set; }

        public override void Visit(UniqueConstraintDefinition node)
        {
            Constraint ??= node;
        }

        public override void Visit(ForeignKeyConstraintDefinition node)
        {
            Constraint ??= node;
        }

        public override void Visit(CheckConstraintDefinition node)
        {
            Constraint ??= node;
        }
    }
}
