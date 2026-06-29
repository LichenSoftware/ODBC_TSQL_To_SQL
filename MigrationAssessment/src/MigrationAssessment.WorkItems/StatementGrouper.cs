using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Groups analyzed statements into logical work item clusters based on
/// feature name and database object affinity.
/// </summary>
public sealed class StatementGrouper : IStatementGrouper
{
    private readonly ILogger<StatementGrouper> _logger;

    /// <summary>
    /// Feature-to-risk-level mapping consistent with the RiskScorer in the Analysis layer.
    /// Used to determine per-feature risk for the multi-feature assignment rule.
    /// </summary>
    private static readonly Dictionary<string, int> FeatureRiskMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Risk 2
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

        // Risk 3
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

        // Risk 4
        ["MERGE"] = 4,
        ["TABLE_VALUED_PARAMETER"] = 4,
        ["TABLE_VARIABLE"] = 4,
        ["GLOBAL_TEMP_TABLE"] = 4,
        ["NOLOCK"] = 4,
        ["ROWLOCK"] = 4,
        ["UPDLOCK"] = 4,
        ["PIVOT"] = 4,
        ["UNPIVOT"] = 4,

        // Risk 5
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

    public StatementGrouper(ILogger<StatementGrouper> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public IReadOnlyList<StatementGroup> GroupStatements(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        int minimumRiskLevel)
    {
        return GroupStatementsInternal(statements, featureDetection, minimumRiskLevel, null);
    }

    /// <inheritdoc/>
    public IReadOnlyList<StatementGroup> GroupStatements(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        int minimumRiskLevel,
        IReadOnlyList<ObjectInventoryEntry> objectInventory)
    {
        return GroupStatementsInternal(statements, featureDetection, minimumRiskLevel, objectInventory);
    }

    private IReadOnlyList<StatementGroup> GroupStatementsInternal(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        int minimumRiskLevel,
        IReadOnlyList<ObjectInventoryEntry>? objectInventory)
    {
        // Build a lookup from statement SQL text to object name (if inventory is provided)
        var statementToObject = BuildStatementToObjectMap(statements, objectInventory);

        // Step 1: Filter statements by minimum risk level
        var filteredStatements = statements
            .Where(s => s.RiskScore >= minimumRiskLevel)
            .ToList();

        _logger.LogDebug(
            "Filtered {Original} statements to {Filtered} at minimum risk level {MinRisk}",
            statements.Count, filteredStatements.Count, minimumRiskLevel);

        // Step 2: Group by (SqlTextHash, DatabaseObjectName)
        var groups = new Dictionary<(string SqlHash, string? ObjectName), List<(AnalyzedStatement Statement, string ObjectType)>>();

        foreach (var statement in filteredStatements)
        {
            var sqlHash = ComputeHash(statement.Source.SqlText);

            // Look up the database object for this statement using the inventory map
            string? databaseObjectName = null;
            string objectType = "AdHoc";

            if (statementToObject.TryGetValue(statement, out var objectInfo))
            {
                if (objectInfo.Type != "AdHoc")
                {
                    databaseObjectName = objectInfo.Name;
                    objectType = objectInfo.Type;
                }
            }

            var key = (sqlHash, databaseObjectName);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<(AnalyzedStatement Statement, string ObjectType)>();
                groups[key] = list;
            }

            list.Add((statement, objectType));
        }

        // Step 3: Build StatementGroup for each hash-group
        var result = new List<StatementGroup>();

        foreach (var kvp in groups)
        {
            var statementsInGroup = kvp.Value.Select(x => x.Statement).ToList();

            // Collect ALL distinct features across all statements in this group
            var allFeatures = statementsInGroup
                .SelectMany(s => s.Features)
                .Select(f => f.FeatureName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (allFeatures.Count == 0)
            {
                continue;
            }

            var maxRisk = allFeatures.Max(f => GetFeatureRiskLevel(f));
            var primaryFeature = allFeatures
                .OrderByDescending(f => GetFeatureRiskLevel(f))
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .First();

            // Use the object type from the first entry (all entries in a group share the same object)
            var objectType = kvp.Key.ObjectName is null ? "AdHoc" : kvp.Value[0].ObjectType;

            result.Add(new StatementGroup
            {
                FeatureName = primaryFeature,
                DetectedFeatures = allFeatures,
                DatabaseObjectName = kvp.Key.ObjectName,
                DatabaseObjectType = objectType,
                Statements = statementsInGroup.AsReadOnly(),
                IsServerLevelFeature = false,
                MaxRiskLevel = maxRisk
            });
        }

        // Step 4: Create server-level work item groups from FeatureDetectionResult
        var serverLevelGroups = CreateServerLevelGroups(featureDetection);
        result.AddRange(serverLevelGroups);

        _logger.LogDebug(
            "Created {StatementGroups} statement-based groups and {ServerGroups} server-level groups",
            result.Count - serverLevelGroups.Count, serverLevelGroups.Count);

        return result.AsReadOnly();
    }

    /// <summary>
    /// Computes a SHA-256 hash of the SQL text and returns it as a lowercase hex string.
    /// Used as the grouping key to identify identical SQL statements.
    /// </summary>
    private static string ComputeHash(string sqlText)
    {
        var bytes = Encoding.UTF8.GetBytes(sqlText ?? string.Empty);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Gets the individual risk level for a feature name using the same mapping
    /// as the RiskScorer in the Analysis layer.
    /// </summary>
    internal static int GetFeatureRiskLevel(string featureName)
    {
        return FeatureRiskMap.TryGetValue(featureName, out var risk) ? risk : 1;
    }

    /// <summary>
    /// Builds a mapping from each AnalyzedStatement to its containing database object info,
    /// using the ObjectInventoryBuilder's results. The inventory tells us which object each
    /// statement text belongs to based on DDL pattern detection.
    /// </summary>
    private static Dictionary<AnalyzedStatement, (string Name, string Type)> BuildStatementToObjectMap(
        IReadOnlyList<AnalyzedStatement> statements,
        IReadOnlyList<ObjectInventoryEntry>? objectInventory)
    {
        var map = new Dictionary<AnalyzedStatement, (string Name, string Type)>(
            ReferenceEqualityComparer.Instance);

        if (objectInventory is null || objectInventory.Count == 0)
        {
            return map;
        }

        // The ObjectInventoryBuilder uses the same DDL pattern matching on SQL text.
        // We replicate a lightweight version here: match statement's source SQL to the
        // inventory entry by checking if the SQL text contains the CREATE/ALTER DDL for the object.
        // For a statement that IS the DDL definition, its source text starts with CREATE/ALTER.
        foreach (var statement in statements)
        {
            var sqlText = statement.Source.SqlText;
            if (string.IsNullOrWhiteSpace(sqlText))
            {
                continue;
            }

            foreach (var entry in objectInventory)
            {
                if (entry.Type == "AdHoc")
                {
                    continue;
                }

                // Check if this statement's SQL text contains a CREATE/ALTER for this object
                if (ContainsObjectDefinition(sqlText, entry.Name, entry.Type))
                {
                    map[statement] = (entry.Name, entry.Type);
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    /// Checks if the SQL text contains a CREATE/ALTER definition for the given object.
    /// </summary>
    private static bool ContainsObjectDefinition(string sqlText, string objectName, string objectType)
    {
        // Build a pattern for this specific object
        var keyword = objectType switch
        {
            "StoredProcedure" => @"PROC(?:EDURE)?",
            "View" => "VIEW",
            "ScalarFunction" or "TableValuedFunction" => "FUNCTION",
            "Trigger" => "TRIGGER",
            _ => null
        };

        if (keyword is null)
        {
            return false;
        }

        // Match CREATE/ALTER [OR ALTER] KEYWORD [schema.]objectName
        var pattern = $@"(?:CREATE|ALTER)\s+(?:OR\s+ALTER\s+)?{keyword}\s+(?:\[?\w+\]?\.)?\[?{System.Text.RegularExpressions.Regex.Escape(objectName)}\]?";
        return System.Text.RegularExpressions.Regex.IsMatch(sqlText, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Creates server-level work item groups from FeatureDetectionResult entries
    /// with occurrence count greater than zero.
    /// </summary>
    private static List<StatementGroup> CreateServerLevelGroups(FeatureDetectionResult featureDetection)
    {
        var serverGroups = new List<StatementGroup>();

        foreach (var kvp in featureDetection.FeatureCounts)
        {
            if (kvp.Value > 0)
            {
                serverGroups.Add(new StatementGroup
                {
                    FeatureName = kvp.Key,
                    DetectedFeatures = new[] { kvp.Key },
                    DatabaseObjectName = null,
                    DatabaseObjectType = "ServerLevel",
                    Statements = Array.Empty<AnalyzedStatement>(),
                    IsServerLevelFeature = true,
                    MaxRiskLevel = GetFeatureRiskLevel(kvp.Key)
                });
            }
        }

        return serverGroups;
    }

    /// <summary>
    /// Reference equality comparer for use with statement-to-object mapping.
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<AnalyzedStatement>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public bool Equals(AnalyzedStatement? x, AnalyzedStatement? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(AnalyzedStatement obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
