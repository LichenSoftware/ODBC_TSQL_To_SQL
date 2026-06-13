namespace MigrationAssessment.Core.Models;

/// <summary>
/// Records a failure that occurred during data collection from a specific source.
/// </summary>
public sealed record CollectionFailure
{
    public required string SourceName { get; init; }
    public required string Reason { get; init; }
}
