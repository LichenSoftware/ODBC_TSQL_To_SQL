using MigrationAssessment.Core.Interfaces;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Calculates weighted risk incorporating execution frequency and business importance.
/// Formula: RiskScore × ExecutionFrequency × BusinessImportance
/// </summary>
public sealed class WeightedComplexityCalculator : IWeightedComplexityCalculator
{
    /// <summary>
    /// Calculates the weighted risk for a statement.
    /// Formula: RiskScore × ExecutionFrequency × BusinessImportance
    /// </summary>
    /// <param name="riskScore">The base risk score (1-5).</param>
    /// <param name="executionFrequency">How often the statement executes. Defaults to 1 if less than 1.</param>
    /// <param name="businessImportance">The business importance multiplier. Clamped to [1.0, 5.0].</param>
    /// <returns>The weighted risk value.</returns>
    public double CalculateWeightedRisk(int riskScore, long executionFrequency, double businessImportance)
    {
        // Req 8.3: default frequency of 1 when unavailable (less than 1)
        var frequency = Math.Max(1, executionFrequency);

        // Req 8.4: default importance of 1.0 when unassigned (less than 1.0)
        var importance = Math.Max(1.0, businessImportance);

        // Cap at 5.0 per requirement 8.1 (importance range is 1.0 to 5.0)
        importance = Math.Min(5.0, importance);

        return riskScore * frequency * importance;
    }
}
