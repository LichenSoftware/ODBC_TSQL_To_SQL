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
        "ISNULL", "CHARINDEX", "PATINDEX", "STUFF",
        "STRING_SPLIT"
    };

    /// <summary>
    /// JSON built-in functions that map to JSON_METHOD features.
    /// </summary>
    private static readonly HashSet<string> JsonFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "JSON_VALUE", "JSON_QUERY", "JSON_MODIFY"
    };

    /// <summary>
    /// Functions that map to the OPENJSON feature (Risk 4).
    /// </summary>
    private static readonly HashSet<string> OpenJsonFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "OPENJSON"
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

    // Tracking fields for TOP_WITHOUT_ORDER detection
    private bool _hasTopWithoutOrder;
    private bool _hasOrderBy;
    private TSqlFragment? _topNode;

    /// <summary>
    /// Call after visiting the complete statement to finalize detection
    /// of features that require full-statement context (e.g., TOP without ORDER BY).
    /// </summary>
    public void FinalizeDetection()
    {
        if (_hasTopWithoutOrder && !_hasOrderBy && _topNode is not null)
        {
            AddFeature("TOP_WITHOUT_ORDER", FeatureCategory.QueryFeature, _topNode);
        }
    }

    #region Query Features

    public override void ExplicitVisit(TopRowFilter node)
    {
        AddFeature("TOP", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    /// <summary>
    /// Detects SELECT TOP without ORDER BY (non-deterministic result set).
    /// </summary>
    public override void ExplicitVisit(QuerySpecification node)
    {
        // Check for TOP without ORDER BY at the query specification level.
        // The OrderByClause lives on the parent QueryExpression or SelectStatement,
        // but QuerySpecification.TopRowFilter exists directly.
        if (node.TopRowFilter is not null)
        {
            // Walk up: if no ORDER BY is present in this query specification's enclosing select,
            // flag it. We check if the node's direct OrderByClause is null/empty.
            // Note: ORDER BY is on SelectStatement, not QuerySpecification in the ScriptDom AST.
            // We track it and resolve later in the statement-level visit.
            _hasTopWithoutOrder = true;
            _topNode = node.TopRowFilter;
        }

        base.ExplicitVisit(node);
    }

    /// <summary>
    /// Tracks ORDER BY presence to pair with TOP detection.
    /// </summary>
    public override void ExplicitVisit(OrderByClause node)
    {
        _hasOrderBy = true;
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
            if (functionName.Equals("STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                AddFeature("STRING_SPLIT", FeatureCategory.FunctionUsage, node);
            }
            else if (KnownFunctions.Contains(functionName))
            {
                AddFeature(functionName.ToUpperInvariant(), FeatureCategory.FunctionUsage, node);
            }
            else if (OpenJsonFunctions.Contains(functionName))
            {
                AddFeature("OPENJSON", FeatureCategory.FunctionUsage, node);
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

    #region Error Handling and Control Flow Features

    public override void ExplicitVisit(PrintStatement node)
    {
        AddFeature("PRINT_STATEMENT", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(RaiseErrorStatement node)
    {
        AddFeature("RAISERROR", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    public override void ExplicitVisit(ThrowStatement node)
    {
        AddFeature("THROW", FeatureCategory.TransactionFeature, node);
        base.ExplicitVisit(node);
    }

    #endregion

    #region XML Features

    public override void ExplicitVisit(XmlForClause node)
    {
        // FOR XML PATH, FOR XML AUTO, FOR XML RAW, FOR XML EXPLICIT
        AddFeature("FOR_XML", FeatureCategory.QueryFeature, node);
        base.ExplicitVisit(node);
    }

    #endregion

    #region Table-Valued Functions

    /// <summary>
    /// Detects table-valued functions like STRING_SPLIT
    /// which appear in FROM clauses as SchemaObjectFunctionTableReference.
    /// </summary>
    public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
    {
        var functionName = node.SchemaObject?.BaseIdentifier?.Value;
        if (functionName is not null)
        {
            if (functionName.Equals("STRING_SPLIT", StringComparison.OrdinalIgnoreCase))
            {
                AddFeature("STRING_SPLIT", FeatureCategory.FunctionUsage, node);
            }
            else if (functionName.Equals("OPENJSON", StringComparison.OrdinalIgnoreCase))
            {
                AddFeature("OPENJSON", FeatureCategory.FunctionUsage, node);
            }
        }
        base.ExplicitVisit(node);
    }

    /// <summary>
    /// Detects OPENJSON which has its own dedicated AST node type.
    /// </summary>
    public override void ExplicitVisit(OpenJsonTableReference node)
    {
        AddFeature("OPENJSON", FeatureCategory.FunctionUsage, node);
        base.ExplicitVisit(node);
    }

    #endregion

    #region String Concatenation

    /// <summary>
    /// Detects string concatenation using the + operator on string expressions.
    /// </summary>
    public override void ExplicitVisit(BinaryExpression node)
    {
        if (node.BinaryExpressionType == BinaryExpressionType.Add)
        {
            // Check if either side is a string literal, which indicates string concatenation
            if (IsStringExpression(node.FirstExpression) || IsStringExpression(node.SecondExpression))
            {
                AddFeature("STRING_CONCAT_PLUS", FeatureCategory.FunctionUsage, node);
            }
        }
        base.ExplicitVisit(node);
    }

    private static bool IsStringExpression(ScalarExpression expr)
    {
        return expr is StringLiteral
            || (expr is CastCall cast && IsStringType(cast.DataType))
            || (expr is ConvertCall convert && IsStringType(convert.DataType));
    }

    private static bool IsStringType(DataTypeReference? dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            return sqlType.SqlDataTypeOption is SqlDataTypeOption.VarChar
                or SqlDataTypeOption.NVarChar
                or SqlDataTypeOption.Char
                or SqlDataTypeOption.NChar
                or SqlDataTypeOption.Text
                or SqlDataTypeOption.NText;
        }
        return false;
    }

    #endregion

    #region Implicit Conversion

    /// <summary>
    /// Detects potential implicit type conversions in comparisons
    /// (e.g., integer column compared to string literal).
    /// </summary>
    public override void ExplicitVisit(BooleanComparisonExpression node)
    {
        if (IsImplicitConversion(node.FirstExpression, node.SecondExpression))
        {
            AddFeature("IMPLICIT_CONVERSION", FeatureCategory.QueryFeature, node);
        }
        base.ExplicitVisit(node);
    }

    private static bool IsImplicitConversion(ScalarExpression left, ScalarExpression right)
    {
        // Detect: column/variable compared to string literal where the string looks numeric
        // e.g., WHERE IntColumn = '123'
        if (left is ColumnReferenceExpression && right is StringLiteral strLit)
        {
            return IsNumericString(strLit.Value);
        }
        if (right is ColumnReferenceExpression && left is StringLiteral strLit2)
        {
            return IsNumericString(strLit2.Value);
        }
        return false;
    }

    private static bool IsNumericString(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && double.TryParse(value, out _);
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
