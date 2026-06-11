using System.Text;

namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Produces a normalised SQL string suitable for use as a translation cache key.
///
/// Differences from <see cref="SqlPrinter"/>:
///   - All literal values (integers, decimals, strings) are replaced with a
///     canonical placeholder (?), so two queries that differ only in literal
///     values produce the same cache key.
///   - Parameter names are preserved (they're already placeholders).
///   - Whitespace is collapsed to single spaces.
///   - All identifiers are upper-cased.
///
/// Example:
///   "SELECT * FROM Orders WHERE Id = 42 AND Name = 'Smith'"
///   →  "SELECT * FROM ORDERS WHERE ID = ? AND NAME = ?"
///
/// This is critical for cache hit rate in OLTP workloads, where the same
/// query structure is sent thousands of times with different literal values.
/// </summary>
public sealed class NormalisedKeyPrinter : SqlVisitorBase<string>
{
    protected override string DefaultResult => string.Empty;

    private readonly StringBuilder _sb = new(256);

    private NormalisedKeyPrinter() { }

    public static string Normalise(SqlNode node)
    {
        var p = new NormalisedKeyPrinter();
        node.Accept(p);
        // Collapse runs of whitespace
        return CollapseWhitespace(p._sb.ToString());
    }

    // -------------------------------------------------------------------------
    // Literal replacement — the key difference from SqlPrinter
    // -------------------------------------------------------------------------

    public override string VisitIntegerLiteral(IntegerLiteralExpression node)
    {
        _sb.Append('?');
        return string.Empty;
    }

    public override string VisitDecimalLiteral(DecimalLiteralExpression node)
    {
        _sb.Append('?');
        return string.Empty;
    }

    public override string VisitFloatLiteral(FloatLiteralExpression node)
    {
        _sb.Append('?');
        return string.Empty;
    }

    public override string VisitStringLiteral(StringLiteralExpression node)
    {
        _sb.Append('?');
        return string.Empty;
    }

    public override string VisitNullLiteral(NullLiteralExpression node)
    {
        _sb.Append("NULL");  // NULL is structural, not a literal value
        return string.Empty;
    }

    public override string VisitBooleanLiteral(BooleanLiteralExpression node)
    {
        _sb.Append('?');
        return string.Empty;
    }

    // -------------------------------------------------------------------------
    // Names — upper-cased for normalisation
    // -------------------------------------------------------------------------

    public override string VisitColumnReference(ColumnReferenceExpression node)
    {
        if (node.TableAlias != null) _sb.Append(node.TableAlias.ToUpperInvariant()).Append('.');
        _sb.Append(node.ColumnName.ToUpperInvariant());
        return string.Empty;
    }

    public override string VisitObjectName(ObjectName node)
    {
        if (node.Server   != null) _sb.Append(node.Server.ToUpperInvariant()).Append('.');
        if (node.Database != null) _sb.Append(node.Database.ToUpperInvariant()).Append('.');
        if (node.Schema   != null) _sb.Append(node.Schema.ToUpperInvariant()).Append('.');
        _sb.Append(node.Name.ToUpperInvariant());
        return string.Empty;
    }

    public override string VisitParameter(ParameterExpression node)
    {
        _sb.Append(node.Name.ToUpperInvariant());
        return string.Empty;
    }

    public override string VisitGlobalVariable(GlobalVariableExpression node)
    {
        _sb.Append(node.Name.ToUpperInvariant());
        return string.Empty;
    }

    // -------------------------------------------------------------------------
    // Structural nodes — delegate to SqlPrinter approach but upper-case keywords
    // -------------------------------------------------------------------------

    public override string VisitSelect(SelectStatement node)
    {
        _sb.Append("SELECT");
        if (node.Distinct) _sb.Append(" DISTINCT");
        if (node.Top != null) { _sb.Append(' '); node.Top.Accept(this); }
        _sb.Append(' ');
        for (int i = 0; i < node.SelectList.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            node.SelectList[i].Accept(this);
        }
        if (node.From.Count > 0)
        {
            _sb.Append(" FROM ");
            for (int i = 0; i < node.From.Count; i++)
            {
                if (i > 0) _sb.Append(", ");
                node.From[i].Accept(this);
            }
        }
        if (node.Where != null) { _sb.Append(" WHERE "); node.Where.Accept(this); }
        if (node.GroupBy.Count > 0)
        {
            _sb.Append(" GROUP BY ");
            for (int i = 0; i < node.GroupBy.Count; i++) { if (i > 0) _sb.Append(", "); node.GroupBy[i].Accept(this); }
        }
        if (node.Having != null) { _sb.Append(" HAVING "); node.Having.Accept(this); }
        if (node.OrderBy.Count > 0)
        {
            _sb.Append(" ORDER BY ");
            for (int i = 0; i < node.OrderBy.Count; i++) { if (i > 0) _sb.Append(", "); node.OrderBy[i].Accept(this); }
        }
        if (node.SetOperator != null) node.SetOperator.Accept(this);
        return string.Empty;
    }

    public override string VisitSelectItem(SelectItem node)
    {
        if (node.IsStar)
        {
            if (node.StarQualifier != null) { node.StarQualifier.Accept(this); _sb.Append('.'); }
            _sb.Append('*');
        }
        else
        {
            node.Expression!.Accept(this);
        }
        if (node.Alias != null) _sb.Append(" AS ").Append(node.Alias.ToUpperInvariant());
        return string.Empty;
    }

    public override string VisitTop(TopClause node)
    {
        _sb.Append("TOP ");
        node.Count.Accept(this);
        if (node.Percent) _sb.Append(" PERCENT");
        return string.Empty;
    }

    public override string VisitTableReference(TableReferenceSource node)
    {
        node.Name.Accept(this);
        if (node.Alias != null) _sb.Append(' ').Append(node.Alias.ToUpperInvariant());
        // Table hints are intentionally omitted from cache key — NOLOCK vs no-hint
        // should not produce two separate cache entries for the same logical query
        return string.Empty;
    }

    public override string VisitSubquerySource(SubquerySource node)
    {
        _sb.Append('('); node.Query.Accept(this); _sb.Append(')');
        _sb.Append(" AS ").Append(node.Alias.ToUpperInvariant());
        return string.Empty;
    }

    public override string VisitJoin(JoinedSource node)
    {
        node.Left.Accept(this);
        _sb.Append(' ').Append(node.JoinType switch
        {
            JoinType.Inner      => "INNER JOIN",
            JoinType.LeftOuter  => "LEFT OUTER JOIN",
            JoinType.RightOuter => "RIGHT OUTER JOIN",
            JoinType.FullOuter  => "FULL OUTER JOIN",
            JoinType.Cross      => "CROSS JOIN",
            _                   => "JOIN"
        }).Append(' ');
        node.Right.Accept(this);
        if (node.Condition != null) { _sb.Append(" ON "); node.Condition.Accept(this); }
        return string.Empty;
    }

    public override string VisitBinary(BinaryExpression node)
    {
        node.Left.Accept(this);
        _sb.Append(' ').Append(BinaryOpText(node.Operator)).Append(' ');
        node.Right.Accept(this);
        return string.Empty;
    }

    public override string VisitUnary(UnaryExpression node)
    {
        _sb.Append(node.Operator switch
        {
            UnaryOperator.Negate    => "-",
            UnaryOperator.Not       => "NOT ",
            UnaryOperator.BitwiseNot => "~",
            _                        => ""
        });
        node.Operand.Accept(this);
        return string.Empty;
    }

    public override string VisitBetween(BetweenExpression node)
    {
        node.Value.Accept(this);
        _sb.Append(node.IsNot ? " NOT BETWEEN " : " BETWEEN ");
        node.Low.Accept(this);
        _sb.Append(" AND ");
        node.High.Accept(this);
        return string.Empty;
    }

    public override string VisitInList(InListExpression node)
    {
        node.Value.Accept(this);
        _sb.Append(node.IsNot ? " NOT IN (?)" : " IN (?)");
        // All items collapsed to a single placeholder for cache key purposes
        return string.Empty;
    }

    public override string VisitInSubquery(InSubqueryExpression node)
    {
        node.Value.Accept(this);
        _sb.Append(node.IsNot ? " NOT IN (" : " IN (");
        node.Subquery.Accept(this);
        _sb.Append(')');
        return string.Empty;
    }

    public override string VisitLike(LikeExpression node)
    {
        node.Value.Accept(this);
        _sb.Append(node.IsNot ? " NOT LIKE " : " LIKE ");
        node.Pattern.Accept(this);
        return string.Empty;
    }

    public override string VisitIsNull(IsNullExpression node)
    {
        node.Value.Accept(this);
        _sb.Append(node.IsNot ? " IS NOT NULL" : " IS NULL");
        return string.Empty;
    }

    public override string VisitExists(ExistsExpression node)
    {
        _sb.Append(node.IsNot ? "NOT EXISTS (" : "EXISTS (");
        node.Subquery.Accept(this);
        _sb.Append(')');
        return string.Empty;
    }

    public override string VisitFunctionCall(FunctionCallExpression node)
    {
        node.Name.Accept(this);
        _sb.Append('(');
        if (node.Distinct) _sb.Append("DISTINCT ");
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            node.Arguments[i].Accept(this);
        }
        _sb.Append(')');
        if (node.Over != null) { _sb.Append(" OVER ("); node.Over.Accept(this); _sb.Append(')'); }
        return string.Empty;
    }

    public override string VisitCast(CastExpression node)
    {
        _sb.Append("CAST("); node.Value.Accept(this); _sb.Append(" AS "); node.TargetType.Accept(this); _sb.Append(')');
        return string.Empty;
    }

    public override string VisitConvert(ConvertExpression node)
    {
        _sb.Append(node.IsTryCast ? "TRY_CONVERT(" : "CONVERT(");
        node.TargetType.Accept(this); _sb.Append(", "); node.Value.Accept(this);
        if (node.Style != null) { _sb.Append(", "); node.Style.Accept(this); }
        _sb.Append(')');
        return string.Empty;
    }

    public override string VisitCase(CaseExpression node)
    {
        _sb.Append("CASE");
        if (node.InputExpression != null) { _sb.Append(' '); node.InputExpression.Accept(this); }
        foreach (var w in node.WhenClauses)
        {
            _sb.Append(" WHEN "); w.Condition.Accept(this);
            _sb.Append(" THEN ");  w.Result.Accept(this);
        }
        if (node.ElseExpression != null) { _sb.Append(" ELSE "); node.ElseExpression.Accept(this); }
        _sb.Append(" END");
        return string.Empty;
    }

    public override string VisitDataType(DataTypeNode node)
    {
        _sb.Append(node.TypeName.ToUpperInvariant());
        if (node.IsMax) { _sb.Append("(MAX)"); }
        else if (node.Precision != null) { _sb.Append('(').Append(node.Precision).Append(node.Scale != null ? $",{node.Scale}" : "").Append(')'); }
        else if (node.Length != null) { _sb.Append('(').Append(node.Length).Append(')'); }
        return string.Empty;
    }

    public override string VisitOrderByItem(OrderByItem node)
    {
        node.Expression.Accept(this);
        if (node.Direction == SortDirection.Descending) _sb.Append(" DESC");
        return string.Empty;
    }

    public override string VisitSetOperator(SetOperator node)
    {
        _sb.Append(' ').Append(node.Kind switch
        {
            SetOperatorKind.Union     => "UNION",
            SetOperatorKind.Intersect => "INTERSECT",
            SetOperatorKind.Except    => "EXCEPT",
            _                         => "UNION"
        });
        if (node.All) _sb.Append(" ALL");
        _sb.Append(' '); node.Right.Accept(this);
        return string.Empty;
    }

    public override string VisitInsert(InsertStatement node)
    {
        _sb.Append("INSERT INTO "); node.Target.Accept(this);
        if (node.Columns.Count > 0) _sb.Append(" (").Append(string.Join(", ", node.Columns.Select(c => c.ToUpperInvariant()))).Append(')');
        if (node.ValuesSource != null) { _sb.Append(" VALUES"); _sb.Append("(?)"); }
        else node.SelectSource?.Accept(this);
        return string.Empty;
    }

    public override string VisitUpdate(UpdateStatement node)
    {
        _sb.Append("UPDATE "); node.Target.Accept(this);
        _sb.Append(" SET ");
        for (int i = 0; i < node.Sets.Count; i++)
        {
            if (i > 0) _sb.Append(", ");
            _sb.Append(node.Sets[i].ColumnName.ToUpperInvariant()).Append(" = ");
            node.Sets[i].Value.Accept(this);
        }
        if (node.Where != null) { _sb.Append(" WHERE "); node.Where.Accept(this); }
        return string.Empty;
    }

    public override string VisitDelete(DeleteStatement node)
    {
        _sb.Append("DELETE FROM "); node.Target.Accept(this);
        if (node.Where != null) { _sb.Append(" WHERE "); node.Where.Accept(this); }
        return string.Empty;
    }

    public override string VisitBatch(SqlBatch node)
    {
        for (int i = 0; i < node.Statements.Count; i++)
        {
            if (i > 0) _sb.Append("; ");
            node.Statements[i].Accept(this);
        }
        return string.Empty;
    }

    private static string BinaryOpText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add                => "+",
        BinaryOperator.Subtract           => "-",
        BinaryOperator.Multiply           => "*",
        BinaryOperator.Divide             => "/",
        BinaryOperator.Modulo             => "%",
        BinaryOperator.Equal              => "=",
        BinaryOperator.NotEqual           => "<>",
        BinaryOperator.LessThan           => "<",
        BinaryOperator.GreaterThan        => ">",
        BinaryOperator.LessThanOrEqual    => "<=",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.And                => "AND",
        BinaryOperator.Or                 => "OR",
        BinaryOperator.BitwiseAnd         => "&",
        BinaryOperator.BitwiseOr          => "|",
        BinaryOperator.BitwiseXor         => "^",
        BinaryOperator.StringConcat       => "+",
        _                                 => "?"
    };

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool lastWasSpace = false;
        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }
        return sb.ToString().Trim();
    }
}
