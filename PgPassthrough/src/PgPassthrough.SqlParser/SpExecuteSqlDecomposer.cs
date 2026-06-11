using PgPassthrough.Core.Models;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser;

/// <summary>
/// Decomposes an <c>sp_executesql</c> call into its constituent parts.
///
/// sp_executesql has the signature:
///   sp_executesql @stmt, @params, @p1 = val1, @p2 = val2, ...
///
/// Where:
///   @stmt   — the T-SQL text to execute (a string literal or variable)
///   @params — a type declaration string, e.g. N'@id INT, @name NVARCHAR(50)'
///   remaining args — the actual parameter values, in order or named
///
/// This is the primary mechanism ODBC drivers use for parameterised queries.
/// Without correct handling of sp_executesql, no application using SqlCommand
/// with parameters will work correctly.
///
/// The decomposer:
///   1. Extracts the SQL text from the first argument.
///   2. Parses the @params declaration string to build a typed parameter list.
///   3. Maps the value arguments back to their declared names.
///   4. Returns a <see cref="SpExecuteSqlResult"/> ready for the translation pipeline.
/// </summary>
public static class SpExecuteSqlDecomposer
{
    /// <summary>
    /// Returns true if this RPC request is an sp_executesql call.
    /// </summary>
    public static bool IsSpExecuteSql(RpcRequest request) =>
        string.Equals(request.ProcedureName, "sp_executesql", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Decomposes an sp_executesql RPC request.
    /// Returns null if the call cannot be decomposed (e.g. dynamic SQL using a variable).
    /// </summary>
    public static SpExecuteSqlResult? TryDecompose(RpcRequest request)
    {
        if (request.Parameters.Count == 0) return null;

        // Argument 0: @stmt — the SQL text
        var stmtParam = request.Parameters[0];
        if (stmtParam.Value is not string sqlText || string.IsNullOrWhiteSpace(sqlText))
            return null;

        // Argument 1 (optional): @params — type declarations
        var typedParams = new List<TypedParameter>();
        if (request.Parameters.Count >= 2)
        {
            var paramsParam = request.Parameters[1];
            if (paramsParam.Value is string paramDecls && !string.IsNullOrWhiteSpace(paramDecls))
            {
                typedParams = ParseParamDeclarations(paramDecls);
            }
        }

        // Arguments 2+: actual parameter values
        // These may be positional or named (@name = value)
        var boundParams = new List<QueryParameter>();
        for (int i = 2; i < request.Parameters.Count; i++)
        {
            var arg = request.Parameters[i];
            string name = arg.Name;

            // If name is not set, map by position to the declared parameter list
            if (string.IsNullOrEmpty(name) && i - 2 < typedParams.Count)
                name = typedParams[i - 2].Name;

            // Find the declared type for this parameter
            string? tsqlType = null;
            var declared = typedParams.FirstOrDefault(p =>
                string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (declared != null) tsqlType = declared.TypeDeclaration;

            boundParams.Add(new QueryParameter
            {
                Name     = name.StartsWith('@') ? name : $"@{name}",
                Value    = arg.Value,
                TsqlType = tsqlType
            });
        }

        // Parse the SQL text into an AST for the translation pipeline
        SqlBatch ast;
        try { ast = TSqlParser.Parse(sqlText); }
        catch { return null; } // unparseable dynamic SQL — let translator handle it

        return new SpExecuteSqlResult
        {
            SqlText    = sqlText,
            Ast        = ast,
            Parameters = boundParams
        };
    }

    /// <summary>
    /// Parses a parameter declaration string like:
    ///   N'@id INT, @name NVARCHAR(50), @active BIT'
    /// into a list of <see cref="TypedParameter"/>.
    /// </summary>
    private static List<TypedParameter> ParseParamDeclarations(string declarations)
    {
        var result = new List<TypedParameter>();

        // Split on commas — but be careful of type arguments like DECIMAL(10,2)
        // Strategy: split on commas that are NOT inside parentheses
        var parts = SplitOnTopLevelCommas(declarations.Trim());

        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            // Each part: @name TYPE[(args)]
            int firstSpace = trimmed.IndexOf(' ');
            if (firstSpace <= 0) continue;

            string paramName    = trimmed[..firstSpace].Trim();
            string typeDecl     = trimmed[firstSpace..].Trim();

            if (!paramName.StartsWith('@')) paramName = "@" + paramName;

            result.Add(new TypedParameter(paramName, typeDecl));
        }

        return result;
    }

    private static List<string> SplitOnTopLevelCommas(string s)
    {
        var parts = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s[start..i]);
                start = i + 1;
            }
        }
        if (start < s.Length) parts.Add(s[start..]);
        return parts;
    }

    private sealed record TypedParameter(string Name, string TypeDeclaration);
}

/// <summary>
/// The result of decomposing an sp_executesql call.
/// </summary>
public sealed class SpExecuteSqlResult
{
    /// <summary>The raw SQL text extracted from the @stmt argument.</summary>
    public required string SqlText { get; init; }

    /// <summary>Parsed AST of SqlText.</summary>
    public required SqlBatch Ast { get; init; }

    /// <summary>Bound parameter values with their declared T-SQL types.</summary>
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];
}
