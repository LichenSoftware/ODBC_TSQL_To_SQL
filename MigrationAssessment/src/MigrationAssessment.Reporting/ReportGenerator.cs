using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Reporting;

/// <summary>
/// Generates the complete migration assessment report from analyzed data.
/// </summary>
public sealed class ReportGenerator : IReportGenerator
{
    private readonly IMigrationReadinessScorer _readinessScorer;

    public ReportGenerator(IMigrationReadinessScorer readinessScorer)
    {
        _readinessScorer = readinessScorer;
    }

    /// <inheritdoc />
    public AssessmentReport GenerateReport(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        FeatureDetectionResult featureDetection,
        IReadOnlyList<CollectionFailure> failures,
        SchemaAnalysisResult? schemaAnalysis = null)
    {
        // Edge case: zero statements (Req 10.7)
        if (statements.Count == 0)
        {
            return GenerateEmptyReport(failures);
        }

        var readinessResult = _readinessScorer.CalculateScore(statements, featureDetection);

        var summary = BuildExecutiveSummary(statements, readinessResult);
        var riskBreakdown = BuildRiskBreakdown(statements);
        var topChallenges = BuildTopChallenges(statements);
        var effort = CalculateEffort(statements, objectInventory, schemaAnalysis);
        var recommendation = BuildRecommendation(readinessResult, statements, featureDetection);

        return new AssessmentReport
        {
            Summary = summary,
            RiskBreakdown = riskBreakdown,
            TopChallenges = topChallenges,
            Effort = effort,
            Recommendation = recommendation,
            FailureSummary = failures,
            SchemaAnalysis = schemaAnalysis
        };
    }

    private static AssessmentReport GenerateEmptyReport(IReadOnlyList<CollectionFailure> failures)
    {
        var zeroDistribution = new Dictionary<int, int>
        {
            { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }
        };

        var zeroPercentages = new Dictionary<int, double>
        {
            { 1, 0.0 }, { 2, 0.0 }, { 3, 0.0 }, { 4, 0.0 }, { 5, 0.0 }
        };

        var zeroHours = new HourRange { MinHours = 0, MaxHours = 0 };

        return new AssessmentReport
        {
            Summary = new ExecutiveSummary
            {
                MigrationReadinessScore = 0,
                Classification = "Not Recommended for Migration",
                TotalStatementCount = 0,
                RiskDistribution = zeroDistribution,
                RiskPercentages = zeroPercentages
            },
            RiskBreakdown = new RiskBreakdown
            {
                LevelCounts = zeroDistribution
            },
            TopChallenges = Array.Empty<MigrationChallenge>(),
            Effort = new MigrationEffortEstimate
            {
                SchemaConversion = zeroHours,
                CodeConversion = zeroHours,
                Testing = zeroHours,
                DataMigration = zeroHours,
                PerformanceTuning = zeroHours,
                TotalClassification = "Small"
            },
            Recommendation = new MigrationRecommendation
            {
                Recommendation = "Remain on SQL Server",
                Reasoning = "No statements were analyzed. Insufficient data to recommend migration.",
                MigrationReadinessScore = 0
            },
            FailureSummary = failures
        };
    }

    private static ExecutiveSummary BuildExecutiveSummary(
        IReadOnlyList<AnalyzedStatement> statements,
        MigrationReadinessResult readinessResult)
    {
        var total = statements.Count;
        var riskDistribution = BuildRiskDistributionCounts(statements);

        var riskPercentages = new Dictionary<int, double>();
        for (int level = 1; level <= 5; level++)
        {
            riskPercentages[level] = total > 0
                ? (double)riskDistribution[level] / total * 100.0
                : 0.0;
        }

        return new ExecutiveSummary
        {
            MigrationReadinessScore = readinessResult.Score,
            Classification = readinessResult.Classification,
            TotalStatementCount = total,
            RiskDistribution = riskDistribution,
            RiskPercentages = riskPercentages
        };
    }

    private static RiskBreakdown BuildRiskBreakdown(IReadOnlyList<AnalyzedStatement> statements)
    {
        return new RiskBreakdown
        {
            LevelCounts = BuildRiskDistributionCounts(statements)
        };
    }

    private static Dictionary<int, int> BuildRiskDistributionCounts(IReadOnlyList<AnalyzedStatement> statements)
    {
        var counts = new Dictionary<int, int>
        {
            { 1, 0 }, { 2, 0 }, { 3, 0 }, { 4, 0 }, { 5, 0 }
        };

        foreach (var stmt in statements)
        {
            var level = Math.Clamp(stmt.RiskScore, 1, 5);
            counts[level]++;
        }

        return counts;
    }

    private static IReadOnlyList<MigrationChallenge> BuildTopChallenges(
        IReadOnlyList<AnalyzedStatement> statements)
    {
        return statements
            .OrderByDescending(s => s.WeightedRisk)
            .ThenByDescending(s => s.RiskScore)
            .Take(10)
            .Select(s => new MigrationChallenge
            {
                ObjectName = DeriveObjectName(s),
                ObjectType = s.Classification.ToString(),
                RiskScore = s.RiskScore,
                WeightedRisk = s.WeightedRisk,
                Features = s.Features.Select(f => f.FeatureName).Distinct().ToList()
            })
            .ToList();
    }

    private static string DeriveObjectName(AnalyzedStatement statement)
    {
        var sqlText = statement.Source.SqlText;
        if (string.IsNullOrWhiteSpace(sqlText))
        {
            return "(unknown)";
        }

        // Use first 50 characters of the SQL text as the object name
        return sqlText.Length <= 50
            ? sqlText.Replace("\r", "").Replace("\n", " ").Trim()
            : sqlText[..50].Replace("\r", "").Replace("\n", " ").Trim() + "...";
    }

    private static MigrationEffortEstimate CalculateEffort(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        SchemaAnalysisResult? schemaAnalysis)
    {
        // Schema conversion: use schema analysis if available, otherwise fallback to object count heuristic
        HourRange schemaConversion;
        if (schemaAnalysis is not null && schemaAnalysis.Findings.Count > 0)
        {
            schemaConversion = schemaAnalysis.EstimatedEffort;
        }
        else
        {
            var tableCount = objectInventory.Tables.Count;
            var indexCount = objectInventory.Indexes.Count;
            var constraintCount = objectInventory.Constraints.Count;
            var schemaObjectCount = tableCount + indexCount + constraintCount;

            var schemaMin = (int)(schemaObjectCount * 0.1);
            var schemaMax = (int)(schemaObjectCount * 0.5);
            schemaConversion = new HourRange { MinHours = schemaMin, MaxHours = schemaMax };
        }

        // Code conversion: based on Risk 3-5 statement counts
        var risk3Count = statements.Count(s => s.RiskScore == 3);
        var risk4Count = statements.Count(s => s.RiskScore == 4);
        var risk5Count = statements.Count(s => s.RiskScore == 5);

        // Hours per statement by risk level (heuristic)
        var codeMin = (int)(risk3Count * 0.5 + risk4Count * 4 + risk5Count * 40);
        var codeMax = (int)(risk3Count * 4 + risk4Count * 40 + risk5Count * 200);

        // Testing: 1.5x of code conversion
        var testMin = (int)(codeMin * 1.5);
        var testMax = (int)(codeMax * 1.5);

        // Data migration: based on table count
        var dataMin = objectInventory.Tables.Count * 1;
        var dataMax = objectInventory.Tables.Count * 4;

        // Performance tuning: based on high-risk items (Risk 4-5)
        var highRiskCount = risk4Count + risk5Count;
        var perfMin = highRiskCount * 2;
        var perfMax = highRiskCount * 8;

        // Total classification based on max hours sum
        var totalMaxHours = schemaConversion.MaxHours + codeMax + testMax + dataMax + perfMax;
        var totalClassification = ClassifyTotalEffort(totalMaxHours);

        return new MigrationEffortEstimate
        {
            SchemaConversion = schemaConversion,
            CodeConversion = new HourRange { MinHours = codeMin, MaxHours = codeMax },
            Testing = new HourRange { MinHours = testMin, MaxHours = testMax },
            DataMigration = new HourRange { MinHours = dataMin, MaxHours = dataMax },
            PerformanceTuning = new HourRange { MinHours = perfMin, MaxHours = perfMax },
            TotalClassification = totalClassification
        };
    }

    private static string ClassifyTotalEffort(int maxTotalHours)
    {
        return maxTotalHours switch
        {
            <= 100 => "Small",
            <= 500 => "Medium",
            <= 2000 => "Large",
            _ => "Enterprise"
        };
    }

    private static MigrationRecommendation BuildRecommendation(
        MigrationReadinessResult readinessResult,
        IReadOnlyList<AnalyzedStatement> statements,
        FeatureDetectionResult featureDetection)
    {
        var score = readinessResult.Score ?? 0;
        var risk5Count = statements.Count(s => s.RiskScore == 5);
        var risk4Count = statements.Count(s => s.RiskScore == 4);

        // Detect architectural features from feature detection
        var architecturalFeatures = featureDetection.FeatureCounts
            .Where(kv => kv.Value > 0)
            .Select(kv => kv.Key)
            .ToList();

        string recommendation;
        string reasoning;

        if (score >= 76 && risk5Count == 0)
        {
            recommendation = "Direct PostgreSQL Migration";
            reasoning = $"Migration readiness score of {score} indicates a good candidate. " +
                        $"No critical (Risk 5) statements were found. " +
                        $"{risk4Count} high-risk (Risk 4) statements require attention but are manageable.";
        }
        else if (score >= 51 && risk5Count <= 5)
        {
            recommendation = "PostgreSQL Migration with Compatibility Middleware";
            reasoning = $"Migration readiness score of {score} indicates moderate complexity. " +
                        $"{risk5Count} critical (Risk 5) and {risk4Count} high-risk (Risk 4) statements " +
                        $"require compatibility middleware to bridge gaps during migration.";
        }
        else if (score >= 26)
        {
            recommendation = "Partial Migration";
            reasoning = $"Migration readiness score of {score} indicates significant challenges. " +
                        $"{risk5Count} critical (Risk 5) and {risk4Count} high-risk (Risk 4) statements " +
                        $"suggest only portions of the workload can be migrated to PostgreSQL.";
        }
        else
        {
            recommendation = "Remain on SQL Server";
            reasoning = $"Migration readiness score of {score} indicates the database is not a viable " +
                        $"migration candidate. {risk5Count} critical (Risk 5) and {risk4Count} high-risk (Risk 4) " +
                        $"statements represent extensive SQL Server dependencies.";
        }

        // Append architectural feature notes if present
        if (architecturalFeatures.Count > 0)
        {
            reasoning += $" Architectural features detected: {string.Join(", ", architecturalFeatures)}.";
        }

        return new MigrationRecommendation
        {
            Recommendation = recommendation,
            Reasoning = reasoning,
            MigrationReadinessScore = readinessResult.Score
        };
    }
}
