namespace MigrationAssessment.Core.Models;

/// <summary>
/// A detected SQL Server-specific feature within a statement, including its location.
/// </summary>
public sealed record DetectedFeature
{
    public required string FeatureName { get; init; }
    public required FeatureCategory Category { get; init; }
    public required string StatementId { get; init; }
    public required int Line { get; init; }
    public required int Column { get; init; }
}
