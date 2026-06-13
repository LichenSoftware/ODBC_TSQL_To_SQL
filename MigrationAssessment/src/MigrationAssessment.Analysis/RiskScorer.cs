using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Assigns a deterministic risk score (1-5) to a statement based on its detected features.
/// Uses a lookup table mapping feature names to risk levels and returns the maximum
/// risk level among all detected features.
/// </summary>
public sealed class RiskScorer : IRiskScorer
{
    /// <summary>
    /// Deterministic lookup table mapping feature names to risk levels.
    /// Features not in this table are treated as Risk 1 (standard SQL).
    /// </summary>
    private static readonly Dictionary<string, int> FeatureRiskMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Risk 2 - Simple translations (5-30 minutes)
        ["TOP"] = 2,
        ["ISNULL"] = 2,
        ["GETDATE"] = 2,
        ["LEN"] = 2,
        ["CHARINDEX"] = 2,
        ["PATINDEX"] = 2,
        ["STUFF"] = 2,
        ["DATEADD"] = 2,
        ["DATEDIFF"] = 2,
        ["DATEPART"] = 2,
        ["OFFSET_FETCH"] = 2,
        ["STRING_CONCAT"] = 2,

        // Risk 3 - Procedural changes (30 min - 4 hours)
        ["TRY_CATCH"] = 3,
        ["DYNAMIC_SQL"] = 3,
        ["EXPLICIT_TRANSACTION"] = 3,
        ["SAVEPOINT"] = 3,
        ["TEMP_TABLE"] = 3,
        ["OUTPUT"] = 3,
        ["CROSS_APPLY"] = 3,
        ["OUTER_APPLY"] = 3,
        ["JSON_METHOD"] = 3,
        ["IDENTITY"] = 3,
        ["CTE"] = 3,

        // Risk 4 - Significant redesign (4-40 hours)
        ["MERGE"] = 4,
        ["TABLE_VALUED_PARAMETER"] = 4,
        ["TABLE_VARIABLE"] = 4,
        ["GLOBAL_TEMP_TABLE"] = 4,
        ["NOLOCK"] = 4,
        ["ROWLOCK"] = 4,
        ["UPDLOCK"] = 4,
        ["PIVOT"] = 4,
        ["UNPIVOT"] = 4,

        // Risk 5 - Architectural (requires replacement, 40+ hours)
        ["OPENQUERY"] = 5,
        ["OPENROWSET"] = 5,
        ["XML_METHOD"] = 5,
        ["SQL_CLR"] = 5,
        ["SERVICE_BROKER"] = 5,
        ["LINKED_SERVER"] = 5,
        ["REPLICATION"] = 5,
        ["FILESTREAM"] = 5,
        ["MEMORY_OPTIMIZED"] = 5,
    };

    /// <inheritdoc />
    public int ScoreStatement(IReadOnlyList<DetectedFeature> features, bool parseFailed)
    {
        // If parse failed, default to Risk 3 per Requirement 7.7
        if (parseFailed)
            return 3;

        // If no features detected, it's standard SQL = Risk 1 per Requirement 7.1
        if (features.Count == 0)
            return 1;

        // Assign max risk level among detected features per Requirement 7.6
        int maxRisk = 1;
        foreach (var feature in features)
        {
            if (FeatureRiskMap.TryGetValue(feature.FeatureName, out var risk))
            {
                maxRisk = Math.Max(maxRisk, risk);
            }
        }

        return maxRisk;
    }
}
