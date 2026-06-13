namespace MigrationAssessment.Core.Models;

/// <summary>
/// Result of analyzing a single collected statement, including classification, detected features, and risk.
/// </summary>
public sealed record AnalyzedStatement
{
    public required CollectedStatement Source { get; init; }
    public required StatementClassification Classification { get; init; }
    public required IReadOnlyList<DetectedFeature> Features { get; init; }
    public required int RiskScore { get; init; }
    public required double WeightedRisk { get; init; }
    public bool ParseSucceeded { get; init; }
    public string? ParseError { get; init; }
    public int? ErrorLine { get; init; }
    public int? ErrorColumn { get; init; }
    public bool AnalysisComplete { get; init; } = true;
}
