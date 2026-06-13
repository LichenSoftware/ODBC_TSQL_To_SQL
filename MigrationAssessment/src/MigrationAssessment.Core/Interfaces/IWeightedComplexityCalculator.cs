namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Calculates weighted risk incorporating execution frequency and business importance.
/// </summary>
public interface IWeightedComplexityCalculator
{
    /// <summary>
    /// Calculates the weighted risk value for a statement.
    /// </summary>
    /// <param name="riskScore">The base risk score (1-5).</param>
    /// <param name="executionFrequency">How often the statement executes (≥ 1).</param>
    /// <param name="businessImportance">The business importance multiplier (1.0-5.0).</param>
    /// <returns>The weighted risk value.</returns>
    double CalculateWeightedRisk(int riskScore, long executionFrequency, double businessImportance);
}
