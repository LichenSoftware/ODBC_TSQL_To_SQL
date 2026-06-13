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
        CancellationToken ct)
    {
        try
        {
            var jsonOutput = BuildJsonOutput(report, statements, objectInventory, featureDetection);
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
        FeatureDetectionResult featureDetection)
    {
        return new
        {
            AssessmentMetadata = BuildAssessmentMetadata(),
            ExecutiveSummary = BuildExecutiveSummary(report.Summary),
            ObjectInventory = BuildObjectInventory(objectInventory),
            FeatureInventory = BuildFeatureInventory(featureDetection),
            AnalyzedStatements = BuildAnalyzedStatements(statements),
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
            EngineVersion = version
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

    private static List<object> BuildObjectInventory(DatabaseObjectInventory inventory)
    {
        var items = new List<object>();

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
            DetectedFeatures = s.Features.Select(f => f.FeatureName).ToList()
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
        return new
        {
            SchemaConversion = new { effort.SchemaConversion.MinHours, effort.SchemaConversion.MaxHours },
            CodeConversion = new { effort.CodeConversion.MinHours, effort.CodeConversion.MaxHours },
            Testing = new { effort.Testing.MinHours, effort.Testing.MaxHours },
            DataMigration = new { effort.DataMigration.MinHours, effort.DataMigration.MaxHours },
            PerformanceTuning = new { effort.PerformanceTuning.MinHours, effort.PerformanceTuning.MaxHours },
            effort.TotalClassification
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
