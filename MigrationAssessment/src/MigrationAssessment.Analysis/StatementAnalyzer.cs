using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Analyzes parsed T-SQL statements by walking the AST with a visitor to detect
/// SQL Server-specific features across query constructs, functions, temporary objects,
/// and transaction patterns.
/// </summary>
public sealed class StatementAnalyzer : IStatementAnalyzer
{
    private readonly ILogger<StatementAnalyzer> _logger;

    public StatementAnalyzer(ILogger<StatementAnalyzer> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public StatementAnalysisResult Analyze(ParsedStatementResult parsedStatement, string statementId)
    {
        if (parsedStatement.AstNode is null)
        {
            return new StatementAnalysisResult
            {
                Features = [],
                AnalysisComplete = true
            };
        }

        if (parsedStatement.AstNode is not TSqlFragment fragment)
        {
            _logger.LogWarning(
                "AST node for statement {StatementId} is not a TSqlFragment (type: {Type})",
                statementId, parsedStatement.AstNode.GetType().Name);

            return new StatementAnalysisResult
            {
                Features = [],
                AnalysisComplete = true
            };
        }

        try
        {
            var visitor = new FeatureDetectionVisitor(statementId);
            fragment.Accept(visitor);

            return new StatementAnalysisResult
            {
                Features = visitor.DetectedFeatures,
                AnalysisComplete = true
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Analysis failed mid-way for statement {StatementId} at position {Position}",
                statementId, fragment.StartOffset);

            // Return partial results collected before the failure
            return new StatementAnalysisResult
            {
                Features = [],
                AnalysisComplete = false,
                FailurePosition = fragment.StartOffset
            };
        }
    }
}
