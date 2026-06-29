using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;
using HourRange = MigrationAssessment.WorkItems.Models.HourRange;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for PriorityCalculator and EffortEstimator.
/// Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5, 5.6
/// </summary>
public class PriorityAndEffortPropertyTests
{
    private readonly PriorityCalculator _priorityCalculator = new();
    private readonly EffortEstimator _effortEstimator = new();

    /// <summary>
    /// Base effort ranges indexed by risk level (matching EffortEstimator).
    /// </summary>
    private static readonly (double Min, double Max)[] BaseEffort =
    {
        (0, 0),       // index 0 unused
        (0.08, 0.17), // Risk 1
        (0.25, 0.75), // Risk 2
        (1.0, 3.0),   // Risk 3
        (4.0, 12.0),  // Risk 4
        (12.0, 32.0)  // Risk 5
    };

    private const double ReductionFactor = 0.7;

    #region Generators

    private static AnalyzedStatement CreateStatement(double weightedRisk, int riskScore = 3)
    {
        return new AnalyzedStatement
        {
            Source = new CollectedStatement
            {
                SqlText = $"SELECT TOP ({riskScore}) * FROM dbo.Table1",
                Source = StatementSource.QueryStore,
                QueryHash = Guid.NewGuid().ToString("N")
            },
            Classification = StatementClassification.Select,
            Features = new[]
            {
                new DetectedFeature
                {
                    FeatureName = "TOP",
                    Category = FeatureCategory.QueryFeature,
                    StatementId = Guid.NewGuid().ToString(),
                    Line = 1,
                    Column = 1
                }
            },
            RiskScore = riskScore,
            WeightedRisk = weightedRisk,
            ParseSucceeded = true
        };
    }

    private static StatementGroup CreateGroup(
        IReadOnlyList<AnalyzedStatement> statements,
        string featureName = "TOP",
        string? databaseObjectName = "dbo.TestProc")
    {
        var maxRisk = statements.Count > 0 ? statements.Max(s => s.RiskScore) : 1;
        return new StatementGroup
        {
            FeatureName = featureName,
            DetectedFeatures = new[] { featureName },
            DatabaseObjectName = databaseObjectName,
            DatabaseObjectType = databaseObjectName is null ? "AdHoc" : "StoredProcedure",
            Statements = statements,
            MaxRiskLevel = maxRisk
        };
    }

    private static WorkItem CreateWorkItem(
        string id,
        double priorityScore,
        int riskLevel,
        HourRange effort,
        int statementCount = 1)
    {
        return new WorkItem
        {
            Id = id,
            Title = $"[Risk {riskLevel}] Convert TOP in dbo.TestProc",
            Description = "Test work item",
            SqlServerPattern = "SELECT TOP 10 * FROM Table1",
            PostgresEquivalent = "SELECT * FROM Table1 LIMIT 10",
            AffectedObjects = new[]
            {
                new AffectedObject
                {
                    Name = "dbo.TestProc",
                    Type = "StoredProcedure",
                    StatementCount = statementCount
                }
            },
            RiskLevel = riskLevel,
            Priority = "Medium",
            PriorityScore = priorityScore,
            EstimatedEffort = effort,
            ConfidenceLevel = riskLevel <= 2 ? ConfidenceLevel.High : riskLevel == 3 ? ConfidenceLevel.Medium : ConfidenceLevel.Low,
            AcceptanceCriteria = new[] { "Criterion 1", "Criterion 2" },
            RemediationGuidance = "Replace TOP with LIMIT",
            Tags = new[] { $"risk-{riskLevel}", "query-feature", "semi-automatic" }
        };
    }

    /// <summary>
    /// Generates a StatementGroup with random WeightedRisk values.
    /// </summary>
    private static Gen<StatementGroup> GenStatementGroup()
    {
        return from stmtCount in Gen.Choose(1, 10)
               from weightedRisks in Gen.ArrayOf(stmtCount,
                   Gen.Choose(1, 1000).Select(x => x / 10.0)) // 0.1 to 100.0
               from riskLevel in Gen.Choose(1, 5)
               let statements = weightedRisks
                   .Select(wr => CreateStatement(wr, riskLevel))
                   .ToList()
               select CreateGroup(statements);
    }

    /// <summary>
    /// Generates a list of WorkItems with distinct PriorityScores for priority label testing.
    /// </summary>
    private static Gen<IReadOnlyList<WorkItem>> GenWorkItemList()
    {
        return from count in Gen.Choose(1, 30)
               from scores in Gen.ArrayOf(count,
                   Gen.Choose(1, 10000).Select(x => x / 10.0))
               from riskLevels in Gen.ArrayOf(count, Gen.Choose(1, 5))
               from stmtCounts in Gen.ArrayOf(count, Gen.Choose(1, 20))
               let items = Enumerable.Range(0, count)
                   .Select(i => CreateWorkItem(
                       $"WI-{(i + 1):D3}",
                       scores[i],
                       riskLevels[i],
                       new HourRange { MinHours = 1.0, MaxHours = 10.0 },
                       stmtCounts[i]))
                   .ToList()
               select (IReadOnlyList<WorkItem>)items;
    }

    /// <summary>
    /// Generates a list of WorkItems with known effort values for total effort testing.
    /// </summary>
    private static Gen<IReadOnlyList<WorkItem>> GenWorkItemsWithEffort()
    {
        return from count in Gen.Choose(1, 20)
               from minHours in Gen.ArrayOf(count, Gen.Choose(0, 1000).Select(x => x / 10.0))
               from maxHours in Gen.ArrayOf(count, Gen.Choose(0, 1000).Select(x => x / 10.0))
               let items = Enumerable.Range(0, count)
                   .Select(i =>
                   {
                       var min = Math.Min(minHours[i], maxHours[i]);
                       var max = Math.Max(minHours[i], maxHours[i]);
                       return CreateWorkItem(
                           $"WI-{(i + 1):D3}",
                           50.0,
                           3,
                           new HourRange { MinHours = min, MaxHours = max });
                   })
                   .ToList()
               select (IReadOnlyList<WorkItem>)items;
    }

    #endregion

    #region Property 9: Priority score equals sum of weighted risks

    /// <summary>
    /// Property 9: Priority score equals sum of weighted risks — verify PriorityScore = Σ(WeightedRisk)
    /// for all statements in group.
    ///
    /// **Validates: Requirements 5.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property PriorityScore_EqualsSumOfWeightedRisks()
    {
        return Prop.ForAll(GenStatementGroup().ToArbitrary(), group =>
        {
            var score = _priorityCalculator.CalculatePriorityScore(group);
            var expectedScore = group.Statements.Sum(s => s.WeightedRisk);

            score.Should().BeApproximately(expectedScore, 1e-10,
                "PriorityScore should equal sum of WeightedRisk values");
        });
    }

    /// <summary>
    /// Property 9 (additional): Verify with a single statement that the score equals its WeightedRisk.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property PriorityScore_SingleStatement_EqualsItsWeightedRisk()
    {
        var gen = from weightedRisk in Gen.Choose(1, 5000).Select(x => x / 10.0)
                  from risk in Gen.Choose(1, 5)
                  let stmt = CreateStatement(weightedRisk, risk)
                  select CreateGroup(new[] { stmt });

        return Prop.ForAll(gen.ToArbitrary(), group =>
        {
            var score = _priorityCalculator.CalculatePriorityScore(group);
            score.Should().BeApproximately(group.Statements[0].WeightedRisk, 1e-10);
        });
    }

    #endregion

    #region Property 10: Percentile-based priority labels

    /// <summary>
    /// Property 10: Percentile-based priority labels — verify labels match percentile thresholds.
    /// Critical: rank ≤ ⌈count × 0.10⌉, High: rank in (top 10%, top 30%],
    /// Medium: rank in (top 30%, top 70%], Low: rank > top 70%.
    ///
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property PriorityLabels_MatchPercentileThresholds()
    {
        return Prop.ForAll(GenWorkItemList().ToArbitrary(), workItems =>
        {
            var result = _priorityCalculator.AssignPriorityLabels(workItems);

            var totalCount = result.Count;
            var criticalBound = (int)Math.Ceiling(totalCount * 0.10);
            var highBound = (int)Math.Ceiling(totalCount * 0.30);
            var mediumBound = (int)Math.Ceiling(totalCount * 0.70);

            for (int i = 0; i < result.Count; i++)
            {
                int rank = i + 1;
                var (_, label) = result[i];

                var expectedLabel = rank <= criticalBound ? "Critical"
                    : rank <= highBound ? "High"
                    : rank <= mediumBound ? "Medium"
                    : "Low";

                label.Should().Be(expectedLabel,
                    $"item at rank {rank} of {totalCount} (criticalBound={criticalBound}, " +
                    $"highBound={highBound}, mediumBound={mediumBound}) should have label '{expectedLabel}'");
            }
        });
    }

    /// <summary>
    /// Property 10 (single item): A single work item always gets "Critical" label.
    /// </summary>
    [Property(MaxTest = 50)]
    public Property PriorityLabels_SingleItem_GetsCritical()
    {
        var gen = from score in Gen.Choose(1, 1000).Select(x => x / 10.0)
                  from risk in Gen.Choose(1, 5)
                  let item = CreateWorkItem("WI-001", score, risk,
                      new HourRange { MinHours = 1.0, MaxHours = 5.0 })
                  select new List<WorkItem> { item } as IReadOnlyList<WorkItem>;

        return Prop.ForAll(gen.ToArbitrary(), workItems =>
        {
            var result = _priorityCalculator.AssignPriorityLabels(workItems);

            result.Should().HaveCount(1);
            result[0].Priority.Should().Be("Critical",
                "a single work item should always be labeled Critical (top 10% of 1 = rank 1)");
        });
    }

    #endregion

    #region Property 11: Effort estimation geometric series

    /// <summary>
    /// Property 11: Effort estimation geometric series — verify effort formula produces correct values
    /// for given risk level and count: minHours = BaseMin(R) × (1 - 0.7^N) / 0.3,
    /// maxHours = BaseMax(R) × (1 - 0.7^N) / 0.3.
    ///
    /// **Validates: Requirements 5.3, 5.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property EffortEstimation_MatchesGeometricSeriesFormula()
    {
        var gen = from riskLevel in Gen.Choose(1, 5)
                  from statementCount in Gen.Choose(1, 50)
                  select (riskLevel, statementCount);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (riskLevel, statementCount) = tuple;

            var result = _effortEstimator.EstimateEffort(riskLevel, statementCount);

            var (baseMin, baseMax) = BaseEffort[riskLevel];
            var seriesMultiplier = (1.0 - Math.Pow(ReductionFactor, statementCount)) / (1.0 - ReductionFactor);
            var rawMin = baseMin * seriesMultiplier;
            var rawMax = baseMax * seriesMultiplier;

            // Confidence-based clamping: high (risk 1-2) ≤1.5x, medium (risk 3) ≤2x, low (risk 4-5) ≤3x
            var maxRatio = riskLevel switch
            {
                <= 2 => 1.5,
                3 => 2.0,
                _ => 3.0
            };

            double expectedMin = rawMin;
            double expectedMax = rawMax;
            if (rawMin > 0 && rawMax > 0 && rawMax / rawMin > maxRatio)
            {
                expectedMin = rawMax / maxRatio;
            }

            result.MinHours.Should().BeApproximately(expectedMin, 1e-10,
                $"MinHours for risk={riskLevel}, count={statementCount} should match geometric series formula with confidence clamping");
            result.MaxHours.Should().BeApproximately(expectedMax, 1e-10,
                $"MaxHours for risk={riskLevel}, count={statementCount} should match geometric series formula with confidence clamping");
        });
    }

    /// <summary>
    /// Property 11 (boundary): Zero statements should produce zero effort.
    /// </summary>
    [Property(MaxTest = 20)]
    public Property EffortEstimation_ZeroStatements_ProducesZeroEffort()
    {
        var gen = Gen.Choose(1, 5);

        return Prop.ForAll(gen.ToArbitrary(), riskLevel =>
        {
            var result = _effortEstimator.EstimateEffort(riskLevel, 0);

            result.MinHours.Should().Be(0.0);
            result.MaxHours.Should().Be(0.0);
        });
    }

    /// <summary>
    /// Property 11 (monotonicity): More statements should produce >= effort than fewer statements.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property EffortEstimation_MoreStatements_ProducesMoreOrEqualEffort()
    {
        var gen = from riskLevel in Gen.Choose(1, 5)
                  from n1 in Gen.Choose(1, 25)
                  from extra in Gen.Choose(1, 25)
                  select (riskLevel, n1, n1 + extra);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (riskLevel, fewer, more) = tuple;

            var fewerResult = _effortEstimator.EstimateEffort(riskLevel, fewer);
            var moreResult = _effortEstimator.EstimateEffort(riskLevel, more);

            moreResult.MinHours.Should().BeGreaterThanOrEqualTo(fewerResult.MinHours,
                "more statements should require at least as much effort");
            moreResult.MaxHours.Should().BeGreaterThanOrEqualTo(fewerResult.MaxHours,
                "more statements should require at least as much effort");
        });
    }

    #endregion

    #region Property 12: Total effort equals sum of parts

    /// <summary>
    /// Property 12: Total effort equals sum of parts — verify total min/max = sum of individual min/max.
    ///
    /// **Validates: Requirements 5.5**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TotalEffort_EqualsSumOfIndividualEfforts()
    {
        return Prop.ForAll(GenWorkItemsWithEffort().ToArbitrary(), workItems =>
        {
            var total = _effortEstimator.CalculateTotalEffort(workItems);

            var expectedMin = workItems.Sum(w => w.EstimatedEffort.MinHours);
            var expectedMax = workItems.Sum(w => w.EstimatedEffort.MaxHours);

            total.MinHours.Should().BeApproximately(expectedMin, 1e-10,
                "total MinHours should equal sum of all individual MinHours");
            total.MaxHours.Should().BeApproximately(expectedMax, 1e-10,
                "total MaxHours should equal sum of all individual MaxHours");
        });
    }

    /// <summary>
    /// Property 12 (empty): Empty work item list should produce zero total effort.
    /// </summary>
    [Fact]
    public void TotalEffort_EmptyList_ProducesZero()
    {
        var total = _effortEstimator.CalculateTotalEffort(Array.Empty<WorkItem>());

        total.MinHours.Should().Be(0.0);
        total.MaxHours.Should().Be(0.0);
    }

    #endregion

    #region Property 4: Effort is sum of per-feature efforts

    /// <summary>
    /// Property 4: Effort is sum of per-feature efforts — for any list of features and statement count,
    /// EstimateEffort(features, count) equals sum(EstimateEffort(risk(f), count) for f in features.Distinct()).
    ///
    /// **Validates: Requirements 7.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property MultiFeatureEffort_EqualsSumOfPerFeatureEfforts()
    {
        // Known feature names from the FeatureRiskMap (various risk levels)
        var knownFeatures = new[]
        {
            "TOP", "ISNULL", "GETDATE", "LEN", "CHARINDEX",      // Risk 2
            "TRY_CATCH", "DYNAMIC_SQL", "TEMP_TABLE", "CTE",      // Risk 3
            "MERGE", "TABLE_VARIABLE", "PIVOT",                    // Risk 4
            "OPENQUERY", "XML_METHOD", "SQL_CLR"                   // Risk 5
        };

        var gen = from featureCount in Gen.Choose(1, 5)
                  from featureIndices in Gen.ArrayOf(featureCount, Gen.Choose(0, knownFeatures.Length - 1))
                  from statementCount in Gen.Choose(1, 20)
                  let features = featureIndices.Select(i => knownFeatures[i]).ToList()
                  select (features: (IReadOnlyList<string>)features, statementCount);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (features, statementCount) = tuple;

            // Call the multi-feature overload
            var actual = _effortEstimator.EstimateEffort(features, statementCount);

            // Compute expected: sum of per-feature raw efforts then apply clamping for max risk
            double rawMin = 0, rawMax = 0;
            int maxRisk = 1;
            foreach (var feature in features.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var riskLevel = StatementGrouper.GetFeatureRiskLevel(feature);
                if (riskLevel > maxRisk) maxRisk = riskLevel;

                var (baseMin, baseMax) = BaseEffort[riskLevel];
                var seriesMultiplier = (1.0 - Math.Pow(ReductionFactor, statementCount)) / (1.0 - ReductionFactor);
                rawMin += baseMin * seriesMultiplier;
                rawMax += baseMax * seriesMultiplier;
            }

            // Apply confidence-based clamping using max risk level
            var maxRatio = maxRisk switch
            {
                <= 2 => 1.5,
                3 => 2.0,
                _ => 3.0
            };

            double expectedMin = rawMin;
            double expectedMax = rawMax;
            if (rawMin > 0 && rawMax > 0 && rawMax / rawMin > maxRatio)
            {
                expectedMin = rawMax / maxRatio;
            }

            actual.MinHours.Should().BeApproximately(expectedMin, 1e-10,
                $"MinHours for {features.Count} features at count={statementCount} should match confidence-clamped sum");
            actual.MaxHours.Should().BeApproximately(expectedMax, 1e-10,
                $"MaxHours for {features.Count} features at count={statementCount} should match confidence-clamped sum");
        });
    }

    /// <summary>
    /// Property 4 (boundary): Single feature multi-feature overload should equal single-feature overload.
    ///
    /// **Validates: Requirements 7.1**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MultiFeatureEffort_SingleFeature_EqualsDirectRiskOverload()
    {
        var knownFeatures = new[]
        {
            "TOP", "ISNULL", "GETDATE", "TRY_CATCH", "MERGE", "OPENQUERY"
        };

        var gen = from featureIndex in Gen.Choose(0, knownFeatures.Length - 1)
                  from statementCount in Gen.Choose(1, 20)
                  select (feature: knownFeatures[featureIndex], statementCount);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (feature, statementCount) = tuple;
            var features = new List<string> { feature };

            var multiResult = _effortEstimator.EstimateEffort((IReadOnlyList<string>)features, statementCount);
            var singleResult = _effortEstimator.EstimateEffort(
                StatementGrouper.GetFeatureRiskLevel(feature), statementCount);

            multiResult.MinHours.Should().BeApproximately(singleResult.MinHours, 1e-10,
                "single-feature multi-overload should match direct risk-level overload for MinHours");
            multiResult.MaxHours.Should().BeApproximately(singleResult.MaxHours, 1e-10,
                "single-feature multi-overload should match direct risk-level overload for MaxHours");
        });
    }

    /// <summary>
    /// Property 4 (deduplication): Duplicate features should not multiply effort —
    /// EstimateEffort(["TOP","TOP"], count) == EstimateEffort(["TOP"], count).
    ///
    /// **Validates: Requirements 7.1**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MultiFeatureEffort_DuplicateFeatures_DoNotMultiplyEffort()
    {
        var knownFeatures = new[]
        {
            "TOP", "ISNULL", "TRY_CATCH", "MERGE", "OPENQUERY"
        };

        var gen = from featureIndex in Gen.Choose(0, knownFeatures.Length - 1)
                  from duplicateCount in Gen.Choose(2, 5)
                  from statementCount in Gen.Choose(1, 20)
                  let feature = knownFeatures[featureIndex]
                  let duplicated = Enumerable.Repeat(feature, duplicateCount).ToList()
                  select (duplicated: (IReadOnlyList<string>)duplicated,
                          single: (IReadOnlyList<string>)new List<string> { feature },
                          statementCount);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (duplicated, single, statementCount) = tuple;

            var duplicatedResult = _effortEstimator.EstimateEffort(duplicated, statementCount);
            var singleResult = _effortEstimator.EstimateEffort(single, statementCount);

            duplicatedResult.MinHours.Should().BeApproximately(singleResult.MinHours, 1e-10,
                "duplicate features should be deduplicated — same effort as single");
            duplicatedResult.MaxHours.Should().BeApproximately(singleResult.MaxHours, 1e-10,
                "duplicate features should be deduplicated — same effort as single");
        });
    }

    #endregion

    #region Property 13: Output ordering by priority

    /// <summary>
    /// Property 13: Output ordering by priority — verify descending PriorityScore with correct
    /// tie-breaking (risk level descending, then statement count descending).
    ///
    /// **Validates: Requirements 5.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property OutputOrdering_DescendingPriorityScore_WithTieBreaking()
    {
        return Prop.ForAll(GenWorkItemList().ToArbitrary(), workItems =>
        {
            var result = _priorityCalculator.AssignPriorityLabels(workItems);

            // Verify output is sorted by PriorityScore descending
            for (int i = 0; i < result.Count - 1; i++)
            {
                var current = result[i].Item;
                var next = result[i + 1].Item;

                current.PriorityScore.Should().BeGreaterThanOrEqualTo(next.PriorityScore,
                    $"item at position {i} should have PriorityScore >= item at position {i + 1}");

                // If PriorityScores are equal, check tie-breaking
                if (Math.Abs(current.PriorityScore - next.PriorityScore) < 1e-10)
                {
                    // Tie-break 1: risk level descending
                    if (current.RiskLevel != next.RiskLevel)
                    {
                        current.RiskLevel.Should().BeGreaterThanOrEqualTo(next.RiskLevel,
                            $"equal PriorityScore items should be ordered by risk level descending");
                    }
                    else
                    {
                        // Tie-break 2: statement count descending
                        var currentCount = current.AffectedObjects.Sum(ao => ao.StatementCount);
                        var nextCount = next.AffectedObjects.Sum(ao => ao.StatementCount);

                        currentCount.Should().BeGreaterThanOrEqualTo(nextCount,
                            "equal PriorityScore and risk level should be ordered by statement count descending");
                    }
                }
            }
        });
    }

    /// <summary>
    /// Property 13 (additional): Verify items with higher PriorityScore always appear before lower ones.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OutputOrdering_HigherScoreAlwaysBeforeLower()
    {
        // Generate items with guaranteed distinct scores
        var gen = from count in Gen.Choose(2, 15)
                  from baseScores in Gen.ArrayOf(count, Gen.Choose(1, 1000))
                  let distinctScores = baseScores.Select((s, i) => s * 10.0 + i).ToArray() // ensure distinct
                  from riskLevels in Gen.ArrayOf(count, Gen.Choose(1, 5))
                  let items = Enumerable.Range(0, count)
                      .Select(i => CreateWorkItem(
                          $"WI-{(i + 1):D3}",
                          distinctScores[i],
                          riskLevels[i],
                          new HourRange { MinHours = 1.0, MaxHours = 5.0 }))
                      .ToList()
                  select (IReadOnlyList<WorkItem>)items;

        return Prop.ForAll(gen.ToArbitrary(), workItems =>
        {
            var result = _priorityCalculator.AssignPriorityLabels(workItems);

            // Every adjacent pair should have PriorityScore >= next
            for (int i = 0; i < result.Count - 1; i++)
            {
                result[i].Item.PriorityScore.Should().BeGreaterThanOrEqualTo(
                    result[i + 1].Item.PriorityScore,
                    "output must be ordered by PriorityScore descending");
            }
        });
    }

    #endregion
}
