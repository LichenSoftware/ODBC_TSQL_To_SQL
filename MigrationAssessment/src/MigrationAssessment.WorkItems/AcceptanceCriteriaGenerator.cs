namespace MigrationAssessment.WorkItems;

/// <summary>
/// Generates verifiable acceptance criteria for migration work items.
/// Always produces at least 2 criteria. Higher risk levels (3-5) receive
/// additional verification steps appropriate to their complexity.
/// </summary>
public sealed class AcceptanceCriteriaGenerator
{
    /// <summary>
    /// Generates acceptance criteria for a work item.
    /// Always produces at least 2 criteria. Risk 4-5 get additional criteria.
    /// </summary>
    /// <param name="featureName">The SQL Server feature/construct name.</param>
    /// <param name="riskLevel">The risk level (1-5).</param>
    /// <param name="objectName">The affected database object name (or "Ad Hoc Queries").</param>
    /// <returns>A list of verifiable acceptance criteria strings.</returns>
    public IReadOnlyList<string> GenerateCriteria(string featureName, int riskLevel, string objectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        ArgumentException.ThrowIfNullOrWhiteSpace(objectName);

        var criteria = new List<string>
        {
            $"All instances of {featureName} usage have been replaced in {objectName}.",
            "The PostgreSQL equivalent produces correct results matching the original SQL Server behavior."
        };

        if (riskLevel >= 3)
        {
            criteria.Add("Unit tests verify the converted logic handles edge cases correctly.");
        }

        if (riskLevel == 4)
        {
            criteria.Add("The redesigned PostgreSQL pattern handles concurrency scenarios correctly.");
        }

        if (riskLevel >= 5)
        {
            criteria.Add("The alternative architecture has been reviewed and approved by the team.");
            criteria.Add(
                "Integration tests confirm the replacement solution interoperates correctly with dependent systems.");
        }

        return criteria;
    }
}
