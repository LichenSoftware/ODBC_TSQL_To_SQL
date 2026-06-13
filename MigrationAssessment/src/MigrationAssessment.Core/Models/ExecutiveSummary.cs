namespace MigrationAssessment.Core.Models;

/// <summary>
/// Executive summary section of the assessment report.
/// </summary>
public sealed record ExecutiveSummary
{
    public required int? MigrationReadinessScore { get; init; }
    public required string Classification { get; init; }
    public required int TotalStatementCount { get; init; }
    public required IReadOnlyDictionary<int, int> RiskDistribution { get; init; }
    public required IReadOnlyDictionary<int, double> RiskPercentages { get; init; }
}
