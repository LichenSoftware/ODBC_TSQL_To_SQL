using System.Text.Json;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Reads and parses assessment JSON files produced by the Migration Assessment Engine.
/// </summary>
public sealed class AssessmentJsonReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Reads and parses an assessment JSON file.
    /// </summary>
    /// <param name="filePath">Path to the assessment JSON file.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A result containing parsed data or an error.</returns>
    public async Task<AssessmentReadResult> ReadAsync(string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Assessment file not found: {filePath}"
            };
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(filePath, ct);
        }
        catch (Exception ex)
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Failed to read assessment file '{filePath}': {ex.Message}"
            };
        }

        return Parse(json, filePath);
    }

    /// <summary>
    /// Parses assessment JSON content from a string.
    /// </summary>
    internal AssessmentReadResult Parse(string json, string sourcePath)
    {
        AssessmentJsonDocument? doc;
        try
        {
            doc = JsonSerializer.Deserialize<AssessmentJsonDocument>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Invalid JSON in assessment file '{sourcePath}': {ex.Message}"
            };
        }

        if (doc is null)
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Assessment file '{sourcePath}' deserialized to null"
            };
        }

        // Validate required schema properties
        if (doc.AnalyzedStatements is null)
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Schema validation failed for '{sourcePath}': missing required property 'analyzedStatements'"
            };
        }

        if (doc.FeatureInventory is null)
        {
            return new AssessmentReadResult
            {
                Succeeded = false,
                ErrorMessage = $"Schema validation failed for '{sourcePath}': missing required property 'featureInventory'"
            };
        }

        // Check for empty assessment (0 statements and 0 feature inventory entries with occurrences)
        var hasStatements = doc.AnalyzedStatements.Count > 0;
        var hasFeatures = doc.FeatureInventory.Any(f => f.OccurrenceCount > 0);

        if (!hasStatements && !hasFeatures)
        {
            return new AssessmentReadResult
            {
                Succeeded = true,
                ErrorMessage = "Assessment contains zero analyzed statements and zero detected features. No remediation work items are needed.",
                Statements = [],
                FeatureDetection = new FeatureDetectionResult
                {
                    FeatureCounts = new Dictionary<string, int>(),
                    DetailedInventory = [],
                    InaccessibleFeatures = []
                },
                ObjectInventory = []
            };
        }

        // Map to domain models
        var statements = MapStatements(doc.AnalyzedStatements);
        var featureDetection = MapFeatureDetection(doc.FeatureInventory);
        var objectInventory = MapObjectInventory(doc.ObjectInventory);

        return new AssessmentReadResult
        {
            Succeeded = true,
            Statements = statements,
            FeatureDetection = featureDetection,
            ObjectInventory = objectInventory
        };
    }

    private static IReadOnlyList<AnalyzedStatement> MapStatements(IReadOnlyList<AnalyzedStatementJson> jsonStatements)
    {
        var results = new List<AnalyzedStatement>(jsonStatements.Count);

        for (var i = 0; i < jsonStatements.Count; i++)
        {
            var s = jsonStatements[i];
            var features = MapFeatures(s.DetectedFeatures, i);
            var classification = InferClassification(s.StatementText);

            results.Add(new AnalyzedStatement
            {
                Source = new CollectedStatement
                {
                    SqlText = s.StatementText ?? string.Empty,
                    Source = StatementSource.QueryStore,
                    QueryHash = $"hash-{i:D4}",
                    ExecutionCount = 1
                },
                Classification = classification,
                Features = features,
                RiskScore = s.RiskScore,
                WeightedRisk = s.WeightedRisk,
                ParseSucceeded = true
            });
        }

        return results;
    }

    private static IReadOnlyList<DetectedFeature> MapFeatures(IReadOnlyList<string>? featureNames, int statementIndex)
    {
        if (featureNames is null || featureNames.Count == 0)
            return [];

        var features = new List<DetectedFeature>(featureNames.Count);
        foreach (var name in featureNames)
        {
            features.Add(new DetectedFeature
            {
                FeatureName = name,
                Category = InferFeatureCategory(name),
                StatementId = $"stmt-{statementIndex:D4}",
                Line = 1,
                Column = 1
            });
        }
        return features;
    }

    private static FeatureCategory InferFeatureCategory(string featureName)
    {
        return featureName.ToUpperInvariant() switch
        {
            "TOP" or "MERGE" or "CROSS_APPLY" or "OUTER_APPLY"
                or "PIVOT" or "UNPIVOT" or "OPENQUERY" or "OPENROWSET" => FeatureCategory.QueryFeature,

            "ISNULL" or "GETDATE" or "LEN" or "CHARINDEX" or "PATINDEX"
                or "STUFF" or "DATEADD" or "DATEDIFF" or "DATEPART"
                or "XML_METHOD" => FeatureCategory.FunctionUsage,

            "TEMP_TABLE" or "GLOBAL_TEMP_TABLE" => FeatureCategory.TemporaryObject,

            "UPDLOCK" or "ROWLOCK" or "NOLOCK" or "TABLOCK" or "HOLDLOCK"
                or "TRY_CATCH" or "DYNAMIC_SQL" or "OUTPUT_CLAUSE" => FeatureCategory.TransactionFeature,

            _ => FeatureCategory.QueryFeature
        };
    }

    private static StatementClassification InferClassification(string? statementText)
    {
        if (string.IsNullOrWhiteSpace(statementText))
            return StatementClassification.Unknown;

        // Find the first SQL keyword after any parameter declarations
        var text = statementText.TrimStart();

        // Skip parameter declarations like (@Param type, ...)
        if (text.StartsWith('(') && text.Contains(')'))
        {
            var closeParen = text.IndexOf(')');
            text = text[(closeParen + 1)..].TrimStart();
        }

        var upper = text.ToUpperInvariant();

        if (upper.StartsWith("SELECT")) return StatementClassification.Select;
        if (upper.StartsWith("INSERT")) return StatementClassification.Insert;
        if (upper.StartsWith("UPDATE")) return StatementClassification.Update;
        if (upper.StartsWith("DELETE")) return StatementClassification.Delete;
        if (upper.StartsWith("MERGE")) return StatementClassification.Merge;
        if (upper.StartsWith("CREATE") || upper.StartsWith("ALTER") || upper.StartsWith("DROP"))
            return StatementClassification.Ddl;
        if (upper.StartsWith("GRANT") || upper.StartsWith("REVOKE") || upper.StartsWith("DENY"))
            return StatementClassification.Dcl;
        if (upper.StartsWith("BEGIN") || upper.StartsWith("COMMIT") || upper.StartsWith("ROLLBACK"))
            return StatementClassification.Tcl;

        return StatementClassification.Unknown;
    }

    private static FeatureDetectionResult MapFeatureDetection(IReadOnlyList<FeatureInventoryJson> inventory)
    {
        var counts = new Dictionary<string, int>(inventory.Count);
        foreach (var item in inventory)
        {
            if (!string.IsNullOrEmpty(item.FeatureName))
            {
                counts[item.FeatureName] = item.OccurrenceCount;
            }
        }

        return new FeatureDetectionResult
        {
            FeatureCounts = counts,
            DetailedInventory = [],
            InaccessibleFeatures = []
        };
    }

    private static IReadOnlyList<ObjectInventoryEntry> MapObjectInventory(IReadOnlyList<ObjectInventoryJson>? inventory)
    {
        if (inventory is null || inventory.Count == 0)
            return [];

        var results = new List<ObjectInventoryEntry>(inventory.Count);
        foreach (var item in inventory)
        {
            if (string.IsNullOrEmpty(item.Name) || string.IsNullOrEmpty(item.Type))
                continue;

            results.Add(new ObjectInventoryEntry
            {
                Name = item.Name,
                Type = item.Type,
                StatementCount = item.StatementCount,
                MaxRiskScore = item.MaxRiskScore,
                ConversionCategories = item.ConversionCategories ?? [],
                DetectedFeatures = item.DetectedFeatures ?? []
            });
        }

        return results;
    }

    #region JSON DTOs

    private sealed class AssessmentJsonDocument
    {
        public IReadOnlyList<AnalyzedStatementJson>? AnalyzedStatements { get; set; }
        public IReadOnlyList<FeatureInventoryJson>? FeatureInventory { get; set; }
        public IReadOnlyList<ObjectInventoryJson>? ObjectInventory { get; set; }
    }

    private sealed class AnalyzedStatementJson
    {
        public string? StatementText { get; set; }
        public int RiskScore { get; set; }
        public double WeightedRisk { get; set; }
        public string? ConversionCategory { get; set; }
        public IReadOnlyList<string>? DetectedFeatures { get; set; }
    }

    private sealed class FeatureInventoryJson
    {
        public string? FeatureName { get; set; }
        public int OccurrenceCount { get; set; }
    }

    private sealed class ObjectInventoryJson
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public int StatementCount { get; set; }
        public int MaxRiskScore { get; set; }
        public IReadOnlyList<string>? ConversionCategories { get; set; }
        public IReadOnlyList<string>? DetectedFeatures { get; set; }
    }

    #endregion
}
