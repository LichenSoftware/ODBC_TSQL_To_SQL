using System.Text;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Generates remediation guidance for work items by looking up the feature in the
/// knowledge base and combining it with the actual assessed SQL example.
/// If no known mapping exists, indicates manual analysis is required and sets
/// the "requires-research" flag.
/// </summary>
public sealed class RemediationGuidanceGenerator
{
    private const int MaxSqlLength = 500;
    private readonly IRemediationKnowledgeBase _knowledgeBase;

    public RemediationGuidanceGenerator(IRemediationKnowledgeBase knowledgeBase)
    {
        ArgumentNullException.ThrowIfNull(knowledgeBase);
        _knowledgeBase = knowledgeBase;
    }

    /// <summary>
    /// Generates remediation guidance for a work item covering multiple detected features.
    /// Produces one heading section per feature with a shared SQL example at the top.
    /// </summary>
    /// <param name="detectedFeatures">All feature names detected in the statement group.</param>
    /// <param name="primarySqlText">The actual SQL text from the highest-WeightedRisk statement (max 500 chars).</param>
    /// <returns>A tuple of (guidance string, requires-research flag).</returns>
    public (string Guidance, bool RequiresResearch) GenerateGuidance(IReadOnlyList<string> detectedFeatures, string primarySqlText)
    {
        ArgumentNullException.ThrowIfNull(detectedFeatures);
        if (detectedFeatures.Count == 0)
            throw new ArgumentException("detectedFeatures must contain at least one entry.", nameof(detectedFeatures));

        var truncatedSql = TruncateSql(primarySqlText);
        var sb = new StringBuilder();
        var anyRequiresResearch = false;

        // Shared "Before" section at top
        sb.AppendLine("## Before (SQL Server)");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(truncatedSql);
        sb.AppendLine("```");
        sb.AppendLine();

        // One section per feature
        foreach (var feature in detectedFeatures)
        {
            sb.AppendLine($"### {feature}");
            sb.AppendLine();

            var entry = _knowledgeBase.GetGuidance(feature);
            if (entry is null)
            {
                sb.AppendLine($"No known PostgreSQL equivalent for '{feature}'. Manual analysis required.");
                anyRequiresResearch = true;
            }
            else
            {
                sb.AppendLine(entry.IncompatibilityExplanation);
                sb.AppendLine();
                sb.AppendLine("**PostgreSQL equivalent:**");
                sb.AppendLine("```sql");
                sb.AppendLine(entry.PostgresEquivalent);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine(entry.RemediationSteps);
            }
            sb.AppendLine();
        }

        return (sb.ToString(), anyRequiresResearch);
    }

    /// <summary>
    /// Generates remediation guidance for a single feature.
    /// Delegates to the multi-feature overload.
    /// </summary>
    /// <param name="featureName">The SQL Server feature name.</param>
    /// <param name="primarySqlText">The actual SQL text from the highest-WeightedRisk statement (max 500 chars).</param>
    /// <returns>A tuple of (guidance string, requires-research flag).</returns>
    public (string Guidance, bool RequiresResearch) GenerateGuidance(string featureName, string primarySqlText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureName);
        return GenerateGuidance(new[] { featureName }, primarySqlText);
    }

    private static string TruncateSql(string? sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return string.Empty;
        }

        return sqlText.Length <= MaxSqlLength
            ? sqlText
            : sqlText[..MaxSqlLength];
    }
}
