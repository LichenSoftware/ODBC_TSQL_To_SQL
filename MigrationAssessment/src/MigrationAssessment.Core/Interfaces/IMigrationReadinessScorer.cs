using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Calculates the overall migration readiness score and classification.
/// </summary>
public interface IMigrationReadinessScorer
{
    /// <summary>
    /// Calculates the migration readiness score from analyzed statements and server features.
    /// </summary>
    /// <param name="statements">All analyzed statements.</param>
    /// <param name="serverFeatures">Detected server-level features.</param>
    /// <returns>The readiness result including score, classification, and data sufficiency.</returns>
    MigrationReadinessResult CalculateScore(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult serverFeatures);
}
