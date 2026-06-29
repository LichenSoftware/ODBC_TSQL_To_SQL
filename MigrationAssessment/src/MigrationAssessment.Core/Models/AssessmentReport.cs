namespace MigrationAssessment.Core.Models;

/// <summary>
/// The complete migration assessment report containing all sections.
/// </summary>
public sealed record AssessmentReport
{
    public required ExecutiveSummary Summary { get; init; }
    public required RiskBreakdown RiskBreakdown { get; init; }
    public required IReadOnlyList<MigrationChallenge> TopChallenges { get; init; }
    public required MigrationEffortEstimate Effort { get; init; }
    public required MigrationRecommendation Recommendation { get; init; }
    public required IReadOnlyList<CollectionFailure> FailureSummary { get; init; }
    public SchemaAnalysisResult? SchemaAnalysis { get; init; }
}
