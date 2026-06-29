using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Writes work items to a human-readable Markdown file.
/// </summary>
public interface IWorkItemMarkdownWriter
{
    /// <summary>
    /// Generates a Markdown report from the work item result at the specified path.
    /// </summary>
    Task<WriteResult> WriteAsync(
        WorkItemResult result,
        string outputPath,
        CancellationToken ct);
}
