using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems;

/// <summary>
/// Estimates effort for work items based on risk level and statement count
/// using a geometric series with a 0.7 reduction factor per additional statement.
/// </summary>
public sealed class EffortEstimator : IEffortEstimator
{
    /// <summary>
    /// Base effort ranges indexed by risk level (index 0 unused).
    /// Each tuple is (MinHours, MaxHours) for a single statement at that risk level.
    /// </summary>
    private static readonly (double Min, double Max)[] BaseEffort =
    {
        (0, 0),       // index 0 unused
        (0, 0.08),    // Risk 1: 0 to 5 minutes
        (0.08, 0.5),  // Risk 2: 5 minutes to 30 minutes
        (0.5, 4.0),   // Risk 3: 30 minutes to 4 hours
        (4.0, 40.0),  // Risk 4: 4 hours to 40 hours
        (40.0, 80.0)  // Risk 5: 40 hours to 80 hours
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

        foreach (var feature in detectedFeatures.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var riskLevel = StatementGrouper.GetFeatureRiskLevel(feature);
            var featureEffort = EstimateEffort(riskLevel, statementCount);
            totalMin += featureEffort.MinHours;
            totalMax += featureEffort.MaxHours;
        }

        return new HourRange { MinHours = totalMin, MaxHours = totalMax };
    }

    /// <inheritdoc />
    public HourRange EstimateEffort(int riskLevel, int statementCount)
    {
        if (statementCount <= 0)
        {
            return new HourRange { MinHours = 0, MaxHours = 0 };
        }

        // Clamp risk level to valid range [1, 5]
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
}
