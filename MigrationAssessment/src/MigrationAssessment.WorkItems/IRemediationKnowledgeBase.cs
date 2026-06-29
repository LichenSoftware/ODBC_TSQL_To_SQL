using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Provides remediation guidance templates for known SQL Server features.
/// </summary>
public interface IRemediationKnowledgeBase
{
    /// <summary>
    /// Gets remediation guidance for a feature, including PostgreSQL equivalent.
    /// Returns null if no guidance is available for the feature.
    /// </summary>
    RemediationEntry? GetGuidance(string featureName);

    /// <summary>
    /// Checks if a feature has known guidance in the knowledge base.
    /// </summary>
    bool HasGuidance(string featureName);
}
