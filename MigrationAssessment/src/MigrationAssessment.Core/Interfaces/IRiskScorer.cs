using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Assigns a risk score (1-5) to a statement based on its detected features.
/// </summary>
public interface IRiskScorer
{
    /// <summary>
    /// Scores a statement based on its detected features and parse status.
    /// </summary>
    /// <param name="features">The features detected in the statement.</param>
    /// <param name="parseFailed">Whether the statement failed to parse.</param>
    /// <returns>A risk score from 1 (trivial) to 5 (critical).</returns>
    int ScoreStatement(IReadOnlyList<DetectedFeature> features, bool parseFailed);
}
