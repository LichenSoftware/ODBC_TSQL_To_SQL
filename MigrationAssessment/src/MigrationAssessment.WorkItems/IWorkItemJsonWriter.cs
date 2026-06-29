using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Writes work items to a JSON file conforming to the published schema.
/// </summary>
public interface IWorkItemJsonWriter
{
    /// <summary>
    /// Serializes the work item result to a JSON file at the specified path.
    /// </summary>
    Task<WriteResult> WriteAsync(
        WorkItemResult result,
        string outputPath,
        CancellationToken ct);
}
