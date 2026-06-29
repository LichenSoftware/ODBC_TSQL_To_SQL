using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Reporting;

/// <summary>
/// Generates deterministic risk rationale strings for analyzed statements
/// based on a lookup table keyed by the highest-risk detected feature.
/// </summary>
public static class RiskRationaleGenerator
{
    /// <summary>
    /// Lookup table mapping feature names to their rationale explanation.
    /// Keyed on uppercase feature name for case-insensitive matching.
    /// </summary>
    private static readonly Dictionary<string, string> FeatureRationales = new(StringComparer.OrdinalIgnoreCase)
    {
        // Risk 2 - Simple translations
        ["TOP"] = "TOP clause translates to PostgreSQL LIMIT with no behavioral change.",
        ["ISNULL"] = "ISNULL translates directly to PostgreSQL COALESCE with no behavioral change.",
        ["GETDATE"] = "GETDATE() translates to PostgreSQL NOW() or CURRENT_TIMESTAMP with no behavioral change.",
        ["LEN"] = "LEN() translates to PostgreSQL LENGTH() with no behavioral change.",
        ["CHARINDEX"] = "CHARINDEX translates to PostgreSQL POSITION() or STRPOS() with no behavioral change.",
        ["PATINDEX"] = "PATINDEX translates to PostgreSQL regexp pattern matching with minor syntax adjustment.",
        ["STUFF"] = "STUFF() translates to PostgreSQL OVERLAY() with minor syntax adjustment.",
        ["DATEADD"] = "DATEADD translates to PostgreSQL interval arithmetic with minor syntax change.",
        ["DATEDIFF"] = "DATEDIFF translates to PostgreSQL date subtraction or EXTRACT with minor refactoring.",
        ["DATEPART"] = "DATEPART translates to PostgreSQL EXTRACT() with minor syntax change.",
        ["OFFSET_FETCH"] = "OFFSET/FETCH translates directly to PostgreSQL LIMIT/OFFSET syntax.",
        ["STRING_CONCAT"] = "String concatenation operator (+) translates to PostgreSQL || operator.",
        ["STRING_CONCAT_PLUS"] = "String concatenation with + operator translates to PostgreSQL || operator.",
        ["TOP_WITHOUT_ORDER"] = "TOP without ORDER BY produces non-deterministic results; add ORDER BY or use LIMIT.",
        ["PRINT_STATEMENT"] = "PRINT has no PostgreSQL equivalent; replace with RAISE NOTICE in PL/pgSQL.",
        ["THROW"] = "THROW translates to PostgreSQL RAISE EXCEPTION with minor syntax change.",
        ["IMPLICIT_CONVERSION"] = "Implicit type conversion may behave differently; add explicit CAST for PostgreSQL.",
        ["STRING_SPLIT"] = "STRING_SPLIT() translates to PostgreSQL string_to_table() or regexp_split_to_table().",

        // Risk 3 - Procedural changes
        ["TRY_CATCH"] = "TRY_CATCH block requires restructuring to PostgreSQL BEGIN/EXCEPTION/END pattern.",
        ["DYNAMIC_SQL"] = "Dynamic SQL (EXEC/sp_executesql) requires conversion to PostgreSQL EXECUTE format.",
        ["EXPLICIT_TRANSACTION"] = "Explicit transaction control requires review of PostgreSQL transaction semantics.",
        ["SAVEPOINT"] = "SAVEPOINT usage requires verification of PostgreSQL savepoint behavior differences.",
        ["TEMP_TABLE"] = "Local temp table (#name) translates to PostgreSQL TEMP TABLE but lifecycle rules differ.",
        ["OUTPUT"] = "OUTPUT clause requires conversion to PostgreSQL RETURNING clause with scope differences.",
        ["CROSS_APPLY"] = "CROSS APPLY translates to PostgreSQL LATERAL JOIN with minor syntax adjustment.",
        ["OUTER_APPLY"] = "OUTER APPLY translates to PostgreSQL LEFT JOIN LATERAL with minor syntax adjustment.",
        ["JSON_METHOD"] = "JSON methods require conversion to PostgreSQL json/jsonb operators and functions.",
        ["IDENTITY"] = "IDENTITY columns translate to PostgreSQL GENERATED ALWAYS AS IDENTITY or SERIAL.",
        ["CTE"] = "CTE usage is compatible but recursive CTE syntax differences require validation.",
        ["RAISERROR"] = "RAISERROR requires conversion to PostgreSQL RAISE EXCEPTION with different format syntax.",

        // Risk 4 - Significant redesign
        ["MERGE"] = "MERGE statement syntax differs significantly; DELETE branch requires separate handling in PostgreSQL.",
        ["TABLE_VALUED_PARAMETER"] = "Table-valued parameters have no direct equivalent; requires redesign using UNNEST or temp tables.",
        ["TABLE_VARIABLE"] = "Table variables have no PostgreSQL equivalent; requires conversion to temp tables or CTEs.",
        ["GLOBAL_TEMP_TABLE"] = "GLOBAL_TEMP_TABLE (##name) has no PostgreSQL equivalent; architectural redesign required for cross-session state.",
        ["NOLOCK"] = "NOLOCK hint is not supported; read consistency strategy must be reconsidered under MVCC.",
        ["ROWLOCK"] = "ROWLOCK hint is not supported in PostgreSQL; application-level locking redesign required.",
        ["UPDLOCK"] = "UPDLOCK hint is not supported in PostgreSQL; SELECT FOR UPDATE or advisory locks required.",
        ["PIVOT"] = "PIVOT requires conversion to PostgreSQL crosstab() from tablefunc or conditional aggregation.",
        ["UNPIVOT"] = "UNPIVOT requires conversion to PostgreSQL LATERAL with VALUES or unnest() pattern.",
        ["OPENJSON"] = "OPENJSON requires conversion to PostgreSQL jsonb_each()/jsonb_array_elements() functions.",
        ["FOR_XML"] = "FOR XML requires conversion to PostgreSQL json_agg()/xmlagg() or string_agg() functions.",

        // Risk 5 - Architectural
        ["OPENQUERY"] = "OPENQUERY requires replacement with PostgreSQL foreign data wrappers or application-layer integration.",
        ["OPENROWSET"] = "OPENROWSET requires replacement with PostgreSQL foreign data wrappers or file_fdw.",
        ["XML_METHOD"] = "XML_METHOD usage requires XPath rewrite using PostgreSQL xpath() — no direct equivalent exists.",
        ["SQL_CLR"] = "SQL_CLR assemblies have no PostgreSQL equivalent; requires rewrite as PL/pgSQL or application code.",
        ["SERVICE_BROKER"] = "SERVICE_BROKER has no PostgreSQL equivalent; requires external message queue (e.g., RabbitMQ, SQS).",
        ["LINKED_SERVER"] = "LINKED_SERVER references require replacement with PostgreSQL foreign data wrappers (postgres_fdw).",
        ["REPLICATION"] = "SQL Server replication has no direct equivalent; requires PostgreSQL logical replication or CDC setup.",
        ["FILESTREAM"] = "FILESTREAM has no PostgreSQL equivalent; requires migration to large objects (lo) or external storage.",
        ["MEMORY_OPTIMIZED"] = "Memory-optimized tables have no PostgreSQL equivalent; standard tables with tuning required."
    };

    /// <summary>
    /// Risk-level ordering used to determine which feature drives the score.
    /// Matches the RiskScorer lookup.
    /// </summary>
    private static readonly Dictionary<string, int> FeatureRiskMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TOP"] = 2, ["ISNULL"] = 2, ["GETDATE"] = 2, ["LEN"] = 2,
        ["CHARINDEX"] = 2, ["PATINDEX"] = 2, ["STUFF"] = 2,
        ["DATEADD"] = 2, ["DATEDIFF"] = 2, ["DATEPART"] = 2,
        ["OFFSET_FETCH"] = 2, ["STRING_CONCAT"] = 2,
        ["STRING_CONCAT_PLUS"] = 2, ["TOP_WITHOUT_ORDER"] = 2,
        ["PRINT_STATEMENT"] = 2, ["THROW"] = 2,
        ["IMPLICIT_CONVERSION"] = 2, ["STRING_SPLIT"] = 2,

        ["TRY_CATCH"] = 3, ["DYNAMIC_SQL"] = 3, ["EXPLICIT_TRANSACTION"] = 3,
        ["SAVEPOINT"] = 3, ["TEMP_TABLE"] = 3, ["OUTPUT"] = 3,
        ["CROSS_APPLY"] = 3, ["OUTER_APPLY"] = 3, ["JSON_METHOD"] = 3,
        ["IDENTITY"] = 3, ["CTE"] = 3, ["RAISERROR"] = 3,

        ["MERGE"] = 4, ["TABLE_VALUED_PARAMETER"] = 4, ["TABLE_VARIABLE"] = 4,
        ["GLOBAL_TEMP_TABLE"] = 4, ["NOLOCK"] = 4, ["ROWLOCK"] = 4,
        ["UPDLOCK"] = 4, ["PIVOT"] = 4, ["UNPIVOT"] = 4,
        ["OPENJSON"] = 4, ["FOR_XML"] = 4,

        ["OPENQUERY"] = 5, ["OPENROWSET"] = 5, ["XML_METHOD"] = 5,
        ["SQL_CLR"] = 5, ["SERVICE_BROKER"] = 5, ["LINKED_SERVER"] = 5,
        ["REPLICATION"] = 5, ["FILESTREAM"] = 5, ["MEMORY_OPTIMIZED"] = 5
    };

    /// <summary>
    /// Generates a deterministic risk rationale for an analyzed statement.
    /// Returns a single sentence (under 200 characters) naming the highest-risk
    /// feature and explaining why it drives the assigned score.
    /// </summary>
    public static string GenerateRationale(IReadOnlyList<DetectedFeature> features, int riskScore)
    {
        if (features.Count == 0)
        {
            return "Standard DML with no SQL Server-specific features detected.";
        }

        // Find the highest-risk feature (by risk level, then alphabetical for determinism)
        var highestRiskFeature = features
            .OrderByDescending(f => GetFeatureRisk(f.FeatureName))
            .ThenBy(f => f.FeatureName, StringComparer.OrdinalIgnoreCase)
            .First();

        if (FeatureRationales.TryGetValue(highestRiskFeature.FeatureName, out var rationale))
        {
            return rationale;
        }

        // Fallback for unknown features
        return riskScore switch
        {
            >= 4 => $"{highestRiskFeature.FeatureName} requires manual conversion; no direct PostgreSQL equivalent available.",
            3 => $"{highestRiskFeature.FeatureName} requires procedural restructuring for PostgreSQL compatibility.",
            2 => $"{highestRiskFeature.FeatureName} requires simple function substitution with no behavioral change.",
            _ => "Standard DML with no SQL Server-specific features detected."
        };
    }

    private static int GetFeatureRisk(string featureName)
    {
        return FeatureRiskMap.TryGetValue(featureName, out var risk) ? risk : 1;
    }
}
