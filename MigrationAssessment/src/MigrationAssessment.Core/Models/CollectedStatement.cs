namespace MigrationAssessment.Core.Models;

/// <summary>
/// Represents a collected SQL statement with its source metadata and performance metrics.
/// </summary>
public sealed record CollectedStatement
{
    public required string SqlText { get; init; }
    public required StatementSource Source { get; init; }
    public required string QueryHash { get; init; }
    public long ExecutionCount { get; init; } = 1;
    public double AvgDurationMs { get; init; }
    public double CpuMs { get; init; }
    public long LogicalReads { get; init; }
    public long? PlanId { get; init; }
    public string? PlanHash { get; init; }
    public string? DatabaseName { get; init; }
    public string? ExecutingPrincipal { get; init; }
    public DateTimeOffset? ExecutionTimestamp { get; init; }
}
