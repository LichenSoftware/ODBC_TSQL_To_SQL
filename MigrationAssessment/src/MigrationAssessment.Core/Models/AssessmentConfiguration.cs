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
}
