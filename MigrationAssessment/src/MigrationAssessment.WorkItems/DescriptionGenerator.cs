using System.Text;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Generates plain-language work item descriptions explaining the incompatibility,
/// occurrence count, and business impact based on execution frequency.
/// </summary>
public sealed class DescriptionGenerator
{
    /// <summary>
    /// Generates a multi-line description for a work item with multiple detected features,
    /// including what the SQL Server constructs are, why they are incompatible with PostgreSQL,
    /// how many occurrences were found, and the business impact based on execution frequency.
    /// </summary>
    /// <param name="detectedFeatures">All detected SQL Server feature/construct names.</param>
    /// <param name="riskLevel">The risk level (1-5).</param>
    /// <param name="occurrenceCount">Number of occurrences found in the analyzed codebase.</param>
    /// <param name="totalExecutionCount">Combined total executions recorded across all occurrences.</param>
    /// <param name="objectName">The affected database object name, or null for ad hoc queries.</param>
    /// <returns>A multi-line plain-language description.</returns>
    public string GenerateDescription(
        IReadOnlyList<string> detectedFeatures,
        int riskLevel,
        int occurrenceCount,
        long totalExecutionCount,
        string? objectName)
    {
        ArgumentNullException.ThrowIfNull(detectedFeatures);
        if (detectedFeatures.Count == 0)
            throw new ArgumentException("At least one feature must be provided.", nameof(detectedFeatures));

        var resolvedObjectName = string.IsNullOrWhiteSpace(objectName)
            ? "Ad Hoc Queries"
            : objectName;

        var sb = new StringBuilder();

        // Line 1: What the constructs are and where they're found
        if (detectedFeatures.Count == 1)
        {
            sb.AppendLine(
                $"The SQL Server feature '{detectedFeatures[0]}' is used in {resolvedObjectName} " +
                $"and is not directly supported in PostgreSQL.");
        }
        else
        {
            var featureList = string.Join(", ", detectedFeatures.Select(f => $"'{f}'"));
            sb.AppendLine(
                $"The SQL Server features {featureList} are used in {resolvedObjectName} " +
                $"and are not directly supported in PostgreSQL.");
        }

        // Line 2: Occurrence count and execution frequency
        sb.AppendLine(
            $"Found {occurrenceCount} {(occurrenceCount == 1 ? "occurrence" : "occurrences")} " +
            $"across the analyzed codebase with a combined total of " +
            $"{totalExecutionCount:N0} {(totalExecutionCount == 1 ? "execution" : "executions")} recorded.");

        // Line 3: Business impact assessment
        var impactLevel = GetBusinessImpactLevel(totalExecutionCount);
        sb.AppendLine(
            $"This represents a {impactLevel} business impact based on execution frequency.");

        // Line 4: Risk level explanation
        sb.Append(
            $"Risk Level: {riskLevel} ({GetRiskLevelExplanation(riskLevel)}).");

        return sb.ToString();
    }

    /// <summary>
    /// Generates a multi-line description for a work item including what the SQL Server
    /// construct is, why it's incompatible with PostgreSQL, how many occurrences were found,
    /// and the business impact based on execution frequency.
    /// </summary>
    /// <param name="featureName">The SQL Server feature/construct name.</param>
    /// <param name="riskLevel">The risk level (1-5).</param>
    /// <param name="occurrenceCount">Number of occurrences found in the analyzed codebase.</param>
    /// <param name="totalExecutionCount">Combined total executions recorded across all occurrences.</param>
    /// <param name="objectName">The affected database object name, or null for ad hoc queries.</param>
    /// <returns>A multi-line plain-language description.</returns>
    public string GenerateDescription(
        string featureName,
        int riskLevel,
        int occurrenceCount,
        long totalExecutionCount,
        string? objectName)
        => GenerateDescription(new[] { featureName }, riskLevel, occurrenceCount, totalExecutionCount, objectName);

    private static string GetBusinessImpactLevel(long totalExecutionCount)
    {
        return totalExecutionCount switch
        {
            0 => "minimal",
            < 100 => "low",
            < 1_000 => "moderate",
            < 10_000 => "significant",
            < 100_000 => "high",
            _ => "critical"
        };
    }

    private static string GetRiskLevelExplanation(int riskLevel)
    {
        return riskLevel switch
        {
            1 => "fully compatible or trivial change",
            2 => "simple syntax substitution for PostgreSQL compatibility",
            3 => "requires procedural logic changes for PostgreSQL compatibility",
            4 => "requires design pattern changes for PostgreSQL compatibility",
            5 => "requires architectural redesign or alternative technology for PostgreSQL migration",
            _ => "unknown risk classification"
        };
    }
}
