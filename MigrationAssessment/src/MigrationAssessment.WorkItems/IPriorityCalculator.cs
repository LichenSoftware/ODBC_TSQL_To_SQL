using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Calculates priority scores and assigns percentile-based priority labels.
/// </summary>
public interface IPriorityCalculator
{
    /// <summary>
    /// Calculates priority score for a statement group.
    /// Score = sum of WeightedRisk across all statements in the group.
    /// </summary>
    double CalculatePriorityScore(StatementGroup group);

    /// <summary>
    /// Assigns priority labels to all work items based on percentile ranking.
    /// Critical: top 10%, High: 70th-89th percentile, Medium: 30th-69th percentile, Low: below 30th percentile.
    /// </summary>
    IReadOnlyList<(WorkItem Item, string Priority)> AssignPriorityLabels(
        IReadOnlyList<WorkItem> workItems);
}
