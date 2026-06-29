using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Groups analyzed statements into logical work item clusters based on
/// feature name and database object affinity.
/// </summary>
public interface IStatementGrouper
{
    /// <summary>
    /// Groups statements into work item clusters.
    /// Each cluster becomes one work item.
    /// </summary>
    IReadOnlyList<StatementGroup> GroupStatements(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        int minimumRiskLevel);

    /// <summary>
    /// Groups statements into work item clusters using the parsed object inventory
    /// to attribute statements to their containing named objects.
    /// </summary>
    IReadOnlyList<StatementGroup> GroupStatements(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        int minimumRiskLevel,
        IReadOnlyList<ObjectInventoryEntry> objectInventory);
}
