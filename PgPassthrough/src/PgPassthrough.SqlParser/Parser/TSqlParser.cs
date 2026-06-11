using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Lexer;

namespace PgPassthrough.SqlParser.Parser;

/// <summary>
/// Recursive-descent T-SQL parser.
///
/// Input:  Token list from <see cref="TSqlLexer"/>.
/// Output: <see cref="SqlBatch"/> containing one or more <see cref="SqlStatement"/> nodes.
///
/// Design principles:
/// - Fail fast with a <see cref="ParseException"/> on hard syntax errors.
/// - For unsupported or unrecognised constructs, produce an
///   <see cref="UnparsedStatement"/> and skip to the next statement boundary.
/// - Never silently drop tokens that might matter for correctness.
/// - Operator precedence via a Pratt-style recursive expression parser.
/// </summary>
public sealed class TSqlParser
{
    private readonly ParseContext _ctx;

    private TSqlParser(ParseContext ctx) => _ctx = ctx;

    /// <summary>
    /// Parse a complete T-SQL batch and return the AST root.
    /// Errors in individual statements produce <see cref="UnparsedStatement"/> nodes;
    /// the batch itself never throws.
    /// </summary>
    public static SqlBatch Parse(string sql)
    {
        var tokens = TSqlLexer.Tokenize(sql);
        var ctx    = new ParseContext(tokens);
        var parser = new TSqlParser(ctx);
        return parser.ParseBatch();
    }

    // =========================================================================
    // Batch
    // =========================================================================

    private SqlBatch ParseBatch()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var statements = new List<SqlStatement>();

        while (!_ctx.IsEof)
        {
            // Skip semicolons between statements
            while (_ctx.TryConsume(TokenKind.Semicolon) != null) { }

            // Skip GO separator (treated as statement delimiter)
            if (_ctx.Is(TokenKind.Identifier) &&
                string.Equals(_ctx.Current.Value, "GO", StringComparison.OrdinalIgnoreCase))
            {
                _ctx.Consume();
                continue;
            }

            if (_ctx.IsEof) break;

            try
            {
                var stmt = ParseStatement();
                if (stmt != null)
                    statements.Add(stmt);
            }
            catch (ParseException ex)
            {
                // Recover by collecting raw tokens until the next statement boundary
                statements.Add(RecoverUnparsed(ex.Message));
            }
        }

        return new SqlBatch
        {
            Statements = statements,
            Line = line,
            Column = col
        };
    }

    // =========================================================================
    // Statement dispatch
    // =========================================================================

    private SqlStatement? ParseStatement()
    {
        SkipStatementTerminators();
        if (_ctx.IsEof) return null;

        var t = _ctx.Current;

        return t.Kind switch
        {
            TokenKind.KwSelect                          => ParseSelect(),
            TokenKind.KwInsert                          => ParseInsert(),
            TokenKind.KwUpdate                          => ParseUpdate(),
            TokenKind.KwDelete                          => ParseDelete(),
            TokenKind.KwTruncate                        => ParseTruncate(),
            TokenKind.KwCreate                          => ParseCreate(),
            TokenKind.KwDrop                            => ParseDrop(),
            TokenKind.KwBegin                           => ParseBegin(),
            TokenKind.KwCommit                          => ParseCommit(),
            TokenKind.KwRollback                        => ParseRollback(),
            TokenKind.KwSave                            => ParseSaveTransaction(),
            TokenKind.KwSet                             => ParseSet(),
            TokenKind.KwUse                             => ParseUse(),
            TokenKind.KwExec or TokenKind.KwExecute     => ParseExecute(),
            TokenKind.KwIf                              => ParseIf(),
            TokenKind.KwWhile                           => ParseWhile(),
            TokenKind.KwEnd                             => ParseEndBlock(),  // standalone END
            TokenKind.KwPrint                           => ParsePrint(),
            TokenKind.KwReturn                          => ParseReturn(),
            TokenKind.Identifier when IsKeyword("DECLARE") => ParseDeclare(),
            _                                           => ParseUnknownStatement()
        };
    }

    private void SkipStatementTerminators()
    {
        while (_ctx.Is(TokenKind.Semicolon) ||
               (_ctx.Is(TokenKind.Identifier) &&
                string.Equals(_ctx.Current.Value, "GO", StringComparison.OrdinalIgnoreCase)))
        {
            _ctx.Consume();
        }
    }

    // =========================================================================
    // SELECT
    // =========================================================================

    private SelectStatement ParseSelect()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Expect(TokenKind.KwSelect);

        bool distinct = false;
        if (_ctx.TryConsume(TokenKind.KwDistinct) != null) distinct = true;

        // TOP clause
        TopClause? top = null;
        if (_ctx.Is(TokenKind.KwTop)) top = ParseTop();

        // SELECT list
        var selectList = ParseSelectList();

        // INTO clause
        IntoClause? into = null;
        if (_ctx.Is(TokenKind.KwInto)) into = ParseIntoClause();

        // FROM clause
        var from = new List<TableSource>();
        if (_ctx.Is(TokenKind.KwFrom))
        {
            _ctx.Consume();
            from = ParseTableSources();
        }

        // WHERE
        SqlExpression? where = null;
        if (_ctx.Is(TokenKind.KwWhere))
        {
            _ctx.Consume();
            where = ParseExpression();
        }

        // GROUP BY
        var groupBy = new List<SqlExpression>();
        if (_ctx.Is(TokenKind.KwGroupBy))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.KwBy); // consume BY
            groupBy = ParseExpressionList();
        }
        // Handle GROUP already consumed (KwGroupBy is the GROUP token; BY is separate)
        // Actually KwGroupBy is the keyword "GROUP" — BY is KwBy
        // Re-check: in Keywords.cs GROUP => KwGroupBy, BY => KwBy
        // The groupBy parse above: _ctx.Expect(KwBy) is correct.

        // HAVING
        SqlExpression? having = null;
        if (_ctx.Is(TokenKind.KwHaving))
        {
            _ctx.Consume();
            having = ParseExpression();
        }

        // ORDER BY
        var orderBy = new List<OrderByItem>();
        OffsetFetchClause? offsetFetch = null;
        if (_ctx.Is(TokenKind.KwOrderBy))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.KwBy);
            orderBy = ParseOrderByList();
            offsetFetch = TryParseOffsetFetch();
        }

        // UNION / INTERSECT / EXCEPT
        SetOperator? setOp = null;
        if (_ctx.IsAny(TokenKind.KwUnion, TokenKind.KwMerge) ||
            (_ctx.Is(TokenKind.Identifier) &&
             (IsKeyword("INTERSECT") || IsKeyword("EXCEPT"))))
        {
            setOp = ParseSetOperator();
        }

        return new SelectStatement
        {
            Top         = top,
            Distinct    = distinct,
            SelectList  = selectList,
            Into        = into,
            From        = from,
            Where       = where,
            GroupBy     = groupBy,
            Having      = having,
            OrderBy     = orderBy,
            OffsetFetch = offsetFetch,
            SetOperator = setOp,
            Line        = line,
            Column      = col
        };
    }

    private TopClause ParseTop()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // TOP

        bool parens = _ctx.TryConsume(TokenKind.OpenParen) != null;
        // TOP count is always a simple literal or parameter — use ParseUnary, not ParseExpression,
        // to avoid consuming a bare * as a multiply operator when TOP is not parenthesised.
        var count = parens ? ParseExpression() : ParseUnary();
        if (parens) _ctx.Expect(TokenKind.CloseParen);

        bool percent  = _ctx.TryConsume(TokenKind.KwPercent) != null;
        bool withTies = false;
        if (_ctx.Is(TokenKind.KwWith))
        {
            int saved = _ctx.SavePosition();
            _ctx.Consume(); // WITH
            if (_ctx.Is(TokenKind.KwWithTies)) { _ctx.Consume(); withTies = true; }
            else _ctx.RestorePosition(saved);
        }

        return new TopClause { Count = count, Percent = percent, WithTies = withTies, Line = line, Column = col };
    }

    private List<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();
        items.Add(ParseSelectItem());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            items.Add(ParseSelectItem());
        }
        return items;
    }

    private SelectItem ParseSelectItem()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;

        // Bare *
        if (_ctx.Is(TokenKind.Star))
        {
            _ctx.Consume();
            return new SelectItem { IsStar = true, Line = line, Column = col };
        }

        // Qualified wildcard: table.* or schema.table.*
        if (IsQualifiedStar())
        {
            var qualifier = ParseObjectNameUpToStar();
            _ctx.Expect(TokenKind.Star);
            return new SelectItem { IsStar = true, StarQualifier = qualifier, Line = line, Column = col };
        }

        var expr = ParseExpression();

        // Optional alias: AS alias, or bare alias (not a reserved keyword)
        string? alias = null;
        if (_ctx.Is(TokenKind.KwAs))
        {
            _ctx.Consume();
            alias = ConsumeIdentifierOrKeywordAsName();
        }
        else if (CanBeColumnAlias())
        {
            alias = _ctx.Consume().Value;
        }

        return new SelectItem { Expression = expr, Alias = alias, Line = line, Column = col };
    }

    private bool IsQualifiedStar()
    {
        // Lookahead: identifier DOT [identifier DOT]... STAR
        // e.g. t.* or schema.table.*
        // Must have at least one DOT before the star.
        if (!_ctx.Is(TokenKind.Identifier)) return false;
        int saved = _ctx.SavePosition();
        try
        {
            _ctx.Consume(); // consume first identifier
            // Must see a dot next, otherwise it's just an identifier (not a qualified star)
            if (!_ctx.Is(TokenKind.Dot)) return false;

            while (_ctx.Is(TokenKind.Dot))
            {
                _ctx.Consume(); // consume dot
                if (_ctx.Is(TokenKind.Star)) return true;   // found the .*
                if (!_ctx.Is(TokenKind.Identifier)) return false;
                _ctx.Consume(); // consume next identifier
            }
            return false;
        }
        finally { _ctx.RestorePosition(saved); }
    }

    private ObjectName ParseObjectNameUpToStar()
    {
        // Consume identifier DOT ... up to (but not including) the *
        var parts = new List<string>();
        parts.Add(_ctx.Consume().Value);
        while (_ctx.Is(TokenKind.Dot) && !_ctx.Peek(1).IsEof && _ctx.Peek(1).Kind != TokenKind.Star)
        {
            _ctx.Consume(); // dot
            parts.Add(_ctx.Consume().Value);
        }
        if (_ctx.Is(TokenKind.Dot)) _ctx.Consume(); // consume final dot before *
        return PartsToObjectName(parts);
    }

    private IntoClause ParseIntoClause()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // INTO
        var table = ParseObjectName();
        return new IntoClause { Table = table, Line = line, Column = col };
    }

    // =========================================================================
    // Table sources (FROM clause)
    // =========================================================================

    private List<TableSource> ParseTableSources()
    {
        var sources = new List<TableSource>();
        sources.Add(ParseTableSourceWithJoins());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            sources.Add(ParseTableSourceWithJoins());
        }
        return sources;
    }

    private TableSource ParseTableSourceWithJoins()
    {
        var left = ParsePrimaryTableSource();

        while (IsJoinKeyword())
        {
            var joinType  = ConsumeJoinType();
            var right     = ParsePrimaryTableSource();
            SqlExpression? condition = null;

            if (joinType != JoinType.Cross && joinType != JoinType.CrossApply && joinType != JoinType.OuterApply)
            {
                _ctx.Expect(TokenKind.KwOn);
                condition = ParseExpression();
            }

            left = new JoinedSource
            {
                Left      = left,
                JoinType  = joinType,
                Right     = right,
                Condition = condition,
                Line      = left.Line,
                Column    = left.Column
            };
        }

        return left;
    }

    private bool IsJoinKeyword()
    {
        if (_ctx.IsAny(TokenKind.KwJoin, TokenKind.KwInner, TokenKind.KwLeft,
                       TokenKind.KwRight, TokenKind.KwFull, TokenKind.KwCross))
            return true;
        if (_ctx.Is(TokenKind.Identifier) &&
            (IsKeyword("OUTER") || IsKeyword("APPLY")))
            return true;
        return false;
    }

    private JoinType ConsumeJoinType()
    {
        // INNER [JOIN], LEFT [OUTER] JOIN, RIGHT [OUTER] JOIN,
        // FULL [OUTER] JOIN, CROSS JOIN, CROSS APPLY, OUTER APPLY
        if (_ctx.Is(TokenKind.KwCross))
        {
            _ctx.Consume();
            if (_ctx.Is(TokenKind.KwApply)) { _ctx.Consume(); return JoinType.CrossApply; }
            _ctx.TryConsume(TokenKind.KwJoin);
            return JoinType.Cross;
        }
        if (_ctx.Is(TokenKind.Identifier) && IsKeyword("OUTER"))
        {
            _ctx.Consume();
            if (_ctx.Is(TokenKind.KwApply)) { _ctx.Consume(); return JoinType.OuterApply; }
        }
        if (_ctx.Is(TokenKind.KwLeft))
        {
            _ctx.Consume();
            _ctx.TryConsume(TokenKind.KwOuter);
            _ctx.Expect(TokenKind.KwJoin);
            return JoinType.LeftOuter;
        }
        if (_ctx.Is(TokenKind.KwRight))
        {
            _ctx.Consume();
            _ctx.TryConsume(TokenKind.KwOuter);
            _ctx.Expect(TokenKind.KwJoin);
            return JoinType.RightOuter;
        }
        if (_ctx.Is(TokenKind.KwFull))
        {
            _ctx.Consume();
            _ctx.TryConsume(TokenKind.KwOuter);
            _ctx.Expect(TokenKind.KwJoin);
            return JoinType.FullOuter;
        }
        if (_ctx.Is(TokenKind.KwInner))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.KwJoin);
            return JoinType.Inner;
        }
        // Bare JOIN = INNER JOIN
        _ctx.Expect(TokenKind.KwJoin);
        return JoinType.Inner;
    }

    private TableSource ParsePrimaryTableSource()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;

        // Subquery (SELECT ...)
        if (_ctx.Is(TokenKind.OpenParen))
        {
            int saved = _ctx.SavePosition();
            _ctx.Consume(); // (
            if (_ctx.Is(TokenKind.KwSelect))
            {
                var subq = ParseSelect();
                _ctx.Expect(TokenKind.CloseParen);
                var alias = ConsumeOptionalAlias();
                return new SubquerySource
                {
                    Query  = subq,
                    Alias  = alias ?? string.Empty,
                    Line   = line,
                    Column = col
                };
            }
            _ctx.RestorePosition(saved);
        }

        var name  = ParseObjectName();
        var tableAlias = ConsumeOptionalAlias();

        // WITH (...) table hints
        var hints = new List<TableHint>();
        if (_ctx.Is(TokenKind.KwWith) && _ctx.Peek(1).Kind == TokenKind.OpenParen)
        {
            _ctx.Consume(); // WITH
            _ctx.Consume(); // (
            hints = ParseTableHints();
            _ctx.Expect(TokenKind.CloseParen);
        }

        return new TableReferenceSource
        {
            Name   = name,
            Alias  = tableAlias,
            Hints  = hints,
            Line   = line,
            Column = col
        };
    }

    private List<TableHint> ParseTableHints()
    {
        var hints = new List<TableHint>();
        while (!_ctx.Is(TokenKind.CloseParen) && !_ctx.IsEof)
        {
            int hline = _ctx.Current.Line, hcol = _ctx.Current.Column;
            var name = _ctx.Consume().Value;
            hints.Add(new TableHint { HintName = name.ToUpperInvariant(), Line = hline, Column = hcol });
            _ctx.TryConsume(TokenKind.Comma);
        }
        return hints;
    }

    private string? ConsumeOptionalAlias()
    {
        if (_ctx.Is(TokenKind.KwAs))
        {
            _ctx.Consume();
            return ConsumeIdentifierOrKeywordAsName();
        }
        if (CanBeTableAlias()) return _ctx.Consume().Value;
        return null;
    }

    // =========================================================================
    // ORDER BY helpers
    // =========================================================================

    private List<OrderByItem> ParseOrderByList()
    {
        var items = new List<OrderByItem>();
        items.Add(ParseOrderByItem());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            items.Add(ParseOrderByItem());
        }
        return items;
    }

    private OrderByItem ParseOrderByItem()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var expr = ParseExpression();
        var dir  = SortDirection.Ascending;
        if (_ctx.Is(TokenKind.KwAsc))  { _ctx.Consume(); dir = SortDirection.Ascending; }
        else if (_ctx.Is(TokenKind.KwDesc)) { _ctx.Consume(); dir = SortDirection.Descending; }
        return new OrderByItem { Expression = expr, Direction = dir, Line = line, Column = col };
    }

    private OffsetFetchClause? TryParseOffsetFetch()
    {
        if (!(_ctx.Is(TokenKind.Identifier) && IsKeyword("OFFSET"))) return null;
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // OFFSET
        var offset = ParseExpression();
        // ROWS or ROW
        if (_ctx.IsAny(TokenKind.KwRows, TokenKind.KwRow)) _ctx.Consume();

        SqlExpression? fetch = null;
        if (_ctx.Is(TokenKind.Identifier) && IsKeyword("FETCH"))
        {
            _ctx.Consume(); // FETCH
            // NEXT or FIRST
            if (_ctx.Is(TokenKind.Identifier)) _ctx.Consume();
            fetch = ParseExpression();
            if (_ctx.IsAny(TokenKind.KwRows, TokenKind.KwRow)) _ctx.Consume();
            // ONLY
            if (_ctx.Is(TokenKind.Identifier) && IsKeyword("ONLY")) _ctx.Consume();
        }
        return new OffsetFetchClause { Offset = offset, Fetch = fetch, Line = line, Column = col };
    }

    private SetOperator? ParseSetOperator()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        SetOperatorKind kind;

        if (_ctx.Is(TokenKind.KwUnion))
        {
            _ctx.Consume();
            kind = SetOperatorKind.Union;
        }
        else if (_ctx.Is(TokenKind.Identifier) && IsKeyword("INTERSECT"))
        {
            _ctx.Consume();
            kind = SetOperatorKind.Intersect;
        }
        else if (_ctx.Is(TokenKind.Identifier) && IsKeyword("EXCEPT"))
        {
            _ctx.Consume();
            kind = SetOperatorKind.Except;
        }
        else return null;

        bool all = _ctx.TryConsume(TokenKind.KwAll) != null;
        var right = ParseSelect();
        return new SetOperator { Kind = kind, All = all, Right = right, Line = line, Column = col };
    }

    // =========================================================================
    // INSERT
    // =========================================================================

    private InsertStatement ParseInsert()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // INSERT
        _ctx.TryConsume(TokenKind.KwInto);

        var target  = ParseObjectName();
        var columns = new List<string>();

        // Optional column list: (col1, col2, ...)
        if (_ctx.Is(TokenKind.OpenParen))
        {
            _ctx.Consume();
            columns.Add(ConsumeIdentifierOrKeywordAsName());
            while (_ctx.Is(TokenKind.Comma))
            {
                _ctx.Consume();
                columns.Add(ConsumeIdentifierOrKeywordAsName());
            }
            _ctx.Expect(TokenKind.CloseParen);
        }

        // OUTPUT clause (before VALUES/SELECT)
        OutputClause? output = TryParseOutputClause();

        // VALUES or SELECT
        ValuesClause? values = null;
        SelectStatement? select = null;

        if (_ctx.Is(TokenKind.KwValues))
        {
            _ctx.Consume();
            values = ParseValuesClause();
        }
        else if (_ctx.Is(TokenKind.KwSelect))
        {
            select = ParseSelect();
        }

        return new InsertStatement
        {
            Target       = target,
            Columns      = columns,
            ValuesSource = values,
            SelectSource = select,
            Output       = output,
            Line         = line,
            Column       = col
        };
    }

    private ValuesClause ParseValuesClause()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var rows = new List<IReadOnlyList<SqlExpression>>();

        do
        {
            _ctx.Expect(TokenKind.OpenParen);
            var row = ParseExpressionList();
            _ctx.Expect(TokenKind.CloseParen);
            rows.Add(row);
        } while (_ctx.TryConsume(TokenKind.Comma) != null
                 && _ctx.Is(TokenKind.OpenParen));

        return new ValuesClause { RowValues = rows, Line = line, Column = col };
    }

    // =========================================================================
    // UPDATE
    // =========================================================================

    private UpdateStatement ParseUpdate()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // UPDATE

        // UPDATE TOP (n)
        TopClause? top = null;
        if (_ctx.Is(TokenKind.KwTop)) top = ParseTop();

        var target = ParseObjectName();
        string? targetAlias = ConsumeOptionalAlias();

        _ctx.Expect(TokenKind.KwSet);
        var sets = ParseSetClauses();

        var from = new List<TableSource>();
        if (_ctx.Is(TokenKind.KwFrom))
        {
            _ctx.Consume();
            from = ParseTableSources();
        }

        SqlExpression? where = null;
        if (_ctx.Is(TokenKind.KwWhere))
        {
            _ctx.Consume();
            where = ParseExpression();
        }

        OutputClause? output = TryParseOutputClause();

        return new UpdateStatement
        {
            Target      = target,
            TargetAlias = targetAlias,
            Sets        = sets,
            From        = from,
            Where       = where,
            Output      = output,
            Line        = line,
            Column      = col
        };
    }

    private List<SetClause> ParseSetClauses()
    {
        var clauses = new List<SetClause>();
        clauses.Add(ParseSetClause());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            clauses.Add(ParseSetClause());
        }
        return clauses;
    }

    private SetClause ParseSetClause()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        // Support table.column = value (table alias prefix)
        var colName = ConsumeIdentifierOrKeywordAsName();
        if (_ctx.Is(TokenKind.Dot))
        {
            _ctx.Consume(); // dot
            colName = ConsumeIdentifierOrKeywordAsName(); // actual column name
        }
        _ctx.Expect(TokenKind.Equal);
        var value = ParseExpression();
        return new SetClause { ColumnName = colName, Value = value, Line = line, Column = col };
    }

    // =========================================================================
    // DELETE
    // =========================================================================

    private DeleteStatement ParseDelete()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // DELETE
        _ctx.TryConsume(TokenKind.KwFrom);

        // TOP (n) optional
        TopClause? top = null;
        if (_ctx.Is(TokenKind.KwTop)) top = ParseTop();

        var target      = ParseObjectName();
        string? alias   = ConsumeOptionalAlias();

        var from = new List<TableSource>();
        if (_ctx.Is(TokenKind.KwFrom))
        {
            _ctx.Consume();
            from = ParseTableSources();
        }

        SqlExpression? where = null;
        if (_ctx.Is(TokenKind.KwWhere))
        {
            _ctx.Consume();
            where = ParseExpression();
        }

        OutputClause? output = TryParseOutputClause();

        return new DeleteStatement
        {
            Target      = target,
            TargetAlias = alias,
            From        = from,
            Where       = where,
            Output      = output,
            Line        = line,
            Column      = col
        };
    }

    // =========================================================================
    // TRUNCATE TABLE
    // =========================================================================

    private TruncateTableStatement ParseTruncate()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // TRUNCATE
        _ctx.Expect(TokenKind.KwTable);
        var table = ParseObjectName();
        return new TruncateTableStatement { Table = table, Line = line, Column = col };
    }

    // =========================================================================
    // CREATE / DROP
    // =========================================================================

    private SqlStatement ParseCreate()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // CREATE

        if (_ctx.Is(TokenKind.KwTable))
        {
            _ctx.Consume();
            return ParseCreateTableBody(line, col);
        }

        // CREATE INDEX, VIEW, PROC, etc. — unsupported for now
        return RecoverUnparsed("CREATE statement other than CREATE TABLE is not yet supported", line, col);
    }

    private CreateTableStatement ParseCreateTableBody(int line, int col)
    {
        // Optional IF NOT EXISTS (non-standard but tolerated)
        if (_ctx.Is(TokenKind.Identifier) && IsKeyword("IF"))
        {
            int s = _ctx.SavePosition();
            _ctx.Consume();
            if (_ctx.Is(TokenKind.KwNot) && _ctx.Peek(1).Kind == TokenKind.Identifier
                && string.Equals(_ctx.Peek(1).Value, "EXISTS", StringComparison.OrdinalIgnoreCase))
            {
                _ctx.Consume(); _ctx.Consume();
            }
            else _ctx.RestorePosition(s);
        }

        var table  = ParseObjectName();
        bool isTemp = table.IsTemporaryTable;

        _ctx.Expect(TokenKind.OpenParen);
        var columns = ParseColumnDefinitions();
        _ctx.Expect(TokenKind.CloseParen);

        return new CreateTableStatement
        {
            Table       = table,
            IsTemporary = isTemp,
            Columns     = columns,
            Line        = line,
            Column      = col
        };
    }

    private List<ColumnDefinition> ParseColumnDefinitions()
    {
        var cols = new List<ColumnDefinition>();
        while (!_ctx.Is(TokenKind.CloseParen) && !_ctx.IsEof)
        {
            // Skip table-level constraints (PRIMARY KEY, UNIQUE, CHECK, FOREIGN KEY)
            if (_ctx.IsAny(TokenKind.KwPrimary, TokenKind.KwUnique,
                           TokenKind.KwCheck, TokenKind.KwForeign, TokenKind.KwConstraint))
            {
                SkipToNextColumnOrEnd();
                continue;
            }
            cols.Add(ParseColumnDefinition());
            _ctx.TryConsume(TokenKind.Comma);
        }
        return cols;
    }

    private ColumnDefinition ParseColumnDefinition()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var name     = ConsumeIdentifierOrKeywordAsName();
        var dataType = ParseDataType();

        bool nullable         = true;
        bool isIdentity       = false;
        int  identitySeed     = 1;
        int  identityInc      = 1;
        bool isPrimary        = false;
        bool isUnique         = false;
        SqlExpression? defVal = null;

        // Column constraints
        while (!_ctx.IsAny(TokenKind.Comma, TokenKind.CloseParen) && !_ctx.IsEof)
        {
            if (_ctx.Is(TokenKind.KwNot) && _ctx.Peek(1).Kind == TokenKind.KwNull)
            {
                _ctx.Consume(); _ctx.Consume(); nullable = false; continue;
            }
            if (_ctx.Is(TokenKind.KwNull)) { _ctx.Consume(); nullable = true; continue; }
            if (_ctx.Is(TokenKind.KwIdentity))
            {
                _ctx.Consume();
                isIdentity = true;
                if (_ctx.Is(TokenKind.OpenParen))
                {
                    _ctx.Consume();
                    identitySeed = (int)long.Parse(_ctx.Expect(TokenKind.IntegerLiteral).Value);
                    _ctx.Expect(TokenKind.Comma);
                    identityInc  = (int)long.Parse(_ctx.Expect(TokenKind.IntegerLiteral).Value);
                    _ctx.Expect(TokenKind.CloseParen);
                }
                continue;
            }
            if (_ctx.Is(TokenKind.KwPrimary))
            {
                _ctx.Consume();
                _ctx.TryConsume(TokenKind.KwKey);
                isPrimary = true;
                continue;
            }
            if (_ctx.Is(TokenKind.KwUnique)) { _ctx.Consume(); isUnique = true; continue; }
            if (_ctx.Is(TokenKind.KwDefault))
            {
                _ctx.Consume();
                defVal = ParseExpression();
                continue;
            }
            if (_ctx.Is(TokenKind.KwConstraint))
            {
                _ctx.Consume();
                ConsumeIdentifierOrKeywordAsName(); // constraint name
                continue;
            }
            break;
        }

        return new ColumnDefinition
        {
            Name              = name,
            DataType          = dataType,
            IsNullable        = nullable,
            IsIdentity        = isIdentity,
            IdentitySeed      = identitySeed,
            IdentityIncrement = identityInc,
            IsPrimaryKey      = isPrimary,
            IsUnique          = isUnique,
            DefaultValue      = defVal,
            Line              = line,
            Column            = col
        };
    }

    private void SkipToNextColumnOrEnd()
    {
        int depth = 0;
        while (!_ctx.IsEof)
        {
            if (_ctx.Is(TokenKind.OpenParen)) { depth++; _ctx.Consume(); continue; }
            if (_ctx.Is(TokenKind.CloseParen))
            {
                if (depth == 0) break;
                depth--; _ctx.Consume(); continue;
            }
            if (_ctx.Is(TokenKind.Comma) && depth == 0) { _ctx.Consume(); break; }
            _ctx.Consume();
        }
    }

    private SqlStatement ParseDrop()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // DROP

        if (_ctx.Is(TokenKind.KwTable))
        {
            _ctx.Consume();
            bool ifExists = TryConsumeIfExists();
            var tables = new List<ObjectName>();
            tables.Add(ParseObjectName());
            while (_ctx.Is(TokenKind.Comma))
            {
                _ctx.Consume();
                tables.Add(ParseObjectName());
            }
            return new DropTableStatement { Tables = tables, IfExists = ifExists, Line = line, Column = col };
        }

        return RecoverUnparsed("DROP statement other than DROP TABLE is not yet supported", line, col);
    }

    private bool TryConsumeIfExists()
    {
        // IF is KwIf, EXISTS is KwExists
        if (!_ctx.Is(TokenKind.KwIf)) return false;
        int s = _ctx.SavePosition();
        _ctx.Consume(); // IF
        if (_ctx.Is(TokenKind.KwExists) ||
            (string.Equals(_ctx.Current.Value, "EXISTS", StringComparison.OrdinalIgnoreCase)))
        {
            _ctx.Consume(); // EXISTS
            return true;
        }
        _ctx.RestorePosition(s);
        return false;
    }

    // =========================================================================
    // Transaction statements
    // =========================================================================

    private SqlStatement ParseBegin()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // BEGIN

        // BEGIN TRANSACTION / TRAN
        if (_ctx.IsAny(TokenKind.KwTransaction, TokenKind.KwTran))
        {
            _ctx.Consume();
            string? name = TryConsumeTransactionName();
            return new BeginTransactionStatement { TransactionName = name, Line = line, Column = col };
        }

        // BEGIN TRY
        if (_ctx.Is(TokenKind.KwTry))
        {
            _ctx.Consume();
            return ParseBeginTryBlock(line, col);
        }

        // BEGIN...END block
        return ParseBeginEndBody(line, col);
    }

    private SqlStatement ParseBeginTryBlock(int line, int col)
    {
        // Collect statements until END TRY
        var tryStmts = ParseStatementsUntilEnd("TRY");
        var tryBlock = new BeginEndBlock { Statements = tryStmts, Line = line, Column = col };

        // Expect BEGIN CATCH ... END CATCH
        if (_ctx.Is(TokenKind.KwBegin))
        {
            _ctx.Consume();
            if (_ctx.Is(TokenKind.KwCatch)) _ctx.Consume();
            var catchStmts = ParseStatementsUntilEnd("CATCH");
            // We don't have a TryCatch node — represent as two BEGIN/END blocks
            // The translator will emit equivalent PostgreSQL exception handling
            return new BeginEndBlock { Statements = tryStmts, Line = line, Column = col };
        }

        return tryBlock;
    }

    private BeginEndBlock ParseBeginEndBody(int line, int col)
    {
        var stmts = ParseStatementsUntilEnd(null);
        return new BeginEndBlock { Statements = stmts, Line = line, Column = col };
    }

    private List<SqlStatement> ParseStatementsUntilEnd(string? expectedEnd)
    {
        var stmts = new List<SqlStatement>();
        while (!_ctx.IsEof)
        {
            SkipStatementTerminators();
            if (_ctx.Is(TokenKind.KwEnd))
            {
                _ctx.Consume(); // END
                // consume optional TRY / CATCH label
                if (expectedEnd != null && _ctx.Is(TokenKind.Identifier) &&
                    string.Equals(_ctx.Current.Value, expectedEnd, StringComparison.OrdinalIgnoreCase))
                    _ctx.Consume();
                break;
            }
            if (_ctx.IsEof) break;
            try { stmts.Add(ParseStatement()!); }
            catch (ParseException ex) { stmts.Add(RecoverUnparsed(ex.Message)); }
        }
        return stmts;
    }

    private SqlStatement ParseEndBlock()
    {
        // Standalone END without a matching BEGIN — treat as empty block
        _ctx.Consume();
        return new BeginEndBlock { Statements = [], Line = _ctx.Current.Line, Column = _ctx.Current.Column };
    }

    private CommitTransactionStatement ParseCommit()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume();
        _ctx.TryConsume(TokenKind.KwTransaction);
        _ctx.TryConsume(TokenKind.KwTran);
        string? name = TryConsumeTransactionName();
        return new CommitTransactionStatement { TransactionName = name, Line = line, Column = col };
    }

    private RollbackTransactionStatement ParseRollback()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume();
        _ctx.TryConsume(TokenKind.KwTransaction);
        _ctx.TryConsume(TokenKind.KwTran);
        string? name = TryConsumeTransactionName();
        return new RollbackTransactionStatement { TransactionName = name, Line = line, Column = col };
    }

    private SaveTransactionStatement ParseSaveTransaction()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // SAVE
        _ctx.TryConsume(TokenKind.KwTransaction);
        _ctx.TryConsume(TokenKind.KwTran);
        string name = ConsumeIdentifierOrKeywordAsName();
        return new SaveTransactionStatement { SavepointName = name, Line = line, Column = col };
    }

    private string? TryConsumeTransactionName()
    {
        if (_ctx.Is(TokenKind.Identifier) && !IsStatementStartKeyword())
            return _ctx.Consume().Value;
        return null;
    }

    // =========================================================================
    // SET options
    // =========================================================================

    private SqlStatement ParseSet()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // SET

        // SET @variable = value (variable assignment)
        if (_ctx.Is(TokenKind.Parameter))
        {
            var varName = _ctx.Consume().Value;
            _ctx.Expect(TokenKind.Equal);
            var value = ParseExpression();
            // Represent as a SetOption with the variable name
            return new SetOptionStatement
            {
                OptionName = varName,
                Value      = value,
                Line       = line,
                Column     = col
            };
        }

        var optionName = ConsumeSetOptionName();
        bool isOn  = false;
        SqlExpression? val = null;

        if (IsOnOffToken(true))   { _ctx.Consume(); isOn = true; }
        else if (IsOnOffToken(false)) { _ctx.Consume(); isOn = false; }
        else val = ParseExpression();

        return new SetOptionStatement { OptionName = optionName, IsOn = isOn, Value = val, Line = line, Column = col };
    }

    private string ConsumeSetOptionName()
    {
        // Handles multi-word option names like CONCAT_NULL_YIELDS_NULL
        return _ctx.Consume().Value.ToUpperInvariant();
    }

    // =========================================================================
    // USE database
    // =========================================================================

    private UseDatabaseStatement ParseUse()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // USE
        var name = ConsumeIdentifierOrKeywordAsName();
        return new UseDatabaseStatement { DatabaseName = name, Line = line, Column = col };
    }

    // =========================================================================
    // EXEC / EXECUTE
    // =========================================================================

    private ExecuteStatement ParseExecute()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // EXEC or EXECUTE

        // Optional return variable: EXEC @rc = proc
        if (_ctx.Is(TokenKind.Parameter) && _ctx.Peek(1).Kind == TokenKind.Equal)
        {
            _ctx.Consume(); // @rc
            _ctx.Consume(); // =
        }

        var name = ParseObjectName();
        var args = new List<ProcedureArgument>();

        if (!IsStatementEndToken())
        {
            args = ParseProcedureArguments();
        }

        return new ExecuteStatement { ProcedureName = name, Arguments = args, Line = line, Column = col };
    }

    private List<ProcedureArgument> ParseProcedureArguments()
    {
        var args = new List<ProcedureArgument>();
        if (_ctx.Is(TokenKind.OpenParen)) _ctx.Consume(); // optional parens
        bool hasOpenParen = false;

        if (!IsStatementEndToken())
        {
            args.Add(ParseProcedureArgument());
            while (_ctx.Is(TokenKind.Comma))
            {
                _ctx.Consume();
                if (IsStatementEndToken()) break;
                args.Add(ParseProcedureArgument());
            }
        }

        if (hasOpenParen) _ctx.TryConsume(TokenKind.CloseParen);
        return args;
    }

    private ProcedureArgument ParseProcedureArgument()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        string? paramName = null;

        // Named argument: @name = value
        if (_ctx.Is(TokenKind.Parameter) && _ctx.Peek(1).Kind == TokenKind.Equal)
        {
            paramName = _ctx.Consume().Value;
            _ctx.Consume(); // =
        }

        var value    = ParseExpression();
        bool isOutput = _ctx.IsAny(TokenKind.KwOutput, TokenKind.KwOut);
        if (isOutput) _ctx.Consume();

        return new ProcedureArgument
        {
            ParameterName = paramName,
            Value         = value,
            IsOutput      = isOutput,
            Line          = line,
            Column        = col
        };
    }

    // =========================================================================
    // IF / WHILE
    // =========================================================================

    private IfStatement ParseIf()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // IF
        var condition = ParseExpression();
        var then      = ParseStatement() ?? new BeginEndBlock { Statements = [], Line = line, Column = col };

        SqlStatement? els = null;
        if (_ctx.Is(TokenKind.KwElse))
        {
            _ctx.Consume();
            els = ParseStatement();
        }

        return new IfStatement { Condition = condition, ThenBranch = then, ElseBranch = els, Line = line, Column = col };
    }

    private WhileStatement ParseWhile()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // WHILE
        var condition = ParseExpression();
        var body      = ParseStatement() ?? new BeginEndBlock { Statements = [], Line = line, Column = col };
        return new WhileStatement { Condition = condition, Body = body, Line = line, Column = col };
    }

    // =========================================================================
    // PRINT / RETURN / DECLARE
    // =========================================================================

    private PrintStatement ParsePrint()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume();
        return new PrintStatement { Expression = ParseExpression(), Line = line, Column = col };
    }

    private ReturnStatement ParseReturn()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume();
        SqlExpression? val = IsStatementEndToken() ? null : ParseExpression();
        return new ReturnStatement { Value = val, Line = line, Column = col };
    }

    private DeclareStatement ParseDeclare()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // DECLARE
        var decls = new List<VariableDeclaration>();
        decls.Add(ParseVariableDeclaration());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            decls.Add(ParseVariableDeclaration());
        }
        return new DeclareStatement { Declarations = decls, Line = line, Column = col };
    }

    private VariableDeclaration ParseVariableDeclaration()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var name     = _ctx.Expect(TokenKind.Parameter).Value;
        var dataType = ParseDataType();

        SqlExpression? init = null;
        if (_ctx.Is(TokenKind.Equal))
        {
            _ctx.Consume();
            init = ParseExpression();
        }
        return new VariableDeclaration { Name = name, DataType = dataType, InitialValue = init, Line = line, Column = col };
    }

    // =========================================================================
    // OUTPUT clause
    // =========================================================================

    private OutputClause? TryParseOutputClause()
    {
        if (!(_ctx.Is(TokenKind.KwOutput) || _ctx.Is(TokenKind.KwOutput))) return null;
        // OUTPUT is used in two contexts; only parse it here if it follows DML
        // Simplified: skip for now
        return null;
    }

    // =========================================================================
    // Data type
    // =========================================================================

    private DataTypeNode ParseDataType()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var typeName = _ctx.Consume().Value;

        int? length    = null;
        bool isMax     = false;
        int? precision = null;
        int? scale     = null;

        if (_ctx.Is(TokenKind.OpenParen))
        {
            _ctx.Consume();
            if (_ctx.Is(TokenKind.Star))
            {
                _ctx.Consume(); // MAX
                isMax = true;
            }
            else if (_ctx.Is(TokenKind.Identifier) && IsKeyword("MAX"))
            {
                _ctx.Consume();
                isMax = true;
            }
            else
            {
                int first = (int)long.Parse(_ctx.Expect(TokenKind.IntegerLiteral).Value);
                if (_ctx.Is(TokenKind.Comma))
                {
                    _ctx.Consume();
                    int second = (int)long.Parse(_ctx.Expect(TokenKind.IntegerLiteral).Value);
                    precision = first;
                    scale     = second;
                }
                else
                {
                    length = first;
                }
            }
            _ctx.Expect(TokenKind.CloseParen);
        }

        return new DataTypeNode
        {
            TypeName  = typeName.ToUpperInvariant(),
            Length    = length,
            IsMax     = isMax,
            Precision = precision,
            Scale     = scale,
            Line      = line,
            Column    = col
        };
    }

    // =========================================================================
    // Object names: [server.][database.][schema.]name
    // =========================================================================

    private ObjectName ParseObjectName()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var parts = new List<string>();
        parts.Add(ConsumeIdentifierOrKeywordAsName());

        while (_ctx.Is(TokenKind.Dot))
        {
            _ctx.Consume();
            if (_ctx.Is(TokenKind.Star)) break; // stop before wildcard
            parts.Add(ConsumeIdentifierOrKeywordAsName());
        }

        return PartsToObjectName(parts, line, col);
    }

    private ObjectName PartsToObjectName(List<string> parts, int line = 0, int col = 0)
    {
        return parts.Count switch
        {
            1 => new ObjectName { Name = parts[0], Line = line, Column = col },
            2 => new ObjectName { Schema = parts[0], Name = parts[1], Line = line, Column = col },
            3 => new ObjectName { Database = parts[0], Schema = parts[1], Name = parts[2], Line = line, Column = col },
            4 => new ObjectName { Server = parts[0], Database = parts[1], Schema = parts[2], Name = parts[3], Line = line, Column = col },
            _ => new ObjectName { Name = parts[^1], Line = line, Column = col }
        };
    }

    // =========================================================================
    // Expression parsing (Pratt / precedence-climbing)
    // =========================================================================

    private SqlExpression ParseExpression() => ParseOr();

    private SqlExpression ParseOr()
    {
        var left = ParseAnd();
        while (_ctx.Is(TokenKind.KwOr))
        {
            _ctx.Consume();
            var right = ParseAnd();
            left = Binary(left, BinaryOperator.Or, right);
        }
        return left;
    }

    private SqlExpression ParseAnd()
    {
        var left = ParseNot();
        while (_ctx.Is(TokenKind.KwAnd))
        {
            _ctx.Consume();
            var right = ParseNot();
            left = Binary(left, BinaryOperator.And, right);
        }
        return left;
    }

    private SqlExpression ParseNot()
    {
        if (_ctx.Is(TokenKind.KwNot))
        {
            int line = _ctx.Current.Line, col = _ctx.Current.Column;
            _ctx.Consume();
            return new UnaryExpression
            {
                Operator = UnaryOperator.Not,
                Operand  = ParseNot(),
                Line     = line,
                Column   = col
            };
        }
        return ParseComparison();
    }

    private SqlExpression ParseComparison()
    {
        var left = ParseAddSub();
        return ParseComparisonRhs(left);
    }

    private SqlExpression ParseComparisonRhs(SqlExpression left)
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;

        // IS [NOT] NULL
        if (_ctx.Is(TokenKind.KwIs))
        {
            _ctx.Consume();
            bool isNot = _ctx.TryConsume(TokenKind.KwNot) != null;
            _ctx.Expect(TokenKind.KwNull);
            return new IsNullExpression { Value = left, IsNot = isNot, Line = line, Column = col };
        }

        // [NOT] IN (...)
        bool notIn = false;
        if (_ctx.Is(TokenKind.KwNot) && _ctx.Peek(1).Kind == TokenKind.KwIn)
        { _ctx.Consume(); notIn = true; }

        if (_ctx.Is(TokenKind.KwIn))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.OpenParen);
            if (_ctx.Is(TokenKind.KwSelect))
            {
                var sub = ParseSelect();
                _ctx.Expect(TokenKind.CloseParen);
                return new InSubqueryExpression { Value = left, Subquery = sub, IsNot = notIn, Line = line, Column = col };
            }
            var items = ParseExpressionList();
            _ctx.Expect(TokenKind.CloseParen);
            return new InListExpression { Value = left, Items = items, IsNot = notIn, Line = line, Column = col };
        }

        // [NOT] LIKE pattern [ESCAPE e]
        bool notLike = false;
        if (_ctx.Is(TokenKind.KwNot) && _ctx.Peek(1).Kind == TokenKind.KwLike)
        { _ctx.Consume(); notLike = true; }

        if (_ctx.Is(TokenKind.KwLike))
        {
            _ctx.Consume();
            var pattern = ParseAddSub();
            SqlExpression? escape = null;
            if (_ctx.Is(TokenKind.Identifier) && IsKeyword("ESCAPE"))
            { _ctx.Consume(); escape = ParseAddSub(); }
            return new LikeExpression { Value = left, Pattern = pattern, Escape = escape, IsNot = notLike, Line = line, Column = col };
        }

        // [NOT] BETWEEN low AND high
        bool notBetween = false;
        if (_ctx.Is(TokenKind.KwNot) && _ctx.Peek(1).Kind == TokenKind.KwBetween)
        { _ctx.Consume(); notBetween = true; }

        if (_ctx.Is(TokenKind.KwBetween))
        {
            _ctx.Consume();
            var low  = ParseAddSub();
            _ctx.Expect(TokenKind.KwAnd);
            var high = ParseAddSub();
            return new BetweenExpression { Value = left, Low = low, High = high, IsNot = notBetween, Line = line, Column = col };
        }

        // Comparison operators
        BinaryOperator? op = _ctx.Current.Kind switch
        {
            TokenKind.Equal              => BinaryOperator.Equal,
            TokenKind.NotEqual           => BinaryOperator.NotEqual,
            TokenKind.LessThan           => BinaryOperator.LessThan,
            TokenKind.GreaterThan        => BinaryOperator.GreaterThan,
            TokenKind.LessThanOrEqual    => BinaryOperator.LessThanOrEqual,
            TokenKind.GreaterThanOrEqual => BinaryOperator.GreaterThanOrEqual,
            TokenKind.NotLessThan        => BinaryOperator.GreaterThanOrEqual,
            TokenKind.NotGreaterThan     => BinaryOperator.LessThanOrEqual,
            _                            => (BinaryOperator?)null
        };

        if (op != null)
        {
            _ctx.Consume();
            var right = ParseAddSub();
            return Binary(left, op.Value, right);
        }

        return left;
    }

    private SqlExpression ParseAddSub()
    {
        var left = ParseMulDiv();
        while (_ctx.IsAny(TokenKind.Plus, TokenKind.Minus, TokenKind.Pipe))
        {
            var op = _ctx.Current.Kind == TokenKind.Plus   ? BinaryOperator.Add
                   : _ctx.Current.Kind == TokenKind.Minus  ? BinaryOperator.Subtract
                   : BinaryOperator.BitwiseOr;
            _ctx.Consume();
            left = Binary(left, op, ParseMulDiv());
        }
        return left;
    }

    private SqlExpression ParseMulDiv()
    {
        var left = ParseUnary();
        while (_ctx.IsAny(TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
                          TokenKind.Ampersand, TokenKind.Caret))
        {
            var op = _ctx.Current.Kind switch
            {
                TokenKind.Star      => BinaryOperator.Multiply,
                TokenKind.Slash     => BinaryOperator.Divide,
                TokenKind.Percent   => BinaryOperator.Modulo,
                TokenKind.Ampersand => BinaryOperator.BitwiseAnd,
                TokenKind.Caret     => BinaryOperator.BitwiseXor,
                _                   => BinaryOperator.Multiply
            };
            _ctx.Consume();
            left = Binary(left, op, ParseUnary());
        }
        return left;
    }

    private SqlExpression ParseUnary()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        if (_ctx.Is(TokenKind.Minus))
        {
            _ctx.Consume();
            return new UnaryExpression { Operator = UnaryOperator.Negate, Operand = ParseUnary(), Line = line, Column = col };
        }
        if (_ctx.Is(TokenKind.Plus)) { _ctx.Consume(); return ParseUnary(); } // +expr is a no-op
        if (_ctx.Is(TokenKind.Tilde))
        {
            _ctx.Consume();
            return new UnaryExpression { Operator = UnaryOperator.BitwiseNot, Operand = ParseUnary(), Line = line, Column = col };
        }
        return ParsePrimary();
    }

    private SqlExpression ParsePrimary()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        var t = _ctx.Current;

        switch (t.Kind)
        {
            case TokenKind.IntegerLiteral:
                _ctx.Consume();
                return new IntegerLiteralExpression { Value = long.Parse(t.Value), Line = line, Column = col };

            case TokenKind.DecimalLiteral:
                _ctx.Consume();
                return new DecimalLiteralExpression { Value = decimal.Parse(t.Value), RawText = t.Value, Line = line, Column = col };

            case TokenKind.FloatLiteral:
                _ctx.Consume();
                return new FloatLiteralExpression { Value = double.Parse(t.Value), RawText = t.Value, Line = line, Column = col };

            case TokenKind.StringLiteral:
                _ctx.Consume();
                return new StringLiteralExpression { Value = t.Value, Line = line, Column = col };

            case TokenKind.HexLiteral:
                _ctx.Consume();
                return new StringLiteralExpression { Value = t.Value, Line = line, Column = col };

            case TokenKind.MoneyLiteral:
                _ctx.Consume();
                if (decimal.TryParse(t.Value.TrimStart('$'), out var money))
                    return new DecimalLiteralExpression { Value = money, RawText = t.Value, Line = line, Column = col };
                return new StringLiteralExpression { Value = t.Value, Line = line, Column = col };

            case TokenKind.KwNull:
                _ctx.Consume();
                return new NullLiteralExpression { Line = line, Column = col };

            case TokenKind.Parameter:
                _ctx.Consume();
                return new ParameterExpression { Name = t.Value, Line = line, Column = col };

            case TokenKind.GlobalVariable:
                _ctx.Consume();
                return new GlobalVariableExpression { Name = t.Value, Line = line, Column = col };

            case TokenKind.OpenParen:
                return ParseParenthesisedExpression(line, col);

            case TokenKind.KwCase:
                return ParseCaseExpression(line, col);

            case TokenKind.KwCast:
                return ParseCast(line, col);

            case TokenKind.KwConvert:
                return ParseConvert(line, col);

            case TokenKind.KwTry_Cast:
            case TokenKind.KwTry_Convert:
                return ParseConvert(line, col, isTryCast: true);

            case TokenKind.KwExists:
                return ParseExists(line, col);

            case TokenKind.KwIsnull:
                return ParseIsNullFunction(line, col);

            case TokenKind.KwCoalesce:
            case TokenKind.KwNullif:
                return ParseNamedFunction(t.Value, line, col);

            case TokenKind.KwSelect:
                // Scalar subquery
                var sub = ParseSelect();
                return new SubqueryExpression { Query = sub, Line = line, Column = col };
        }

        // Identifier → either a function call or a column/table reference
        if (t.Kind == TokenKind.Identifier || IsTypeKeyword(t.Kind))
        {
            return ParseIdentifierOrCall(line, col);
        }

        // Keyword used as an expression (e.g. CURRENT_TIMESTAMP)
        if (IsBuiltInConstantKeyword(t))
        {
            _ctx.Consume();
            return new FunctionCallExpression
            {
                Name      = new ObjectName { Name = t.Value.ToUpperInvariant(), Line = line, Column = col },
                Arguments = [],
                Line      = line,
                Column    = col
            };
        }

        throw _ctx.ParseError($"Unexpected token '{t.Value}' ({t.Kind}) in expression");
    }

    private SqlExpression ParseParenthesisedExpression(int line, int col)
    {
        _ctx.Consume(); // (

        // Subquery
        if (_ctx.Is(TokenKind.KwSelect))
        {
            var sub = ParseSelect();
            _ctx.Expect(TokenKind.CloseParen);
            return new SubqueryExpression { Query = sub, Line = line, Column = col };
        }

        var expr = ParseExpression();
        _ctx.Expect(TokenKind.CloseParen);
        return expr;
    }

    private SqlExpression ParseCaseExpression(int line, int col)
    {
        _ctx.Consume(); // CASE

        // Simple CASE: CASE expr WHEN val THEN result ... END
        // Searched CASE: CASE WHEN condition THEN result ... END
        SqlExpression? input = null;
        if (!_ctx.Is(TokenKind.KwWhen)) input = ParseExpression();

        var whens = new List<WhenClause>();
        while (_ctx.Is(TokenKind.KwWhen))
        {
            int wline = _ctx.Current.Line, wcol = _ctx.Current.Column;
            _ctx.Consume(); // WHEN
            var cond   = ParseExpression();
            _ctx.Expect(TokenKind.KwThen);
            var result = ParseExpression();
            whens.Add(new WhenClause { Condition = cond, Result = result, Line = wline, Column = wcol });
        }

        SqlExpression? elseExpr = null;
        if (_ctx.Is(TokenKind.KwElse)) { _ctx.Consume(); elseExpr = ParseExpression(); }

        _ctx.Expect(TokenKind.KwEnd);

        return new CaseExpression
        {
            InputExpression = input,
            WhenClauses     = whens,
            ElseExpression  = elseExpr,
            Line            = line,
            Column          = col
        };
    }

    private CastExpression ParseCast(int line, int col)
    {
        _ctx.Consume(); // CAST
        _ctx.Expect(TokenKind.OpenParen);
        var value      = ParseExpression();
        _ctx.Expect(TokenKind.KwAs);
        var targetType = ParseDataType();
        _ctx.Expect(TokenKind.CloseParen);
        return new CastExpression { Value = value, TargetType = targetType, Line = line, Column = col };
    }

    private ConvertExpression ParseConvert(int line, int col, bool isTryCast = false)
    {
        _ctx.Consume(); // CONVERT / TRY_CAST / TRY_CONVERT
        _ctx.Expect(TokenKind.OpenParen);
        var targetType = ParseDataType();
        _ctx.Expect(TokenKind.Comma);
        var value      = ParseExpression();
        SqlExpression? style = null;
        if (_ctx.Is(TokenKind.Comma)) { _ctx.Consume(); style = ParseExpression(); }
        _ctx.Expect(TokenKind.CloseParen);
        return new ConvertExpression { TargetType = targetType, Value = value, Style = style, IsTryCast = isTryCast, Line = line, Column = col };
    }

    private ExistsExpression ParseExists(int line, int col)
    {
        _ctx.Consume(); // EXISTS
        _ctx.Expect(TokenKind.OpenParen);
        var sub = ParseSelect();
        _ctx.Expect(TokenKind.CloseParen);
        return new ExistsExpression { Subquery = sub, Line = line, Column = col };
    }

    private FunctionCallExpression ParseIsNullFunction(int line, int col)
    {
        _ctx.Consume(); // ISNULL
        _ctx.Expect(TokenKind.OpenParen);
        var check   = ParseExpression();
        _ctx.Expect(TokenKind.Comma);
        var replace = ParseExpression();
        _ctx.Expect(TokenKind.CloseParen);
        return new FunctionCallExpression
        {
            Name      = new ObjectName { Name = "ISNULL", Line = line, Column = col },
            Arguments = [check, replace],
            Line      = line,
            Column    = col
        };
    }

    private FunctionCallExpression ParseNamedFunction(string name, int line, int col)
    {
        _ctx.Consume(); // function keyword
        _ctx.Expect(TokenKind.OpenParen);
        var args = ParseExpressionList();
        _ctx.Expect(TokenKind.CloseParen);
        return new FunctionCallExpression
        {
            Name      = new ObjectName { Name = name.ToUpperInvariant(), Line = line, Column = col },
            Arguments = args,
            Line      = line,
            Column    = col
        };
    }

    private SqlExpression ParseIdentifierOrCall(int line, int col)
    {
        // Parse the qualified name
        var nameParts = new List<string>();
        nameParts.Add(_ctx.Consume().Value);

        while (_ctx.Is(TokenKind.Dot))
        {
            _ctx.Consume();
            nameParts.Add(ConsumeIdentifierOrKeywordAsName());
        }

        var objName = PartsToObjectName(nameParts, line, col);

        // Function call: name(...)
        if (_ctx.Is(TokenKind.OpenParen))
        {
            _ctx.Consume(); // (
            bool distinct = _ctx.TryConsume(TokenKind.KwDistinct) != null;

            // COUNT(*) special case
            List<SqlExpression> args;
            if (_ctx.Is(TokenKind.Star))
            {
                _ctx.Consume();
                args = [new IntegerLiteralExpression { Value = 1, Line = line, Column = col }]; // placeholder
            }
            else if (_ctx.Is(TokenKind.CloseParen))
            {
                args = [];
            }
            else
            {
                args = ParseExpressionList();
            }
            _ctx.Expect(TokenKind.CloseParen);

            // Optional OVER clause
            OverClause? over = null;
            if (_ctx.Is(TokenKind.KwOver)) over = ParseOverClause();

            return new FunctionCallExpression
            {
                Name      = objName,
                Arguments = args,
                Distinct  = distinct,
                Over      = over,
                Line      = line,
                Column    = col
            };
        }

        // Column reference: [table.]column
        if (nameParts.Count == 1)
            return new ColumnReferenceExpression { ColumnName = nameParts[0], Line = line, Column = col };

        return new ColumnReferenceExpression
        {
            TableAlias = string.Join(".", nameParts[..^1]),
            ColumnName = nameParts[^1],
            Line       = line,
            Column     = col
        };
    }

    private OverClause ParseOverClause()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        _ctx.Consume(); // OVER
        _ctx.Expect(TokenKind.OpenParen);

        var partitionBy = new List<SqlExpression>();
        if (_ctx.Is(TokenKind.KwPartition))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.KwBy);
            partitionBy = ParseExpressionList();
        }

        var orderBy = new List<OrderByItem>();
        if (_ctx.Is(TokenKind.KwOrderBy))
        {
            _ctx.Consume();
            _ctx.Expect(TokenKind.KwBy);
            orderBy = ParseOrderByList();
        }

        WindowFrame? frame = null;
        if (_ctx.IsAny(TokenKind.KwRows, TokenKind.KwRange))
        {
            var unit = _ctx.Is(TokenKind.KwRows) ? WindowFrameUnit.Rows : WindowFrameUnit.Range;
            _ctx.Consume();
            var start = ParseWindowFrameBound();
            WindowFrameBound? end = null;
            if (_ctx.Is(TokenKind.KwAnd)) { _ctx.Consume(); end = ParseWindowFrameBound(); }
            frame = new WindowFrame { Unit = unit, Start = start, End = end, Line = line, Column = col };
        }

        _ctx.Expect(TokenKind.CloseParen);
        return new OverClause { PartitionBy = partitionBy, OrderBy = orderBy, Frame = frame, Line = line, Column = col };
    }

    private WindowFrameBound ParseWindowFrameBound()
    {
        int line = _ctx.Current.Line, col = _ctx.Current.Column;
        if (_ctx.Is(TokenKind.KwUnbounded))
        {
            _ctx.Consume();
            var kind2 = _ctx.Is(TokenKind.KwPreceding) ? WindowFrameBoundKind.UnboundedPreceding : WindowFrameBoundKind.UnboundedFollowing;
            _ctx.Consume();
            return new WindowFrameBound { Kind = kind2, Line = line, Column = col };
        }
        if (_ctx.Is(TokenKind.KwCurrent))
        {
            _ctx.Consume();
            _ctx.TryConsume(TokenKind.KwRow);
            return new WindowFrameBound { Kind = WindowFrameBoundKind.CurrentRow, Line = line, Column = col };
        }
        var offset = ParseExpression();
        var kind   = _ctx.Is(TokenKind.KwPreceding) ? WindowFrameBoundKind.Preceding : WindowFrameBoundKind.Following;
        _ctx.Consume();
        return new WindowFrameBound { Kind = kind, Offset = offset, Line = line, Column = col };
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private List<SqlExpression> ParseExpressionList()
    {
        var list = new List<SqlExpression>();
        list.Add(ParseExpression());
        while (_ctx.Is(TokenKind.Comma))
        {
            _ctx.Consume();
            // Stop if the next token looks like the start of a new clause, not an expression
            if (IsStatementEndToken() || _ctx.Is(TokenKind.CloseParen)) break;
            list.Add(ParseExpression());
        }
        return list;
    }

    private static BinaryExpression Binary(SqlExpression left, BinaryOperator op, SqlExpression right)
        => new() { Left = left, Operator = op, Right = right, Line = left.Line, Column = left.Column };

    private bool CanBeColumnAlias()
    {
        var t = _ctx.Current;
        if (t.IsEof) return false;
        if (t.Kind == TokenKind.Identifier) return true;
        // Some keywords are allowed as aliases in SQL Server
        return t.Kind switch
        {
            TokenKind.KwKey      => true,
            TokenKind.KwDate     => true,
            TokenKind.KwTime2    => true,
            TokenKind.Identifier => true,
            _                    => false
        };
    }

    private bool CanBeTableAlias()
    {
        var t = _ctx.Current;
        if (t.IsEof) return false;
        // Avoid consuming INTO, WHERE, ON, etc. as aliases
        if (t.Kind == TokenKind.Identifier && !IsStatementKeyword(t)) return true;
        return false;
    }

    private static bool IsStatementKeyword(Token t) => t.Kind switch
    {
        TokenKind.KwWhere   or TokenKind.KwOn     or TokenKind.KwSet  or
        TokenKind.KwJoin    or TokenKind.KwInner  or TokenKind.KwLeft or
        TokenKind.KwRight   or TokenKind.KwFull   or TokenKind.KwCross or
        TokenKind.KwOrderBy or TokenKind.KwGroupBy or TokenKind.KwHaving or
        TokenKind.KwUnion   or TokenKind.KwWith   or TokenKind.KwInto  or
        TokenKind.KwFrom    or TokenKind.KwWhen => true,
        _ => false
    };

    private bool IsStatementEndToken()
    {
        var t = _ctx.Current;
        if (t.IsEof) return true;
        if (t.Kind == TokenKind.Semicolon) return true;
        if (t.Kind == TokenKind.Identifier &&
            string.Equals(t.Value, "GO", StringComparison.OrdinalIgnoreCase)) return true;
        return t.Kind switch
        {
            TokenKind.KwSelect or TokenKind.KwInsert or TokenKind.KwUpdate or
            TokenKind.KwDelete or TokenKind.KwCreate or TokenKind.KwDrop   or
            TokenKind.KwBegin  or TokenKind.KwEnd    or TokenKind.KwIf     or
            TokenKind.KwWhile  or TokenKind.KwExec   or TokenKind.KwExecute => true,
            _ => false
        };
    }

    private bool IsStatementStartKeyword() => IsStatementEndToken();

    private string ConsumeIdentifierOrKeywordAsName()
    {
        var t = _ctx.Current;
        if (t.IsEof) throw _ctx.ParseError("Expected identifier but reached end of input");
        _ctx.Consume();
        return t.Value;
    }

    private bool IsKeyword(string kw)
        => string.Equals(_ctx.Current.Value, kw, StringComparison.OrdinalIgnoreCase);

    private bool IsOnOffToken(bool on)
    {
        // ON is TokenKind.KwOn, OFF is an Identifier (not a keyword in the table)
        if (on)  return _ctx.Is(TokenKind.KwOn) ||
                        (string.Equals(_ctx.Current.Value, "ON", StringComparison.OrdinalIgnoreCase));
        return string.Equals(_ctx.Current.Value, "OFF", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTypeKeyword(TokenKind k) => k switch
    {
        TokenKind.KwInt or TokenKind.KwBigint or TokenKind.KwSmallint or
        TokenKind.KwVarchar or TokenKind.KwNvarchar or TokenKind.KwChar or
        TokenKind.KwNchar or TokenKind.KwDecimal or TokenKind.KwNumeric or
        TokenKind.KwDatetime or TokenKind.KwDatetime2 or TokenKind.KwDate or
        TokenKind.KwTime2 or TokenKind.KwFloat or TokenKind.KwReal or
        TokenKind.KwBit or TokenKind.KwBinary or TokenKind.KwVarbinary or
        TokenKind.KwUniqueidentifier or TokenKind.KwXml => true,
        _ => false
    };

    private static bool IsBuiltInConstantKeyword(Token t)
    {
        if (t.Kind != TokenKind.Identifier) return false;
        return t.Value.ToUpperInvariant() switch
        {
            "CURRENT_TIMESTAMP" or "CURRENT_USER" or "CURRENT_DATE" or
            "SESSION_USER"      or "SYSTEM_USER"  or "USER" => true,
            _ => false
        };
    }

    private SqlStatement ParseUnknownStatement()
    {
        var first = _ctx.Current;
        return RecoverUnparsed($"Unrecognised statement starting with '{first.Value}'",
                               first.Line, first.Column);
    }

    private UnparsedStatement RecoverUnparsed(string reason, int line = 0, int col = 0)
    {
        if (line == 0) { line = _ctx.Current.Line; col = _ctx.Current.Column; }
        var sb = new System.Text.StringBuilder();
        while (!_ctx.IsEof && !IsStatementBoundary())
        {
            sb.Append(_ctx.Current.Value).Append(' ');
            _ctx.Consume();
        }
        _ctx.TryConsume(TokenKind.Semicolon);
        return new UnparsedStatement { RawSql = sb.ToString().Trim(), Reason = reason, Line = line, Column = col };
    }

    private bool IsStatementBoundary()
    {
        if (_ctx.Is(TokenKind.Semicolon)) return true;
        if (_ctx.Is(TokenKind.Identifier) &&
            string.Equals(_ctx.Current.Value, "GO", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
