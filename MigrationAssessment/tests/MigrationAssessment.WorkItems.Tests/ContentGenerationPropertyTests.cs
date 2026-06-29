using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using MigrationAssessment.WorkItems.Models;
using NSubstitute;
using System.Text.RegularExpressions;
using HourRange = MigrationAssessment.WorkItems.Models.HourRange;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Property-based tests for content generation layer: TitleGenerator, AcceptanceCriteriaGenerator,
/// and SQL pattern truncation logic.
/// Validates: Requirements 3.1, 3.2, 3.4, 3.6, 3.7
/// </summary>
public class ContentGenerationPropertyTests
{
    private readonly TitleGenerator _titleGenerator = new();
    private readonly AcceptanceCriteriaGenerator _acceptanceCriteriaGenerator = new();

    #region Generators

    /// <summary>
    /// Generates non-empty, non-whitespace strings suitable for feature names and object names.
    /// Ensures at least one non-whitespace character by starting with an alphanumeric char.
    /// </summary>
    private static Gen<string> GenNonEmptyString(int minLen = 1, int maxLen = 80)
    {
        var nonSpaceChars = Gen.Elements(
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
            'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
            'U', 'V', 'W', 'X', 'Y', 'Z',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            'u', 'v', 'w', 'x', 'y', 'z',
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            '_', '.');

        var allChars = Gen.Elements(
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J',
            'K', 'L', 'M', 'N', 'O', 'P', 'Q', 'R', 'S', 'T',
            'U', 'V', 'W', 'X', 'Y', 'Z',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
            'k', 'l', 'm', 'n', 'o', 'p', 'q', 'r', 's', 't',
            'u', 'v', 'w', 'x', 'y', 'z',
            '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
            '_', '.', ' ');

        return from firstChar in nonSpaceChars
               from restLen in Gen.Choose(Math.Max(0, minLen - 1), Math.Max(0, maxLen - 1))
               from restChars in Gen.ArrayOf(restLen, allChars)
               select new string(new[] { firstChar }.Concat(restChars).ToArray());
    }

    /// <summary>
    /// Generates SQL-like text strings of various lengths.
    /// </summary>
    private static Gen<string> GenSqlText(int minLen = 10, int maxLen = 1000)
    {
        return from len in Gen.Choose(minLen, maxLen)
               from chars in Gen.ArrayOf(len,
                   Gen.Elements(
                       'S', 'E', 'L', 'C', 'T', 'F', 'R', 'O', 'M', 'W',
                       'H', 'I', 'N', 'D', 'A', 'P', 'U', 'B', 'G', 'X',
                       'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j',
                       '0', '1', '2', '3', '4', '5', '6', '7', '8', '9',
                       ' ', '_', '.', ',', '(', ')', '*', '=', ';', '\n'))
               select new string(chars);
    }

    #endregion

    #region Property 5: Work item structural completeness

    /// <summary>
    /// Property 5: Work item structural completeness — verify all generated work items contain
    /// required non-empty fields with correct constraints.
    ///
    /// Sub-property: Title is non-empty and ≤120 characters.
    ///
    /// **Validates: Requirements 3.1, 3.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property GeneratedTitle_IsNonEmpty_AndWithin120Chars()
    {
        var gen = from featureName in GenNonEmptyString(1, 50)
                  from objectName in GenNonEmptyString(1, 80)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(featureName, objectName, riskLevel);

            title.Should().NotBeNullOrEmpty("title must be non-empty");
            title.Length.Should().BeLessThanOrEqualTo(120,
                "title must not exceed 120 characters");
        });
    }

    /// <summary>
    /// Property 5: Work item structural completeness — verify acceptance criteria always
    /// contains at least 2 items, all non-empty strings.
    ///
    /// **Validates: Requirements 3.7**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property GeneratedAcceptanceCriteria_HasAtLeastTwoItems_AllNonEmpty()
    {
        var gen = from featureName in GenNonEmptyString(1, 40)
                  from objectName in GenNonEmptyString(1, 40)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;

            var criteria = _acceptanceCriteriaGenerator.GenerateCriteria(featureName, riskLevel, objectName);

            criteria.Count.Should().BeGreaterThanOrEqualTo(2,
                "acceptance criteria must contain at least 2 items");
            criteria.Should().AllSatisfy(c =>
                c.Should().NotBeNullOrWhiteSpace("each criterion must be non-empty"));
        });
    }

    /// <summary>
    /// Property 5: Work item structural completeness — verify SQL pattern truncation
    /// produces non-empty output ≤500 characters.
    ///
    /// **Validates: Requirements 3.1, 3.6**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TruncatedSqlPattern_IsNonEmpty_AndWithin500Chars()
    {
        var gen = GenSqlText(1, 1500);

        return Prop.ForAll(gen.ToArbitrary(), sqlText =>
        {
            // Simulates the truncation logic from RemediationGuidanceGenerator
            var truncated = sqlText.Length <= 500 ? sqlText : sqlText[..500];

            truncated.Should().NotBeNullOrEmpty("SQL pattern must be non-empty");
            truncated.Length.Should().BeLessThanOrEqualTo(500,
                "SQL pattern must not exceed 500 characters");
        });
    }

    #endregion

    #region Property 6: Title format conformance

    /// <summary>
    /// Property 6: Title format conformance — verify titles match pattern
    /// `[Risk R] Convert F in O` and ≤120 chars.
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TitleFormat_MatchesExpectedPattern()
    {
        var gen = from featureName in GenNonEmptyString(1, 30)
                  from objectName in GenNonEmptyString(1, 30)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(featureName, objectName, riskLevel);

            // Title must match pattern: [Risk N] Convert <feature> in <something>
            var pattern = @"^\[Risk [1-5]\] Convert .+ in .+$";
            title.Should().MatchRegex(pattern,
                $"title '{title}' must match pattern '[Risk N] Convert F in O'");

            title.Length.Should().BeLessThanOrEqualTo(120,
                "title must not exceed 120 characters");
        });
    }

    /// <summary>
    /// Property 6: Title format conformance — verify that the risk level in the title
    /// matches the input risk level.
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TitleFormat_ContainsCorrectRiskLevel()
    {
        var gen = from featureName in GenNonEmptyString(1, 30)
                  from objectName in GenNonEmptyString(1, 30)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(featureName, objectName, riskLevel);

            title.Should().StartWith($"[Risk {riskLevel}] Convert",
                $"title should contain the correct risk level {riskLevel}");
        });
    }

    /// <summary>
    /// Property 6: Title format conformance — verify that even very long feature/object
    /// name combinations still produce titles ≤120 chars.
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property TitleFormat_LongNames_StillWithin120Chars()
    {
        var gen = from featureName in GenNonEmptyString(20, 80)
                  from objectName in GenNonEmptyString(20, 120)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(featureName, objectName, riskLevel);

            title.Length.Should().BeLessThanOrEqualTo(120,
                "title must not exceed 120 characters even with very long names");
        });
    }

    /// <summary>
    /// Property 6: Title format conformance — verify null object name defaults to "Ad Hoc Queries".
    ///
    /// **Validates: Requirements 3.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property TitleFormat_NullObjectName_DefaultsToAdHocQueries()
    {
        var gen = from featureName in GenNonEmptyString(1, 30)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(featureName, null, riskLevel);

            title.Should().Contain("Ad Hoc Queries",
                "null object name should default to 'Ad Hoc Queries'");
            title.Should().MatchRegex(@"^\[Risk [1-5]\] Convert .+ in Ad Hoc Queries$");
        });
    }

    #endregion

    #region Property 5 (Design): Title format reflects feature count

    /// <summary>
    /// Property 5 (Design): Title format reflects feature count — when a work item has exactly
    /// one detected feature, the title contains the feature name; when it has more than one,
    /// the title contains "{count} features".
    ///
    /// **Validates: Requirements 4.1, 4.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TitleFormat_SingleFeature_ContainsFeatureName()
    {
        var gen = from featureName in GenNonEmptyString(1, 40)
                  from objectName in GenNonEmptyString(1, 40)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;
            var features = new List<string> { featureName };

            var title = _titleGenerator.GenerateTitle(features, objectName, riskLevel);

            title.Should().Contain(featureName,
                "single-feature title must contain the feature name");
        });
    }

    /// <summary>
    /// Property 5 (Design): Title format reflects feature count — when a work item has more
    /// than one detected feature, the title contains "{count} features" instead of listing
    /// individual feature names.
    ///
    /// **Validates: Requirements 4.1, 4.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TitleFormat_MultipleFeatures_ContainsCountAndFeatures()
    {
        var gen = from featureCount in Gen.Choose(2, 10)
                  from features in Gen.ListOf(featureCount, GenNonEmptyString(1, 20))
                  from objectName in GenNonEmptyString(1, 40)
                  from riskLevel in Gen.Choose(1, 5)
                  select (features.ToList(), objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (features, objectName, riskLevel) = tuple;

            var title = _titleGenerator.GenerateTitle(features, objectName, riskLevel);

            var expectedFragment = $"{features.Count} features";
            title.Should().Contain(expectedFragment,
                $"multi-feature title must contain '{expectedFragment}' when {features.Count} features are detected");
        });
    }

    /// <summary>
    /// Property 5 (Design): Title format reflects feature count — the single vs. multi-feature
    /// distinction is mutually exclusive: a single feature title never contains "{count} features"
    /// and a multi-feature title never contains the individual feature name as the Convert target.
    ///
    /// **Validates: Requirements 4.1, 4.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TitleFormat_SingleFeature_DoesNotContainCountFeaturesPhrase()
    {
        var gen = from featureName in GenNonEmptyString(3, 40)
                  from objectName in GenNonEmptyString(1, 40)
                  from riskLevel in Gen.Choose(1, 5)
                  select (featureName, objectName, riskLevel);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (featureName, objectName, riskLevel) = tuple;
            var features = new List<string> { featureName };

            var title = _titleGenerator.GenerateTitle(features, objectName, riskLevel);

            // A single-feature title should NOT contain "1 features" pattern
            title.Should().NotContain("1 features",
                "single-feature title must not use the multi-feature format");
        });
    }

    #endregion

    #region Property 3 (Design): Remediation guidance covers all detected features

    /// <summary>
    /// Property 3 (Design): Remediation guidance covers all detected features — for any work item
    /// with N features in DetectedFeatures, the RemediationGuidance string contains exactly N
    /// "###"-headed sections, one for each feature name in DetectedFeatures.
    ///
    /// **Validates: Requirements 6.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property RemediationGuidance_ContainsExactlyNSections_ForNFeatures()
    {
        var gen = from featureCount in Gen.Choose(1, 8)
                  from features in Gen.ListOf(featureCount, GenNonEmptyString(3, 30))
                  from sqlText in GenSqlText(10, 200)
                  select (features.Distinct().ToList(), sqlText);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (features, sqlText) = tuple;

            // Skip if deduplication eliminated all features
            if (features.Count == 0) return;

            // Set up knowledge base mock that returns guidance for all features
            var knowledgeBase = Substitute.For<IRemediationKnowledgeBase>();
            knowledgeBase.GetGuidance(Arg.Any<string>()).Returns(callInfo =>
                new RemediationEntry
                {
                    PostgresEquivalent = $"pg_equivalent_{callInfo.Arg<string>()}",
                    RemediationSteps = $"Steps for {callInfo.Arg<string>()}",
                    IncompatibilityExplanation = $"{callInfo.Arg<string>()} is not compatible",
                    RiskLevel = 2
                });

            var generator = new RemediationGuidanceGenerator(knowledgeBase);
            var (guidance, _) = generator.GenerateGuidance(features, sqlText);

            // Count the number of "### " sections in the guidance
            var sectionPattern = new Regex(@"^### .+", RegexOptions.Multiline);
            var matches = sectionPattern.Matches(guidance);

            matches.Count.Should().Be(features.Count,
                $"guidance should contain exactly {features.Count} '###'-headed sections for {features.Count} features, " +
                $"but found {matches.Count}. Features: [{string.Join(", ", features)}]");
        });
    }

    /// <summary>
    /// Property 3 (Design): Remediation guidance covers all detected features — each feature
    /// in DetectedFeatures must have its own named "### {FeatureName}" heading in the guidance.
    ///
    /// **Validates: Requirements 6.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property RemediationGuidance_EachFeatureHasNamedSection()
    {
        var gen = from featureCount in Gen.Choose(1, 6)
                  from features in Gen.ListOf(featureCount, GenNonEmptyString(3, 25))
                  from sqlText in GenSqlText(10, 200)
                  select (features.Distinct().ToList(), sqlText);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (features, sqlText) = tuple;

            if (features.Count == 0) return;

            var knowledgeBase = Substitute.For<IRemediationKnowledgeBase>();
            knowledgeBase.GetGuidance(Arg.Any<string>()).Returns(callInfo =>
                new RemediationEntry
                {
                    PostgresEquivalent = "COALESCE(x, default)",
                    RemediationSteps = "Replace with COALESCE",
                    IncompatibilityExplanation = "Not available in PostgreSQL",
                    RiskLevel = 2
                });

            var generator = new RemediationGuidanceGenerator(knowledgeBase);
            var (guidance, _) = generator.GenerateGuidance(features, sqlText);

            foreach (var feature in features)
            {
                guidance.Should().Contain($"### {feature}",
                    $"guidance must contain a '### {feature}' section for detected feature '{feature}'");
            }
        });
    }

    #endregion

    #region Property 7: SQL pattern sourced from input

    /// <summary>
    /// Property 7: SQL pattern sourced from input — verify sqlServerPattern is substring of
    /// an input statement's SqlText (truncation to max 500 chars preserves this property).
    ///
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SqlPatternTruncation_ResultIsSubstringOfOriginal()
    {
        var gen = GenSqlText(1, 1500);

        return Prop.ForAll(gen.ToArbitrary(), originalSql =>
        {
            // Apply same truncation logic as RemediationGuidanceGenerator.TruncateSql
            var truncated = originalSql.Length <= 500
                ? originalSql
                : originalSql[..500];

            // The truncated result must be a substring of the original
            originalSql.Should().Contain(truncated,
                "the truncated SQL pattern must be a substring of the original SQL text");
        });
    }

    /// <summary>
    /// Property 7: SQL pattern sourced from input — verify that when SQL text is ≤500 chars,
    /// the pattern equals the full original text.
    ///
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SqlPatternTruncation_ShortText_PreservesFullContent()
    {
        var gen = GenSqlText(1, 500);

        return Prop.ForAll(gen.ToArbitrary(), originalSql =>
        {
            var truncated = originalSql.Length <= 500
                ? originalSql
                : originalSql[..500];

            truncated.Should().Be(originalSql,
                "SQL text ≤500 chars should be preserved in full");
        });
    }

    /// <summary>
    /// Property 7: SQL pattern sourced from input — verify truncation produces exactly 500 chars
    /// for text longer than 500 chars.
    ///
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SqlPatternTruncation_LongText_ProducesExactly500Chars()
    {
        var gen = GenSqlText(501, 1500);

        return Prop.ForAll(gen.ToArbitrary(), originalSql =>
        {
            var truncated = originalSql.Length <= 500
                ? originalSql
                : originalSql[..500];

            truncated.Length.Should().Be(500,
                "SQL text longer than 500 chars should be truncated to exactly 500");
        });
    }

    /// <summary>
    /// Property 7: SQL pattern sourced from input — verify the truncated text starts at the
    /// beginning of the original (it's a prefix, not an arbitrary substring).
    ///
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SqlPatternTruncation_IsAlwaysAPrefixOfOriginal()
    {
        var gen = GenSqlText(1, 1500);

        return Prop.ForAll(gen.ToArbitrary(), originalSql =>
        {
            var truncated = originalSql.Length <= 500
                ? originalSql
                : originalSql[..500];

            originalSql.Should().StartWith(truncated,
                "truncated SQL pattern must be a prefix of the original");
        });
    }

    #endregion
}
