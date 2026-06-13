namespace MigrationAssessment.Core.Models;

/// <summary>
/// Breakdown of statements by risk level.
/// </summary>
public sealed record RiskBreakdown
{
    public required IReadOnlyDictionary<int, int> LevelCounts { get; init; }
}
