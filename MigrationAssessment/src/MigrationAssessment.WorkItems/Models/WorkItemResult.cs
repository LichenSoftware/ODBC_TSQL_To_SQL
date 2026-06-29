namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Result of work item generation.
/// </summary>
public sealed record WorkItemResult
{
    /// <summary>All generated work items, ordered by PriorityScore descending.</summary>
    public required IReadOnlyList<WorkItem> WorkItems { get; init; }

    /// <summary>Generation metadata.</summary>
    public required WorkItemMetadata Metadata { get; init; }

    /// <summary>Whether generation succeeded.</summary>
    public bool Succeeded { get; init; } = true;

    /// <summary>Error message if generation failed.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Validation summary from the post-generation validation gate.
    /// Contains any warnings about SQL syntax, object attribution, or effort ranges.
    /// Null if validation was not run.
    /// </summary>
    public ValidationSummary? ValidationSummary { get; init; }
}
