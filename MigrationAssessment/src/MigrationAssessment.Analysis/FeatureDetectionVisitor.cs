using Microsoft.SqlServer.TransactSql.ScriptDom;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// A TSqlFragmentVisitor that walks the AST to detect SQL Server-specific features
/// including query constructs, function calls, temporary objects, and transaction patterns.
/// </summary>
internal sealed class FeatureDetectionVisitor : TSqlFragmentVisitor
{
    private readonly string _statementId;
    private readonly List<DetectedFeature> _features = [];

    /// <summary>
    /// Known T-SQL functions that map to FunctionUsage features.
    /// </summary>
    private static readonly HashSet<string> KnownFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GETDATE", "DATEADD", "DATEDIFF", "DATEPART",
        "ISNULL", "CHARINDEX", "PATINDEX", "STUFF"
    };

    /// <summary>
    /// JSON built-in functions that map to JSON_METHOD features.
    /// </summary>
    private static readonly HashSet<string> JsonFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "JSON_VALUE", "JSON_QUERY", "OPENJSON", "JSON_MODIFY"
    };

    /// <summary>
    /// XML method names that map to XML_METHOD features.
    /// </summary>
    private static readonly HashSet<string> XmlMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "value", "query", "exist", "modify", "nodes"
    };

    public FeatureDetectionVisitor(string statementId)
    {
        _statementId = statementId;
    }

    public IReadOnlyList<DetectedFeature> DetectedFeatures => _features;

    #region Query Features

    public override void ExplicitVisit(TopRowFilter node)
    {
        AddFeature("TOP", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(OffsetClause node)
    {
        AddFeature("OFFSET_FETCH", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(MergeStatement node)
    {
        AddFeature("MERGE", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(OutputClause node)
    {
        AddFeature("OUTPUT", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(OutputIntoClause node)
    {
        AddFeature("OUTPUT", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(UnqualifiedJoin node)
    {
        if (node.UnqualifiedJoinType == UnqualifiedJoinType.CrossApply)
        {
            AddFeature("CROSS_APPLY", FeatureCategory.QueryFeature, node);
        }
        else if (node.UnqualifiedJoinType == UnqualifiedJoinType.OuterApply)
        {
            AddFeature("OUTER_APPLY", FeatureCategory.QueryFeature, node);
        }

        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(PivotedTableReference node)
    {
        AddFeature("PIVOT", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(UnpivotedTableReference node)
    {
        AddFeature("UNPIVOT", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(ExecuteStatement node)
    {
        AddFeature("DYNAMIC_SQL", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    #endregion

    #region Function Usage

    public override void ExplicitVisit(FunctionCall node)
    {
        var functionName = node.FunctionName?.Value;
        if (functionName is not null)
        {
            if (KnownFunctions.Contains(functionName))
            {
                AddFeature(functionName.ToUpperInvariant(), FeatureCategory.FunctionUsage, node);
            }
            else if (JsonFunctions.Contains(functionName))
            {
                AddFeature("JSON_METHOD", FeatureCategory.FunctionUsage, node);
            }
            else if (functionName.Equals("OPENQUERY", StringComparison.OrdinalIgnoreCase))
            {
                AddFeature("OPENQUERY", FeatureCategory.QueryFeature, node);
            }
            else if (functionName.Equals("OPENROWSET", StringComparison.OrdinalIgnoreCase))
            {
                AddFeature("OPENROWSET", FeatureCategory.QueryFeature, node);
            }
        }

        // Check for XML methods (e.g., col.value(), col.query())
        if (node.CallTarget is MultiPartIdentifierCallTarget
            && functionName is not null
            && XmlMethods.Contains(functionName))
        {
            AddFeature("XML_METHOD", FeatureCategory.FunctionUsage, node);
        }

        base.ExplicitVisit(node);
    }

    #endregion

    #region Temporary Objects

    public override void ExplicitVisit(NamedTableReference node)
    {
        var tableName = node.SchemaObject?.BaseIdentifier?.Value;
        if (tableName is not null)
        {
            if (tableName.StartsWith("##", StringComparison.Ordinal))
            {
                AddFeature("GLOBAL_TEMP_TABLE", FeatureCategory.TemporaryObject, node);
            }
            else if (tableName.StartsWith('#'))
            {
                AddFeature("TEMP_TABLE", FeatureCategory.TemporaryObject, node);
            }
        }

        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(CreateTableStatement node)
    {
        var tableName = node.SchemaObjectName?.BaseIdentifier?.Value;
        if (tableName is not null)
        {
            if (tableName.StartsWith("##", StringComparison.Ordinal))
            {
                AddFeature("GLOBAL_TEMP_TABLE", FeatureCategory.TemporaryObject, node);
            }
            else if (tableName.StartsWith('#'))
            {
                AddFeature("TEMP_TABLE", FeatureCategory.TemporaryObject, node);
            }
        }

        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(DeclareTableVariableBody node)
    {
        AddFeature("TABLE_VARIABLE", FeatureCategory.TemporaryObject, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(DeclareTableVariableStatement node)
    {
        AddFeature("TABLE_VARIABLE", FeatureCategory.TemporaryObject, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(ProcedureParameter node)
    {
        // Detect table-valued parameters (parameter with table type)
        if (node.DataType is UserDataTypeReference)
        {
            // Table-valued parameters use user-defined table types and have READONLY modifier
            if (node.Modifier == ParameterModifier.ReadOnly)
            {
                AddFeature("TABLE_VALUED_PARAMETER", FeatureCategory.TemporaryObject, node);
            }
        }

        base.ExplicitVisit(node);
    }

    #endregion

    #region Transaction Features

    public override void ExplicitVisit(TryCatchStatement node)
    {
        AddFeature("TRY_CATCH", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(BeginTransactionStatement node)
    {
        AddFeature("EXPLICIT_TRANSACTION", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(SaveTransactionStatement node)
    {
        AddFeature("SAVEPOINT", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(TableHint node)
    {
        DetectLockingHint(node.HintKind, node);
        base.ExplicitVisit(node);
    }

    #endregion

    #region Helpers

    private void DetectLockingHint(TableHintKind hintKind, TSqlFragment node)
    {
        switch (hintKind)
        {
            case TableHintKind.NoLock:
                AddFeature("NOLOCK", FeatureCategory.TransactionFeature, node);
                break;
            case TableHintKind.Rowlock:
                AddFeature("ROWLOCK", FeatureCategory.TransactionFeature, node);
                break;
            case TableHintKind.UpdLock:
                AddFeature("UPDLOCK", FeatureCategory.TransactionFeature, node);
                break;
        }
    }

    private void AddFeature(string featureName, FeatureCategory category, TSqlFragment node)
    {
        _features.Add(new DetectedFeature
        {
            FeatureName = featureName,
            Category = category,
            StatementId = _statementId,
            Line = node.StartLine,
            Column = node.StartColumn
        });
    }

    #endregion
}
