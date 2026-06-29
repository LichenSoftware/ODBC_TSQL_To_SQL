using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis;

/// <summary>
/// Builds the enriched object inventory by:
/// 1. Parsing each programmable object's source text (from sys.sql_modules) to detect features and risk.
/// 2. Correlating Query Store statements to their containing objects via text matching.
/// 3. Grouping remaining statements under "Ad Hoc".
/// </summary>
public sealed class ObjectInventoryBuilder : IObjectInventoryBuilder
{
    private readonly IStatementParser _parser;
    private readonly IStatementAnalyzer _analyzer;
    private readonly IRiskScorer _riskScorer;
    private readonly ILogger<ObjectInventoryBuilder> _logger;

    private static readonly TSql160Parser TsqlParser = new(initialQuotedIdentifiers: true);

    public ObjectInventoryBuilder(
        IStatementParser parser,
        IStatementAnalyzer analyzer,
        IRiskScorer riskScorer,
        ILogger<ObjectInventoryBuilder> logger)
    {
        _parser = parser;
        _analyzer = analyzer;
        _riskScorer = riskScorer;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<ObjectInventoryEntry> BuildInventory(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory)
    {
        var entries = new List<ObjectInventoryEntry>();
        var attributedStatements = new HashSet<AnalyzedStatement>(ReferenceEqualityComparer.Instance);

        // Step 1: Process each programmable object from the metadata collector
        foreach (var obj in objectInventory.ProgrammableObjects)
        {
            var objectType = MapObjectType(obj.ObjectType);

            if (obj.SourceText is null)
            {
                // Encrypted or CLR — we know the object exists but can't analyze it
                entries.Add(new ObjectInventoryEntry
                {
                    Name = obj.ObjectName,
                    Type = objectType,
                    StatementCount = 0,
                    MaxRiskScore = 3, // Default to risk 3 for inaccessible objects
                    ConversionCategories = ["manual"],
                    DetectedFeatures = obj.IsEncrypted
                        ? ["ENCRYPTED_OBJECT"]
                        : ["CLR_OR_EXTERNAL"]
                });
                continue;
            }

            // Parse the object's full source text to detect features
            var objectAnalysis = AnalyzeObjectSource(obj.SourceText, obj.ObjectName);

            // Try to correlate Query Store statements to this object
            var matchedStatements = CorrelateStatements(statements, obj.SourceText, attributedStatements);

            // Merge features from the object source analysis and any matched Query Store statements
            var allFeatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in objectAnalysis.DetectedFeatures) allFeatures.Add(f);
            foreach (var stmt in matchedStatements)
            {
                foreach (var f in stmt.Features)
                    allFeatures.Add(f.FeatureName);
            }

            // Calculate max risk from object source and matched statements
            var maxRisk = objectAnalysis.MaxRiskScore;
            foreach (var stmt in matchedStatements)
            {
                if (stmt.RiskScore > maxRisk) maxRisk = stmt.RiskScore;
            }

            // Statement count: number of statements found in the object source
            var statementCount = objectAnalysis.StatementCount;
            if (statementCount == 0 && matchedStatements.Count > 0)
            {
                statementCount = matchedStatements.Count;
            }

            // Build conversion categories from risk scores found
            var riskScores = new HashSet<int> { objectAnalysis.MaxRiskScore };
            foreach (var r in objectAnalysis.AllRiskScores) riskScores.Add(r);
            foreach (var stmt in matchedStatements) riskScores.Add(stmt.RiskScore);
            riskScores.Remove(0); // Remove default zero if present

            var conversionCategories = riskScores
                .Select(GetConversionCategory)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (conversionCategories.Count == 0)
                conversionCategories.Add("automatic");

            entries.Add(new ObjectInventoryEntry
            {
                Name = obj.ObjectName,
                Type = objectType,
                StatementCount = statementCount,
                MaxRiskScore = maxRisk > 0 ? maxRisk : 1,
                ConversionCategories = conversionCategories,
                DetectedFeatures = allFeatures.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList()
            });
        }

        // Step 2: Group remaining (unattributed) statements under "Ad Hoc"
        var adHocStatements = statements.Where(s => !attributedStatements.Contains(s)).ToList();
        if (adHocStatements.Count > 0)
        {
            var maxRisk = adHocStatements.Max(s => s.RiskScore);
            var features = adHocStatements
                .SelectMany(s => s.Features)
                .Select(f => f.FeatureName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var conversionCategories = adHocStatements
                .Select(s => GetConversionCategory(s.RiskScore))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                .ToList();

            entries.Add(new ObjectInventoryEntry
            {
                Name = "Ad Hoc",
                Type = "AdHoc",
                StatementCount = adHocStatements.Count,
                MaxRiskScore = maxRisk,
                ConversionCategories = conversionCategories,
                DetectedFeatures = features
            });
        }

        // Sort: named objects first (alphabetically by name), Ad Hoc last
        entries.Sort((a, b) =>
        {
            if (a.Type == "AdHoc" && b.Type != "AdHoc") return 1;
            if (a.Type != "AdHoc" && b.Type == "AdHoc") return -1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return entries;
    }

    /// <summary>
    /// Parses the full object source text to count statements and detect features.
    /// </summary>
    private ObjectSourceAnalysis AnalyzeObjectSource(string sourceText, string objectName)
    {
        try
        {
            var parsed = _parser.ParseBatch(sourceText);
            var allFeatures = new List<string>();
            var allRiskScores = new List<int>();

            foreach (var stmt in parsed)
            {
                var analysisResult = _analyzer.Analyze(stmt, $"{objectName}_{stmt.OrdinalPosition}");
                var riskScore = _riskScorer.ScoreStatement(analysisResult.Features, !stmt.ParseSucceeded);

                allRiskScores.Add(riskScore);
                foreach (var f in analysisResult.Features)
                {
                    allFeatures.Add(f.FeatureName);
                }
            }

            return new ObjectSourceAnalysis
            {
                StatementCount = parsed.Count,
                MaxRiskScore = allRiskScores.Count > 0 ? allRiskScores.Max() : 1,
                AllRiskScores = allRiskScores,
                DetectedFeatures = allFeatures.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to analyze source text for object {ObjectName}", objectName);
            return new ObjectSourceAnalysis
            {
                StatementCount = 0,
                MaxRiskScore = 3, // Unknown risk
                AllRiskScores = [3],
                DetectedFeatures = []
            };
        }
    }

    /// <summary>
    /// Attempts to correlate Query Store statements to a programmable object by checking
    /// if the statement text is contained within the object's source definition.
    /// </summary>
    private static List<AnalyzedStatement> CorrelateStatements(
        IReadOnlyList<AnalyzedStatement> statements,
        string objectSourceText,
        HashSet<AnalyzedStatement> alreadyAttributed)
    {
        var matched = new List<AnalyzedStatement>();

        // Normalize the object source for comparison
        var normalizedSource = NormalizeForComparison(objectSourceText);

        foreach (var stmt in statements)
        {
            if (alreadyAttributed.Contains(stmt))
                continue;

            // Get the core SQL text (strip Query Store parameter prefix if present)
            var stmtText = stmt.Source.SqlText;
            if (string.IsNullOrWhiteSpace(stmtText))
                continue;

            var normalizedStmt = NormalizeForComparison(StripParameterPrefix(stmtText));

            // Check if this statement's normalized text appears in the object source
            if (normalizedStmt.Length >= 20 && normalizedSource.Contains(normalizedStmt))
            {
                matched.Add(stmt);
                alreadyAttributed.Add(stmt);
            }
        }

        return matched;
    }

    /// <summary>
    /// Strips the Query Store parameterized prefix: (@p1 type, @p2 type)SELECT ...
    /// </summary>
    private static string StripParameterPrefix(string sqlText)
    {
        if (sqlText.Length > 0 && sqlText[0] == '(')
        {
            // Find the matching closing paren
            var depth = 0;
            for (int i = 0; i < sqlText.Length; i++)
            {
                if (sqlText[i] == '(') depth++;
                else if (sqlText[i] == ')') depth--;

                if (depth == 0)
                {
                    return sqlText[(i + 1)..].TrimStart();
                }
            }
        }
        return sqlText;
    }

    /// <summary>
    /// Normalizes SQL text for fuzzy comparison: lowercases, collapses whitespace.
    /// </summary>
    private static string NormalizeForComparison(string text)
    {
        // Collapse all whitespace (including \r\n) into single spaces and lowercase
        return Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Maps SQL Server sys.objects.type_desc to our inventory type.
    /// </summary>
    private static string MapObjectType(string typeDesc)
    {
        return typeDesc.ToUpperInvariant() switch
        {
            "SQL_STORED_PROCEDURE" => "StoredProcedure",
            "VIEW" => "View",
            "SQL_SCALAR_FUNCTION" => "ScalarFunction",
            "SQL_INLINE_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "SQL_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "SQL_TRIGGER" => "Trigger",
            "CLR_STORED_PROCEDURE" => "StoredProcedure",
            "CLR_SCALAR_FUNCTION" => "ScalarFunction",
            "CLR_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "AGGREGATE_FUNCTION" => "ScalarFunction",
            _ => "StoredProcedure" // Fallback
        };
    }

    /// <summary>
    /// Maps a risk score to a conversion category.
    /// </summary>
    private static string GetConversionCategory(int riskScore)
    {
        return riskScore switch
        {
            <= 2 => "automatic",
            3 => "semi-automatic",
            _ => "manual"
        };
    }

    private sealed record ObjectSourceAnalysis
    {
        public required int StatementCount { get; init; }
        public required int MaxRiskScore { get; init; }
        public required List<int> AllRiskScores { get; init; }
        public required List<string> DetectedFeatures { get; init; }
    }

    /// <summary>
    /// Reference equality comparer for AnalyzedStatement (record type uses value equality by default).
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<AnalyzedStatement>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public bool Equals(AnalyzedStatement? x, AnalyzedStatement? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(AnalyzedStatement obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
