namespace MigrationAssessment.Core.Models;

/// <summary>
/// Options controlling how statement collection is performed.
/// </summary>
public sealed record CollectionOptions
{
    public TimeSpan QueryTimeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxBatchSize { get; init; } = 10_000;
}
