namespace PgPassthrough.SqlParser.Ast;

// ============================================================================
// Expression base
// ============================================================================

/// <summary>Base for all expression-level nodes.</summary>
public abstract class SqlExpression : SqlNode { }

// ============================================================================
// Literals
// ============================================================================

public sealed class IntegerLiteralExpression : SqlExpression
{
    public long Value { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitIntegerLiteral(this);
}

public sealed class DecimalLiteralExpression : SqlExpression
{
    public decimal Value { get; init; }
    public string RawText { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitDecimalLiteral(this);
}

public sealed class FloatLiteralExpression : SqlExpression
{
    public double Value { get; init; }
    public string RawText { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitFloatLiteral(this);
}

public sealed class StringLiteralExpression : SqlExpression
{
    public string Value { get; init; } = string.Empty;
    /// <summary>True if the original was N'...' (Unicode prefix).</summary>
    public bool IsUnicode { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitStringLiteral(this);
}

public sealed class NullLiteralExpression : SqlExpression
{
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitNullLiteral(this);
}

public sealed class BooleanLiteralExpression : SqlExpression
{
    public bool Value { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBooleanLiteral(this);
}

// ============================================================================
// Column / name references
// ============================================================================

/// <summary>
/// A possibly-qualified name: server.database.schema.object or any prefix thereof.
/// </summary>
public sealed class ObjectName : SqlNode
{
    public string? Server { get; init; }
    public string? Database { get; init; }
    public string? Schema { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>Canonical two-part name: schema.name or just name.</summary>
    public string TwoPartName => Schema != null ? $"{Schema}.{Name}" : Name;

    /// <summary>True if the name starts with # (temp table).</summary>
    public bool IsTemporaryTable => Name.StartsWith('#');

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitObjectName(this);
}

/// <summary>
/// A column reference: [table.]column
/// </summary>
public sealed class ColumnReferenceExpression : SqlExpression
{
    /// <summary>Optional table/alias qualifier.</summary>
    public string? TableAlias { get; init; }
    public string ColumnName { get; init; } = string.Empty;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitColumnReference(this);
}

// ============================================================================
// Parameters
// ============================================================================

public sealed class ParameterExpression : SqlExpression
{
    /// <summary>The parameter name including the @ prefix.</summary>
    public string Name { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitParameter(this);
}

public sealed class GlobalVariableExpression : SqlExpression
{
    /// <summary>E.g. "@@ROWCOUNT", "@@IDENTITY".</summary>
    public string Name { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitGlobalVariable(this);
}

// ============================================================================
// Binary and unary operators
// ============================================================================

public sealed class BinaryExpression : SqlExpression
{
    public SqlExpression Left { get; init; } = null!;
    public BinaryOperator Operator { get; init; }
    public SqlExpression Right { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBinary(this);
}

public enum BinaryOperator
{
    Add, Subtract, Multiply, Divide, Modulo,
    BitwiseAnd, BitwiseOr, BitwiseXor,
    Equal, NotEqual, LessThan, GreaterThan, LessThanOrEqual, GreaterThanOrEqual,
    And, Or,
    Like, NotLike,
    In, NotIn,
    Is, IsNot,
    StringConcat    // + when used between strings
}

public sealed class UnaryExpression : SqlExpression
{
    public UnaryOperator Operator { get; init; }
    public SqlExpression Operand { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitUnary(this);
}

public enum UnaryOperator { Negate, Not, BitwiseNot }

// ============================================================================
// Predicates
// ============================================================================

public sealed class BetweenExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public SqlExpression Low { get; init; } = null!;
    public SqlExpression High { get; init; } = null!;
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBetween(this);
}

public sealed class InListExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public IReadOnlyList<SqlExpression> Items { get; init; } = [];
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitInList(this);
}

public sealed class InSubqueryExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public SelectStatement Subquery { get; init; } = null!;
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitInSubquery(this);
}

public sealed class LikeExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public SqlExpression Pattern { get; init; } = null!;
    public SqlExpression? Escape { get; init; }
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitLike(this);
}

public sealed class IsNullExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitIsNull(this);
}

public sealed class ExistsExpression : SqlExpression
{
    public SelectStatement Subquery { get; init; } = null!;
    public bool IsNot { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitExists(this);
}

// ============================================================================
// Functions
// ============================================================================

public sealed class FunctionCallExpression : SqlExpression
{
    /// <summary>Function name, possibly qualified (e.g. "dbo.udf_GetPrice").</summary>
    public ObjectName Name { get; init; } = null!;
    public IReadOnlyList<SqlExpression> Arguments { get; init; } = [];
    public bool Distinct { get; init; }
    public OverClause? Over { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitFunctionCall(this);
}

public sealed class OverClause : SqlNode
{
    public IReadOnlyList<SqlExpression> PartitionBy { get; init; } = [];
    public IReadOnlyList<OrderByItem> OrderBy { get; init; } = [];
    public WindowFrame? Frame { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitOverClause(this);
}

public sealed class WindowFrame : SqlNode
{
    public WindowFrameUnit Unit { get; init; }
    public WindowFrameBound Start { get; init; } = null!;
    public WindowFrameBound? End { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitWindowFrame(this);
}

public enum WindowFrameUnit { Rows, Range }

public sealed class WindowFrameBound : SqlNode
{
    public WindowFrameBoundKind Kind { get; init; }
    public SqlExpression? Offset { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitWindowFrameBound(this);
}

public enum WindowFrameBoundKind
{
    UnboundedPreceding, Preceding, CurrentRow, Following, UnboundedFollowing
}

// ============================================================================
// CAST / CONVERT
// ============================================================================

public sealed class CastExpression : SqlExpression
{
    public SqlExpression Value { get; init; } = null!;
    public DataTypeNode TargetType { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitCast(this);
}

public sealed class ConvertExpression : SqlExpression
{
    public DataTypeNode TargetType { get; init; } = null!;
    public SqlExpression Value { get; init; } = null!;
    /// <summary>Optional style argument (e.g. CONVERT(VARCHAR, dt, 103)).</summary>
    public SqlExpression? Style { get; init; }
    public bool IsTryCast { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitConvert(this);
}

// ============================================================================
// CASE expression
// ============================================================================

public sealed class CaseExpression : SqlExpression
{
    /// <summary>Non-null for simple CASE (CASE expr WHEN val THEN ...) form.</summary>
    public SqlExpression? InputExpression { get; init; }
    public IReadOnlyList<WhenClause> WhenClauses { get; init; } = [];
    public SqlExpression? ElseExpression { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitCase(this);
}

public sealed class WhenClause : SqlNode
{
    public SqlExpression Condition { get; init; } = null!;
    public SqlExpression Result { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitWhenClause(this);
}

// ============================================================================
// Subquery expression
// ============================================================================

public sealed class SubqueryExpression : SqlExpression
{
    public SelectStatement Query { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSubquery(this);
}

// ============================================================================
// Data type node
// ============================================================================

public sealed class DataTypeNode : SqlNode
{
    public string TypeName { get; init; } = string.Empty;
    public int? Length { get; init; }
    public bool IsMax { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitDataType(this);
}

// ============================================================================
// Table sources
// ============================================================================

public abstract class TableSource : SqlNode { }

public sealed class TableReferenceSource : TableSource
{
    public ObjectName Name { get; init; } = null!;
    public string? Alias { get; init; }
    public IReadOnlyList<TableHint> Hints { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitTableReference(this);
}

public sealed class SubquerySource : TableSource
{
    public SelectStatement Query { get; init; } = null!;
    public string Alias { get; init; } = string.Empty;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSubquerySource(this);
}

public sealed class JoinedSource : TableSource
{
    public TableSource Left { get; init; } = null!;
    public JoinType JoinType { get; init; }
    public TableSource Right { get; init; } = null!;
    public SqlExpression? Condition { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitJoin(this);
}

public enum JoinType
{
    Inner, LeftOuter, RightOuter, FullOuter, Cross,
    CrossApply, OuterApply
}

public sealed class TableHint : SqlNode
{
    public string HintName { get; init; } = string.Empty;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitTableHint(this);
}
