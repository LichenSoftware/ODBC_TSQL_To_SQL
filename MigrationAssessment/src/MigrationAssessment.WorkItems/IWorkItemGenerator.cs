using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Generates structured work items from assessment results.
/// </summary>
public interface IWorkItemGenerator
{
    /// <summary>
    /// Generates work items from in-memory assessment data (pipeline integration).
    /// </summary>
    WorkItemResult GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config);

    /// <summary>
    /// Generates work items from in-memory assessment data with object inventory for object attribution.
    /// </summary>
    WorkItemResult GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection,
        WorkItemConfiguration config,
        IReadOnlyList<ObjectInventoryEntry> objectInventory);

    /// <summary>
    /// Generates work items from a saved assessment JSON file (standalone mode).
    /// </summary>
    Task<WorkItemResult> GenerateFromFileAsync(
        string assessmentJsonPath,
        WorkItemConfiguration config,
        CancellationToken ct);
}
