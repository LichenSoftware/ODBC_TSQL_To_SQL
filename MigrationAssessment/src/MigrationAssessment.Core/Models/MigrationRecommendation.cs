namespace MigrationAssessment.Core.Models;

/// <summary>
/// The final migration recommendation with reasoning and score.
/// </summary>
public sealed record MigrationRecommendation
{
    public required string Recommendation { get; init; }
    public required string Reasoning { get; init; }
    public required int? MigrationReadinessScore { get; init; }
}
