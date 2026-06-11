using System.Text;

namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Walks a T-SQL AST and regenerates canonical SQL text.
///
/// This is NOT a translator — it emits T-SQL syntax, not PostgreSQL.
/// Its purpose is:
///   1. Debugging: inspect what the parser produced.
///   2. Round-trip testing: parse → print → compare to normalised input.
///   3. UnparsedStatement passthrough: re-emit raw SQL for unsupported nodes.
///   4. Cache key normalisation: the printer output is used as the translation
///      cache key after stripping literal values (see <see cref="NormalisedKeyPrinter"/>).
///
/// Output style:
///   - Keywords in UPPER CASE.
///   - Identifiers unquoted (no brackets or double-quotes).
///   - Consistent single-space separation.
///   - No trailing semicolons.
/// </summary>
public sealed class SqlPrinter : ISqlVisitor<string>
{
    private readonly StringBuilder _sb;
    private int _indent;
    private const string IndentUnit = "    ";

    private SqlPrinter()
    {
        _sb = new StringBuilder(256);
    }

    /// <summary>Converts any <see cref="SqlNode"/> to its canonical SQL text.</summary>
    public static string Print(SqlNode node)
    {
        var printer = new SqlPrinter();
        node.Accept(printer);
        return printer._sb.ToString().Trim();
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private SqlPrinter Append(string s)      { _sb.Append(s); return this; }
    private SqlPrinter Append(char c)        { _sb.Append(c); return this; }
    private SqlPrinter Space()               { _sb.Append(' '); return this; }
    private SqlPrinter Newline()             { _sb.AppendLine(); AppendIndent(); return this; }
    private void AppendIndent()              => _sb.Append(string.Concat(Enumerable.Repeat(IndentUnit, _indent)));

    private string V(SqlNode node)           => node.Accept(this);

    private void CommaSeparated<T>(IReadOnlyList<T> items) where T : SqlNode
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) Append(", ");
            items[i].Accept(this);
        }
    }

    private string QuoteIdentifier(string name)
    {
        // Re-quote only names that require it (contain spaces, are keywords, etc.)
        // For the printer's purpose (debugging/cache keys), unquoted is fine.
        return name;
    }

    private string PrintObjectName(ObjectName name)
    {
        var sb = new StringBuilder();
        if (name.Server   != null) sb.Append(name.Server).Append('.');
        if (name.Database != null) sb.Append(name.Database).Append('.');
        if (name.Schema   != null) sb.Append(name.Schema).Append('.');
        sb.Append(name.Name);
        return sb.ToString();
    }

    // =========================================================================
    // Batch
    // =========================================================================

    public string VisitBatch(SqlBatch node)
    {
        for (int i = 0; i < node.Statements.Count; i++)
        {
            if (i > 0) { _sb.AppendLine(); _sb.AppendLine(); }
            node.Statements[i].Accept(this);
            _sb.Append(';');
        }
        return string.Empty;
    }

    // =========================================================================
    // SELECT
    // =========================================================================

    public string VisitSelect(SelectStatement node)
    {
        Append("SELECT");
        if (node.Distinct) Append(" DISTINCT");

        if (node.Top != null) { Space(); node.Top.Accept(this); }

        Newline(); _indent++;
        CommaSeparated(node.SelectList);
        _indent--;

        if (node.Into != null) { Newline(); node.Into.Accept(this); }

        if (node.From.Count > 0)
        {
            Newline(); Append("FROM ");
            for (int i = 0; i < node.From.Count; i++)
            {
                if (i > 0) Append(", ");
                node.From[i].Accept(this);
            }
        }

        if (node.Where != null)
        {
            Newline(); Append("WHERE "); node.Where.Accept(this);
        }

        if (node.GroupBy.Count > 0)
        {
            Newline(); Append("GROUP BY ");
            for (int i = 0; i < node.GroupBy.Count; i++)
            {
                if (i > 0) Append(", ");
                node.GroupBy[i].Accept(this);
            }
        }

        if (node.Having != null)
        {
            Newline(); Append("HAVING "); node.Having.Accept(this);
        }

        if (node.OrderBy.Count > 0)
        {
            Newline(); Append("ORDER BY ");
            CommaSeparated(node.OrderBy);
        }

        if (node.OffsetFetch != null)
        {
            Newline(); node.OffsetFetch.Accept(this);
        }

        if (node.SetOperator != null)
        {
            Newline(); node.SetOperator.Accept(this);
        }

        return string.Empty;
    }

    public string VisitTop(TopClause node)
    {
        Append("TOP ");
        if (node.Count is IntegerLiteralExpression || node.Count is ParameterExpression)
            node.Count.Accept(this);
        else { Append('('); node.Count.Accept(this); Append(')'); }
        if (node.Percent)  Append(" PERCENT");
        if (node.WithTies) Append(" WITH TIES");
        return string.Empty;
    }

    public string VisitSelectItem(SelectItem node)
    {
        if (node.IsStar)
        {
            if (node.StarQualifier != null)
                Append(PrintObjectName(node.StarQualifier)).Append('.');
            Append('*');
        }
        else
        {
            node.Expression!.Accept(this);
        }
        if (node.Alias != null) Append(" AS ").Append(node.Alias);
        return string.Empty;
    }

    public string VisitIntoClause(IntoClause node)
    {
        Append("INTO ").Append(PrintObjectName(node.Table));
        return string.Empty;
    }

    public string VisitOrderByItem(OrderByItem node)
    {
        node.Expression.Accept(this);
        if (node.Direction == SortDirection.Descending) Append(" DESC");
        return string.Empty;
    }

    public string VisitOffsetFetch(OffsetFetchClause node)
    {
        Append("OFFSET "); node.Offset.Accept(this); Append(" ROWS");
        if (node.Fetch != null)
        {
            Append(" FETCH NEXT "); node.Fetch.Accept(this); Append(" ROWS ONLY");
        }
        return string.Empty;
    }

    public string VisitSetOperator(SetOperator node)
    {
        Append(node.Kind switch
        {
            SetOperatorKind.Union     => "UNION",
            SetOperatorKind.Intersect => "INTERSECT",
            SetOperatorKind.Except    => "EXCEPT",
            _                         => "UNION"
        });
        if (node.All) Append(" ALL");
        Newline(); node.Right.Accept(this);
        return string.Empty;
    }

    // =========================================================================
    // INSERT
    // =========================================================================

    public string VisitInsert(InsertStatement node)
    {
        Append("INSERT INTO ").Append(PrintObjectName(node.Target));
        if (node.Columns.Count > 0)
        {
            Append(" (");
            Append(string.Join(", ", node.Columns));
            Append(')');
        }
        if (node.ValuesSource != null) { Space(); node.ValuesSource.Accept(this); }
        else if (node.SelectSource != null) { Newline(); node.SelectSource.Accept(this); }
        return string.Empty;
    }

    public string VisitValuesClause(ValuesClause node)
    {
        Append("VALUES");
        for (int r = 0; r < node.RowValues.Count; r++)
        {
            if (r > 0) Append(',');
            Append(" (");
            var row = node.RowValues[r];
            for (int c = 0; c < row.Count; c++)
            {
                if (c > 0) Append(", ");
                row[c].Accept(this);
            }
            Append(')');
        }
        return string.Empty;
    }

    // =========================================================================
    // UPDATE
    // =========================================================================

    public string VisitUpdate(UpdateStatement node)
    {
        Append("UPDATE ").Append(PrintObjectName(node.Target));
        if (node.TargetAlias != null) Space().Append(node.TargetAlias);
        Newline(); Append("SET ");
        CommaSeparated(node.Sets);
        if (node.From.Count > 0)
        {
            Newline(); Append("FROM ");
            for (int i = 0; i < node.From.Count; i++)
            {
                if (i > 0) Append(", ");
                node.From[i].Accept(this);
            }
        }
        if (node.Where != null) { Newline(); Append("WHERE "); node.Where.Accept(this); }
        return string.Empty;
    }

    public string VisitSetClause(SetClause node)
    {
        Append(node.ColumnName).Append(" = "); node.Value.Accept(this);
        return string.Empty;
    }

    // =========================================================================
    // DELETE
    // =========================================================================

    public string VisitDelete(DeleteStatement node)
    {
        Append("DELETE ");
        if (node.TargetAlias != null) Append(node.TargetAlias).Space();
        Append("FROM ").Append(PrintObjectName(node.Target));
        if (node.From.Count > 0)
        {
            Newline(); Append("FROM ");
            for (int i = 0; i < node.From.Count; i++)
            {
                if (i > 0) Append(", ");
                node.From[i].Accept(this);
            }
        }
        if (node.Where != null) { Newline(); Append("WHERE "); node.Where.Accept(this); }
        return string.Empty;
    }

    public string VisitOutputClause(OutputClause node)
    {
        Append("OUTPUT ");
        CommaSeparated(node.Items);
        if (node.IntoTable != null) Append(" INTO ").Append(PrintObjectName(node.IntoTable));
        return string.Empty;
    }

    // =========================================================================
    // DDL
    // =========================================================================

    public string VisitTruncateTable(TruncateTableStatement node)
    {
        Append("TRUNCATE TABLE ").Append(PrintObjectName(node.Table));
        return string.Empty;
    }

    public string VisitCreateTable(CreateTableStatement node)
    {
        Append("CREATE TABLE ").Append(PrintObjectName(node.Table));
        Append(" (");
        _indent++;
        for (int i = 0; i < node.Columns.Count; i++)
        {
            Newline();
            node.Columns[i].Accept(this);
            if (i < node.Columns.Count - 1) Append(',');
        }
        _indent--;
        Newline(); Append(')');
        return string.Empty;
    }

    public string VisitColumnDefinition(ColumnDefinition node)
    {
        Append(node.Name).Space(); node.DataType.Accept(this);
        if (node.IsIdentity)
        {
            Append(" IDENTITY(").Append(node.IdentitySeed.ToString())
                .Append(',').Append(node.IdentityIncrement.ToString()).Append(')');
        }
        Append(node.IsNullable ? " NULL" : " NOT NULL");
        if (node.IsPrimaryKey) Append(" PRIMARY KEY");
        if (node.IsUnique)     Append(" UNIQUE");
        if (node.DefaultValue != null) { Append(" DEFAULT "); node.DefaultValue.Accept(this); }
        return string.Empty;
    }

    public string VisitDropTable(DropTableStatement node)
    {
        Append("DROP TABLE");
        if (node.IfExists) Append(" IF EXISTS");
        Append(' ');
        for (int i = 0; i < node.Tables.Count; i++)
        {
            if (i > 0) Append(", ");
            Append(PrintObjectName(node.Tables[i]));
        }
        return string.Empty;
    }

    // =========================================================================
    // Transactions
    // =========================================================================

    public string VisitBeginTransaction(BeginTransactionStatement node)
    {
        Append("BEGIN TRANSACTION");
        if (node.TransactionName != null) Space().Append(node.TransactionName);
        return string.Empty;
    }

    public string VisitCommitTransaction(CommitTransactionStatement node)
    {
        Append("COMMIT");
        if (node.TransactionName != null) Space().Append(node.TransactionName);
        return string.Empty;
    }

    public string VisitRollbackTransaction(RollbackTransactionStatement node)
    {
        Append("ROLLBACK");
        if (node.TransactionName != null) Space().Append(node.TransactionName);
        return string.Empty;
    }

    public string VisitSaveTransaction(SaveTransactionStatement node)
    {
        Append("SAVE TRANSACTION ").Append(node.SavepointName);
        return string.Empty;
    }

    // =========================================================================
    // SET / USE / EXEC
    // =========================================================================

    public string VisitSetOption(SetOptionStatement node)
    {
        Append("SET ").Append(node.OptionName);
        if (node.Value != null) { Space(); node.Value.Accept(this); }
        else Append(node.IsOn ? " ON" : " OFF");
        return string.Empty;
    }

    public string VisitUseDatabase(UseDatabaseStatement node)
    {
        Append("USE ").Append(node.DatabaseName);
        return string.Empty;
    }

    public string VisitExecute(ExecuteStatement node)
    {
        Append("EXEC ").Append(PrintObjectName(node.ProcedureName));
        if (node.Arguments.Count > 0)
        {
            Space();
            CommaSeparated(node.Arguments);
        }
        return string.Empty;
    }

    public string VisitProcedureArgument(ProcedureArgument node)
    {
        if (node.ParameterName != null) Append(node.ParameterName).Append(" = ");
        node.Value.Accept(this);
        if (node.IsOutput) Append(" OUTPUT");
        return string.Empty;
    }

    // =========================================================================
    // Control flow
    // =========================================================================

    public string VisitIf(IfStatement node)
    {
        Append("IF "); node.Condition.Accept(this);
        Newline(); _indent++; node.ThenBranch.Accept(this); _indent--;
        if (node.ElseBranch != null)
        {
            Newline(); Append("ELSE");
            Newline(); _indent++; node.ElseBranch.Accept(this); _indent--;
        }
        return string.Empty;
    }

    public string VisitWhile(WhileStatement node)
    {
        Append("WHILE "); node.Condition.Accept(this);
        Newline(); _indent++; node.Body.Accept(this); _indent--;
        return string.Empty;
    }

    public string VisitBeginEnd(BeginEndBlock node)
    {
        Append("BEGIN");
        _indent++;
        foreach (var s in node.Statements)
        {
            Newline(); s.Accept(this); Append(';');
        }
        _indent--;
        Newline(); Append("END");
        return string.Empty;
    }

    public string VisitPrint(PrintStatement node)
    {
        Append("PRINT "); node.Expression.Accept(this);
        return string.Empty;
    }

    public string VisitReturn(ReturnStatement node)
    {
        Append("RETURN");
        if (node.Value != null) { Space(); node.Value.Accept(this); }
        return string.Empty;
    }

    public string VisitDeclare(DeclareStatement node)
    {
        Append("DECLARE ");
        CommaSeparated(node.Declarations);
        return string.Empty;
    }

    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        Append(node.Name).Space(); node.DataType.Accept(this);
        if (node.InitialValue != null) { Append(" = "); node.InitialValue.Accept(this); }
        return string.Empty;
    }

    public string VisitUnparsed(UnparsedStatement node)
    {
        // Passthrough: emit raw SQL unchanged
        Append(node.RawSql);
        return string.Empty;
    }

    // =========================================================================
    // Expressions — literals
    // =========================================================================

    public string VisitIntegerLiteral(IntegerLiteralExpression node)
    {
        Append(node.Value.ToString());
        return string.Empty;
    }

    public string VisitDecimalLiteral(DecimalLiteralExpression node)
    {
        Append(string.IsNullOrEmpty(node.RawText) ? node.Value.ToString() : node.RawText);
        return string.Empty;
    }

    public string VisitFloatLiteral(FloatLiteralExpression node)
    {
        Append(string.IsNullOrEmpty(node.RawText) ? node.Value.ToString() : node.RawText);
        return string.Empty;
    }

    public string VisitStringLiteral(StringLiteralExpression node)
    {
        if (node.IsUnicode) Append('N');
        Append('\'').Append(node.Value.Replace("'", "''")).Append('\'');
        return string.Empty;
    }

    public string VisitNullLiteral(NullLiteralExpression node)
    {
        Append("NULL");
        return string.Empty;
    }

    public string VisitBooleanLiteral(BooleanLiteralExpression node)
    {
        Append(node.Value ? "1" : "0");
        return string.Empty;
    }

    // =========================================================================
    // Expressions — names and references
    // =========================================================================

    public string VisitObjectName(ObjectName node)
    {
        Append(PrintObjectName(node));
        return string.Empty;
    }

    public string VisitColumnReference(ColumnReferenceExpression node)
    {
        if (node.TableAlias != null) Append(node.TableAlias).Append('.');
        Append(node.ColumnName);
        return string.Empty;
    }

    public string VisitParameter(ParameterExpression node)
    {
        Append(node.Name);
        return string.Empty;
    }

    public string VisitGlobalVariable(GlobalVariableExpression node)
    {
        Append(node.Name);
        return string.Empty;
    }

    // =========================================================================
    // Expressions — operators
    // =========================================================================

    private static string OperatorText(BinaryOperator op) => op switch
    {
        BinaryOperator.Add                => "+",
        BinaryOperator.Subtract           => "-",
        BinaryOperator.Multiply           => "*",
        BinaryOperator.Divide             => "/",
        BinaryOperator.Modulo             => "%",
        BinaryOperator.BitwiseAnd         => "&",
        BinaryOperator.BitwiseOr          => "|",
        BinaryOperator.BitwiseXor         => "^",
        BinaryOperator.Equal              => "=",
        BinaryOperator.NotEqual           => "<>",
        BinaryOperator.LessThan           => "<",
        BinaryOperator.GreaterThan        => ">",
        BinaryOperator.LessThanOrEqual    => "<=",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.And                => "AND",
        BinaryOperator.Or                 => "OR",
        BinaryOperator.StringConcat       => "+",
        _                                 => "?"
    };

    public string VisitBinary(BinaryExpression node)
    {
        // Parenthesise compound expressions to preserve precedence in round-trips
        bool needsParens = node.Operator is BinaryOperator.And or BinaryOperator.Or;
        if (needsParens) Append('(');
        node.Left.Accept(this);
        Append(' ').Append(OperatorText(node.Operator)).Append(' ');
        node.Right.Accept(this);
        if (needsParens) Append(')');
        return string.Empty;
    }

    public string VisitUnary(UnaryExpression node)
    {
        switch (node.Operator)
        {
            case UnaryOperator.Negate:     Append('-'); break;
            case UnaryOperator.Not:        Append("NOT "); break;
            case UnaryOperator.BitwiseNot: Append('~'); break;
        }
        node.Operand.Accept(this);
        return string.Empty;
    }

    // =========================================================================
    // Expressions — predicates
    // =========================================================================

    public string VisitBetween(BetweenExpression node)
    {
        node.Value.Accept(this);
        Append(node.IsNot ? " NOT BETWEEN " : " BETWEEN ");
        node.Low.Accept(this);
        Append(" AND ");
        node.High.Accept(this);
        return string.Empty;
    }

    public string VisitInList(InListExpression node)
    {
        node.Value.Accept(this);
        Append(node.IsNot ? " NOT IN (" : " IN (");
        for (int i = 0; i < node.Items.Count; i++)
        {
            if (i > 0) Append(", ");
            node.Items[i].Accept(this);
        }
        Append(')');
        return string.Empty;
    }

    public string VisitInSubquery(InSubqueryExpression node)
    {
        node.Value.Accept(this);
        Append(node.IsNot ? " NOT IN (" : " IN (");
        _indent++; node.Subquery.Accept(this); _indent--;
        Append(')');
        return string.Empty;
    }

    public string VisitLike(LikeExpression node)
    {
        node.Value.Accept(this);
        Append(node.IsNot ? " NOT LIKE " : " LIKE ");
        node.Pattern.Accept(this);
        if (node.Escape != null) { Append(" ESCAPE "); node.Escape.Accept(this); }
        return string.Empty;
    }

    public string VisitIsNull(IsNullExpression node)
    {
        node.Value.Accept(this);
        Append(node.IsNot ? " IS NOT NULL" : " IS NULL");
        return string.Empty;
    }

    public string VisitExists(ExistsExpression node)
    {
        Append(node.IsNot ? "NOT EXISTS (" : "EXISTS (");
        _indent++; node.Subquery.Accept(this); _indent--;
        Append(')');
        return string.Empty;
    }

    // =========================================================================
    // Expressions — functions
    // =========================================================================

    public string VisitFunctionCall(FunctionCallExpression node)
    {
        Append(PrintObjectName(node.Name)).Append('(');
        if (node.Distinct) Append("DISTINCT ");
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            if (i > 0) Append(", ");
            node.Arguments[i].Accept(this);
        }
        Append(')');
        if (node.Over != null) { Space(); node.Over.Accept(this); }
        return string.Empty;
    }

    public string VisitOverClause(OverClause node)
    {
        Append("OVER (");
        if (node.PartitionBy.Count > 0)
        {
            Append("PARTITION BY ");
            for (int i = 0; i < node.PartitionBy.Count; i++)
            {
                if (i > 0) Append(", ");
                node.PartitionBy[i].Accept(this);
            }
        }
        if (node.OrderBy.Count > 0)
        {
            if (node.PartitionBy.Count > 0) Space();
            Append("ORDER BY ");
            CommaSeparated(node.OrderBy);
        }
        if (node.Frame != null) { Space(); node.Frame.Accept(this); }
        Append(')');
        return string.Empty;
    }

    public string VisitWindowFrame(WindowFrame node)
    {
        Append(node.Unit == WindowFrameUnit.Rows ? "ROWS" : "RANGE");
        Append(" BETWEEN ");
        node.Start.Accept(this);
        if (node.End != null) { Append(" AND "); node.End.Accept(this); }
        return string.Empty;
    }

    public string VisitWindowFrameBound(WindowFrameBound node)
    {
        switch (node.Kind)
        {
            case WindowFrameBoundKind.UnboundedPreceding: Append("UNBOUNDED PRECEDING"); break;
            case WindowFrameBoundKind.Preceding:           node.Offset?.Accept(this); Append(" PRECEDING"); break;
            case WindowFrameBoundKind.CurrentRow:          Append("CURRENT ROW"); break;
            case WindowFrameBoundKind.Following:           node.Offset?.Accept(this); Append(" FOLLOWING"); break;
            case WindowFrameBoundKind.UnboundedFollowing:  Append("UNBOUNDED FOLLOWING"); break;
        }
        return string.Empty;
    }

    public string VisitCast(CastExpression node)
    {
        Append("CAST("); node.Value.Accept(this); Append(" AS "); node.TargetType.Accept(this); Append(')');
        return string.Empty;
    }

    public string VisitConvert(ConvertExpression node)
    {
        Append(node.IsTryCast ? "TRY_CONVERT(" : "CONVERT(");
        node.TargetType.Accept(this);
        Append(", ");
        node.Value.Accept(this);
        if (node.Style != null) { Append(", "); node.Style.Accept(this); }
        Append(')');
        return string.Empty;
    }

    public string VisitCase(CaseExpression node)
    {
        Append("CASE");
        if (node.InputExpression != null) { Space(); node.InputExpression.Accept(this); }
        foreach (var w in node.WhenClauses)
        {
            Append(" WHEN "); w.Condition.Accept(this);
            Append(" THEN ");  w.Result.Accept(this);
        }
        if (node.ElseExpression != null) { Append(" ELSE "); node.ElseExpression.Accept(this); }
        Append(" END");
        return string.Empty;
    }

    public string VisitWhenClause(WhenClause node)
    {
        // Handled inline in VisitCase — this overload is a no-op
        return string.Empty;
    }

    public string VisitSubquery(SubqueryExpression node)
    {
        Append('('); _indent++; node.Query.Accept(this); _indent--; Append(')');
        return string.Empty;
    }

    public string VisitDataType(DataTypeNode node)
    {
        Append(node.TypeName);
        if (node.IsMax) { Append("(MAX)"); }
        else if (node.Precision != null)
        {
            Append('(').Append(node.Precision.Value.ToString());
            if (node.Scale != null) Append(',').Append(node.Scale.Value.ToString());
            Append(')');
        }
        else if (node.Length != null) { Append('(').Append(node.Length.Value.ToString()).Append(')'); }
        return string.Empty;
    }

    // =========================================================================
    // Table sources
    // =========================================================================

    public string VisitTableReference(TableReferenceSource node)
    {
        Append(PrintObjectName(node.Name));
        if (node.Alias != null) Append(" AS ").Append(node.Alias);
        if (node.Hints.Count > 0)
        {
            Append(" WITH (");
            for (int i = 0; i < node.Hints.Count; i++)
            {
                if (i > 0) Append(", ");
                Append(node.Hints[i].HintName);
            }
            Append(')');
        }
        return string.Empty;
    }

    public string VisitSubquerySource(SubquerySource node)
    {
        Append('('); _indent++; Newline(); node.Query.Accept(this); _indent--; Newline(); Append(')');
        Append(" AS ").Append(node.Alias);
        return string.Empty;
    }

    public string VisitJoin(JoinedSource node)
    {
        node.Left.Accept(this);
        Newline();
        Append(node.JoinType switch
        {
            JoinType.Inner      => "INNER JOIN",
            JoinType.LeftOuter  => "LEFT OUTER JOIN",
            JoinType.RightOuter => "RIGHT OUTER JOIN",
            JoinType.FullOuter  => "FULL OUTER JOIN",
            JoinType.Cross      => "CROSS JOIN",
            JoinType.CrossApply => "CROSS APPLY",
            JoinType.OuterApply => "OUTER APPLY",
            _                   => "JOIN"
        });
        Space(); node.Right.Accept(this);
        if (node.Condition != null) { Append(" ON "); node.Condition.Accept(this); }
        return string.Empty;
    }

    public string VisitTableHint(TableHint node)
    {
        Append(node.HintName);
        return string.Empty;
    }
}
