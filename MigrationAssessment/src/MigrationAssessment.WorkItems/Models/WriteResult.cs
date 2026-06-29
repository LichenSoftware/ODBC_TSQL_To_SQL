namespace MigrationAssessment.WorkItems.Models;

/// <summary>
/// Result of a write operation (JSON or Markdown output).
/// </summary>
public sealed record WriteResult
{
    /// <summary>Whether the write operation succeeded</summary>
    public required bool Succeeded { get; init; }

    /// <summary>Error message if the write operation failed</summary>
    public string? ErrorMessage { get; init; }
}
