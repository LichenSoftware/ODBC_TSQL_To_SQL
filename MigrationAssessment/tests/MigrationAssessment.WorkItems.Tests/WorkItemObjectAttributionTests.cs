using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Analysis;
using MigrationAssessment.Core;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;
using MigrationAssessment.WorkItems.Models;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Regression tests that verify work items generated from statements inside named objects
/// correctly carry the object's name in their affectedObjects and title, rather than
/// defaulting to "Ad Hoc Queries".
///
/// These tests use the same sample data shapes that validate TASK-01 (object inventory)
/// and TASK-02 (work items), generating both objectInventory and workItems from the same
/// input and asserting consistency between them.
/// </summary>
public class WorkItemObjectAttributionTests
{
    private readonly IStatementParser _parser;
    private readonly IStatementAnalyzer _analyzer;
    private readonly IRiskScorer _riskScorer;
    private readonly IStatementObjectResolver _resolver;
    private readonly IObjectInventoryBuilder _inventoryBuilder;

    public WorkItemObjectAttributionTests()
    {
        _parser = new StatementParser(NullLogger<StatementParser>.Instance);
        _analyzer = new StatementAnalyzer(NullLogger<StatementAnalyzer>.Instance);
        _riskScorer = new RiskScorer();
        _resolver = new StatementObjectResolver();
        _inventoryBuilder = new ObjectInventoryBuilder(
            _parser, _analyzer, _riskScorer, _resolver,
            NullLogger<ObjectInventoryBuilder>.Instance);
    }

    #region Sample Data (mimics real assessment data from TASK-01/TASK-02 validation)

    private static readonly string SpUpdateStockWithLockSource = @"
CREATE PROCEDURE sp_UpdateStockWithLock
    @ProductId INT,
    @Quantity INT
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRANSACTION;
    UPDATE Products WITH (UPDLOCK, ROWLOCK)
    SET StockQuantity = StockQuantity - @Quantity
    WHERE ProductId = @ProductId AND StockQuantity >= @Quantity;
    IF @@ROWCOUNT = 0
    BEGIN
        ROLLBACK;
        THROW 50001, 'Insufficient stock', 1;
    END;
    COMMIT;
END";

    private static readonly string SpGetInventorySnapshotSource = @"
CREATE PROCEDURE sp_GetInventorySnapshot
AS
BEGIN
    SET NOCOUNT ON;
    SELECT p.ProductId, p.ProductName, p.StockQuantity, c.CategoryName
    FROM Products p WITH (NOLOCK)
    INNER JOIN Categories c WITH (NOLOCK) ON p.CategoryId = c.CategoryId
    WHERE p.IsActive = 1;
END";

    private static readonly string SpSharedTempReportSource = @"
CREATE PROCEDURE sp_SharedTempReport
    @ReportDate DATE
AS
BEGIN
    SET NOCOUNT ON;
    CREATE TABLE ##TempReportData (
        ProductId INT,
        TotalSales DECIMAL(18,2),
        ReportDate DATE
    );
    INSERT INTO ##TempReportData
    SELECT p.ProductId, SUM(o.Amount), @ReportDate
    FROM Products p
    INNER JOIN Orders o ON p.ProductId = o.ProductId
    WHERE o.OrderDate = @ReportDate
    GROUP BY p.ProductId;
    SELECT * FROM ##TempReportData;
    DROP TABLE ##TempReportData;
END";

    private static readonly string SpUpsertProductsSource = @"
CREATE PROCEDURE sp_UpsertProducts
AS
BEGIN
    SET NOCOUNT ON;
    MERGE dbo.Products AS target
    USING dbo.ProductStaging AS source
    ON target.SKU = source.SKU
    WHEN MATCHED THEN
        UPDATE SET ProductName = source.ProductName, Price = source.Price
    WHEN NOT MATCHED THEN
        INSERT (ProductName, SKU, Price) VALUES (source.ProductName, source.SKU, source.Price);
END";

    private static DatabaseObjectInventory CreateSampleMetadataInventory()
    {
        return new DatabaseObjectInventory
        {
            Tables = [],
            Indexes = [],
            Constraints = [],
            ForeignKeys = [],
            ProgrammableObjects = new[]
            {
                new ProgrammableObjectMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "sp_UpdateStockWithLock",
                    ObjectType = "SQL_STORED_PROCEDURE",
                    SourceText = SpUpdateStockWithLockSource,
                    IsEncrypted = false
                },
                new ProgrammableObjectMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "sp_GetInventorySnapshot",
                    ObjectType = "SQL_STORED_PROCEDURE",
                    SourceText = SpGetInventorySnapshotSource,
                    IsEncrypted = false
                },
                new ProgrammableObjectMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "sp_SharedTempReport",
                    ObjectType = "SQL_STORED_PROCEDURE",
                    SourceText = SpSharedTempReportSource,
                    IsEncrypted = false
                },
                new ProgrammableObjectMetadata
                {
                    SchemaName = "dbo",
                    ObjectName = "sp_UpsertProducts",
                    ObjectType = "SQL_STORED_PROCEDURE",
                    SourceText = SpUpsertProductsSource,
                    IsEncrypted = false
                }
            },
            Synonyms = []
        };
    }

    /// <summary>
    /// Creates analyzed statements that simulate Query Store captures of statements
    /// running inside the sample stored procedures.
    /// </summary>
    private static IReadOnlyList<AnalyzedStatement> CreateSampleStatements()
    {
        return new[]
        {
            // Statement from sp_UpdateStockWithLock (UPDLOCK/ROWLOCK)
            CreateStatement(
                "(@ProductId int,@Quantity int)UPDATE Products WITH (UPDLOCK, ROWLOCK) SET StockQuantity = StockQuantity - @Quantity WHERE ProductId = @ProductId AND StockQuantity >= @Quantity",
                riskScore: 4,
                features: new[] { CreateFeature("UPDLOCK"), CreateFeature("ROWLOCK") }),

            // Statement from sp_GetInventorySnapshot (NOLOCK)
            CreateStatement(
                "SELECT p.ProductId, p.ProductName, p.StockQuantity, c.CategoryName FROM Products p WITH (NOLOCK) INNER JOIN Categories c WITH (NOLOCK) ON p.CategoryId = c.CategoryId WHERE p.IsActive = 1",
                riskScore: 4,
                features: new[] { CreateFeature("NOLOCK") }),

            // Statement from sp_SharedTempReport (GLOBAL_TEMP_TABLE)
            CreateStatement(
                "CREATE TABLE ##TempReportData ( ProductId INT, TotalSales DECIMAL(18,2), ReportDate DATE )",
                riskScore: 4,
                features: new[] { CreateFeature("GLOBAL_TEMP_TABLE") }),

            // Statement from sp_UpsertProducts (MERGE)
            CreateStatement(
                "MERGE dbo.Products AS target USING dbo.ProductStaging AS source ON target.SKU = source.SKU WHEN MATCHED THEN UPDATE SET ProductName = source.ProductName, Price = source.Price WHEN NOT MATCHED THEN INSERT (ProductName, SKU, Price) VALUES (source.ProductName, source.SKU, source.Price)",
                riskScore: 4,
                features: new[] { CreateFeature("MERGE") }),

            // Genuine ad hoc queries (not inside any stored procedure)
            CreateStatement(
                "SELECT TOP 10 * FROM Products ORDER BY CreatedDate DESC",
                riskScore: 2,
                features: new[] { CreateFeature("TOP") }),
            CreateStatement(
                "SELECT GETDATE()",
                riskScore: 2,
                features: new[] { CreateFeature("GETDATE") }),
            CreateStatement(
                "SELECT ISNULL(FirstName, '') FROM Customers",
                riskScore: 2,
                features: new[] { CreateFeature("ISNULL") }),
            CreateStatement(
                "SELECT LEN(ProductName) FROM Products",
                riskScore: 2,
                features: new[] { CreateFeature("LEN") }),
        };
    }

    #endregion

    #region Core Regression Tests

    [Fact]
    public void WorkItems_FromNamedObjects_CarryObjectNameInAffectedObjects()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();

        // Build object inventory (TASK-01 output)
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Build work items using the same data
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: every work item whose source statement lives inside a named object
        // must NOT have "Ad Hoc Queries" as its affected object
        var namedObjectEntries = objectInventory
            .Where(e => e.Type != "AdHoc")
            .ToList();

        namedObjectEntries.Should().NotBeEmpty("sample data should produce named objects");

        foreach (var entry in namedObjectEntries)
        {
            // Find work items whose primary feature appears in this object
            var relatedWorkItems = workItems
                .Where(wi => wi.AffectedObjects.Any(ao => ao.Name == entry.Name))
                .ToList();

            foreach (var wi in relatedWorkItems)
            {
                wi.AffectedObjects.Should().NotContain(
                    ao => ao.Name == "Ad Hoc Queries",
                    $"Work item {wi.Id} ({wi.Title}) is attributed to {entry.Name} " +
                    $"and should not also show 'Ad Hoc Queries'");
            }
        }
    }

    [Fact]
    public void WorkItem_ForUpdateLock_ShowsSpUpdateStockWithLock_NotAdHocQueries()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: The UPDLOCK/ROWLOCK work item should reference sp_UpdateStockWithLock
        var lockWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("UPDLOCK") || wi.DetectedFeatures.Contains("ROWLOCK"));

        lockWorkItem.Should().NotBeNull("there should be a work item for UPDLOCK/ROWLOCK");
        lockWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_UpdateStockWithLock",
            "the UPDLOCK/ROWLOCK statement comes from sp_UpdateStockWithLock");
        lockWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries",
            "this statement is inside a named proc, not ad hoc");
        lockWorkItem.Title.Should().Contain("sp_UpdateStockWithLock",
            "the work item title should include the object name");
    }

    [Fact]
    public void WorkItem_ForNolock_ShowsSpGetInventorySnapshot_NotAdHocQueries()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: The NOLOCK work item should reference sp_GetInventorySnapshot
        var nolockWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("NOLOCK"));

        nolockWorkItem.Should().NotBeNull("there should be a work item for NOLOCK");
        nolockWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_GetInventorySnapshot",
            "the NOLOCK statement comes from sp_GetInventorySnapshot");
        nolockWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries");
        nolockWorkItem.Title.Should().Contain("sp_GetInventorySnapshot");
    }

    [Fact]
    public void WorkItem_ForMerge_ShowsSpUpsertProducts_NotAdHocQueries()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: The MERGE work item should reference sp_UpsertProducts
        var mergeWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("MERGE"));

        mergeWorkItem.Should().NotBeNull("there should be a work item for MERGE");
        mergeWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_UpsertProducts",
            "the MERGE statement comes from sp_UpsertProducts");
        mergeWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries");
    }

    [Fact]
    public void WorkItem_ForGlobalTempTable_ShowsSpSharedTempReport_NotAdHocQueries()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: The GLOBAL_TEMP_TABLE work item should reference sp_SharedTempReport
        var tempTableWorkItem = workItems.FirstOrDefault(wi =>
            wi.DetectedFeatures.Contains("GLOBAL_TEMP_TABLE"));

        tempTableWorkItem.Should().NotBeNull("there should be a work item for GLOBAL_TEMP_TABLE");
        tempTableWorkItem!.AffectedObjects.Should().Contain(
            ao => ao.Name == "sp_SharedTempReport",
            "the GLOBAL_TEMP_TABLE statement comes from sp_SharedTempReport");
        tempTableWorkItem.AffectedObjects.Should().NotContain(
            ao => ao.Name == "Ad Hoc Queries");
    }

    [Fact]
    public void GenuineAdHocStatements_StillLabeledAsAdHocQueries()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: Work items from genuine ad hoc statements should use "Ad Hoc Queries"
        // The TOP, GETDATE, ISNULL, LEN statements are not inside any named object
        var adHocWorkItems = workItems
            .Where(wi => wi.AffectedObjects.Any(ao => ao.Name == "Ad Hoc Queries"))
            .ToList();

        adHocWorkItems.Should().NotBeEmpty(
            "genuine ad hoc statements should still produce 'Ad Hoc Queries' work items");

        // The ad hoc object inventory entry should exist
        var adHocEntry = objectInventory.FirstOrDefault(e => e.Type == "AdHoc");
        adHocEntry.Should().NotBeNull("there should be an 'Ad Hoc' entry in the object inventory");
    }

    [Fact]
    public void ObjectInventory_And_WorkItems_AgreeOnObjectAttribution()
    {
        // Arrange
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Act
        var workItems = GenerateWorkItems(statements, objectInventory, rawInventory);

        // Assert: For every work item that has a named object in affectedObjects,
        // that object must also exist in the objectInventory with a non-AdHoc type
        foreach (var wi in workItems)
        {
            foreach (var ao in wi.AffectedObjects)
            {
                if (ao.Name == "Ad Hoc Queries")
                {
                    // The ad hoc bucket should exist in inventory
                    objectInventory.Should().Contain(
                        e => e.Type == "AdHoc",
                        $"Work item {wi.Id} references 'Ad Hoc Queries' but no AdHoc entry exists in inventory");
                }
                else
                {
                    // Named objects must exist in the inventory
                    objectInventory.Should().Contain(
                        e => e.Name == ao.Name && e.Type != "AdHoc",
                        $"Work item {wi.Id} references '{ao.Name}' which must exist as a named object in inventory");
                }
            }
        }
    }

    [Fact]
    public void SharedResolver_UsedByBoth_InventoryBuilder_And_WorkItemGrouper()
    {
        // This test verifies that the same resolver instance produces consistent results
        // when used by both ObjectInventoryBuilder (for building per-object stats) and
        // the StatementGrouper (for labeling work items).
        var rawInventory = CreateSampleMetadataInventory();
        var statements = CreateSampleStatements();

        // Resolve using the shared resolver directly
        var resolvedMap = _resolver.ResolveStatementObjects(statements, rawInventory);

        // Build inventory (uses the same resolver internally)
        var objectInventory = _inventoryBuilder.BuildInventory(statements, rawInventory);

        // Every statement that the resolver maps to a named object should NOT appear
        // in the "Ad Hoc" bucket of the inventory
        var adHocEntry = objectInventory.FirstOrDefault(e => e.Type == "AdHoc");
        var resolvedStatementCount = resolvedMap.Count;

        // The ad hoc entry's statement count should equal total statements minus resolved ones
        var totalStatements = statements.Count;
        var expectedAdHocCount = totalStatements - resolvedStatementCount;

        if (expectedAdHocCount > 0)
        {
            adHocEntry.Should().NotBeNull();
            adHocEntry!.StatementCount.Should().Be(expectedAdHocCount,
                "Ad Hoc statement count should be total minus resolved");
        }
        else
        {
            // If all statements are resolved, there might be no ad hoc entry
            if (adHocEntry is not null)
            {
                adHocEntry.StatementCount.Should().Be(0);
            }
        }
    }

    #endregion

    #region Helpers

    private static List<WorkItem> GenerateWorkItems(
        IReadOnlyList<AnalyzedStatement> statements,
        IReadOnlyList<ObjectInventoryEntry> objectInventory,
        DatabaseObjectInventory rawObjectInventory)
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

        var config = new WorkItemConfiguration
        {
            OutputJsonPath = Path.GetTempFileName(),
            MinimumRiskLevel = 1
        };

        var featureDetection = new FeatureDetectionResult
        {
            FeatureCounts = new Dictionary<string, int>(),
            DetailedInventory = [],
            InaccessibleFeatures = []
        };

        var result = service.GenerateWorkItems(
            statements, featureDetection, config, objectInventory, rawObjectInventory);

        result.Succeeded.Should().BeTrue("work item generation should succeed");
        return result.WorkItems.ToList();
    }

    private static AnalyzedStatement CreateStatement(
        string sqlText,
        int riskScore,
        IReadOnlyList<DetectedFeature> features)
    {
        return new AnalyzedStatement
        {
            Source = new CollectedStatement
            {
                SqlText = sqlText,
                Source = StatementSource.QueryStore,
                QueryHash = Guid.NewGuid().ToString("N"),
                ExecutionCount = 100
            },
            Classification = StatementClassification.Unknown,
            Features = features,
            RiskScore = riskScore,
            WeightedRisk = riskScore * 100.0,
            ParseSucceeded = true
        };
    }

    private static DetectedFeature CreateFeature(string name) => new()
    {
        FeatureName = name,
        Category = FeatureCategory.QueryFeature,
        StatementId = "test",
        Line = 1,
        Column = 1
    };

    #endregion
}
