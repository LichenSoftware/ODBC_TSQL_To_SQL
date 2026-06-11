using System.Text;
using PgPassthrough.Core.Models;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.Translation.FunctionMap;

namespace PgPassthrough.Translation.Translators;

/// <summary>
/// The core T-SQL → PostgreSQL translator.
/// Implements <see cref="ISqlVisitor{TResult}"/> where TResult = string.
///
/// Each Visit method returns a SQL fragment string. The top-level VisitBatch
/// concatenates all statement translations separated by semicolons.
///
/// Key translations:
///   - SELECT TOP N → LIMIT N
///   - ISNULL(a,b) → COALESCE(a,b)
///   - GETDATE() → NOW()
///   - CONVERT(type, val) → CAST(val AS type)
///   - [bracketed] identifiers → "double-quoted" identifiers
///   - @param → $N positional parameters (for parameterised queries)
///   - @@ROWCOUNT → lastval() etc.
///   - Table hints stripped with warning
///   - IDENTITY → GENERATED ALWAYS AS IDENTITY
///   - #temp → pg_temp.temp
///   - String + operator → || concatenation (context-sensitive)
/// </summary>
internal sealed class PgSqlEmitter : ISqlVisitor<string>
{
    private readonly List<TranslationWarning> _warnings = new();
    private readonly TranslationContext _context;

    public IReadOnlyList<TranslationWarning> Warnings => _warnings;

    public PgSqlEmitter(TranslationContext context)
    {
        _context = context;
    }

    private void Warn(string code, string message) =>
        _warnings.Add(new TranslationWarning { Code = code, Message = message });

    private string V(SqlNode node) => node.Accept(this);
    private string Expr(SqlExpression? e) => e == null ? "" : e.Accept(this);

    // =========================================================================
    // Batch
    // =========================================================================

    public string VisitBatch(SqlBatch node)
    {
        var parts = node.Statements
            .Select(s => s.Accept(this))
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(";\n", parts);
    }

    // =========================================================================
    // SELECT
    // =========================================================================

    public string VisitSelect(SelectStatement node)
    {
        var sb = new StringBuilder("SELECT");

        if (node.Distinct) sb.Append(" DISTINCT");

        // Select list
        sb.Append(' ');
        sb.Append(string.Join(", ", node.SelectList.Select(V)));

        // INTO
        if (node.Into != null)
            sb.Append(" INTO ").Append(TranslateTableName(node.Into.Table));

        // FROM
        if (node.From.Count > 0)
        {
            sb.Append(" FROM ");
            sb.Append(string.Join(", ", node.From.Select(V)));
        }

        // WHERE
        if (node.Where != null)
            sb.Append(" WHERE ").Append(Expr(node.Where));

        // GROUP BY
        if (node.GroupBy.Count > 0)
            sb.Append(" GROUP BY ").Append(string.Join(", ", node.GroupBy.Select(Expr)));

        // HAVING
        if (node.Having != null)
            sb.Append(" HAVING ").Append(Expr(node.Having));

        // ORDER BY
        if (node.OrderBy.Count > 0)
            sb.Append(" ORDER BY ").Append(string.Join(", ", node.OrderBy.Select(V)));

        // TOP → LIMIT (must come after ORDER BY in PG)
        if (node.Top != null && node.OffsetFetch == null)
        {
            sb.Append(" LIMIT ").Append(Expr(node.Top.Count));
        }

        // OFFSET/FETCH
        if (node.OffsetFetch != null)
        {
            sb.Append(" LIMIT ");
            sb.Append(node.OffsetFetch.Fetch != null ? Expr(node.OffsetFetch.Fetch) : "ALL");
            sb.Append(" OFFSET ").Append(Expr(node.OffsetFetch.Offset));
        }

        // UNION / INTERSECT / EXCEPT
        if (node.SetOperator != null)
            sb.Append(' ').Append(V(node.SetOperator));

        return sb.ToString();
    }

    public string VisitTop(TopClause node) => Expr(node.Count);

    public string VisitSelectItem(SelectItem node)
    {
        if (node.IsStar)
        {
            if (node.StarQualifier != null)
                return $"{TranslateTableName(node.StarQualifier)}.*";
            return "*";
        }
        var expr = Expr(node.Expression!);
        return node.Alias != null ? $"{expr} AS {QuoteIdent(node.Alias)}" : expr;
    }

    public string VisitIntoClause(IntoClause node) => TranslateTableName(node.Table);

    public string VisitOrderByItem(OrderByItem node)
    {
        var dir = node.Direction == SortDirection.Descending ? " DESC" : "";
        return Expr(node.Expression) + dir;
    }

    public string VisitOffsetFetch(OffsetFetchClause node) => string.Empty; // handled inline

    public string VisitSetOperator(SetOperator node)
    {
        var op = node.Kind switch
        {
            SetOperatorKind.Union     => "UNION",
            SetOperatorKind.Intersect => "INTERSECT",
            SetOperatorKind.Except    => "EXCEPT",
            _                         => "UNION"
        };
        if (node.All) op += " ALL";
        return $"{op} {V(node.Right)}";
    }

    // =========================================================================
    // INSERT
    // =========================================================================

    public string VisitInsert(InsertStatement node)
    {
        var sb = new StringBuilder("INSERT INTO ");
        sb.Append(TranslateTableName(node.Target));

        if (node.Columns.Count > 0)
            sb.Append(" (").Append(string.Join(", ", node.Columns.Select(QuoteIdent))).Append(')');

        if (node.ValuesSource != null)
            sb.Append(' ').Append(V(node.ValuesSource));
        else if (node.SelectSource != null)
            sb.Append(' ').Append(V(node.SelectSource));

        return sb.ToString();
    }

    public string VisitValuesClause(ValuesClause node)
    {
        var rows = node.RowValues.Select(row =>
            "(" + string.Join(", ", row.Select(Expr)) + ")");
        return "VALUES " + string.Join(", ", rows);
    }

    // =========================================================================
    // UPDATE
    // =========================================================================

    public string VisitUpdate(UpdateStatement node)
    {
        var sb = new StringBuilder("UPDATE ");
        sb.Append(TranslateTableName(node.Target));
        if (node.TargetAlias != null)
            sb.Append(" AS ").Append(QuoteIdent(node.TargetAlias));

        sb.Append(" SET ");
        sb.Append(string.Join(", ", node.Sets.Select(V)));

        // PostgreSQL UPDATE FROM syntax
        if (node.From.Count > 0)
        {
            sb.Append(" FROM ");
            sb.Append(string.Join(", ", node.From.Select(V)));
        }

        if (node.Where != null)
            sb.Append(" WHERE ").Append(Expr(node.Where));

        return sb.ToString();
    }

    public string VisitSetClause(SetClause node)
        => $"{QuoteIdent(node.ColumnName)} = {Expr(node.Value)}";

    // =========================================================================
    // DELETE
    // =========================================================================

    public string VisitDelete(DeleteStatement node)
    {
        var sb = new StringBuilder("DELETE FROM ");
        sb.Append(TranslateTableName(node.Target));

        if (node.From.Count > 0)
        {
            sb.Append(" USING ");
            sb.Append(string.Join(", ", node.From.Select(V)));
        }

        if (node.Where != null)
            sb.Append(" WHERE ").Append(Expr(node.Where));

        return sb.ToString();
    }

    public string VisitOutputClause(OutputClause node)
    {
        Warn("PG001", "OUTPUT clause is not fully supported; using RETURNING.");
        return "RETURNING *";
    }

    // =========================================================================
    // DDL
    // =========================================================================

    public string VisitTruncateTable(TruncateTableStatement node)
        => $"TRUNCATE TABLE {TranslateTableName(node.Table)}";

    public string VisitCreateTable(CreateTableStatement node)
    {
        var sb = new StringBuilder("CREATE ");
        if (node.IsTemporary) sb.Append("TEMPORARY ");
        sb.Append("TABLE ");
        sb.Append(TranslateTempTableName(node.Table));
        sb.Append(" (\n");
        sb.Append(string.Join(",\n", node.Columns.Select(c => "  " + V(c))));
        sb.Append("\n)");
        return sb.ToString();
    }

    public string VisitColumnDefinition(ColumnDefinition node)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteIdent(node.Name)).Append(' ');

        if (node.IsIdentity)
        {
            // Use GENERATED ALWAYS AS IDENTITY instead of SERIAL
            string baseType = TypeMap.Translate(node.DataType.TypeName,
                node.DataType.Length, node.DataType.IsMax,
                node.DataType.Precision, node.DataType.Scale);
            sb.Append(baseType);
            sb.Append(" GENERATED ALWAYS AS IDENTITY");
            if (node.IdentitySeed != 1 || node.IdentityIncrement != 1)
                sb.Append($" (START WITH {node.IdentitySeed} INCREMENT BY {node.IdentityIncrement})");
        }
        else
        {
            sb.Append(V(node.DataType));
        }

        sb.Append(node.IsNullable ? " NULL" : " NOT NULL");
        if (node.IsPrimaryKey) sb.Append(" PRIMARY KEY");
        if (node.IsUnique) sb.Append(" UNIQUE");
        if (node.DefaultValue != null) sb.Append(" DEFAULT ").Append(Expr(node.DefaultValue));
        return sb.ToString();
    }

    public string VisitDropTable(DropTableStatement node)
    {
        var tables = string.Join(", ", node.Tables.Select(TranslateTableName));
        return node.IfExists
            ? $"DROP TABLE IF EXISTS {tables}"
            : $"DROP TABLE {tables}";
    }

    // =========================================================================
    // Transactions
    // =========================================================================

    public string VisitBeginTransaction(BeginTransactionStatement node) => "BEGIN";
    public string VisitCommitTransaction(CommitTransactionStatement node) => "COMMIT";
    public string VisitRollbackTransaction(RollbackTransactionStatement node)
    {
        if (node.TransactionName != null)
            return $"ROLLBACK TO SAVEPOINT {QuoteIdent(node.TransactionName)}";
        return "ROLLBACK";
    }
    public string VisitSaveTransaction(SaveTransactionStatement node)
        => $"SAVEPOINT {QuoteIdent(node.SavepointName)}";

    // =========================================================================
    // SET / USE / EXEC
    // =========================================================================

    public string VisitSetOption(SetOptionStatement node)
    {
        // Most T-SQL SET options have no PostgreSQL equivalent.
        // SET NOCOUNT, ANSI_NULLS, QUOTED_IDENTIFIER — silently consumed.
        string opt = node.OptionName.ToUpperInvariant();

        // Variable assignment: SET @var = expr
        if (opt.StartsWith('@'))
            return $"{opt} := {(node.Value != null ? Expr(node.Value) : (node.IsOn ? "TRUE" : "FALSE"))}";

        // Known no-ops in PostgreSQL
        if (opt is "NOCOUNT" or "ANSI_NULLS" or "ANSI_PADDING" or "ANSI_WARNINGS"
            or "QUOTED_IDENTIFIER" or "CONCAT_NULL_YIELDS_NULL" or "ARITHABORT"
            or "ARITH_ABORT" or "XACT_ABORT")
        {
            return $"-- SET {opt} {(node.IsOn ? "ON" : "OFF")} (no-op in PostgreSQL)";
        }

        if (opt == "TRANSACTION ISOLATION LEVEL" && node.Value != null)
        {
            return $"SET TRANSACTION ISOLATION LEVEL {Expr(node.Value)}";
        }

        Warn("PG002", $"SET {opt} is not translated and may not behave correctly.");
        return $"-- SET {opt} (unsupported)";
    }

    public string VisitUseDatabase(UseDatabaseStatement node)
    {
        // PostgreSQL doesn't support USE; schema search_path is the closest.
        Warn("PG003", $"USE {node.DatabaseName} cannot be translated. Set search_path instead.");
        return $"SET search_path TO {QuoteIdent(node.DatabaseName)}, public";
    }

    public string VisitExecute(ExecuteStatement node)
    {
        var procName = TranslateTableName(node.ProcedureName);
        if (node.Arguments.Count == 0)
            return $"SELECT * FROM {procName}()";

        var args = string.Join(", ", node.Arguments.Select(V));
        return $"SELECT * FROM {procName}({args})";
    }

    public string VisitProcedureArgument(ProcedureArgument node) => Expr(node.Value);

    // =========================================================================
    // Control flow
    // =========================================================================

    public string VisitIf(IfStatement node)
    {
        // PostgreSQL anonymous DO block for IF/ELSE
        var sb = new StringBuilder("DO $$ BEGIN\n");
        sb.Append("IF ").Append(Expr(node.Condition)).Append(" THEN\n");
        sb.Append("  ").Append(V(node.ThenBranch)).Append(";\n");
        if (node.ElseBranch != null)
            sb.Append("ELSE\n  ").Append(V(node.ElseBranch)).Append(";\n");
        sb.Append("END IF;\nEND $$");
        return sb.ToString();
    }

    public string VisitWhile(WhileStatement node)
    {
        var sb = new StringBuilder("DO $$ BEGIN\n");
        sb.Append("WHILE ").Append(Expr(node.Condition)).Append(" LOOP\n");
        sb.Append("  ").Append(V(node.Body)).Append(";\n");
        sb.Append("END LOOP;\nEND $$");
        return sb.ToString();
    }

    public string VisitBeginEnd(BeginEndBlock node)
    {
        var stmts = node.Statements.Select(s => V(s));
        return string.Join(";\n", stmts);
    }

    public string VisitPrint(PrintStatement node)
        => $"RAISE NOTICE '%', {Expr(node.Expression)}";

    public string VisitReturn(ReturnStatement node)
        => node.Value != null ? $"RETURN {Expr(node.Value)}" : "RETURN";

    public string VisitDeclare(DeclareStatement node)
    {
        // DECLARE in PG is only valid inside DO/function blocks.
        // Emit as a comment + placeholder.
        var decls = node.Declarations.Select(V);
        return $"-- DECLARE {string.Join(", ", decls)}";
    }

    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        var type = V(node.DataType);
        var init = node.InitialValue != null ? $" := {Expr(node.InitialValue)}" : "";
        return $"{node.Name} {type}{init}";
    }

    public string VisitUnparsed(UnparsedStatement node)
    {
        Warn("PG004", $"Statement could not be parsed: {node.Reason}");
        return $"-- UNPARSED: {node.RawSql}";
    }

    // =========================================================================
    // Expressions — Literals
    // =========================================================================

    public string VisitIntegerLiteral(IntegerLiteralExpression node) => node.Value.ToString();
    public string VisitDecimalLiteral(DecimalLiteralExpression node) => node.RawText.Length > 0 ? node.RawText : node.Value.ToString();
    public string VisitFloatLiteral(FloatLiteralExpression node) => node.RawText.Length > 0 ? node.RawText : node.Value.ToString();
    public string VisitStringLiteral(StringLiteralExpression node) => $"'{EscapeString(node.Value)}'";
    public string VisitNullLiteral(NullLiteralExpression node) => "NULL";
    public string VisitBooleanLiteral(BooleanLiteralExpression node) => node.Value ? "TRUE" : "FALSE";

    // =========================================================================
    // Expressions — Names
    // =========================================================================

    public string VisitObjectName(ObjectName node) => TranslateTableName(node);

    public string VisitColumnReference(ColumnReferenceExpression node)
    {
        if (node.TableAlias != null)
            return $"{QuoteIdent(node.TableAlias)}.{QuoteIdent(node.ColumnName)}";
        return QuoteIdent(node.ColumnName);
    }

    public string VisitParameter(ParameterExpression node)
    {
        // Keep @param syntax — the execution layer maps to Npgsql $N
        return node.Name;
    }

    public string VisitGlobalVariable(GlobalVariableExpression node)
    {
        var upper = node.Name.ToUpperInvariant();
        if (TsqlFunctionMap.GlobalVariables.TryGetValue(upper, out var pgExpr))
            return pgExpr;
        Warn("PG005", $"Global variable {node.Name} has no known PostgreSQL equivalent.");
        return $"NULL /* {node.Name} */";
    }

    // =========================================================================
    // Expressions — Operators
    // =========================================================================

    public string VisitBinary(BinaryExpression node)
    {
        string left  = Expr(node.Left);
        string right = Expr(node.Right);

        // T-SQL string concatenation with + → PG ||
        if (node.Operator == BinaryOperator.Add && LooksLikeString(node.Left))
            return $"({left} || {right})";

        string op = node.Operator switch
        {
            BinaryOperator.Add                => "+",
            BinaryOperator.Subtract           => "-",
            BinaryOperator.Multiply           => "*",
            BinaryOperator.Divide             => "/",
            BinaryOperator.Modulo             => "%",
            BinaryOperator.BitwiseAnd         => "&",
            BinaryOperator.BitwiseOr          => "|",
            BinaryOperator.BitwiseXor         => "#",  // PG uses # for XOR
            BinaryOperator.Equal              => "=",
            BinaryOperator.NotEqual           => "<>",
            BinaryOperator.LessThan           => "<",
            BinaryOperator.GreaterThan        => ">",
            BinaryOperator.LessThanOrEqual    => "<=",
            BinaryOperator.GreaterThanOrEqual => ">=",
            BinaryOperator.And                => "AND",
            BinaryOperator.Or                 => "OR",
            BinaryOperator.StringConcat       => "||",
            _                                 => "="
        };

        bool isLogical = node.Operator is BinaryOperator.And or BinaryOperator.Or;
        return isLogical ? $"({left} {op} {right})" : $"{left} {op} {right}";
    }

    public string VisitUnary(UnaryExpression node)
    {
        string operand = Expr(node.Operand);
        return node.Operator switch
        {
            UnaryOperator.Negate    => $"-{operand}",
            UnaryOperator.Not       => $"NOT {operand}",
            UnaryOperator.BitwiseNot => $"~{operand}",
            _                        => operand
        };
    }

    // =========================================================================
    // Expressions — Predicates
    // =========================================================================

    public string VisitBetween(BetweenExpression node)
    {
        string not = node.IsNot ? "NOT " : "";
        return $"{Expr(node.Value)} {not}BETWEEN {Expr(node.Low)} AND {Expr(node.High)}";
    }

    public string VisitInList(InListExpression node)
    {
        string not = node.IsNot ? "NOT " : "";
        var items = string.Join(", ", node.Items.Select(Expr));
        return $"{Expr(node.Value)} {not}IN ({items})";
    }

    public string VisitInSubquery(InSubqueryExpression node)
    {
        string not = node.IsNot ? "NOT " : "";
        return $"{Expr(node.Value)} {not}IN ({V(node.Subquery)})";
    }

    public string VisitLike(LikeExpression node)
    {
        string not = node.IsNot ? "NOT " : "";
        var result = $"{Expr(node.Value)} {not}LIKE {Expr(node.Pattern)}";
        if (node.Escape != null) result += $" ESCAPE {Expr(node.Escape)}";
        return result;
    }

    public string VisitIsNull(IsNullExpression node)
    {
        string not = node.IsNot ? " IS NOT NULL" : " IS NULL";
        return Expr(node.Value) + not;
    }

    public string VisitExists(ExistsExpression node)
    {
        string not = node.IsNot ? "NOT " : "";
        return $"{not}EXISTS ({V(node.Subquery)})";
    }

    // =========================================================================
    // Expressions — Functions
    // =========================================================================

    public string VisitFunctionCall(FunctionCallExpression node)
    {
        string funcName = node.Name.Name.ToUpperInvariant();
        var args = node.Arguments;

        // Special-case function translations
        if (TsqlFunctionMap.SpecialFunctions.Contains(funcName))
            return TranslateSpecialFunction(funcName, args, node);

        // Direct rename
        if (TsqlFunctionMap.DirectRenames.TryGetValue(funcName, out var pgName))
        {
            // Some "renames" include full expressions (e.g. GETUTCDATE → NOW() AT TIME ZONE 'UTC')
            if (pgName.Contains('('))
                return pgName; // already has parens

            funcName = pgName;
        }

        // Standard function call
        var sb = new StringBuilder(funcName);
        sb.Append('(');
        if (node.Distinct) sb.Append("DISTINCT ");
        sb.Append(string.Join(", ", args.Select(Expr)));
        sb.Append(')');

        if (node.Over != null) sb.Append(' ').Append(V(node.Over));

        return sb.ToString();
    }

    private string TranslateSpecialFunction(string funcName, IReadOnlyList<SqlExpression> args, FunctionCallExpression node)
    {
        switch (funcName)
        {
            case "ISNULL":
                // ISNULL(a, b) → COALESCE(a, b)
                return $"COALESCE({Expr(args[0])}, {Expr(args[1])})";

            case "CHARINDEX":
                // CHARINDEX(sub, str) → POSITION(sub IN str)
                if (args.Count >= 2)
                    return $"POSITION({Expr(args[0])} IN {Expr(args[1])})";
                return $"POSITION({Expr(args[0])})";

            case "SUBSTRING":
                // SUBSTRING(s, start, len) → SUBSTRING(s FROM start FOR len)
                if (args.Count >= 3)
                    return $"SUBSTRING({Expr(args[0])} FROM {Expr(args[1])} FOR {Expr(args[2])})";
                if (args.Count >= 2)
                    return $"SUBSTRING({Expr(args[0])} FROM {Expr(args[1])})";
                return $"SUBSTRING({Expr(args[0])})";

            case "STUFF":
                // STUFF(s, start, len, replacement) → OVERLAY(s PLACING replacement FROM start FOR len)
                if (args.Count >= 4)
                    return $"OVERLAY({Expr(args[0])} PLACING {Expr(args[3])} FROM {Expr(args[1])} FOR {Expr(args[2])})";
                break;

            case "DATEADD":
                // DATEADD(part, n, date) → date + INTERVAL 'n part'
                if (args.Count >= 3)
                {
                    string part = ExtractDatePart(args[0]);
                    return $"({Expr(args[2])} + ({Expr(args[1])}) * INTERVAL '1 {part}')";
                }
                break;

            case "DATEDIFF":
                // DATEDIFF(part, start, end) → EXTRACT(EPOCH FROM end - start) / divisor
                if (args.Count >= 3)
                {
                    string part = ExtractDatePart(args[0]);
                    string divisor = part switch
                    {
                        "second" => "1",
                        "minute" => "60",
                        "hour"   => "3600",
                        "day"    => "86400",
                        _        => "1"
                    };
                    return $"EXTRACT(EPOCH FROM ({Expr(args[2])} - {Expr(args[1])}))::INTEGER / {divisor}";
                }
                break;

            case "DATEPART":
                // DATEPART(part, date) → EXTRACT(part FROM date)
                if (args.Count >= 2)
                {
                    string part = ExtractDatePart(args[0]);
                    return $"EXTRACT({part} FROM {Expr(args[1])})::INTEGER";
                }
                break;

            case "DATENAME":
                // DATENAME(part, date) → TO_CHAR(date, format)
                if (args.Count >= 2)
                {
                    string part = ExtractDatePart(args[0]);
                    string fmt = part switch
                    {
                        "month"   => "'Month'",
                        "weekday" => "'Day'",
                        _         => $"'{part}'"
                    };
                    return $"TO_CHAR({Expr(args[1])}, {fmt})";
                }
                break;

            case "SPACE":
                // SPACE(n) → REPEAT(' ', n)
                return $"REPEAT(' ', {Expr(args[0])})";

            case "SQUARE":
                // SQUARE(x) → POWER(x, 2)
                return $"POWER({Expr(args[0])}, 2)";

            case "SCOPE_IDENTITY":
                return "lastval()";

            case "OBJECT_ID":
                if (args.Count >= 1)
                    return $"to_regclass({Expr(args[0])})::OID";
                break;
        }

        // Fallback: emit as-is with warning
        Warn("PG006", $"Function {funcName} could not be translated; emitting as-is.");
        var argList = string.Join(", ", args.Select(Expr));
        var sb2 = new StringBuilder(funcName).Append('(').Append(argList).Append(')');
        if (node.Over != null) sb2.Append(' ').Append(V(node.Over));
        return sb2.ToString();
    }

    public string VisitOverClause(OverClause node)
    {
        var sb = new StringBuilder("OVER (");
        if (node.PartitionBy.Count > 0)
            sb.Append("PARTITION BY ").Append(string.Join(", ", node.PartitionBy.Select(Expr)));
        if (node.OrderBy.Count > 0)
        {
            if (node.PartitionBy.Count > 0) sb.Append(' ');
            sb.Append("ORDER BY ").Append(string.Join(", ", node.OrderBy.Select(V)));
        }
        if (node.Frame != null) sb.Append(' ').Append(V(node.Frame));
        sb.Append(')');
        return sb.ToString();
    }

    public string VisitWindowFrame(WindowFrame node)
    {
        var unit = node.Unit == WindowFrameUnit.Rows ? "ROWS" : "RANGE";
        var sb = new StringBuilder(unit).Append(" BETWEEN ").Append(V(node.Start));
        if (node.End != null) sb.Append(" AND ").Append(V(node.End));
        return sb.ToString();
    }

    public string VisitWindowFrameBound(WindowFrameBound node) => node.Kind switch
    {
        WindowFrameBoundKind.UnboundedPreceding => "UNBOUNDED PRECEDING",
        WindowFrameBoundKind.Preceding          => $"{Expr(node.Offset)} PRECEDING",
        WindowFrameBoundKind.CurrentRow         => "CURRENT ROW",
        WindowFrameBoundKind.Following          => $"{Expr(node.Offset)} FOLLOWING",
        WindowFrameBoundKind.UnboundedFollowing => "UNBOUNDED FOLLOWING",
        _                                       => "CURRENT ROW"
    };

    // =========================================================================
    // Expressions — CAST / CONVERT / CASE
    // =========================================================================

    public string VisitCast(CastExpression node)
        => $"CAST({Expr(node.Value)} AS {V(node.TargetType)})";

    public string VisitConvert(ConvertExpression node)
    {
        // CONVERT(type, value, style?) → CAST(value AS type) or TO_CHAR for date styles
        if (node.Style != null)
        {
            // Date style conversion: emit TO_CHAR for string output
            var typeUpper = node.TargetType.TypeName.ToUpperInvariant();
            if (typeUpper is "VARCHAR" or "NVARCHAR" or "CHAR" or "NCHAR")
            {
                string format = TranslateDateStyle(node.Style);
                return $"TO_CHAR({Expr(node.Value)}, {format})";
            }
        }
        // Standard CAST
        return $"CAST({Expr(node.Value)} AS {V(node.TargetType)})";
    }

    public string VisitCase(CaseExpression node)
    {
        var sb = new StringBuilder("CASE");
        if (node.InputExpression != null) sb.Append(' ').Append(Expr(node.InputExpression));
        foreach (var w in node.WhenClauses)
            sb.Append(" WHEN ").Append(Expr(w.Condition)).Append(" THEN ").Append(Expr(w.Result));
        if (node.ElseExpression != null)
            sb.Append(" ELSE ").Append(Expr(node.ElseExpression));
        sb.Append(" END");
        return sb.ToString();
    }

    public string VisitWhenClause(WhenClause node) => string.Empty; // handled inline in VisitCase

    public string VisitSubquery(SubqueryExpression node) => $"({V(node.Query)})";

    public string VisitDataType(DataTypeNode node)
        => TypeMap.Translate(node.TypeName, node.Length, node.IsMax, node.Precision, node.Scale);

    // =========================================================================
    // Table sources
    // =========================================================================

    public string VisitTableReference(TableReferenceSource node)
    {
        var sb = new StringBuilder(TranslateTableName(node.Name));
        if (node.Alias != null) sb.Append(" AS ").Append(QuoteIdent(node.Alias));
        // Table hints are stripped (no PG equivalent)
        if (node.Hints.Count > 0)
        {
            foreach (var h in node.Hints)
                Warn("PG007", $"Table hint '{h.HintName}' stripped (no PostgreSQL equivalent).");
        }
        return sb.ToString();
    }

    public string VisitSubquerySource(SubquerySource node)
        => $"({V(node.Query)}) AS {QuoteIdent(node.Alias)}";

    public string VisitJoin(JoinedSource node)
    {
        string joinKw = node.JoinType switch
        {
            JoinType.Inner      => "INNER JOIN",
            JoinType.LeftOuter  => "LEFT OUTER JOIN",
            JoinType.RightOuter => "RIGHT OUTER JOIN",
            JoinType.FullOuter  => "FULL OUTER JOIN",
            JoinType.Cross      => "CROSS JOIN",
            JoinType.CrossApply => "CROSS JOIN LATERAL",
            JoinType.OuterApply => "LEFT JOIN LATERAL",
            _                   => "JOIN"
        };
        var sb = new StringBuilder(V(node.Left));
        sb.Append(' ').Append(joinKw).Append(' ').Append(V(node.Right));
        if (node.Condition != null) sb.Append(" ON ").Append(Expr(node.Condition));
        else if (node.JoinType is JoinType.OuterApply) sb.Append(" ON TRUE");
        return sb.ToString();
    }

    public string VisitTableHint(TableHint node) => string.Empty;

    // =========================================================================
    // Helpers
    // =========================================================================

    private string TranslateTableName(ObjectName name)
    {
        // #temp → pg_temp.temp_name (strip the # prefix)
        if (name.IsTemporaryTable)
        {
            var pgName = name.Name.TrimStart('#');
            return $"pg_temp.{QuoteIdent(pgName)}";
        }

        // Strip server and database qualifiers (PG doesn't support cross-db queries)
        if (name.Schema != null)
            return $"{QuoteIdent(name.Schema)}.{QuoteIdent(name.Name)}";

        return QuoteIdent(name.Name);
    }

    private string TranslateTempTableName(ObjectName name)
    {
        // For CREATE TEMPORARY TABLE, don't schema-qualify
        if (name.IsTemporaryTable)
            return QuoteIdent(name.Name.TrimStart('#'));
        return TranslateTableName(name);
    }

    private static string QuoteIdent(string name)
    {
        // Only quote if needed (contains special chars, is a PG keyword, etc.)
        // For safety and correctness, always lowercase and quote
        if (NeedsQuoting(name))
            return $"\"{name.Replace("\"", "\"\"")}\"";
        return name;
    }

    private static bool NeedsQuoting(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        // If it has spaces, starts with a digit, or is mixed case, quote it
        if (name.Contains(' ') || char.IsDigit(name[0])) return true;
        // Don't force-quote simple identifiers — let PG case-fold naturally
        return false;
    }

    private static string EscapeString(string s) => s.Replace("'", "''");

    private static bool LooksLikeString(SqlExpression expr) =>
        expr is StringLiteralExpression
        || (expr is ColumnReferenceExpression)  // can't definitively know; assume yes for + between strings
        || (expr is FunctionCallExpression fn && fn.Name.Name.ToUpperInvariant() is
            "LEFT" or "RIGHT" or "UPPER" or "LOWER" or "LTRIM" or "RTRIM" or "REPLACE"
            or "SUBSTRING" or "CONCAT" or "CONCAT_WS" or "FORMAT" or "REPLICATE" or "REVERSE");

    private static string ExtractDatePart(SqlExpression partExpr)
    {
        // Date parts arrive as identifiers (not strings) from the parser
        string raw = partExpr switch
        {
            ColumnReferenceExpression col => col.ColumnName,
            StringLiteralExpression str   => str.Value,
            _                             => "day"
        };
        return raw.ToLowerInvariant() switch
        {
            "yy" or "yyyy" or "year"     => "year",
            "qq" or "q" or "quarter"     => "quarter",
            "mm" or "m" or "month"       => "month",
            "dy" or "y" or "dayofyear"   => "doy",
            "dd" or "d" or "day"         => "day",
            "wk" or "ww" or "week"       => "week",
            "dw" or "weekday"            => "dow",
            "hh" or "hour"               => "hour",
            "mi" or "n" or "minute"      => "minute",
            "ss" or "s" or "second"      => "second",
            "ms" or "millisecond"        => "milliseconds",
            "mcs" or "microsecond"       => "microseconds",
            _                            => raw.ToLowerInvariant()
        };
    }

    private string TranslateDateStyle(SqlExpression styleExpr)
    {
        // CONVERT style numbers → TO_CHAR format strings
        if (styleExpr is IntegerLiteralExpression intLit)
        {
            return intLit.Value switch
            {
                101 => "'MM/DD/YYYY'",
                102 => "'YYYY.MM.DD'",
                103 => "'DD/MM/YYYY'",
                104 => "'DD.MM.YYYY'",
                105 => "'DD-MM-YYYY'",
                108 => "'HH24:MI:SS'",
                112 => "'YYYYMMDD'",
                120 or 20 => "'YYYY-MM-DD HH24:MI:SS'",
                121 or 21 => "'YYYY-MM-DD HH24:MI:SS.MS'",
                126 => "'YYYY-MM-DD\"T\"HH24:MI:SS'",
                _   => $"'YYYY-MM-DD HH24:MI:SS' /* style {intLit.Value} */"
            };
        }
        return "'YYYY-MM-DD HH24:MI:SS'";
    }
}
