namespace MigrationAssessment.Core.Models;

/// <summary>
/// A top migration challenge identified in the assessment.
/// </summary>
public sealed record MigrationChallenge
{
    public required string ObjectName { get; init; }
    public required string ObjectType { get; init; }
    public required int RiskScore { get; init; }
    public required double WeightedRisk { get; init; }
    public required IReadOnlyList<string> Features { get; init; }
}
