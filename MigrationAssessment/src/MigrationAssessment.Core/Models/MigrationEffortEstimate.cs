namespace MigrationAssessment.Core.Models;

/// <summary>
/// Estimated effort for the migration broken down by category.
/// </summary>
public sealed record MigrationEffortEstimate
{
    public required HourRange SchemaConversion { get; init; }
    public required HourRange CodeConversion { get; init; }
    public required HourRange Testing { get; init; }
    public required HourRange DataMigration { get; init; }
    public required HourRange PerformanceTuning { get; init; }
    public required string TotalClassification { get; init; }

    /// <summary>
    /// Optional confidence summary breaking down effort by confidence level.
    /// Present when work item data is available to compute per-item confidence.
    /// </summary>
    public EffortConfidenceSummary? ConfidenceSummary { get; init; }
}

/// <summary>
/// Aggregated effort breakdown by confidence level for the assessment report.
/// </summary>
public sealed record EffortConfidenceSummary
{
    public required HourRange HighConfidenceHours { get; init; }
    public required HourRange MediumConfidenceHours { get; init; }
    public required HourRange LowConfidenceHours { get; init; }
    public required string Notes { get; init; }
}
