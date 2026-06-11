namespace PgPassthrough.SqlParser.Ast;

// ============================================================================
// Statement base
// ============================================================================

/// <summary>Base for all statement-level nodes.</summary>
public abstract class SqlStatement : SqlNode { }

// ============================================================================
// SELECT
// ============================================================================

public sealed class SelectStatement : SqlStatement
{
    public TopClause? Top { get; init; }
    public bool Distinct { get; init; }
    public IReadOnlyList<SelectItem> SelectList { get; init; } = [];
    public IntoClause? Into { get; init; }
    public IReadOnlyList<TableSource> From { get; init; } = [];
    public SqlExpression? Where { get; init; }
    public IReadOnlyList<SqlExpression> GroupBy { get; init; } = [];
    public SqlExpression? Having { get; init; }
    public IReadOnlyList<OrderByItem> OrderBy { get; init; } = [];
    public OffsetFetchClause? OffsetFetch { get; init; }
    public SetOperator? SetOperator { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSelect(this);
}

public sealed class TopClause : SqlNode
{
    public SqlExpression Count { get; init; } = null!;
    public bool Percent { get; init; }
    public bool WithTies { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitTop(this);
}

public sealed class SelectItem : SqlNode
{
    /// <summary>null = wildcard (*)</summary>
    public SqlExpression? Expression { get; init; }
    public bool IsStar { get; init; }
    /// <summary>Optional "schema.table.*" qualifier when IsStar is true.</summary>
    public ObjectName? StarQualifier { get; init; }
    /// <summary>Column alias (AS alias or bare alias).</summary>
    public string? Alias { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSelectItem(this);
}

public sealed class IntoClause : SqlNode
{
    public ObjectName Table { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitIntoClause(this);
}

public sealed class OrderByItem : SqlNode
{
    public SqlExpression Expression { get; init; } = null!;
    public SortDirection Direction { get; init; } = SortDirection.Ascending;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitOrderByItem(this);
}

public enum SortDirection { Ascending, Descending }

public sealed class OffsetFetchClause : SqlNode
{
    public SqlExpression Offset { get; init; } = null!;
    public SqlExpression? Fetch { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitOffsetFetch(this);
}

public sealed class SetOperator : SqlNode
{
    public SetOperatorKind Kind { get; init; }
    public bool All { get; init; }
    public SelectStatement Right { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSetOperator(this);
}

public enum SetOperatorKind { Union, Intersect, Except }

// ============================================================================
// INSERT
// ============================================================================

public sealed class InsertStatement : SqlStatement
{
    public ObjectName Target { get; init; } = null!;
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>Either ValuesSource or SelectSource is set.</summary>
    public ValuesClause? ValuesSource { get; init; }
    public SelectStatement? SelectSource { get; init; }
    public OutputClause? Output { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitInsert(this);
}

public sealed class ValuesClause : SqlNode
{
    public IReadOnlyList<IReadOnlyList<SqlExpression>> RowValues { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitValuesClause(this);
}

// ============================================================================
// UPDATE
// ============================================================================

public sealed class UpdateStatement : SqlStatement
{
    public ObjectName Target { get; init; } = null!;
    public string? TargetAlias { get; init; }
    public IReadOnlyList<SetClause> Sets { get; init; } = [];
    public IReadOnlyList<TableSource> From { get; init; } = [];
    public SqlExpression? Where { get; init; }
    public OutputClause? Output { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitUpdate(this);
}

public sealed class SetClause : SqlNode
{
    public string ColumnName { get; init; } = string.Empty;
    public SqlExpression Value { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSetClause(this);
}

// ============================================================================
// DELETE
// ============================================================================

public sealed class DeleteStatement : SqlStatement
{
    public ObjectName Target { get; init; } = null!;
    public string? TargetAlias { get; init; }
    public IReadOnlyList<TableSource> From { get; init; } = [];
    public SqlExpression? Where { get; init; }
    public OutputClause? Output { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitDelete(this);
}

// ============================================================================
// OUTPUT clause (shared by INSERT/UPDATE/DELETE)
// ============================================================================

public sealed class OutputClause : SqlNode
{
    public IReadOnlyList<SelectItem> Items { get; init; } = [];
    public ObjectName? IntoTable { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitOutputClause(this);
}

// ============================================================================
// TRANSACTION statements
// ============================================================================

public sealed class BeginTransactionStatement : SqlStatement
{
    public string? TransactionName { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBeginTransaction(this);
}

public sealed class CommitTransactionStatement : SqlStatement
{
    public string? TransactionName { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitCommitTransaction(this);
}

public sealed class RollbackTransactionStatement : SqlStatement
{
    public string? TransactionName { get; init; }
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitRollbackTransaction(this);
}

public sealed class SaveTransactionStatement : SqlStatement
{
    public string SavepointName { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSaveTransaction(this);
}

// ============================================================================
// SET options
// ============================================================================

public sealed class SetOptionStatement : SqlStatement
{
    public string OptionName { get; init; } = string.Empty;
    public bool IsOn { get; init; }
    /// <summary>Non-boolean value (e.g. SET ROWCOUNT 100, SET DATEFORMAT mdy).</summary>
    public SqlExpression? Value { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitSetOption(this);
}

// ============================================================================
// USE database
// ============================================================================

public sealed class UseDatabaseStatement : SqlStatement
{
    public string DatabaseName { get; init; } = string.Empty;
    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitUseDatabase(this);
}

// ============================================================================
// EXEC / EXECUTE
// ============================================================================

public sealed class ExecuteStatement : SqlStatement
{
    public ObjectName ProcedureName { get; init; } = null!;
    public IReadOnlyList<ProcedureArgument> Arguments { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitExecute(this);
}

public sealed class ProcedureArgument : SqlNode
{
    public string? ParameterName { get; init; }
    public SqlExpression Value { get; init; } = null!;
    public bool IsOutput { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitProcedureArgument(this);
}

// ============================================================================
// IF / WHILE
// ============================================================================

public sealed class IfStatement : SqlStatement
{
    public SqlExpression Condition { get; init; } = null!;
    public SqlStatement ThenBranch { get; init; } = null!;
    public SqlStatement? ElseBranch { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitIf(this);
}

public sealed class WhileStatement : SqlStatement
{
    public SqlExpression Condition { get; init; } = null!;
    public SqlStatement Body { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitWhile(this);
}

// ============================================================================
// BEGIN...END block
// ============================================================================

public sealed class BeginEndBlock : SqlStatement
{
    public IReadOnlyList<SqlStatement> Statements { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBeginEnd(this);
}

// ============================================================================
// PRINT
// ============================================================================

public sealed class PrintStatement : SqlStatement
{
    public SqlExpression Expression { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitPrint(this);
}

// ============================================================================
// RETURN
// ============================================================================

public sealed class ReturnStatement : SqlStatement
{
    public SqlExpression? Value { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitReturn(this);
}

// ============================================================================
// DECLARE @variable
// ============================================================================

public sealed class DeclareStatement : SqlStatement
{
    public IReadOnlyList<VariableDeclaration> Declarations { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitDeclare(this);
}

public sealed class VariableDeclaration : SqlNode
{
    public string Name { get; init; } = string.Empty;
    public DataTypeNode DataType { get; init; } = null!;
    public SqlExpression? InitialValue { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitVariableDeclaration(this);
}

// ============================================================================
// TRUNCATE TABLE
// ============================================================================

public sealed class TruncateTableStatement : SqlStatement
{
    public ObjectName Table { get; init; } = null!;

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitTruncateTable(this);
}

// ============================================================================
// CREATE TABLE (minimal — enough for temp table creation)
// ============================================================================

public sealed class CreateTableStatement : SqlStatement
{
    public ObjectName Table { get; init; } = null!;
    public bool IsTemporary { get; init; }
    public IReadOnlyList<ColumnDefinition> Columns { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitCreateTable(this);
}

public sealed class ColumnDefinition : SqlNode
{
    public string Name { get; init; } = string.Empty;
    public DataTypeNode DataType { get; init; } = null!;
    public bool IsNullable { get; init; } = true;
    public bool IsIdentity { get; init; }
    public int IdentitySeed { get; init; } = 1;
    public int IdentityIncrement { get; init; } = 1;
    public SqlExpression? DefaultValue { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsUnique { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitColumnDefinition(this);
}

// ============================================================================
// DROP TABLE
// ============================================================================

public sealed class DropTableStatement : SqlStatement
{
    public IReadOnlyList<ObjectName> Tables { get; init; } = [];
    public bool IfExists { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitDropTable(this);
}

// ============================================================================
// Passthrough — unsupported statements preserved verbatim
// ============================================================================

/// <summary>
/// Wraps a SQL statement that the parser could not fully parse.
/// The translator will pass the raw text through unchanged and emit a warning.
/// </summary>
public sealed class UnparsedStatement : SqlStatement
{
    public string RawSql { get; init; } = string.Empty;
    public string? Reason { get; init; }

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitUnparsed(this);
}
