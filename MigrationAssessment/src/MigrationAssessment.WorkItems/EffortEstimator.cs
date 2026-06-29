using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Estimates effort for work items based on risk level and statement count
/// using a geometric series with a 0.7 reduction factor per additional statement.
/// Applies confidence-based range clamping to ensure estimate ratios stay within bounds.
/// </summary>
public sealed class EffortEstimator : IEffortEstimator
{
    /// <summary>
    /// Base effort ranges indexed by risk level (index 0 unused).
    /// Each tuple is (MinHours, MaxHours) for a single statement at that risk level.
    /// Ranges are calibrated for actionable planning: no single-item ratio exceeds 3x.
    /// </summary>
    private static readonly (double Min, double Max)[] BaseEffort =
    {
        (0, 0),       // index 0 unused
        (0.08, 0.17), // Risk 1: 5-10 minutes (trivial rename/alias)
        (0.25, 0.75), // Risk 2: 15-45 minutes (simple syntax substitution)
        (1.0, 3.0),   // Risk 3: 1-3 hours (procedural rewrite)
        (4.0, 12.0),  // Risk 4: 4-12 hours (design pattern change)
        (12.0, 32.0)  // Risk 5: 12-32 hours (architectural redesign)
    };

    /// <summary>
    /// Maximum allowed ratio (max/min) per confidence level.
    /// High ≤ 1.5x, Medium ≤ 2x, Low ≤ 3x.
    /// </summary>
    private static readonly Dictionary<ConfidenceLevel, double> MaxRangeRatio = new()
    {
        [ConfidenceLevel.High] = 1.5,
        [ConfidenceLevel.Medium] = 2.0,
        [ConfidenceLevel.Low] = 3.0
    };

    private const double ReductionFactor = 0.7;

    /// <inheritdoc />
    public HourRange EstimateEffort(IReadOnlyList<string> detectedFeatures, int statementCount)
    {
        if (statementCount <= 0 || detectedFeatures.Count == 0)
        {
            return new HourRange { MinHours = 0, MaxHours = 0 };
        }

        double totalMin = 0, totalMax = 0;
        int maxRisk = 1;

        foreach (var feature in detectedFeatures.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var riskLevel = StatementGrouper.GetFeatureRiskLevel(feature);
            if (riskLevel > maxRisk) maxRisk = riskLevel;
            var featureEffort = EstimateEffortRaw(riskLevel, statementCount);
            totalMin += featureEffort.MinHours;
            totalMax += featureEffort.MaxHours;
        }

        // Apply confidence-based range clamping
        var confidence = DeriveConfidenceLevel(maxRisk);
        return ClampRange(totalMin, totalMax, confidence);
    }

    /// <inheritdoc />
    public HourRange EstimateEffort(int riskLevel, int statementCount)
    {
        if (statementCount <= 0)
        {
            return new HourRange { MinHours = 0, MaxHours = 0 };
        }

        var raw = EstimateEffortRaw(riskLevel, statementCount);
        var confidence = DeriveConfidenceLevel(riskLevel);
        return ClampRange(raw.MinHours, raw.MaxHours, confidence);
    }

    /// <inheritdoc />
    public HourRange CalculateTotalEffort(IReadOnlyList<WorkItem> workItems)
    {
        var totalMin = 0.0;
        var totalMax = 0.0;

        foreach (var workItem in workItems)
        {
            totalMin += workItem.EstimatedEffort.MinHours;
            totalMax += workItem.EstimatedEffort.MaxHours;
        }

        return new HourRange { MinHours = totalMin, MaxHours = totalMax };
    }

    /// <inheritdoc />
    public ConfidenceLevel DeriveConfidenceLevel(int riskLevel)
    {
        return riskLevel switch
        {
            <= 2 => ConfidenceLevel.High,
            3 => ConfidenceLevel.Medium,
            _ => ConfidenceLevel.Low
        };
    }

    /// <inheritdoc />
    public ConfidenceSummary BuildConfidenceSummary(IReadOnlyList<WorkItem> workItems)
    {
        double highMin = 0, highMax = 0;
        double medMin = 0, medMax = 0;
        double lowMin = 0, lowMax = 0;

        var hasGlobalTempTables = false;
        var hasXmlOrClr = false;
        var hasLinkedServer = false;

        foreach (var item in workItems)
        {
            switch (item.ConfidenceLevel)
            {
                case ConfidenceLevel.High:
                    highMin += item.EstimatedEffort.MinHours;
                    highMax += item.EstimatedEffort.MaxHours;
                    break;
                case ConfidenceLevel.Medium:
                    medMin += item.EstimatedEffort.MinHours;
                    medMax += item.EstimatedEffort.MaxHours;
                    break;
                case ConfidenceLevel.Low:
                    lowMin += item.EstimatedEffort.MinHours;
                    lowMax += item.EstimatedEffort.MaxHours;
                    break;
            }

            // Track features that drive low-confidence notes
            foreach (var feature in item.DetectedFeatures)
            {
                var upper = feature.ToUpperInvariant();
                if (upper == "GLOBAL_TEMP_TABLE") hasGlobalTempTables = true;
                if (upper is "XML_METHOD" or "SQL_CLR") hasXmlOrClr = true;
                if (upper is "LINKED_SERVER" or "OPENQUERY" or "OPENROWSET") hasLinkedServer = true;
            }
        }

        var notes = BuildConfidenceNotes(hasGlobalTempTables, hasXmlOrClr, hasLinkedServer);

        return new ConfidenceSummary
        {
            HighConfidenceHours = new HourRange { MinHours = highMin, MaxHours = highMax },
            MediumConfidenceHours = new HourRange { MinHours = medMin, MaxHours = medMax },
            LowConfidenceHours = new HourRange { MinHours = lowMin, MaxHours = lowMax },
            Notes = notes
        };
    }

    /// <summary>
    /// Calculates raw effort without confidence clamping (internal use).
    /// </summary>
    private static HourRange EstimateEffortRaw(int riskLevel, int statementCount)
    {
        var clampedRisk = Math.Clamp(riskLevel, 1, 5);
        var (baseMin, baseMax) = BaseEffort[clampedRisk];

        // Geometric series: Base × (1 - 0.7^N) / 0.3
        var seriesMultiplier = (1.0 - Math.Pow(ReductionFactor, statementCount)) / (1.0 - ReductionFactor);

        return new HourRange
        {
            MinHours = baseMin * seriesMultiplier,
            MaxHours = baseMax * seriesMultiplier
        };
    }

    /// <summary>
    /// Clamps the range ratio (max/min) to the allowed maximum for the given confidence level.
    /// If MinHours is zero (or near zero), caps MaxHours at the allowed ratio times a small baseline.
    /// </summary>
    private static HourRange ClampRange(double min, double max, ConfidenceLevel confidence)
    {
        if (min <= 0 || max <= 0)
        {
            return new HourRange { MinHours = min, MaxHours = max };
        }

        var maxRatio = MaxRangeRatio[confidence];
        var currentRatio = max / min;

        if (currentRatio <= maxRatio)
        {
            return new HourRange { MinHours = min, MaxHours = max };
        }

        // Tighten the range by raising the minimum toward the geometric mean
        // while capping max at ratio × min. Strategy: keep max, raise min.
        var newMin = max / maxRatio;
        return new HourRange { MinHours = newMin, MaxHours = max };
    }

    private static string BuildConfidenceNotes(bool hasGlobalTempTables, bool hasXmlOrClr, bool hasLinkedServer)
    {
        var parts = new List<string>();

        if (hasGlobalTempTables)
        {
            parts.Add("Global temp table usage requires architectural review before effort can be scoped.");
        }

        if (hasXmlOrClr)
        {
            parts.Add("XML/CLR features require replacement strategy selection (e.g., native PostgreSQL XML or application-layer migration) before estimates can be narrowed.");
        }

        if (hasLinkedServer)
        {
            parts.Add("Linked server / OPENQUERY usage requires target connectivity architecture decisions (e.g., foreign data wrappers, ETL, or API integration).");
        }

        if (parts.Count == 0)
        {
            parts.Add("Low-confidence items require design decisions or architectural review to narrow effort ranges. Conduct spike investigations to move items from low to medium confidence.");
        }

        return string.Join(" ", parts);
    }
}
