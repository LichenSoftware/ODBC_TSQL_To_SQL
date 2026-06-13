using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Ranks analyzed statements by weighted risk for prioritization.
/// </summary>
public static class StatementRanker
{
    /// <summary>
    /// Ranks analyzed statements by WeightedRisk descending, with RiskScore descending as a tiebreaker.
    /// </summary>
    /// <param name="statements">The statements to rank.</param>
    /// <returns>An ordered list of statements ranked by priority.</returns>
    public static IReadOnlyList<AnalyzedStatement> RankByWeightedRisk(IEnumerable<AnalyzedStatement> statements)
    {
        return statements
            .OrderByDescending(s => s.WeightedRisk)
            .ThenByDescending(s => s.RiskScore)
            .ToList();
    }
}
