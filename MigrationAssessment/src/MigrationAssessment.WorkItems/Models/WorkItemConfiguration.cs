namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Configuration for work item generation.
/// </summary>
public sealed record WorkItemConfiguration
{
    /// <summary>Output JSON file path. Default: "./work-items.json".</summary>
    public string OutputJsonPath { get; init; } = "./work-items.json";

    /// <summary>Whether to generate Markdown output. Default: false.</summary>
    public bool MarkdownEnabled { get; init; } = false;

    /// <summary>Markdown output path. Default: same directory as JSON, "work-items.md".</summary>
    public string? MarkdownOutputPath { get; init; }

    /// <summary>Minimum risk level filter (1-5). Default: 1 (include all).</summary>
    public int MinimumRiskLevel { get; init; } = 1;

    /// <summary>Maximum work item count. Default: null (no limit).</summary>
    public int? MaxWorkItemCount { get; init; }
}
