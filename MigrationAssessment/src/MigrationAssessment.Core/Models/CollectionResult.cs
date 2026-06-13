namespace MigrationAssessment.Core.Models;

/// <summary>
/// The result of a statement collection operation from a single source.
/// </summary>
public sealed record CollectionResult
{
    public required IReadOnlyList<CollectedStatement> Statements { get; init; }
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }
    public int TotalEventsProcessed { get; init; }
}
