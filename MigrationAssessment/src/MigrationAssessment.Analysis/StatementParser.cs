using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Parses T-SQL batches using ScriptDom's TSql160Parser, splitting on GO delimiters
/// and classifying each statement by type.
/// </summary>
public sealed class StatementParser : IStatementParser
{
    private readonly ILogger<StatementParser> _logger;
    private readonly TSql160Parser _parser;

    private static readonly Regex GoBatchDelimiter = new(
        @"^\s*GO\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Matches Query Store's parameterized statement prefix format:
    /// (@param1 type1, @param2 type2, ...)actual SQL here
    /// </summary>
    private static readonly Regex ParameterDeclarationPrefix = new(
        @"^\s*\((@\w+\s+\w+(?:\s*\([^)]*\))?(?:\s*,\s*@\w+\s+\w+(?:\s*\([^)]*\))?)*)\)\s*",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private const int MaxErrorStatementTextLength = 1000;

    public StatementParser(ILogger<StatementParser> logger)
    {
        _logger = logger;
        _parser = new TSql160Parser(initialQuotedIdentifiers: true);
    }

    /// <inheritdoc />
    public IReadOnlyList<ParsedStatementResult> ParseBatch(string sqlText)
    {
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return [];
        }

        // Strip Query Store parameterized statement prefix: (@p1 int, @p2 nvarchar(100))SELECT ...
        var cleanedText = StripParameterDeclarationPrefix(sqlText);

        var segments = SplitOnGo(cleanedText);
        var results = new List<ParsedStatementResult>();
        var ordinal = 1;

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                continue;
            }

            var segmentResults = ParseSegment(segment, ref ordinal);
            results.AddRange(segmentResults);
        }

        return results;
    }

    /// <summary>
    /// Strips the Query Store parameterized statement prefix if present.
    /// Query Store captures statements like: (@CustomerID int,@ProductID int)SELECT * FROM...
    /// ScriptDom cannot parse these — we need to remove the prefix.
    /// </summary>
    private static string StripParameterDeclarationPrefix(string sqlText)
    {
        var match = ParameterDeclarationPrefix.Match(sqlText);
        if (match.Success)
        {
            return sqlText[match.Length..];
        }
        return sqlText;
    }

    /// <summary>
    /// Splits the input SQL text on GO batch delimiters (case-insensitive, on its own line).
    /// </summary>
    private static IReadOnlyList<string> SplitOnGo(string sqlText)
    {
        return GoBatchDelimiter.Split(sqlText);
    }

    /// <summary>
    /// Parses a single batch segment (no GO delimiters) using ScriptDom.
    /// </summary>
    private List<ParsedStatementResult> ParseSegment(string segment, ref int ordinal)
    {
        var results = new List<ParsedStatementResult>();

        using var reader = new StringReader(segment);
        var fragment = _parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
        {
            var firstError = errors[0];
            var truncatedText = segment.Length > MaxErrorStatementTextLength
                ? segment[..MaxErrorStatementTextLength]
                : segment;

            // Try to classify from partial AST if available
            var classification = StatementClassification.Unknown;
            if (fragment is TSqlScript partialScript && partialScript.Batches.Count > 0)
            {
                foreach (var batch in partialScript.Batches)
                {
                    foreach (var stmt in batch.Statements)
                    {
                        classification = ClassifyStatement(stmt);
                        results.Add(new ParsedStatementResult
                        {
                            OrdinalPosition = ordinal++,
                            StatementText = GetStatementText(stmt, segment),
                            Classification = classification,
                            ParseSucceeded = false,
                            ParseError = firstError.Message,
                            ErrorLine = firstError.Line,
                            ErrorColumn = firstError.Column,
                            AstNode = stmt
                        });
                    }
                }

                // If we got statements from the partial AST, return them
                if (results.Count > 0)
                {
                    return results;
                }
            }

            // No partial AST statements available — record the whole segment as a failure
            _logger.LogWarning(
                "Parse failure at line {Line}, column {Column}: {Error}",
                firstError.Line, firstError.Column, firstError.Message);

            results.Add(new ParsedStatementResult
            {
                OrdinalPosition = ordinal++,
                StatementText = truncatedText,
                Classification = classification,
                ParseSucceeded = false,
                ParseError = firstError.Message,
                ErrorLine = firstError.Line,
                ErrorColumn = firstError.Column,
                AstNode = fragment
            });

            return results;
        }

        // Parse succeeded — walk the AST to extract individual statements
        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                {
                    var stmtText = GetStatementText(statement, segment);
                    var classification = ClassifyStatement(statement);

                    results.Add(new ParsedStatementResult
                    {
                        OrdinalPosition = ordinal++,
                        StatementText = stmtText,
                        Classification = classification,
                        ParseSucceeded = true,
                        AstNode = statement
                    });
                }
            }
        }

        // If the script parsed but had no statements (e.g., whitespace/comments only), skip
        return results;
    }

    /// <summary>
    /// Extracts the text of a statement from the source using its fragment offsets.
    /// </summary>
    private static string GetStatementText(TSqlFragment fragment, string source)
    {
        if (fragment.StartOffset >= 0 && fragment.FragmentLength > 0
            && fragment.StartOffset + fragment.FragmentLength <= source.Length)
        {
            return source.Substring(fragment.StartOffset, fragment.FragmentLength);
        }

        // Fallback: return entire source (shouldn't happen for well-formed AST)
        return source;
    }

    /// <summary>
    /// Classifies a TSqlStatement into the appropriate StatementClassification enum value.
    /// </summary>
    private static StatementClassification ClassifyStatement(TSqlStatement statement)
    {
        return statement switch
        {
            // DML
            SelectStatement => StatementClassification.Select,
            InsertStatement => StatementClassification.Insert,
            UpdateStatement => StatementClassification.Update,
            DeleteStatement => StatementClassification.Delete,
            MergeStatement => StatementClassification.Merge,

            // Procedural
            CreateProcedureStatement => StatementClassification.Procedural,
            AlterProcedureStatement => StatementClassification.Procedural,
            CreateFunctionStatement => StatementClassification.Procedural,
            AlterFunctionStatement => StatementClassification.Procedural,
            BeginEndBlockStatement => StatementClassification.Procedural,
            WhileStatement => StatementClassification.Procedural,
            IfStatement => StatementClassification.Procedural,
            DeclareVariableStatement => StatementClassification.Procedural,

            // DDL
            CreateTableStatement => StatementClassification.Ddl,
            AlterTableStatement => StatementClassification.Ddl,
            DropTableStatement => StatementClassification.Ddl,
            CreateViewStatement => StatementClassification.Ddl,
            AlterViewStatement => StatementClassification.Ddl,
            DropViewStatement => StatementClassification.Ddl,
            CreateIndexStatement => StatementClassification.Ddl,
            AlterIndexStatement => StatementClassification.Ddl,
            DropIndexStatement => StatementClassification.Ddl,
            CreateSchemaStatement => StatementClassification.Ddl,
            AlterSchemaStatement => StatementClassification.Ddl,
            DropSchemaStatement => StatementClassification.Ddl,
            CreateSequenceStatement => StatementClassification.Ddl,
            AlterSequenceStatement => StatementClassification.Ddl,
            DropSequenceStatement => StatementClassification.Ddl,
            CreateTriggerStatement => StatementClassification.Ddl,
            AlterTriggerStatement => StatementClassification.Ddl,
            DropTriggerStatement => StatementClassification.Ddl,
            CreateTypeStatement => StatementClassification.Ddl,
            DropTypeStatement => StatementClassification.Ddl,
            TruncateTableStatement => StatementClassification.Ddl,
            DropStatisticsStatement => StatementClassification.Ddl,
            CreateStatisticsStatement => StatementClassification.Ddl,
            DropDatabaseStatement => StatementClassification.Ddl,
            CreateDatabaseStatement => StatementClassification.Ddl,
            AlterDatabaseStatement => StatementClassification.Ddl,

            // DCL
            GrantStatement => StatementClassification.Dcl,
            DenyStatement => StatementClassification.Dcl,
            RevokeStatement => StatementClassification.Dcl,

            // TCL
            BeginTransactionStatement => StatementClassification.Tcl,
            CommitTransactionStatement => StatementClassification.Tcl,
            RollbackTransactionStatement => StatementClassification.Tcl,
            SaveTransactionStatement => StatementClassification.Tcl,

            // Unknown (fallback)
            _ => StatementClassification.Unknown
        };
    }
}
