using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Estimates effort for work items based on risk level and statement count
/// with a complexity reduction factor for repeated patterns.
/// </summary>
public interface IEffortEstimator
{
    /// <summary>
    /// Estimates effort for a multi-feature work item.
    /// Sums per-feature effort ranges for each distinct feature's risk level.
    /// </summary>
    HourRange EstimateEffort(IReadOnlyList<string> detectedFeatures, int statementCount);

    /// <summary>
    /// Calculates effort for a work item.
    /// First statement uses full effort range for its risk level.
    /// Each additional statement applies a 0.7 reduction factor.
    /// </summary>
    HourRange EstimateEffort(int riskLevel, int statementCount);

    /// <summary>
    /// Aggregates effort across all work items.
    /// </summary>
    HourRange CalculateTotalEffort(IReadOnlyList<WorkItem> workItems);
}
