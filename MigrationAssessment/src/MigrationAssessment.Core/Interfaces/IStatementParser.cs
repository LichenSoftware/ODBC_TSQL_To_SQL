using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Parses T-SQL text into individual statement results with classification.
/// </summary>
public interface IStatementParser
{
    /// <summary>
    /// Parses a T-SQL batch (which may contain GO separators or multiple statements)
    /// and returns a result for each individual statement found.
    /// </summary>
    /// <param name="sqlText">The raw T-SQL text to parse.</param>
    /// <returns>A list of parsed statement results in ordinal order.</returns>
    IReadOnlyList<ParsedStatementResult> ParseBatch(string sqlText);
}

/// <summary>
/// Result of parsing a single T-SQL statement from a batch.
/// </summary>
public sealed record ParsedStatementResult
{
    /// <summary>
    /// 1-based ordinal position of this statement within the batch.
    /// </summary>
    public required int OrdinalPosition { get; init; }

    /// <summary>
    /// The original SQL text of the statement.
    /// </summary>
    public required string StatementText { get; init; }

    /// <summary>
    /// The classification of the statement type.
    /// </summary>
    public required StatementClassification Classification { get; init; }

    /// <summary>
    /// Whether the statement was successfully parsed.
    /// </summary>
    public bool ParseSucceeded { get; init; } = true;

    /// <summary>
    /// Error description if parsing failed.
    /// </summary>
    public string? ParseError { get; init; }

    /// <summary>
    /// Line number of the parse error (1-based).
    /// </summary>
    public int? ErrorLine { get; init; }

    /// <summary>
    /// Column number of the parse error (1-based).
    /// </summary>
    public int? ErrorColumn { get; init; }

    /// <summary>
    /// The AST node (TSqlFragment) stored as object to avoid ScriptDom dependency in Core.
    /// </summary>
    public object? AstNode { get; init; }
}
