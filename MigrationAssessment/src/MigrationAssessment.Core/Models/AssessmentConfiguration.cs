namespace MigrationAssessment.Core.Models;

/// <summary>
/// Configuration for an assessment pipeline run, including connection, retry, and output settings.
/// </summary>
public sealed record AssessmentConfiguration
{
    public required string ConnectionString { get; init; }
    public TimeSpan ConnectionTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRetryAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public string OutputPath { get; init; } = "./assessment-output.json";
    public double DefaultBusinessImportance { get; init; } = 1.0;

    /// <summary>
    /// Whether to generate work items after the assessment report (opt-in). Default: false.
    /// </summary>
    public bool GenerateWorkItems { get; init; } = false;

    /// <summary>
    /// Output path for the work items JSON file. Default: "./work-items.json".
    /// Only used when GenerateWorkItems is true.
    /// </summary>
    public string WorkItemOutputPath { get; init; } = "./work-items.json";

    /// <summary>
    /// Whether to generate a Markdown report for work items. Default: false.
    /// Only used when GenerateWorkItems is true.
    /// </summary>
    public bool WorkItemMarkdownEnabled { get; init; } = false;

    /// <summary>
    /// Output path for the work items Markdown file. Default: null (uses same directory as JSON).
    /// Only used when WorkItemMarkdownEnabled is true.
    /// </summary>
    public string? WorkItemMarkdownOutputPath { get; init; }

    /// <summary>
    /// Minimum risk level filter for work item generation (1-5). Default: 1 (include all).
    /// </summary>
    public int WorkItemMinRiskLevel { get; init; } = 1;

    /// <summary>
    /// Maximum number of work items to generate. Default: null (no limit).
    /// </summary>
    public int? WorkItemMaxCount { get; init; }
}
