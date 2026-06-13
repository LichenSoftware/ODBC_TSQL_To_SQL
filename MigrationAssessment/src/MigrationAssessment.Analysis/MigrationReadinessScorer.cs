using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Calculates the overall migration readiness score and maps it to a classification.
/// Formula: MigrationReadinessScore = 100 × (1 - (SumOfWeightedRisks / MaxPossibleWeightedRisk))
/// </summary>
public sealed class MigrationReadinessScorer : IMigrationReadinessScorer
{
    /// <inheritdoc />
    public MigrationReadinessResult CalculateScore(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult serverFeatures)
    {
        // Req 9.7: If zero statements AND zero detected features, insufficient data
        var totalFeatureCount = serverFeatures.FeatureCounts.Values.Sum();
        if (statements.Count == 0 && totalFeatureCount == 0)
        {
            return new MigrationReadinessResult
            {
                Score = null,
                Classification = "Insufficient Data",
                HasSufficientData = false
            };
        }

        // If there are no statements but features exist, score is 100 (nothing risky to migrate)
        if (statements.Count == 0)
        {
            return new MigrationReadinessResult
            {
                Score = 100,
                Classification = ClassifyScore(100),
                HasSufficientData = true
            };
        }

        // Calculate sum of weighted risks across all statements
        double sumOfWeightedRisks = statements.Sum(s => s.WeightedRisk);

        // MaxPossibleWeightedRisk: for each statement, the max would be if its RiskScore were 5
        // WeightedRisk = RiskScore × frequency × importance
        // MaxForStatement = 5 × frequency × importance = (WeightedRisk / RiskScore) × 5
        double maxPossibleWeightedRisk = 0;
        foreach (var stmt in statements)
        {
            if (stmt.RiskScore > 0)
            {
                maxPossibleWeightedRisk += (stmt.WeightedRisk / stmt.RiskScore) * 5.0;
            }
            else
            {
                // If RiskScore is 0 (shouldn't happen per design, but defensive),
                // treat as if max contribution would be based on WeightedRisk * 5
                maxPossibleWeightedRisk += stmt.WeightedRisk * 5.0;
            }
        }

        // Avoid division by zero (all statements have zero weighted risk)
        if (maxPossibleWeightedRisk == 0)
        {
            return new MigrationReadinessResult
            {
                Score = 100,
                Classification = ClassifyScore(100),
                HasSufficientData = true
            };
        }

        // Req 9.1: Formula: MigrationReadinessScore = 100 × (1 - (Sum / Max))
        var rawScore = 100.0 * (1.0 - (sumOfWeightedRisks / maxPossibleWeightedRisk));

        // Clamp to [0, 100] and round to integer
        var score = (int)Math.Round(Math.Clamp(rawScore, 0.0, 100.0));

        return new MigrationReadinessResult
        {
            Score = score,
            Classification = ClassifyScore(score),
            HasSufficientData = true
        };
    }

    /// <summary>
    /// Maps a score in [0, 100] to its classification string.
    /// Req 9.2-9.6: Deterministic score-to-classification mapping.
    /// </summary>
    public static string ClassifyScore(int score)
    {
        return score switch
        {
            >= 90 => "Excellent Candidate",
            >= 76 => "Good Candidate",
            >= 51 => "Moderate Candidate - Significant Work Required",
            >= 26 => "High Risk",
            _ => "Not Recommended for Migration"
        };
    }
}
