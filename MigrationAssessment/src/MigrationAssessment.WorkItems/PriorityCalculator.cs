using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Calculates priority scores and assigns percentile-based priority labels to work items.
/// </summary>
public sealed class PriorityCalculator : IPriorityCalculator
{
    /// <summary>
    /// Calculates priority score for a statement group.
    /// Score = sum of WeightedRisk across all statements in the group.
    /// </summary>
    public double CalculatePriorityScore(StatementGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        return group.Statements.Sum(s => s.WeightedRisk);
    }

    /// <summary>
    /// Assigns priority labels to all work items based on percentile ranking.
    /// Items are sorted by PriorityScore descending with tie-breaking by
    /// risk level descending, then total statement count descending.
    /// Labels: Critical (top 10%), High (70th-89th percentile),
    /// Medium (30th-69th percentile), Low (below 30th percentile).
    /// </summary>
    public IReadOnlyList<(WorkItem Item, string Priority)> AssignPriorityLabels(
        IReadOnlyList<WorkItem> workItems)
    {
        ArgumentNullException.ThrowIfNull(workItems);

        if (workItems.Count == 0)
        {
            return [];
        }

        // Sort by PriorityScore descending, then risk level descending,
        // then total statement count descending for tie-breaking.
        var sorted = workItems
            .OrderByDescending(w => w.PriorityScore)
            .ThenByDescending(w => w.RiskLevel)
            .ThenByDescending(w => GetTotalStatementCount(w))
            .ToList();

        int totalCount = sorted.Count;

        // Calculate rank boundaries using ceiling.
        // Critical: rank <= ceil(totalCount * 0.10) — top 10%
        // High: rank in (criticalBound, highBound] — 70th-89th percentile (top 30%)
        // Medium: rank in (highBound, mediumBound] — 30th-69th percentile (top 70%)
        // Low: rank > mediumBound — below 30th percentile
        int criticalBound = (int)Math.Ceiling(totalCount * 0.10);
        int highBound = (int)Math.Ceiling(totalCount * 0.30);
        int mediumBound = (int)Math.Ceiling(totalCount * 0.70);

        var result = new List<(WorkItem Item, string Priority)>(totalCount);

        for (int i = 0; i < sorted.Count; i++)
        {
            int rank = i + 1; // 1-based rank
            string label = GetPriorityLabel(rank, criticalBound, highBound, mediumBound);
            result.Add((sorted[i], label));
        }

        return result;
    }

    private static string GetPriorityLabel(int rank, int criticalBound, int highBound, int mediumBound)
    {
        if (rank <= criticalBound)
        {
            return "Critical";
        }

        if (rank <= highBound)
        {
            return "High";
        }

        if (rank <= mediumBound)
        {
            return "Medium";
        }

        return "Low";
    }

    private static int GetTotalStatementCount(WorkItem workItem)
    {
        return workItem.AffectedObjects.Sum(ao => ao.StatementCount);
    }
}
