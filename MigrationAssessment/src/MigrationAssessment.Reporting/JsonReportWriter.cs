using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Reporting;

/// <summary>
/// Writes the assessment report as a JSON file matching the published schema.
/// </summary>
public sealed class JsonReportWriter : IJsonReportWriter
{
    private readonly ILogger<JsonReportWriter> _logger;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public JsonReportWriter(ILogger<JsonReportWriter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<JsonWriteResult> WriteAsync(
        AssessmentReport report,
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        FeatureDetectionResult featureDetection,
        string outputPath,
        CancellationToken ct,
        IReadOnlyList<ObjectInventoryEntry>? parsedObjectInventory = null)
    {
        try
        {
            var jsonOutput = BuildJsonOutput(report, statements, objectInventory, featureDetection, parsedObjectInventory);
            var json = JsonSerializer.Serialize(jsonOutput, SerializerOptions);

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, json, ct);

            _logger.LogInformation("Assessment report written to {OutputPath}", outputPath);

            return new JsonWriteResult
            {
                Succeeded = true,
                OutputPath = outputPath
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                    or IOException
                                    or DirectoryNotFoundException
                                    or PathTooLongException
                                    or NotSupportedException)
        {
            _logger.LogError(ex, "Failed to write assessment report to {OutputPath}", outputPath);

            return new JsonWriteResult
            {
                Succeeded = false,
                ErrorMessage = $"Failed to write report to '{outputPath}': {ex.Message}"
            };
        }
    }

    private static object BuildJsonOutput(
        AssessmentReport report,
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory,
        FeatureDetectionResult featureDetection,
        IReadOnlyList<ObjectInventoryEntry>? parsedObjectInventory)
    {
        return new
        {
            AssessmentMetadata = BuildAssessmentMetadata(),
            ExecutiveSummary = BuildExecutiveSummary(report.Summary),
            ObjectInventory = BuildObjectInventory(objectInventory, parsedObjectInventory),
            FeatureInventory = BuildFeatureInventory(featureDetection),
            AnalyzedStatements = BuildAnalyzedStatements(statements),
            SchemaAnalysis = BuildSchemaAnalysis(report.SchemaAnalysis),
            MigrationRecommendation = BuildMigrationRecommendation(report.Recommendation),
            Effort = BuildEffort(report.Effort)
        };
    }

    private static object BuildAssessmentMetadata()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";
        return new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            EngineVersion = version,
            ScoringMethodology = "Migration readiness score is calculated as a weighted average across all analyzed statements, " +
                "where each statement's risk level (1-5) is determined by its most complex SQL Server feature. " +
                "Risk 1-2 features have known direct PostgreSQL equivalents (e.g., ISNULL→COALESCE, TOP→LIMIT). " +
                "Risk 3 features require procedural restructuring (e.g., TRY_CATCH, TEMP_TABLE). " +
                "Risk 4-5 features require architectural redesign or have no direct equivalent (e.g., MERGE, XML_METHOD, SQL_CLR). " +
                "Effort ranges use a geometric series model (0.7 reduction factor per repeated pattern) with confidence-based range clamping: " +
                "high-confidence items (risk 1-2) are capped at ≤2x ratio, medium-confidence (risk 3) at ≤4x, and low-confidence (risk 4-5) at ≤7x. " +
                "The overall readiness score ranges from 0 (all risk-5) to 100 (all risk-1), normalized by statement frequency and execution count."
        };
    }

    private static object BuildExecutiveSummary(ExecutiveSummary summary)
    {
        return new
        {
            summary.MigrationReadinessScore,
            summary.Classification,
            TotalStatements = summary.TotalStatementCount,
            RiskDistribution = summary.RiskDistribution.ToDictionary(
                kvp => kvp.Key.ToString(),
                kvp => kvp.Value)
        };
    }

    private static List<object> BuildObjectInventory(
        DatabaseObjectInventory inventory,
        IReadOnlyList<ObjectInventoryEntry>? parsedObjectInventory)
    {
        var items = new List<object>();

        // If we have parsed object inventory (enriched per-object data), emit that
        if (parsedObjectInventory is not null && parsedObjectInventory.Count > 0)
        {
            foreach (var entry in parsedObjectInventory)
            {
                items.Add(new
                {
                    entry.Name,
                    entry.Type,
                    entry.StatementCount,
                    entry.MaxRiskScore,
                    entry.ConversionCategories,
                    entry.DetectedFeatures
                });
            }

            return items;
        }

        // Fallback: legacy metadata-based inventory (flat list)
        foreach (var table in inventory.Tables)
        {
            items.Add(new { ObjectType = "Table", ObjectName = table.TableName, table.SchemaName });
        }

        foreach (var obj in inventory.ProgrammableObjects)
        {
            items.Add(new { obj.ObjectType, obj.ObjectName, obj.SchemaName });
        }

        foreach (var synonym in inventory.Synonyms)
        {
            items.Add(new { ObjectType = "Synonym", ObjectName = synonym.SynonymName, synonym.SchemaName });
        }

        foreach (var index in inventory.Indexes)
        {
            items.Add(new { ObjectType = "Index", ObjectName = index.IndexName, index.SchemaName });
        }

        foreach (var constraint in inventory.Constraints)
        {
            items.Add(new { ObjectType = "Constraint", ObjectName = constraint.ConstraintName, constraint.SchemaName });
        }

        foreach (var fk in inventory.ForeignKeys)
        {
            items.Add(new { ObjectType = "ForeignKey", ObjectName = fk.ConstraintName, fk.SchemaName });
        }

        return items;
    }

    private static List<object> BuildFeatureInventory(FeatureDetectionResult featureDetection)
    {
        return featureDetection.FeatureCounts
            .Select(kvp => (object)new { FeatureName = kvp.Key, OccurrenceCount = kvp.Value })
            .ToList();
    }

    private static List<object> BuildAnalyzedStatements(IReadOnlyList<AnalyzedStatement> statements)
    {
        return statements.Select(s => (object)new
        {
            StatementText = s.Source.SqlText,
            s.RiskScore,
            s.WeightedRisk,
            ConversionCategory = GetConversionCategory(s.RiskScore),
            DetectedFeatures = s.Features.Select(f => f.FeatureName).ToList(),
            RiskRationale = RiskRationaleGenerator.GenerateRationale(s.Features, s.RiskScore)
        }).ToList();
    }

    private static object BuildMigrationRecommendation(MigrationRecommendation recommendation)
    {
        return new
        {
            recommendation.Recommendation,
            recommendation.Reasoning,
            recommendation.MigrationReadinessScore
        };
    }

    private static object BuildEffort(MigrationEffortEstimate effort)
    {
        var result = new Dictionary<string, object>
        {
            ["schemaConversion"] = new { effort.SchemaConversion.MinHours, effort.SchemaConversion.MaxHours },
            ["codeConversion"] = new { effort.CodeConversion.MinHours, effort.CodeConversion.MaxHours },
            ["testing"] = new { effort.Testing.MinHours, effort.Testing.MaxHours },
            ["dataMigration"] = new { effort.DataMigration.MinHours, effort.DataMigration.MaxHours },
            ["performanceTuning"] = new { effort.PerformanceTuning.MinHours, effort.PerformanceTuning.MaxHours },
            ["totalClassification"] = effort.TotalClassification
        };

        if (effort.ConfidenceSummary is not null)
        {
            result["confidenceSummary"] = new
            {
                HighConfidenceHours = new
                {
                    effort.ConfidenceSummary.HighConfidenceHours.MinHours,
                    effort.ConfidenceSummary.HighConfidenceHours.MaxHours
                },
                MediumConfidenceHours = new
                {
                    effort.ConfidenceSummary.MediumConfidenceHours.MinHours,
                    effort.ConfidenceSummary.MediumConfidenceHours.MaxHours
                },
                LowConfidenceHours = new
                {
                    effort.ConfidenceSummary.LowConfidenceHours.MinHours,
                    effort.ConfidenceSummary.LowConfidenceHours.MaxHours
                },
                effort.ConfidenceSummary.Notes
            };
        }

        return result;
    }

    private static object? BuildSchemaAnalysis(SchemaAnalysisResult? schemaAnalysis)
    {
        if (schemaAnalysis is null || schemaAnalysis.Findings.Count == 0)
            return null;

        return new
        {
            Findings = schemaAnalysis.Findings.Select(f => new
            {
                f.TableName,
                f.ColumnName,
                f.IssueType,
                f.SqlServerType,
                f.PostgresType,
                f.RiskScore,
                f.Description
            }).ToList(),
            EstimatedEffort = new
            {
                schemaAnalysis.EstimatedEffort.MinHours,
                schemaAnalysis.EstimatedEffort.MaxHours
            },
            schemaAnalysis.FindingCountsByType
        };
    }

    /// <summary>
    /// Maps a risk score to a conversion category.
    /// Risk 1-2 = automatic, Risk 3 = semi-automatic, Risk 4-5 = manual.
    /// </summary>
    internal static string GetConversionCategory(int riskScore)
    {
        return riskScore switch
        {
            <= 2 => "automatic",
            3 => "semi-automatic",
            _ => "manual"
        };
    }
}
