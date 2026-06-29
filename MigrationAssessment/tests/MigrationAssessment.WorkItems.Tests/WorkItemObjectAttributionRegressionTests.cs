using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Core;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Regression tests for TASK-08: verifies that the file-based work item generation path
/// correctly attributes work items to named database objects from the objectInventory
/// in the assessment JSON, rather than always defaulting to "Ad Hoc Queries".
///
/// These tests are designed to FAIL against the unfixed code (where GenerateFromFileAsync
/// never passes objectInventory to the generator) and PASS after the fix.
/// </summary>
public class WorkItemObjectAttributionRegressionTests
{
    /// <summary>
    /// Generates work items from assessment JSON via the file-based pipeline and validates
    /// that statement-to-object attribution is consistent with the objectInventory section.
    ///
    /// Rule: For every work item, if its source statement's detected features are a subset
    /// of some objectInventory entry's detectedFeatures (and that entry is not AdHoc), then
    /// affectedObjects for that work item must reference that object's name — NOT "Ad Hoc Queries".
    /// </summary>
    [Fact]
    public async Task FileBasedGeneration_WorkItemsReferenceNamedObjects_NotAdHocQueries()
    {
        // Arrange: Use the project's test-assessment.json file
        var testJsonPath = FindTestAssessmentJson();
        testJsonPath.Should().NotBeNull("test-assessment.json must exist in the project root");

        var reader = new AssessmentJsonReader();
        var readResult = await reader.ReadAsync(testJsonPath!, CancellationToken.None);
        readResult.Succeeded.Should().BeTrue();
        readResult.ObjectInventory.Should().NotBeNull()
            .And.NotBeEmpty("the assessment JSON must contain an objectInventory section");

        // Act: Generate work items using the file-based path (same as CLI)
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);
        workItems.Should().NotBeEmpty();

        // Assert: For each work item with features matching a named object,
        // it must NOT use "Ad Hoc Queries" — it must use the object's real name.
        var namedObjects = readResult.ObjectInventory!
            .Where(e => e.Type != "AdHoc")
            .ToList();

        var violations = new List<string>();

        foreach (var wi in workItems)
        {
            var wiFeatures = wi.DetectedFeatures
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (wiFeatures.Count == 0)
                continue;

            // Find named objects whose features fully contain this work item's features
            var matchingObjects = namedObjects
                .Where(obj => wiFeatures.All(f =>
                    obj.DetectedFeatures.Contains(f, StringComparer.OrdinalIgnoreCase)))
                .ToList();

            if (matchingObjects.Count == 0)
                continue;

            // Check: does this work item reference at least one of the matching objects?
            var referencesNamedObject = wi.AffectedObjects
                .Any(ao => matchingObjects.Any(mo =>
                    string.Equals(mo.Name, ao.Name, StringComparison.OrdinalIgnoreCase)));

            // Also check: the feature does NOT appear in the Ad Hoc entry's features
            // (if it does, the statement might genuinely be ad hoc)
            var adHocEntry = readResult.ObjectInventory!
                .FirstOrDefault(e => e.Type == "AdHoc");

            var featureAlsoInAdHoc = adHocEntry is not null &&
                wiFeatures.Any(f =>
                    adHocEntry.DetectedFeatures.Contains(f, StringComparer.OrdinalIgnoreCase));

            // If the feature is exclusively in named objects (not in Ad Hoc), it MUST be attributed
            if (!featureAlsoInAdHoc && !referencesNamedObject)
            {
                var objectNames = string.Join(", ", matchingObjects.Select(o => o.Name));
                violations.Add(
                    $"{wi.Id} ({wi.Title}): features [{string.Join(", ", wiFeatures)}] " +
                    $"match named object(s) [{objectNames}] but affectedObjects shows " +
                    $"[{string.Join(", ", wi.AffectedObjects.Select(ao => ao.Name))}]");
            }
        }

        violations.Should().BeEmpty(
            "all work items whose features exclusively match named objects " +
            "(not in Ad Hoc) must reference those objects in affectedObjects");
    }

    [Fact]
    public async Task WI_UPDLOCK_ROWLOCK_References_SpUpdateStockWithLock()
    {
        var testJsonPath = FindTestAssessmentJson();
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        var lockWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("UPDLOCK") || wi.DetectedFeatures.Contains("ROWLOCK"));

        lockWorkItem.Should().NotBeNull("there should be a work item for UPDLOCK/ROWLOCK");
        lockWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_UpdateStockWithLock",
            "the UPDLOCK/ROWLOCK statement is attributed to sp_UpdateStockWithLock in objectInventory");
        lockWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries");
        lockWorkItem.Title.Should().Contain("sp_UpdateStockWithLock");
    }

    [Fact]
    public async Task WI_MERGE_References_SpUpsertProducts()
    {
        var testJsonPath = FindTestAssessmentJson();
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        var mergeWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("MERGE"));

        mergeWorkItem.Should().NotBeNull("there should be a work item for MERGE");
        mergeWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_UpsertProducts",
            "the MERGE statement is attributed to sp_UpsertProducts in objectInventory");
        mergeWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries");
        mergeWorkItem.Title.Should().Contain("sp_UpsertProducts");
    }

    [Fact]
    public async Task WI_GLOBAL_TEMP_TABLE_References_SpSharedTempReport()
    {
        var testJsonPath = FindTestAssessmentJson();
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        // There may be multiple GLOBAL_TEMP_TABLE work items — all should reference sp_SharedTempReport
        var globalTempWorkItems = workItems
            .Where(wi => wi.DetectedFeatures.Contains("GLOBAL_TEMP_TABLE"))
            .ToList();

        globalTempWorkItems.Should().NotBeEmpty("there should be work items for GLOBAL_TEMP_TABLE");

        foreach (var wi in globalTempWorkItems)
        {
            wi.AffectedObjects.Should().Contain(
                ao => ao.Name == "sp_SharedTempReport",
                $"Work item {wi.Id} with GLOBAL_TEMP_TABLE should reference sp_SharedTempReport");
            wi.AffectedObjects.Should().NotContain(
                ao => ao.Name == "Ad Hoc Queries",
                $"Work item {wi.Id} should not reference Ad Hoc Queries");
            wi.Title.Should().Contain("sp_SharedTempReport");
        }
    }

    [Fact]
    public async Task WI_NOLOCK_FromNamedProc_References_SpGetInventorySnapshot()
    {
        var testJsonPath = FindTestAssessmentJson();
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        // There should be at least one NOLOCK work item attributed to sp_GetInventorySnapshot
        var nolockWorkItems = workItems
            .Where(wi => wi.DetectedFeatures.Contains("NOLOCK"))
            .ToList();

        nolockWorkItems.Should().HaveCountGreaterThanOrEqualTo(2,
            "there should be separate NOLOCK work items for named proc and ad hoc");

        var namedProcItem = nolockWorkItems.FirstOrDefault(wi =>
            wi.AffectedObjects.Any(ao => ao.Name == "sp_GetInventorySnapshot"));

        namedProcItem.Should().NotBeNull(
            "one NOLOCK work item should be attributed to sp_GetInventorySnapshot");
        namedProcItem!.Title.Should().Contain("sp_GetInventorySnapshot");
    }

    [Fact]
    public async Task AdHoc_SysColumns_DiagnosticQuery_KeepsAdHocLabel()
    {
        var testJsonPath = FindTestAssessmentJson();
        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        // The sys.columns diagnostic queries (with NOLOCK) should remain as Ad Hoc Queries
        var adHocNolockItems = workItems
            .Where(wi => wi.DetectedFeatures.Contains("NOLOCK")
                         && wi.AffectedObjects.Any(ao => ao.Name == "Ad Hoc Queries"))
            .ToList();

        adHocNolockItems.Should().NotBeEmpty(
            "the sys.columns diagnostic queries with NOLOCK should stay as Ad Hoc Queries");

        // Verify the SQL pattern references sys.columns (the diagnostic query)
        var primaryItem = adHocNolockItems.First();
        primaryItem.SqlServerPattern.Should().Contain("sys.columns",
            "the ad hoc NOLOCK work item should contain the sys.columns diagnostic query");
    }

    [Fact]
    public async Task ConsistencyCheck_WorkItemObjects_ExistInObjectInventory()
    {
        // Regression rule: for every work item, if its affectedObjects reference a named
        // object (not "Ad Hoc Queries"), that object MUST exist in the objectInventory.
        var testJsonPath = FindTestAssessmentJson();

        var reader = new AssessmentJsonReader();
        var readResult = await reader.ReadAsync(testJsonPath!, CancellationToken.None);
        var objectInventory = readResult.ObjectInventory!;

        var workItems = await GenerateWorkItemsFromFile(testJsonPath!);

        foreach (var wi in workItems)
        {
            foreach (var ao in wi.AffectedObjects)
            {
                if (ao.Name == "Ad Hoc Queries")
                {
                    // Ad Hoc should have a matching entry in inventory
                    objectInventory.Should().Contain(
                        e => e.Type == "AdHoc",
                        $"Work item {wi.Id} references Ad Hoc but no AdHoc entry in inventory");
                }
                else
                {
                    // Named object must exist in the inventory
                    objectInventory.Should().Contain(
                        e => string.Equals(e.Name, ao.Name, StringComparison.OrdinalIgnoreCase)
                             && e.Type != "AdHoc",
                        $"Work item {wi.Id} references '{ao.Name}' which must exist in inventory");
                }
            }
        }
    }

    #region Helpers

    private static async Task<IReadOnlyList<WorkItem>> GenerateWorkItemsFromFile(string assessmentJsonPath)
    {
        var resolver = new StatementObjectResolver();
        var grouper = new StatementGrouper(
            NullLogger<StatementGrouper>.Instance, resolver);
        var priorityCalculator = new PriorityCalculator();
        var effortEstimator = new EffortEstimator();
        var knowledgeBase = new RemediationKnowledgeBase();
        var conversionEngine = new PostgresConversionEngine();
        var deduplicator = new WorkItemDeduplicator();
        var titleGenerator = new TitleGenerator();
        var descriptionGenerator = new DescriptionGenerator();
        var guidanceGenerator = new RemediationGuidanceGenerator(knowledgeBase);
        var acceptanceCriteriaGenerator = new AcceptanceCriteriaGenerator();
        var jsonReader = new AssessmentJsonReader();
        var jsonWriter = new WorkItemJsonWriter();
        var markdownWriter = new WorkItemMarkdownWriter();

        var service = new WorkItemGeneratorService(
            grouper, priorityCalculator, effortEstimator, knowledgeBase, conversionEngine,
            deduplicator, titleGenerator, descriptionGenerator, guidanceGenerator,
            acceptanceCriteriaGenerator, jsonReader, jsonWriter, markdownWriter);

        var outputPath = Path.GetTempFileName();
        try
        {
            var config = new WorkItemConfiguration
            {
                OutputJsonPath = outputPath,
                MinimumRiskLevel = 1,
                MarkdownEnabled = false
            };

            var result = await service.GenerateFromFileAsync(assessmentJsonPath, config, CancellationToken.None);
            result.Succeeded.Should().BeTrue($"work item generation should succeed: {result.ErrorMessage}");
            return result.WorkItems;
        }
        finally
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    private static string? FindTestAssessmentJson()
    {
        // Walk up from the test assembly's output directory to find test-assessment.json
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "test-assessment.json");
            if (File.Exists(candidate))
                return candidate;

            // Also check for the MigrationAssessment solution root marker
            var slnx = Path.Combine(dir, "MigrationAssessment.slnx");
            if (File.Exists(slnx))
            {
                candidate = Path.Combine(dir, "test-assessment.json");
                if (File.Exists(candidate))
                    return candidate;
            }

            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    #endregion
}
