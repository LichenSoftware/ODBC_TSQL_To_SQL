namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Summary of validation checks run against generated work items.
/// Included in the top-level JSON output to surface any inconsistencies.
/// </summary>
public sealed record ValidationSummary
{
    /// <summary>Whether all validation checks passed (zero warnings).</summary>
    public required bool Passed { get; init; }

    /// <summary>Total number of validation warnings found.</summary>
    public required int WarningCount { get; init; }

    /// <summary>Individual validation warnings, empty if all checks passed.</summary>
    public required IReadOnlyList<ValidationWarning> Warnings { get; init; }
}

/// <summary>
/// A single validation warning for a specific work item.
/// </summary>
public sealed record ValidationWarning
{
    /// <summary>The work item ID (e.g., "WI-001") that triggered the warning.</summary>
    public required string WorkItemId { get; init; }

    /// <summary>Warning category: "sql-syntax", "object-attribution", "effort-range".</summary>
    public required string Category { get; init; }

    /// <summary>Human-readable description of the issue.</summary>
    public required string Message { get; init; }
}
