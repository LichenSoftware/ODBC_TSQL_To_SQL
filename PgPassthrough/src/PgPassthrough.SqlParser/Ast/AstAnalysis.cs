namespace PgPassthrough.SqlParser.Ast;

// Collection of small, focused AST analysis visitors.

// ============================================================================
// Temp table detector
// ============================================================================

/// <summary>
/// Returns true if the subtree contains any reference to a temporary table
/// (#name or ##name). Used to decide whether temp-table rewriting is needed.
/// </summary>
public sealed class TempTableDetector : SqlVisitorBase<bool>
{
    protected override bool DefaultResult => false;
    protected override bool DefaultVisit(SqlNode node) => false;

    public static bool ContainsTempTableRef(SqlNode root) =>
        root.Accept(new TempTableDetector());

    public override bool VisitSelect(SelectStatement node)
    {
        if (node.From.Any(f => f.Accept(this))) return true;
        if (node.Into?.Accept(this) == true) return true;
        if (node.SetOperator?.Right.Accept(this) == true) return true;
        return false;
    }

    public override bool VisitTableReference(TableReferenceSource node) =>
        node.Name.IsTemporaryTable;

    public override bool VisitInsert(InsertStatement node) =>
        node.Target.IsTemporaryTable || (node.SelectSource?.Accept(this) ?? false);

    public override bool VisitUpdate(UpdateStatement node) =>
        node.Target.IsTemporaryTable || node.From.Any(f => f.Accept(this));

    public override bool VisitDelete(DeleteStatement node) =>
        node.Target.IsTemporaryTable || node.From.Any(f => f.Accept(this));

    public override bool VisitCreateTable(CreateTableStatement node) =>
        node.IsTemporary;

    public override bool VisitDropTable(DropTableStatement node) =>
        node.Tables.Any(t => t.IsTemporaryTable);

    public override bool VisitBatch(SqlBatch node) =>
        node.Statements.Any(s => s.Accept(this));

    public override bool VisitIntoClause(IntoClause node) =>
        node.Table.IsTemporaryTable;

    public override bool VisitJoin(JoinedSource node) =>
        node.Left.Accept(this) || node.Right.Accept(this);

    public override bool VisitSubquerySource(SubquerySource node) =>
        node.Query.Accept(this);
}

// ============================================================================
// Global variable collector
// ============================================================================

/// <summary>
/// Collects all <see cref="GlobalVariableExpression"/> names referenced in a subtree.
/// Used so the translator can decide which global variable rewrites are needed
/// (e.g. @@ROWCOUNT → pg_affected_rows, @@IDENTITY → lastval()).
/// </summary>
public sealed class GlobalVariableCollector : SqlVisitorBase<bool>
{
    protected override bool DefaultResult => false;

    private readonly HashSet<string> _found = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> Found => _found;

    public static IReadOnlySet<string> Collect(SqlNode root)
    {
        var collector = new GlobalVariableCollector();
        root.Accept(collector);
        return collector.Found;
    }

    public override bool VisitBatch(SqlBatch node)
    {
        foreach (var s in node.Statements) s.Accept(this);
        return false;
    }

    public override bool VisitGlobalVariable(GlobalVariableExpression node)
    {
        _found.Add(node.Name.ToUpperInvariant());
        return false;
    }

    // Walk all expression-bearing nodes
    public override bool VisitBinary(BinaryExpression node)
    {
        node.Left.Accept(this);
        node.Right.Accept(this);
        return false;
    }

    public override bool VisitUnary(UnaryExpression node) { node.Operand.Accept(this); return false; }
    public override bool VisitFunctionCall(FunctionCallExpression node) { foreach (var a in node.Arguments) a.Accept(this); return false; }
    public override bool VisitCase(CaseExpression node)
    {
        node.InputExpression?.Accept(this);
        foreach (var w in node.WhenClauses) { w.Condition.Accept(this); w.Result.Accept(this); }
        node.ElseExpression?.Accept(this);
        return false;
    }
    public override bool VisitCast(CastExpression node) { node.Value.Accept(this); return false; }
    public override bool VisitConvert(ConvertExpression node) { node.Value.Accept(this); node.Style?.Accept(this); return false; }
    public override bool VisitBetween(BetweenExpression node) { node.Value.Accept(this); node.Low.Accept(this); node.High.Accept(this); return false; }
    public override bool VisitInList(InListExpression node) { node.Value.Accept(this); foreach (var i in node.Items) i.Accept(this); return false; }
    public override bool VisitLike(LikeExpression node) { node.Value.Accept(this); node.Pattern.Accept(this); return false; }
    public override bool VisitIsNull(IsNullExpression node) { node.Value.Accept(this); return false; }
    public override bool VisitSelectItem(SelectItem node) { node.Expression?.Accept(this); return false; }
    public override bool VisitSelect(SelectStatement node)
    {
        foreach (var item in node.SelectList) item.Accept(this);
        node.Where?.Accept(this);
        foreach (var g in node.GroupBy) g.Accept(this);
        node.Having?.Accept(this);
        foreach (var o in node.OrderBy) o.Expression.Accept(this);
        return false;
    }
}

// ============================================================================
// Parameter name collector
// ============================================================================

/// <summary>
/// Collects all distinct @parameter names from a statement.
/// Used to validate that all bound parameters are accounted for.
/// </summary>
public sealed class ParameterCollector : SqlVisitorBase<bool>
{
    protected override bool DefaultResult => false;

    private readonly HashSet<string> _params = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlySet<string> Parameters => _params;

    public static IReadOnlySet<string> Collect(SqlNode root)
    {
        var c = new ParameterCollector();
        root.Accept(c);
        return c.Parameters;
    }

    public override bool VisitBatch(SqlBatch node)
    {
        foreach (var s in node.Statements) s.Accept(this);
        return false;
    }

    public override bool VisitParameter(ParameterExpression node)
    {
        _params.Add(node.Name);
        return false;
    }

    public override bool VisitBinary(BinaryExpression node) { node.Left.Accept(this); node.Right.Accept(this); return false; }
    public override bool VisitUnary(UnaryExpression node) { node.Operand.Accept(this); return false; }
    public override bool VisitFunctionCall(FunctionCallExpression node) { foreach (var a in node.Arguments) a.Accept(this); return false; }
    public override bool VisitCase(CaseExpression node)
    {
        node.InputExpression?.Accept(this);
        foreach (var w in node.WhenClauses) { w.Condition.Accept(this); w.Result.Accept(this); }
        node.ElseExpression?.Accept(this);
        return false;
    }
    public override bool VisitCast(CastExpression node) { node.Value.Accept(this); return false; }
    public override bool VisitConvert(ConvertExpression node) { node.Value.Accept(this); node.Style?.Accept(this); return false; }
    public override bool VisitBetween(BetweenExpression node) { node.Value.Accept(this); node.Low.Accept(this); node.High.Accept(this); return false; }
    public override bool VisitInList(InListExpression node) { node.Value.Accept(this); foreach (var i in node.Items) i.Accept(this); return false; }
    public override bool VisitLike(LikeExpression node) { node.Value.Accept(this); node.Pattern.Accept(this); return false; }
    public override bool VisitIsNull(IsNullExpression node) { node.Value.Accept(this); return false; }
    public override bool VisitSelect(SelectStatement node)
    {
        foreach (var item in node.SelectList) item.Accept(this);
        node.Where?.Accept(this);
        foreach (var g in node.GroupBy) g.Accept(this);
        node.Having?.Accept(this);
        foreach (var o in node.OrderBy) o.Expression.Accept(this);
        return false;
    }
    public override bool VisitInsert(InsertStatement node)
    {
        if (node.ValuesSource != null)
            foreach (var row in node.ValuesSource.RowValues)
                foreach (var val in row) val.Accept(this);
        node.SelectSource?.Accept(this);
        return false;
    }
    public override bool VisitUpdate(UpdateStatement node)
    {
        foreach (var s in node.Sets) s.Value.Accept(this);
        node.Where?.Accept(this);
        return false;
    }
    public override bool VisitDelete(DeleteStatement node) { node.Where?.Accept(this); return false; }
}

// ============================================================================
// Hint stripper — detects and reports table hints for translation warnings
// ============================================================================

/// <summary>
/// Returns all table hint names found in the FROM clause of a statement.
/// The translator uses this to emit warnings for hints that have no PostgreSQL
/// equivalent (NOLOCK, UPDLOCK, etc.) and silently strips them.
/// </summary>
public sealed class TableHintCollector : SqlVisitorBase<bool>
{
    protected override bool DefaultResult => false;

    private readonly List<(string HintName, int Line, int Column)> _hints = new();
    public IReadOnlyList<(string HintName, int Line, int Column)> Hints => _hints;

    public static IReadOnlyList<(string HintName, int Line, int Column)> Collect(SqlNode root)
    {
        var c = new TableHintCollector();
        root.Accept(c);
        return c.Hints;
    }

    public override bool VisitTableHint(TableHint node)
    {
        _hints.Add((node.HintName, node.Line, node.Column));
        return false;
    }

    public override bool VisitTableReference(TableReferenceSource node)
    {
        foreach (var h in node.Hints) h.Accept(this);
        return false;
    }

    public override bool VisitJoin(JoinedSource node)
    {
        node.Left.Accept(this);
        node.Right.Accept(this);
        return false;
    }

    public override bool VisitSelect(SelectStatement node)
    {
        foreach (var src in node.From) src.Accept(this);
        return false;
    }

    public override bool VisitBatch(SqlBatch node)
    {
        foreach (var s in node.Statements) s.Accept(this);
        return false;
    }
}
