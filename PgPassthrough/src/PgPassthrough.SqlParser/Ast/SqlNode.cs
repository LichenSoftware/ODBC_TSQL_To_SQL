namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Base class for every AST node.
/// Carries source position so error messages and diagnostics can point back
/// to the exact location in the original T-SQL text.
/// </summary>
public abstract class SqlNode
{
    /// <summary>1-based line in the source where this node begins.</summary>
    public int Line { get; init; }

    /// <summary>1-based column in the source where this node begins.</summary>
    public int Column { get; init; }

    /// <summary>Visitor dispatch. Each concrete node calls the matching visitor method.</summary>
    public abstract TResult Accept<TResult>(ISqlVisitor<TResult> visitor);
}

/// <summary>
/// Root of a parsed T-SQL batch. A batch is a sequence of one or more statements
/// separated by GO or semicolons.
/// </summary>
public sealed class SqlBatch : SqlNode
{
    public IReadOnlyList<SqlStatement> Statements { get; init; } = [];

    public override TResult Accept<TResult>(ISqlVisitor<TResult> visitor)
        => visitor.VisitBatch(this);
}
