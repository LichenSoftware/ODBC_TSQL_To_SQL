using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Deduplicates statement groups by merging statements within the same database object
/// that produce identical feature names, selects the highest-WeightedRisk statement as the
/// primary example, builds cross-reference maps for shared database objects, and assigns
/// sequential unique identifiers.
/// </summary>
public sealed class WorkItemDeduplicator
{
    private const int MaxSqlPatternLength = 500;

    /// <summary>
    /// Deduplicates the provided statement groups, producing a list of <see cref="DeduplicatedGroup"/>
    /// records with assigned IDs, primary SQL examples, combined priority scores, cross-references,
    /// and affected object details.
    /// </summary>
    /// <param name="groups">Statement groups produced by the <see cref="IStatementGrouper"/>.</param>
    /// <returns>A deduplicated list of groups ready for further processing.</returns>
    public IReadOnlyList<DeduplicatedGroup> Deduplicate(IReadOnlyList<StatementGroup> groups)
    {
        if (groups is null || groups.Count == 0)
        {
            return [];
        }

        // Step 1: Process each group — select primary statement, calculate scores, build affected objects.
        var deduplicatedGroups = new List<DeduplicatedGroup>(groups.Count);

        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];

            // Skip groups with no statements (e.g., server-level feature groups)
            if (group.Statements.Count == 0)
            {
                continue;
            }

            var sequentialId = FormatId(deduplicatedGroups.Count + 1);

            var primaryStatement = SelectPrimaryStatement(group.Statements);
            var primarySqlPattern = TruncateSqlText(primaryStatement.Source.SqlText);
            var combinedPriorityScore = CalculateCombinedPriorityScore(group.Statements);
            var affectedObjects = BuildAffectedObjects(group);

            deduplicatedGroups.Add(new DeduplicatedGroup
            {
                Group = group,
                Id = sequentialId,
                PrimarySqlPattern = primarySqlPattern,
                PrimaryStatement = primaryStatement,
                CombinedPriorityScore = combinedPriorityScore,
                RelatedWorkItemIds = [], // Placeholder — filled in step 2
                AffectedObjects = affectedObjects
            });
        }

        // Step 2: Build cross-reference map for database objects appearing in multiple work items.
        var crossReferenceMap = BuildCrossReferenceMap(deduplicatedGroups);

        // Step 3: Apply cross-references to each group.
        if (crossReferenceMap.Count > 0)
        {
            for (var i = 0; i < deduplicatedGroups.Count; i++)
            {
                var existing = deduplicatedGroups[i];
                var relatedIds = GetRelatedWorkItemIds(existing, crossReferenceMap);

                if (relatedIds.Count > 0)
                {
                    deduplicatedGroups[i] = existing with { RelatedWorkItemIds = relatedIds };
                }
            }
        }

        return deduplicatedGroups;
    }

    /// <summary>
    /// Selects the statement with the highest WeightedRisk from the group.
    /// If multiple statements have the same WeightedRisk, the first one encountered is used.
    /// </summary>
    private static AnalyzedStatement SelectPrimaryStatement(IReadOnlyList<AnalyzedStatement> statements)
    {
        var primary = statements[0];

        for (var i = 1; i < statements.Count; i++)
        {
            if (statements[i].WeightedRisk > primary.WeightedRisk)
            {
                primary = statements[i];
            }
        }

        return primary;
    }

    /// <summary>
    /// Truncates SQL text to a maximum of 500 characters.
    /// </summary>
    private static string TruncateSqlText(string sqlText)
    {
        if (string.IsNullOrEmpty(sqlText))
        {
            return string.Empty;
        }

        return sqlText.Length <= MaxSqlPatternLength
            ? sqlText
            : sqlText[..MaxSqlPatternLength];
    }

    /// <summary>
    /// Calculates the combined priority score as the sum of WeightedRisk values
    /// across all statements in the group. This incorporates execution frequency
    /// since WeightedRisk = RiskScore × ExecutionFrequency × BusinessImportance.
    /// </summary>
    private static double CalculateCombinedPriorityScore(IReadOnlyList<AnalyzedStatement> statements)
    {
        var total = 0.0;

        for (var i = 0; i < statements.Count; i++)
        {
            total += statements[i].WeightedRisk;
        }

        return total;
    }

    /// <summary>
    /// Builds the list of affected objects for a statement group.
    /// Each group has a single database object (or "Ad Hoc Queries" for null object name),
    /// with the statement count being the number of statements in the group.
    /// </summary>
    private static IReadOnlyList<AffectedObject> BuildAffectedObjects(StatementGroup group)
    {
        var objectName = group.DatabaseObjectName ?? "Ad Hoc Queries";
        var objectType = group.DatabaseObjectType;

        return
        [
            new AffectedObject
            {
                Name = objectName,
                Type = objectType,
                StatementCount = group.Statements.Count
            }
        ];
    }

    /// <summary>
    /// Builds a cross-reference map: for each database object name that appears in multiple
    /// work items, records the IDs of all work items referencing that object.
    /// </summary>
    private static Dictionary<string, List<string>> BuildCrossReferenceMap(
        List<DeduplicatedGroup> groups)
    {
        // Map: database object name → list of work item IDs referencing it
        var objectToWorkItems = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var objectName = group.Group.DatabaseObjectName;

            // Skip ad hoc queries (null object name) — they don't represent a shared database object
            if (objectName is null)
            {
                continue;
            }

            if (!objectToWorkItems.TryGetValue(objectName, out var workItemIds))
            {
                workItemIds = [];
                objectToWorkItems[objectName] = workItemIds;
            }

            workItemIds.Add(group.Id);
        }

        // Remove entries with only one work item — they have no cross-references
        var crossReferenceMap = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (objectName, workItemIds) in objectToWorkItems)
        {
            if (workItemIds.Count > 1)
            {
                crossReferenceMap[objectName] = workItemIds;
            }
        }

        return crossReferenceMap;
    }

    /// <summary>
    /// Gets the related work item IDs for a given group based on the cross-reference map.
    /// Returns the IDs of other work items that share the same database object.
    /// </summary>
    private static IReadOnlyList<string> GetRelatedWorkItemIds(
        DeduplicatedGroup group,
        Dictionary<string, List<string>> crossReferenceMap)
    {
        var objectName = group.Group.DatabaseObjectName;

        if (objectName is null)
        {
            return [];
        }

        if (!crossReferenceMap.TryGetValue(objectName, out var allIds))
        {
            return [];
        }

        // Return all IDs except the current group's own ID
        var relatedIds = new List<string>(allIds.Count - 1);

        foreach (var id in allIds)
        {
            if (!string.Equals(id, group.Id, StringComparison.Ordinal))
            {
                relatedIds.Add(id);
            }
        }

        return relatedIds;
    }

    /// <summary>
    /// Formats a sequential number into the "WI-NNN" identifier format.
    /// Uses zero-padded 3-digit minimum width.
    /// </summary>
    private static string FormatId(int sequentialNumber)
    {
        return $"WI-{sequentialNumber:D3}";
    }
}
