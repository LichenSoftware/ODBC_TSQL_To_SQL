using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core.Interfaces;

/// <summary>
/// Analyzes a parsed T-SQL statement to detect SQL Server-specific features.
/// </summary>
public interface IStatementAnalyzer
{
    /// <summary>
    /// Walks the AST of a parsed statement to detect SQL Server-specific features
    /// such as query constructs, function calls, temporary objects, and transaction patterns.
    /// </summary>
    /// <param name="parsedStatement">The parsed statement with its AST node.</param>
    /// <param name="statementId">A unique identifier for the statement being analyzed.</param>
    /// <returns>The analysis result containing detected features and completion status.</returns>
    StatementAnalysisResult Analyze(ParsedStatementResult parsedStatement, string statementId);
}

/// <summary>
/// Result of analyzing a single parsed statement for SQL Server-specific features.
/// </summary>
public sealed record StatementAnalysisResult
{
    /// <summary>
    /// All SQL Server-specific features detected within the statement.
    /// </summary>
    public required IReadOnlyList<DetectedFeature> Features { get; init; }

    /// <summary>
    /// Whether the analysis completed successfully for the entire statement.
    /// False indicates the visitor encountered unrecognized syntax mid-analysis.
    /// </summary>
    public required bool AnalysisComplete { get; init; }

    /// <summary>
    /// The character position where analysis failed, if AnalysisComplete is false.
    /// </summary>
    public int? FailurePosition { get; init; }
}
