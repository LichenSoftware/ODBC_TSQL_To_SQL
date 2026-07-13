using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Reporting;

/// <summary>
/// Generates a procedure mapping manifest from a completed conversion session.
/// This manifest documents how T-SQL EXEC calls translate to PostgreSQL function calls,
/// and can be consumed by PgPassthrough for runtime call routing.
/// </summary>
public sealed partial class ProcedureMappingGenerator
{
    private readonly ILogger<ProcedureMappingGenerator> _logger;

    public ProcedureMappingGenerator(ILogger<ProcedureMappingGenerator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Generates the mapping manifest for all callable objects (stored procedures and functions)
    /// in the session.
    /// </summary>
    public ProcedureMappingManifest Generate(
        string sessionId, IReadOnlyList<ConversionSessionEntry> entries)
    {
        var callableTypes = new HashSet<SchemaObjectType>
        {
            SchemaObjectType.StoredProcedure,
            SchemaObjectType.Function
        };

        var callableEntries = entries
            .Where(e => callableTypes.Contains(e.Source.ObjectType))
            .ToList();

        _logger.LogInformation(
            "Generating procedure mappings for {Count} callable objects in session {SessionId}",
            callableEntries.Count, sessionId);

        var mappings = new List<ProcedureMapping>();

        foreach (var entry in callableEntries)
        {
            var mapping = BuildMapping(entry);
            mappings.Add(mapping);
        }

        var summary = new MappingSummary
        {
            TotalMappings = mappings.Count,
            Converted = mappings.Count(m => m.Status == "converted"),
            Flagged = mappings.Count(m => m.Status == "flagged"),
            Failed = mappings.Count(m => m.Status == "failed"),
            NoParameters = mappings.Count(m => m.Parameters.Count == 0),
            WithParameters = mappings.Count(m => m.Parameters.Count > 0)
        };

        return new ProcedureMappingManifest
        {
            SessionId = sessionId,
            GeneratedAt = DateTimeOffset.UtcNow,
            Description = "Procedure-to-function mapping manifest for PgPassthrough. " +
                          "Documents how T-SQL EXEC calls translate to PostgreSQL SELECT * FROM function() calls.",
            Mappings = mappings,
            Summary = summary
        };
    }

    private ProcedureMapping BuildMapping(ConversionSessionEntry entry)
    {
        var source = entry.Source;
        var result = entry.Result;
        var qualifiedName = $"{source.SchemaName}.{source.Name}";

        // Extract parameters from source T-SQL definition
        var sourceParams = ExtractTSqlParameters(source.SourceDefinition);

        // Extract parameters from generated PostgreSQL DDL
        var pgParams = result.GeneratedDdl is not null
            ? ExtractPostgresParameters(result.GeneratedDdl)
            : [];

        // Build parameter mappings by matching positional order
        var parameterMappings = BuildParameterMappings(sourceParams, pgParams);

        // Determine the return type from the PostgreSQL DDL
        var returnType = DetermineReturnType(result.GeneratedDdl);

        // Build call patterns
        var originalCallPattern = sourceParams.Count > 0
            ? $"EXEC {qualifiedName} {string.Join(", ", sourceParams.Select(p => p.Name))}"
            : $"EXEC {qualifiedName}";

        var pgCallPattern = parameterMappings.Count > 0
            ? $"SELECT * FROM {qualifiedName}({string.Join(", ", parameterMappings.Select(p => $"{{{p.Postgres}}}"))})"
            : $"SELECT * FROM {qualifiedName}()";

        // Collect relevant compatibility notes
        var notes = result.CompatibilityNotes
            .Where(n => n.Category is "Return Type" or "Calling Convention" or "Output Parameters"
                or "Parameter Handling")
            .Select(n => n.Description)
            .ToList();

        // Add a standard note about the calling convention change
        if (source.ObjectType == SchemaObjectType.StoredProcedure && notes.All(n => !n.Contains("SELECT * FROM")))
        {
            notes.Insert(0, $"Call changes from 'EXEC {qualifiedName}' to 'SELECT * FROM {qualifiedName}(...)'");
        }

        return new ProcedureMapping
        {
            OriginalName = qualifiedName,
            PostgresName = qualifiedName,
            OriginalType = source.ObjectType == SchemaObjectType.StoredProcedure
                ? "stored_procedure" : "function",
            PostgresType = "function",
            CallPattern = pgCallPattern,
            OriginalCallPattern = originalCallPattern,
            Parameters = parameterMappings,
            ReturnType = returnType,
            Confidence = result.ConfidenceScore,
            Status = result.Status.ToString().ToLowerInvariant(),
            CompatibilityNotes = notes
        };
    }

    private static List<ParameterMapping> BuildParameterMappings(
        List<TSqlParam> sourceParams, List<PgParam> pgParams)
    {
        var mappings = new List<ParameterMapping>();

        for (var i = 0; i < sourceParams.Count; i++)
        {
            var src = sourceParams[i];
            var pg = i < pgParams.Count ? pgParams[i] : null;

            mappings.Add(new ParameterMapping
            {
                Original = src.Name,
                Postgres = pg?.Name ?? src.Name.TrimStart('@'),
                Position = i + 1,
                OriginalType = src.DataType,
                PostgresType = pg?.DataType
            });
        }

        return mappings;
    }

    private static string DetermineReturnType(string? generatedDdl)
    {
        if (string.IsNullOrWhiteSpace(generatedDdl))
            return "unknown";

        if (generatedDdl.Contains("RETURNS TABLE", StringComparison.OrdinalIgnoreCase))
            return "table";
        if (generatedDdl.Contains("RETURNS SETOF", StringComparison.OrdinalIgnoreCase))
            return "setof";
        if (generatedDdl.Contains("RETURNS void", StringComparison.OrdinalIgnoreCase) ||
            generatedDdl.Contains("RETURNS VOID", StringComparison.OrdinalIgnoreCase))
            return "void";

        // Try to extract scalar return type
        var match = ReturnsScalarRegex().Match(generatedDdl);
        if (match.Success)
            return match.Groups[1].Value.Trim().ToLowerInvariant();

        return "table"; // default assumption for stored procedures converted to functions
    }

    /// <summary>
    /// Extracts parameter declarations from a T-SQL CREATE PROCEDURE/FUNCTION definition.
    /// Only looks at the declaration area (between CREATE PROCEDURE/FUNCTION name and the AS keyword).
    /// </summary>
    private static List<TSqlParam> ExtractTSqlParameters(string sourceDefinition)
    {
        var results = new List<TSqlParam>();
        if (string.IsNullOrWhiteSpace(sourceDefinition))
            return results;

        // Extract only the parameter declaration section (between the proc/func name and AS/BEGIN)
        var declSection = ExtractParameterDeclarationSection(sourceDefinition);
        if (string.IsNullOrWhiteSpace(declSection))
            return results;

        // Match parameter declarations like @TopN INT, @Name NVARCHAR(100) = NULL OUTPUT
        var matches = TSqlParamRegex().Matches(declSection);
        foreach (Match match in matches)
        {
            results.Add(new TSqlParam(
                Name: match.Groups[1].Value,
                DataType: match.Groups[2].Value.Trim()));
        }

        return results;
    }

    /// <summary>
    /// Extracts the parameter declaration section from a T-SQL CREATE PROCEDURE/FUNCTION statement.
    /// Returns the text between the object name and the AS keyword.
    /// </summary>
    private static string? ExtractParameterDeclarationSection(string sourceDefinition)
    {
        // Find the start: after CREATE PROCEDURE/FUNCTION name
        var match = TSqlDeclSectionRegex().Match(sourceDefinition);
        if (!match.Success)
            return null;

        // The parameter section is between the object name and AS\nBEGIN or just AS
        var afterName = match.Groups[1].Value;
        return afterName;
    }

    /// <summary>
    /// Extracts parameter declarations from a PostgreSQL CREATE FUNCTION definition.
    /// </summary>
    private static List<PgParam> ExtractPostgresParameters(string generatedDdl)
    {
        var results = new List<PgParam>();
        if (string.IsNullOrWhiteSpace(generatedDdl))
            return results;

        // Find the function signature between the opening paren and RETURNS or closing paren
        var match = PgFunctionSignatureRegex().Match(generatedDdl);
        if (!match.Success)
            return results;

        var paramsBlock = match.Groups[1].Value.Trim();
        if (string.IsNullOrWhiteSpace(paramsBlock))
            return results;

        // Split on commas (but not commas inside parentheses)
        var paramDecls = SplitParams(paramsBlock);

        foreach (var decl in paramDecls)
        {
            var trimmed = decl.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            // Match patterns like: p_TopN INT, IN p_Name VARCHAR(100), OUT p_Result NUMERIC
            var paramMatch = PgParamRegex().Match(trimmed);
            if (paramMatch.Success)
            {
                var direction = paramMatch.Groups[1].Value.Trim();
                var name = paramMatch.Groups[2].Value.Trim();
                var type = paramMatch.Groups[3].Value.Trim();

                // If "name" looks like a type keyword and there's no direction, it might be unnamed
                results.Add(new PgParam(Name: name, DataType: type, Direction: direction));
            }
        }

        return results;
    }

    private static List<string> SplitParams(string paramsBlock)
    {
        var results = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < paramsBlock.Length; i++)
        {
            switch (paramsBlock[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    results.Add(paramsBlock[start..i]);
                    start = i + 1;
                    break;
            }
        }

        if (start < paramsBlock.Length)
            results.Add(paramsBlock[start..]);

        return results;
    }

    [GeneratedRegex(@"(@\w+)\s+([\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase)]
    private static partial Regex TSqlParamRegex();

    [GeneratedRegex(@"(?:CREATE\s+(?:OR\s+ALTER\s+)?(?:PROCEDURE|FUNCTION)\s+[\w.\[\]]+)\s*(.*?)(?=\bAS\b)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TSqlDeclSectionRegex();

    [GeneratedRegex(@"RETURNS\s+(?!TABLE|SETOF)([\w]+(?:\s*\([^)]*\))?)", RegexOptions.IgnoreCase)]
    private static partial Regex ReturnsScalarRegex();

    [GeneratedRegex(@"FUNCTION\s+[\w.""]+\s*\(([^)]*(?:\([^)]*\)[^)]*)*)\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex PgFunctionSignatureRegex();

    [GeneratedRegex(@"^(?:(IN|OUT|INOUT)\s+)?(\w+)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PgParamRegex();

    private sealed record TSqlParam(string Name, string DataType);
    private sealed record PgParam(string Name, string DataType, string Direction);
}
