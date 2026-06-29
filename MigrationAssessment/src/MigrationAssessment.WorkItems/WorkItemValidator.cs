using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Validates generated work items for consistency and correctness before report output.
/// Runs three categories of checks:
/// 1. SQL syntax validation on postgresEquivalent fields
/// 2. Object attribution consistency with the object inventory
/// 3. Effort range sanity (minHours ≤ maxHours, ratio matches confidence band)
/// </summary>
public sealed class WorkItemValidator
{
    /// <summary>
    /// Maximum effort ratio allowed for each confidence level.
    /// High: ≤1.5x, Medium: ≤2x, Low: ≤3x.
    /// </summary>
    private static readonly Dictionary<ConfidenceLevel, double> MaxRatioByConfidence = new()
    {
        [ConfidenceLevel.High] = 1.5,
        [ConfidenceLevel.Medium] = 2.0,
        [ConfidenceLevel.Low] = 3.0
    };

    /// <summary>
    /// Validates all work items and returns a summary with any warnings found.
    /// </summary>
    /// <param name="workItems">The generated work items to validate.</param>
    /// <param name="objectInventory">The parsed object inventory (may be null/empty for standalone mode).</param>
    /// <returns>A validation summary with zero or more warnings.</returns>
    public ValidationSummary Validate(
        IReadOnlyList<WorkItem> workItems,
        IReadOnlyList<ObjectInventoryEntry>? objectInventory)
    {
        var warnings = new List<ValidationWarning>();

        foreach (var wi in workItems)
        {
            // 1. SQL syntax validation
            ValidateSqlSyntax(wi, warnings);

            // 2. Object attribution consistency
            if (objectInventory is not null && objectInventory.Count > 0)
            {
                ValidateObjectAttribution(wi, objectInventory, warnings);
            }

            // 3. Effort range sanity
            ValidateEffortRange(wi, warnings);
        }

        return new ValidationSummary
        {
            Passed = warnings.Count == 0,
            WarningCount = warnings.Count,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Validates that the postgresEquivalent field passes structural SQL validation.
    /// </summary>
    private static void ValidateSqlSyntax(WorkItem wi, List<ValidationWarning> warnings)
    {
        var sql = wi.PostgresEquivalent;

        if (string.IsNullOrWhiteSpace(sql))
        {
            warnings.Add(new ValidationWarning
            {
                WorkItemId = wi.Id,
                Category = "sql-syntax",
                Message = "postgresEquivalent field is empty or whitespace."
            });
            return;
        }

        // Use the same structural validation from the conversion engine
        if (!PostgresConversionEngine.PassesStructuralValidation(sql))
        {
            warnings.Add(new ValidationWarning
            {
                WorkItemId = wi.Id,
                Category = "sql-syntax",
                Message = $"postgresEquivalent field fails structural SQL validation (unbalanced parens/quotes or invalid clause placement)."
            });
        }
    }

    /// <summary>
    /// Validates that affected objects in the work item exist in the object inventory
    /// and that the features addressed by the work item appear in the object's detected features.
    /// </summary>
    private static void ValidateObjectAttribution(
        WorkItem wi,
        IReadOnlyList<ObjectInventoryEntry> objectInventory,
        List<ValidationWarning> warnings)
    {
        foreach (var ao in wi.AffectedObjects)
        {
            // Skip ad hoc — those are validated differently
            if (ao.Name == "Ad Hoc Queries" || ao.Type == "AdHoc")
            {
                // Verify the Ad Hoc entry exists in the inventory
                var adHocEntry = objectInventory.FirstOrDefault(
                    e => e.Type == "AdHoc");
                if (adHocEntry is null)
                {
                    warnings.Add(new ValidationWarning
                    {
                        WorkItemId = wi.Id,
                        Category = "object-attribution",
                        Message = $"Work item references 'Ad Hoc Queries' but no AdHoc entry exists in the object inventory."
                    });
                }
                continue;
            }

            // Named object: must exist in inventory
            var inventoryEntry = objectInventory.FirstOrDefault(
                e => string.Equals(e.Name, ao.Name, StringComparison.OrdinalIgnoreCase)
                     && e.Type != "AdHoc");

            if (inventoryEntry is null)
            {
                warnings.Add(new ValidationWarning
                {
                    WorkItemId = wi.Id,
                    Category = "object-attribution",
                    Message = $"Affected object '{ao.Name}' ({ao.Type}) not found in the object inventory."
                });
                continue;
            }

            // Verify that at least one of the work item's features appears in the object's detected features
            if (wi.DetectedFeatures.Count > 0 && inventoryEntry.DetectedFeatures.Count > 0)
            {
                var hasMatchingFeature = wi.DetectedFeatures.Any(f =>
                    inventoryEntry.DetectedFeatures.Contains(f, StringComparer.OrdinalIgnoreCase));

                if (!hasMatchingFeature)
                {
                    warnings.Add(new ValidationWarning
                    {
                        WorkItemId = wi.Id,
                        Category = "object-attribution",
                        Message = $"Work item features [{string.Join(", ", wi.DetectedFeatures)}] " +
                                  $"do not appear in object '{ao.Name}' detected features " +
                                  $"[{string.Join(", ", inventoryEntry.DetectedFeatures)}]."
                    });
                }
            }
        }
    }

    /// <summary>
    /// Validates effort range: minHours ≤ maxHours and ratio matches confidence band.
    /// </summary>
    private static void ValidateEffortRange(WorkItem wi, List<ValidationWarning> warnings)
    {
        var effort = wi.EstimatedEffort;

        if (effort.MinHours < 0)
        {
            warnings.Add(new ValidationWarning
            {
                WorkItemId = wi.Id,
                Category = "effort-range",
                Message = $"MinHours ({effort.MinHours:F2}) is negative."
            });
        }

        if (effort.MaxHours < effort.MinHours)
        {
            warnings.Add(new ValidationWarning
            {
                WorkItemId = wi.Id,
                Category = "effort-range",
                Message = $"MaxHours ({effort.MaxHours:F2}) is less than MinHours ({effort.MinHours:F2})."
            });
        }

        // Check ratio against confidence band
        if (effort.MinHours > 0)
        {
            var ratio = effort.MaxHours / effort.MinHours;
            var maxAllowedRatio = MaxRatioByConfidence.TryGetValue(wi.ConfidenceLevel, out var max)
                ? max
                : 7.0;

            if (ratio > maxAllowedRatio)
            {
                warnings.Add(new ValidationWarning
                {
                    WorkItemId = wi.Id,
                    Category = "effort-range",
                    Message = $"Effort ratio {ratio:F1}x exceeds maximum {maxAllowedRatio:F1}x " +
                              $"allowed for confidence level '{wi.ConfidenceLevel}'."
                });
            }
        }
    }
}
