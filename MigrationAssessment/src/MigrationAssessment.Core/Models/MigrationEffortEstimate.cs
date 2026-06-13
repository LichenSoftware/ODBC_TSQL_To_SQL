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
}
