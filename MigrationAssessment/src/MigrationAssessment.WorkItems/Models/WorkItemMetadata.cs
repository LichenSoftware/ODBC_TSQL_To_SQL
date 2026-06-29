namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Metadata about the work item generation run.
/// </summary>
public sealed record WorkItemMetadata
{
    /// <summary>When the work items were generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Path to the source assessment file, or null if generated from in-memory data.</summary>
    public required string? SourceAssessmentPath { get; init; }

    /// <summary>Total number of work items generated.</summary>
    public required int TotalWorkItemCount { get; init; }

    /// <summary>Aggregated effort estimate across all work items.</summary>
    public required HourRange TotalEstimatedEffort { get; init; }
}
