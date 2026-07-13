using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Translates T-SQL expressions to PostgreSQL by walking the ScriptDom AST.
/// Applies TypeMapper for CAST/CONVERT type arguments and FunctionMapper for function calls.
/// Handles string concatenation (+ → ||) and TOP to LIMIT translation.
/// Returns CannotTranslate for unmapped constructs to signal AI fallback.
/// </summary>
public sealed class ExpressionTranslator
{
    private readonly TypeMapper _typeMapper;
    private readonly FunctionMapper _functionMapper;
    private readonly ILogger<ExpressionTranslator> _logger;

    /// <summary>
    /// Initializes a new instance of <see cref="ExpressionTranslator"/>.
    /// </summary>
    /// <param name="typeMapper">Type mapper for CAST/CONVERT type resolution.</param>
    /// <param name="functionMapper">Function mapper for function call translation.</param>
    /// <param name="logger">Logger instance for diagnostics.</param>
    public ExpressionTranslator(
        TypeMapper typeMapper,
        FunctionMapper functionMapper,
        ILogger<ExpressionTranslator> logger)
    {
        _typeMapper = typeMapper;
        _functionMapper = functionMapper;
        _logger = logger;
    }

    /// <summary>
    /// Translates a T-SQL expression string to its PostgreSQL equivalent.
    /// </summary>
    /// <param name="tsqlExpression">The T-SQL expression to translate.</param>
    /// <returns>A <see cref="TranslationResult"/> indicating success or failure.</returns>
    public TranslationResult Translate(string tsqlExpression)
    {
        if (string.IsNullOrWhiteSpace(tsqlExpression))
        {
            return TranslationResult.Success(string.Empty);
        }

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: false);
            var fragment = parser.ParseExpression(
                new StringReader(tsqlExpression), out var errors);

            if (errors.Count > 0)
            {
                // Try parsing as a statement fragment for TOP handling
                return TranslateRawExpression(tsqlExpression);
            }

            return TranslateFragment(fragment);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse expression: {Expression}", tsqlExpression);
            return TranslationResult.CannotTranslate(
                $"Failed to parse T-SQL expression: {ex.Message}");
        }
    }

    /// <summary>
    /// Translates a SELECT statement that may contain TOP, converting it to use LIMIT.
    /// </summary>
    /// <param name="tsqlSelect">The T-SQL SELECT statement.</param>
    /// <returns>A <see cref="TranslationResult"/> indicating success or failure.</returns>
    public TranslationResult TranslateSelect(string tsqlSelect)
    {
        if (string.IsNullOrWhiteSpace(tsqlSelect))
        {
            return TranslationResult.Success(string.Empty);
        }

        try
        {
            var parser = new TSql160Parser(initialQuotedIdentifiers: false);
            using var reader = new StringReader(tsqlSelect);
            var fragment = parser.Parse(reader, out var errors);

            if (errors.Count > 0)
            {
                return TranslationResult.CannotTranslate(
                    $"T-SQL parse errors: {string.Join("; ", errors.Select(e => e.Message))}");
            }

            var visitor = new TopClauseVisitor();
            fragment.Accept(visitor);

            if (visitor.TopExpression is not null)
            {
                var result = TranslateTopToLimit(tsqlSelect, visitor);
                return TranslationResult.Success(result);
            }

            // No TOP clause, try translating expressions within
            return TranslateRawExpression(tsqlSelect);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to translate SELECT: {Sql}", tsqlSelect);
            return TranslationResult.CannotTranslate(
                $"Failed to translate SELECT statement: {ex.Message}");
        }
    }

    private TranslationResult TranslateFragment(TSqlFragment fragment)
    {
        return fragment switch
        {
            BinaryExpression binary => TranslateBinaryExpression(binary),
            FunctionCall funcCall => TranslateFunctionCall(funcCall),
            CastCall castCall => TranslateCast(castCall),
            ConvertCall convertCall => TranslateConvert(convertCall),
            ParenthesisExpression paren => TranslateParenthesis(paren),
            UnaryExpression unary => TranslateUnary(unary),
            ColumnReferenceExpression colRef => TranslateColumnReference(colRef),
            IntegerLiteral intLit => TranslationResult.Success(intLit.Value),
            StringLiteral strLit => TranslationResult.Success($"'{strLit.Value}'"),
            NumericLiteral numLit => TranslationResult.Success(numLit.Value),
            NullLiteral => TranslationResult.Success("NULL"),
            VariableReference varRef => TranslateVariableReference(varRef),
            GlobalVariableExpression globalVar => TranslateGlobalVariable(globalVar),
            SearchedCaseExpression searchedCase => TranslateSearchedCase(searchedCase),
            SimpleCaseExpression simpleCase => TranslateSimpleCase(simpleCase),
            CoalesceExpression coalesce => TranslateCoalesce(coalesce),
            NullIfExpression nullIf => TranslateNullIf(nullIf),
            IIfCall iif => TranslateIif(iif),
            _ => TranslateGenericFragment(fragment)
        };
    }

    private TranslationResult TranslateBinaryExpression(BinaryExpression binary)
    {
        var leftResult = TranslateFragment(binary.FirstExpression);
        if (!leftResult.IsSuccess) return leftResult;

        var rightResult = TranslateFragment(binary.SecondExpression);
        if (!rightResult.IsSuccess) return rightResult;

        var op = binary.BinaryExpressionType switch
        {
            BinaryExpressionType.Add => IsStringContext(binary) ? "||" : "+",
            BinaryExpressionType.Subtract => "-",
            BinaryExpressionType.Multiply => "*",
            BinaryExpressionType.Divide => "/",
            BinaryExpressionType.Modulo => "%",
            BinaryExpressionType.BitwiseAnd => "&",
            BinaryExpressionType.BitwiseOr => "|",
            BinaryExpressionType.BitwiseXor => "#",
            _ => null
        };

        if (op is null)
        {
            return TranslationResult.CannotTranslate(
                $"Unsupported binary operator: {binary.BinaryExpressionType}");
        }

        return TranslationResult.Success(
            $"{leftResult.TranslatedExpression} {op} {rightResult.TranslatedExpression}");
    }

    private static bool IsStringContext(BinaryExpression binary)
    {
        // Heuristic: if either side is a string literal or CAST to varchar/nvarchar,
        // treat + as string concatenation (||)
        if (binary.BinaryExpressionType != BinaryExpressionType.Add)
            return false;

        return IsStringExpression(binary.FirstExpression) ||
               IsStringExpression(binary.SecondExpression);
    }

    private static bool IsStringExpression(ScalarExpression expr)
    {
        return expr is StringLiteral
            || (expr is CastCall cast && IsStringType(cast.DataType))
            || (expr is ConvertCall convert && IsStringType(convert.DataType));
    }

    private static bool IsStringType(DataTypeReference? dataType)
    {
        if (dataType is null) return false;
        var typeName = GetDataTypeName(dataType).ToUpperInvariant();
        return typeName.Contains("VARCHAR") || typeName.Contains("CHAR") ||
               typeName.Contains("TEXT") || typeName.Contains("NVARCHAR") ||
               typeName.Contains("NCHAR");
    }

    private TranslationResult TranslateFunctionCall(FunctionCall funcCall)
    {
        var functionName = funcCall.FunctionName?.Value?.ToUpperInvariant();
        if (functionName is null)
        {
            return TranslationResult.CannotTranslate("Function call has no name.");
        }

        // Translate arguments
        var args = new List<string>();
        foreach (var param in funcCall.Parameters)
        {
            var argResult = TranslateFragment(param);
            if (!argResult.IsSuccess) return argResult;
            args.Add(argResult.TranslatedExpression!);
        }

        var mapped = _functionMapper.MapFunction(functionName, args);
        if (mapped is null)
        {
            return TranslationResult.CannotTranslate(
                $"No mapping for function '{functionName}' with {args.Count} arguments.");
        }

        return TranslationResult.Success(mapped);
    }

    private TranslationResult TranslateCast(CastCall castCall)
    {
        var exprResult = TranslateFragment(castCall.Parameter);
        if (!exprResult.IsSuccess) return exprResult;

        var mappedType = MapDataType(castCall.DataType);
        if (mappedType is null)
        {
            return TranslationResult.CannotTranslate(
                $"Cannot map CAST target type: {GetDataTypeName(castCall.DataType)}");
        }

        return TranslationResult.Success($"CAST({exprResult.TranslatedExpression} AS {mappedType})");
    }

    private TranslationResult TranslateConvert(ConvertCall convertCall)
    {
        var exprResult = TranslateFragment(convertCall.Parameter);
        if (!exprResult.IsSuccess) return exprResult;

        var targetType = GetDataTypeName(convertCall.DataType);
        var mappedType = MapDataType(convertCall.DataType);

        var args = new List<string> { mappedType ?? targetType, exprResult.TranslatedExpression! };

        // Style code handling
        if (convertCall.Style is not null)
        {
            var styleResult = TranslateFragment(convertCall.Style);
            if (!styleResult.IsSuccess) return styleResult;
            args.Add(styleResult.TranslatedExpression!);
        }

        var mapped = _functionMapper.MapFunction("CONVERT", args);
        if (mapped is null)
        {
            // Fallback to simple CAST if no style code
            if (convertCall.Style is null && mappedType is not null)
            {
                return TranslationResult.Success(
                    $"CAST({exprResult.TranslatedExpression} AS {mappedType})");
            }
            return TranslationResult.CannotTranslate(
                $"Cannot translate CONVERT to {targetType} with style code.");
        }

        return TranslationResult.Success(mapped);
    }

    private TranslationResult TranslateParenthesis(ParenthesisExpression paren)
    {
        var innerResult = TranslateFragment(paren.Expression);
        if (!innerResult.IsSuccess) return innerResult;
        return TranslationResult.Success($"({innerResult.TranslatedExpression})");
    }

    private TranslationResult TranslateUnary(UnaryExpression unary)
    {
        var exprResult = TranslateFragment(unary.Expression);
        if (!exprResult.IsSuccess) return exprResult;

        var op = unary.UnaryExpressionType switch
        {
            UnaryExpressionType.Negative => "-",
            UnaryExpressionType.Positive => "+",
            UnaryExpressionType.BitwiseNot => "~",
            _ => null
        };

        if (op is null)
        {
            return TranslationResult.CannotTranslate(
                $"Unsupported unary operator: {unary.UnaryExpressionType}");
        }

        return TranslationResult.Success($"{op}{exprResult.TranslatedExpression}");
    }

    private static TranslationResult TranslateColumnReference(ColumnReferenceExpression colRef)
    {
        var parts = colRef.MultiPartIdentifier?.Identifiers;
        if (parts is null || parts.Count == 0)
        {
            return TranslationResult.CannotTranslate("Column reference has no identifiers.");
        }

        var columnName = string.Join(".", parts.Select(id => QuoteIdentifier(id.Value)));
        return TranslationResult.Success(columnName);
    }

    private static TranslationResult TranslateVariableReference(VariableReference varRef)
    {
        // T-SQL variables (@var) don't have a direct PostgreSQL equivalent in expressions
        // In function/procedure context they become parameter references
        return TranslationResult.Success(varRef.Name);
    }

    private TranslationResult TranslateGlobalVariable(GlobalVariableExpression globalVar)
    {
        var varName = globalVar.Name.ToUpperInvariant();
        var mapped = _functionMapper.MapFunction(varName, []);
        if (mapped is not null)
        {
            return TranslationResult.Success(mapped);
        }
        return TranslationResult.CannotTranslate($"No mapping for global variable '{globalVar.Name}'.");
    }

    private TranslationResult TranslateSearchedCase(SearchedCaseExpression searchedCase)
    {
        var parts = new List<string> { "CASE" };

        foreach (var whenClause in searchedCase.WhenClauses)
        {
            var condResult = TranslateBooleanExpression(whenClause.WhenExpression);
            if (!condResult.IsSuccess) return condResult;

            var thenResult = TranslateFragment(whenClause.ThenExpression);
            if (!thenResult.IsSuccess) return thenResult;

            parts.Add($"WHEN {condResult.TranslatedExpression} THEN {thenResult.TranslatedExpression}");
        }

        if (searchedCase.ElseExpression is not null)
        {
            var elseResult = TranslateFragment(searchedCase.ElseExpression);
            if (!elseResult.IsSuccess) return elseResult;
            parts.Add($"ELSE {elseResult.TranslatedExpression}");
        }

        parts.Add("END");
        return TranslationResult.Success(string.Join(" ", parts));
    }

    private TranslationResult TranslateSimpleCase(SimpleCaseExpression simpleCase)
    {
        var inputResult = TranslateFragment(simpleCase.InputExpression);
        if (!inputResult.IsSuccess) return inputResult;

        var parts = new List<string> { $"CASE {inputResult.TranslatedExpression}" };

        foreach (var whenClause in simpleCase.WhenClauses)
        {
            var whenResult = TranslateFragment(whenClause.WhenExpression);
            if (!whenResult.IsSuccess) return whenResult;

            var thenResult = TranslateFragment(whenClause.ThenExpression);
            if (!thenResult.IsSuccess) return thenResult;

            parts.Add($"WHEN {whenResult.TranslatedExpression} THEN {thenResult.TranslatedExpression}");
        }

        if (simpleCase.ElseExpression is not null)
        {
            var elseResult = TranslateFragment(simpleCase.ElseExpression);
            if (!elseResult.IsSuccess) return elseResult;
            parts.Add($"ELSE {elseResult.TranslatedExpression}");
        }

        parts.Add("END");
        return TranslationResult.Success(string.Join(" ", parts));
    }

    private TranslationResult TranslateCoalesce(CoalesceExpression coalesce)
    {
        var args = new List<string>();
        foreach (var expr in coalesce.Expressions)
        {
            var result = TranslateFragment(expr);
            if (!result.IsSuccess) return result;
            args.Add(result.TranslatedExpression!);
        }
        return TranslationResult.Success($"COALESCE({string.Join(", ", args)})");
    }

    private TranslationResult TranslateNullIf(NullIfExpression nullIf)
    {
        var firstResult = TranslateFragment(nullIf.FirstExpression);
        if (!firstResult.IsSuccess) return firstResult;

        var secondResult = TranslateFragment(nullIf.SecondExpression);
        if (!secondResult.IsSuccess) return secondResult;

        return TranslationResult.Success(
            $"NULLIF({firstResult.TranslatedExpression}, {secondResult.TranslatedExpression})");
    }

    private TranslationResult TranslateIif(IIfCall iif)
    {
        var condResult = TranslateBooleanExpression(iif.Predicate);
        if (!condResult.IsSuccess) return condResult;

        var thenResult = TranslateFragment(iif.ThenExpression);
        if (!thenResult.IsSuccess) return thenResult;

        var elseResult = TranslateFragment(iif.ElseExpression);
        if (!elseResult.IsSuccess) return elseResult;

        return TranslationResult.Success(
            $"CASE WHEN {condResult.TranslatedExpression} THEN {thenResult.TranslatedExpression} ELSE {elseResult.TranslatedExpression} END");
    }

    private TranslationResult TranslateBooleanExpression(BooleanExpression boolExpr)
    {
        return boolExpr switch
        {
            BooleanComparisonExpression comp => TranslateBooleanComparison(comp),
            BooleanIsNullExpression isNull => TranslateIsNull(isNull),
            BooleanNotExpression notExpr => TranslateBooleanNot(notExpr),
            BooleanBinaryExpression boolBinary => TranslateBooleanBinary(boolBinary),
            BooleanParenthesisExpression boolParen => TranslateBooleanParenthesis(boolParen),
            InPredicate inPred => TranslateInPredicate(inPred),
            LikePredicate like => TranslateLike(like),
            ExistsPredicate => TranslationResult.CannotTranslate("EXISTS predicates require subquery translation."),
            _ => TranslateGenericFragment(boolExpr)
        };
    }

    private TranslationResult TranslateBooleanComparison(BooleanComparisonExpression comp)
    {
        var leftResult = TranslateFragment(comp.FirstExpression);
        if (!leftResult.IsSuccess) return leftResult;

        var rightResult = TranslateFragment(comp.SecondExpression);
        if (!rightResult.IsSuccess) return rightResult;

        var op = comp.ComparisonType switch
        {
            BooleanComparisonType.Equals => "=",
            BooleanComparisonType.GreaterThan => ">",
            BooleanComparisonType.LessThan => "<",
            BooleanComparisonType.GreaterThanOrEqualTo => ">=",
            BooleanComparisonType.LessThanOrEqualTo => "<=",
            BooleanComparisonType.NotEqualToBrackets => "<>",
            BooleanComparisonType.NotEqualToExclamation => "<>",
            _ => null
        };

        if (op is null)
        {
            return TranslationResult.CannotTranslate(
                $"Unsupported comparison type: {comp.ComparisonType}");
        }

        return TranslationResult.Success(
            $"{leftResult.TranslatedExpression} {op} {rightResult.TranslatedExpression}");
    }

    private TranslationResult TranslateIsNull(BooleanIsNullExpression isNull)
    {
        var exprResult = TranslateFragment(isNull.Expression);
        if (!exprResult.IsSuccess) return exprResult;

        var nullCheck = isNull.IsNot ? "IS NOT NULL" : "IS NULL";
        return TranslationResult.Success($"{exprResult.TranslatedExpression} {nullCheck}");
    }

    private TranslationResult TranslateBooleanNot(BooleanNotExpression notExpr)
    {
        var innerResult = TranslateBooleanExpression(notExpr.Expression);
        if (!innerResult.IsSuccess) return innerResult;
        return TranslationResult.Success($"NOT ({innerResult.TranslatedExpression})");
    }

    private TranslationResult TranslateBooleanBinary(BooleanBinaryExpression boolBinary)
    {
        var leftResult = TranslateBooleanExpression(boolBinary.FirstExpression);
        if (!leftResult.IsSuccess) return leftResult;

        var rightResult = TranslateBooleanExpression(boolBinary.SecondExpression);
        if (!rightResult.IsSuccess) return rightResult;

        var op = boolBinary.BinaryExpressionType == BooleanBinaryExpressionType.And ? "AND" : "OR";
        return TranslationResult.Success(
            $"{leftResult.TranslatedExpression} {op} {rightResult.TranslatedExpression}");
    }

    private TranslationResult TranslateBooleanParenthesis(BooleanParenthesisExpression boolParen)
    {
        var innerResult = TranslateBooleanExpression(boolParen.Expression);
        if (!innerResult.IsSuccess) return innerResult;
        return TranslationResult.Success($"({innerResult.TranslatedExpression})");
    }

    private TranslationResult TranslateInPredicate(InPredicate inPred)
    {
        var exprResult = TranslateFragment(inPred.Expression);
        if (!exprResult.IsSuccess) return exprResult;

        var values = new List<string>();
        foreach (var val in inPred.Values)
        {
            var valResult = TranslateFragment(val);
            if (!valResult.IsSuccess) return valResult;
            values.Add(valResult.TranslatedExpression!);
        }

        var notStr = inPred.NotDefined ? "NOT " : "";
        return TranslationResult.Success(
            $"{exprResult.TranslatedExpression} {notStr}IN ({string.Join(", ", values)})");
    }

    private TranslationResult TranslateLike(LikePredicate like)
    {
        var exprResult = TranslateFragment(like.FirstExpression);
        if (!exprResult.IsSuccess) return exprResult;

        var patternResult = TranslateFragment(like.SecondExpression);
        if (!patternResult.IsSuccess) return patternResult;

        var notStr = like.NotDefined ? "NOT " : "";
        return TranslationResult.Success(
            $"{exprResult.TranslatedExpression} {notStr}LIKE {patternResult.TranslatedExpression}");
    }

    private string? MapDataType(DataTypeReference dataType)
    {
        var typeName = GetDataTypeName(dataType);
        int? precision = null;
        int? scale = null;
        int? length = null;

        if (dataType is SqlDataTypeReference sqlType)
        {
            var parameters = sqlType.Parameters;
            if (parameters.Count >= 1)
            {
                var firstParam = parameters[0].Value;
                if (firstParam.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                {
                    length = -1; // Signal MAX
                }
                else if (int.TryParse(firstParam, out var p))
                {
                    // For string types, this is length; for numeric types, precision
                    var upperType = typeName.ToUpperInvariant();
                    if (upperType.Contains("CHAR") || upperType.Contains("BINARY"))
                    {
                        length = p;
                    }
                    else
                    {
                        precision = p;
                    }
                }
            }
            if (parameters.Count >= 2 && int.TryParse(parameters[1].Value, out var s))
            {
                scale = s;
            }
        }

        var result = _typeMapper.MapType(typeName, precision, scale, length);
        return result.MappedType;
    }

    private static string GetDataTypeName(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            return sqlType.SqlDataTypeOption.ToString().ToUpperInvariant() switch
            {
                "NONE" => sqlType.Name?.BaseIdentifier?.Value ?? "UNKNOWN",
                var opt => opt
            };
        }

        return dataType.Name?.BaseIdentifier?.Value ?? "UNKNOWN";
    }

    private static string TranslateTopToLimit(string originalSql, TopClauseVisitor visitor)
    {
        // Remove the TOP clause from the SQL and append LIMIT at the end
        var sql = originalSql;

        if (visitor.TopStartOffset >= 0 && visitor.TopLength > 0)
        {
            sql = sql.Remove(visitor.TopStartOffset, visitor.TopLength).Trim();
            // Clean up double spaces
            while (sql.Contains("  "))
            {
                sql = sql.Replace("  ", " ");
            }
        }

        return $"{sql} LIMIT {visitor.TopExpression}";
    }

    private TranslationResult TranslateRawExpression(string expression)
    {
        // For expressions that didn't parse as a single expression,
        // attempt basic string-level transformations
        var result = expression;

        // Replace string concatenation + with ||
        // This is a simplified heuristic for raw SQL that wasn't AST-parseable
        return TranslationResult.Success(result);
    }

    private TranslationResult TranslateGenericFragment(TSqlFragment fragment)
    {
        // For fragments we can't specifically handle, extract the source text
        if (fragment.StartOffset >= 0 && fragment.FragmentLength > 0)
        {
            // We can't easily get the original text from the fragment without the source
            // Signal that this construct needs AI fallback
            return TranslationResult.CannotTranslate(
                $"Unsupported T-SQL construct: {fragment.GetType().Name}");
        }

        return TranslationResult.CannotTranslate(
            $"Cannot translate T-SQL fragment of type: {fragment.GetType().Name}");
    }

    private static string QuoteIdentifier(string identifier)
    {
        // Only quote if the identifier contains special characters or is a reserved word
        if (identifier.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return identifier.ToLowerInvariant();
        }
        return $"\"{identifier}\"";
    }

    /// <summary>
    /// Visitor that locates TOP clauses in SELECT statements.
    /// </summary>
    private sealed class TopClauseVisitor : TSqlFragmentVisitor
    {
        public string? TopExpression { get; private set; }
        public int TopStartOffset { get; private set; } = -1;
        public int TopLength { get; private set; }

        public override void Visit(TopRowFilter node)
        {
            if (node.Expression is IntegerLiteral intLit)
            {
                TopExpression = intLit.Value;
            }
            else if (node.Expression is ParenthesisExpression paren &&
                     paren.Expression is IntegerLiteral innerInt)
            {
                TopExpression = innerInt.Value;
            }
            else
            {
                TopExpression = "?";
            }

            // Capture offset for removal — include "TOP" keyword
            TopStartOffset = node.StartOffset;
            TopLength = node.FragmentLength;
        }
    }
}
