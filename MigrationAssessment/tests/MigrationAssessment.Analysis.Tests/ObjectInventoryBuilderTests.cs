using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MigrationAssessment.Analysis;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis.Tests;

public class ObjectInventoryBuilderTests
{
    private readonly ObjectInventoryBuilder _builder;

    public ObjectInventoryBuilderTests()
    {
        var parserLogger = NullLogger<StatementParser>.Instance;
        var analyzerLogger = NullLogger<StatementAnalyzer>.Instance;
        var builderLogger = NullLogger<ObjectInventoryBuilder>.Instance;

        var parser = new StatementParser(parserLogger);
        var analyzer = new StatementAnalyzer(analyzerLogger);
        var riskScorer = new RiskScorer();

        _builder = new ObjectInventoryBuilder(parser, analyzer, riskScorer, builderLogger);
    }

    #region Helpers

    private static DatabaseObjectInventory EmptyInventory() => new()
    {
        Tables = [],
        Indexes = [],
        Constraints = [],
        ForeignKeys = [],
        ProgrammableObjects = [],
        Synonyms = []
    };

    private static DatabaseObjectInventory InventoryWith(params ProgrammableObjectMetadata[] objects) => new()
    {
        Tables = [],
        Indexes = [],
        Constraints = [],
        ForeignKeys = [],
        ProgrammableObjects = objects,
        Synonyms = []
    };

    private static AnalyzedStatement CreateStatement(
        string sqlText,
        int riskScore = 1,
        IReadOnlyList<DetectedFeature>? features = null)
    {
        return new AnalyzedStatement
        {
            Source = new CollectedStatement
            {
                SqlText = sqlText,
                Source = StatementSource.QueryStore,
                QueryHash = Guid.NewGuid().ToString("N"),
                ExecutionCount = 1
            },
            Classification = StatementClassification.Unknown,
            Features = features ?? Array.Empty<DetectedFeature>(),
            RiskScore = riskScore,
            WeightedRisk = riskScore,
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

    [Fact]
    public void BuildInventory_EmptyStatementsAndInventory_ReturnsEmptyList()
    {
        var result = _builder.BuildInventory([], EmptyInventory());

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildInventory_StoredProcedure_DetectedFromMetadata()
    {
        var procSource = @"
CREATE PROCEDURE usp_CreateOrder
    @CustomerId INT,
    @ProductId INT,
    @Quantity INT
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO Orders (CustomerId, ProductId, Quantity, OrderDate)
    VALUES (@CustomerId, @ProductId, @Quantity, GETDATE());
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "usp_CreateOrder",
            ObjectType = "SQL_STORED_PROCEDURE",
            SourceText = procSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("usp_CreateOrder");
        entry.Type.Should().Be("StoredProcedure");
        entry.StatementCount.Should().BeGreaterThan(0);
        entry.MaxRiskScore.Should().BeGreaterThanOrEqualTo(2);
        entry.DetectedFeatures.Should().Contain("GETDATE");
        entry.ConversionCategories.Should().NotBeEmpty();
    }

    [Fact]
    public void BuildInventory_View_DetectedFromMetadata()
    {
        var viewSource = @"
CREATE VIEW vw_ActiveOrders
AS
SELECT o.OrderId, o.CustomerId, c.CustomerName, o.OrderDate
FROM Orders o
INNER JOIN Customers c ON o.CustomerId = c.CustomerId
WHERE o.Status = 'Active'";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "vw_ActiveOrders",
            ObjectType = "VIEW",
            SourceText = viewSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("vw_ActiveOrders");
        entry.Type.Should().Be("View");
        entry.StatementCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public void BuildInventory_ScalarFunction_DetectedFromMetadata()
    {
        var fnSource = @"
CREATE FUNCTION fn_CalculateDiscount(@OrderTotal DECIMAL(10,2))
RETURNS DECIMAL(10,2)
AS
BEGIN
    DECLARE @Discount DECIMAL(10,2);
    SET @Discount = CASE WHEN @OrderTotal > 100 THEN @OrderTotal * 0.1 ELSE 0 END;
    RETURN @Discount;
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "fn_CalculateDiscount",
            ObjectType = "SQL_SCALAR_FUNCTION",
            SourceText = fnSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("fn_CalculateDiscount");
        result[0].Type.Should().Be("ScalarFunction");
    }

    [Fact]
    public void BuildInventory_TableValuedFunction_DetectedFromMetadata()
    {
        var fnSource = @"
CREATE FUNCTION fn_GetOrdersByCustomer(@CustomerId INT)
RETURNS TABLE
AS
RETURN (
    SELECT OrderId, ProductId, Quantity, OrderDate
    FROM Orders
    WHERE CustomerId = @CustomerId
)";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "fn_GetOrdersByCustomer",
            ObjectType = "SQL_INLINE_TABLE_VALUED_FUNCTION",
            SourceText = fnSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("fn_GetOrdersByCustomer");
        result[0].Type.Should().Be("TableValuedFunction");
    }

    [Fact]
    public void BuildInventory_Trigger_DetectedFromMetadata()
    {
        var trigSource = @"
CREATE TRIGGER tr_OrderAudit
ON Orders
AFTER INSERT, UPDATE
AS
BEGIN
    INSERT INTO AuditLog (TableName, Action, Timestamp)
    SELECT 'Orders', 'INSERT/UPDATE', GETDATE()
    FROM inserted;
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "tr_OrderAudit",
            ObjectType = "SQL_TRIGGER",
            SourceText = trigSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("tr_OrderAudit");
        result[0].Type.Should().Be("Trigger");
        result[0].DetectedFeatures.Should().Contain("GETDATE");
    }

    [Fact]
    public void BuildInventory_AdHocStatements_GroupedCorrectly()
    {
        var statements = new[]
        {
            CreateStatement("SELECT * FROM Customers WHERE CustomerId = 1", riskScore: 1),
            CreateStatement("UPDATE Products SET Price = 9.99 WHERE ProductId = 5", riskScore: 1)
        };

        var result = _builder.BuildInventory(statements, EmptyInventory());

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("Ad Hoc");
        entry.Type.Should().Be("AdHoc");
        entry.StatementCount.Should().Be(2);
    }

    [Fact]
    public void BuildInventory_MixedObjectsAndAdHoc_AllDetected()
    {
        var procSource = @"
CREATE PROCEDURE usp_GetCustomer
    @Id INT
AS
BEGIN
    SELECT * FROM Customers WHERE CustomerId = @Id;
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "usp_GetCustomer",
            ObjectType = "SQL_STORED_PROCEDURE",
            SourceText = procSource,
            IsEncrypted = false
        });

        // Ad hoc statement that doesn't match the proc
        var statements = new[]
        {
            CreateStatement("SELECT GETDATE()", riskScore: 2, features: [CreateFeature("GETDATE")])
        };

        var result = _builder.BuildInventory(statements, inventory);

        result.Should().HaveCount(2);
        result.Should().Contain(e => e.Name == "usp_GetCustomer" && e.Type == "StoredProcedure");
        result.Should().Contain(e => e.Name == "Ad Hoc" && e.Type == "AdHoc");
    }

    [Fact]
    public void BuildInventory_CorrelatesQueryStoreStatementToObject()
    {
        var procSource = @"
CREATE PROCEDURE usp_GetTopCustomers
    @TopN INT
AS
BEGIN
    SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName
    ORDER BY TotalSpent DESC;
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "usp_GetTopCustomers",
            ObjectType = "SQL_STORED_PROCEDURE",
            SourceText = procSource,
            IsEncrypted = false
        });

        // Simulate a Query Store statement that matches the proc body
        var queryStoreStmt = @"(@TopN int)SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName
    ORDER BY TotalSpent DESC";

        var statements = new[]
        {
            CreateStatement(queryStoreStmt, riskScore: 2, features: [CreateFeature("TOP"), CreateFeature("ISNULL")])
        };

        var result = _builder.BuildInventory(statements, inventory);

        // The statement should be correlated to the proc, not appear as Ad Hoc
        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("usp_GetTopCustomers");
        entry.Type.Should().Be("StoredProcedure");
        entry.DetectedFeatures.Should().Contain("TOP");
        entry.DetectedFeatures.Should().Contain("ISNULL");
    }

    [Fact]
    public void BuildInventory_EncryptedObject_ReportedWithLimitedInfo()
    {
        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "usp_SecretProc",
            ObjectType = "SQL_STORED_PROCEDURE",
            SourceText = null,
            IsEncrypted = true,
            InaccessibilityReason = "Object definition is encrypted"
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("usp_SecretProc");
        entry.Type.Should().Be("StoredProcedure");
        entry.MaxRiskScore.Should().Be(3); // Unknown risk for encrypted
        entry.DetectedFeatures.Should().Contain("ENCRYPTED_OBJECT");
        entry.ConversionCategories.Should().Contain("manual");
    }

    [Fact]
    public void BuildInventory_NamedObjectsSortedBeforeAdHoc()
    {
        var viewSource = "CREATE VIEW vw_Test AS SELECT 1 AS Col";
        var procSource = "CREATE PROCEDURE aaa_First AS SELECT 1";

        var inventory = InventoryWith(
            new ProgrammableObjectMetadata
            {
                SchemaName = "dbo",
                ObjectName = "vw_Test",
                ObjectType = "VIEW",
                SourceText = viewSource,
                IsEncrypted = false
            },
            new ProgrammableObjectMetadata
            {
                SchemaName = "dbo",
                ObjectName = "aaa_First",
                ObjectType = "SQL_STORED_PROCEDURE",
                SourceText = procSource,
                IsEncrypted = false
            });

        var statements = new[]
        {
            CreateStatement("SELECT 42", riskScore: 1)
        };

        var result = _builder.BuildInventory(statements, inventory);

        result.Should().HaveCount(3);
        // Named objects first (alphabetically), Ad Hoc last
        result[0].Name.Should().Be("aaa_First");
        result[1].Name.Should().Be("vw_Test");
        result[2].Name.Should().Be("Ad Hoc");
    }

    [Fact]
    public void BuildInventory_ProcWithHighRiskFeatures_MaxRiskReflected()
    {
        var procSource = @"
CREATE PROCEDURE usp_ComplexProc
AS
BEGIN
    SELECT TOP 10 * FROM Orders;

    MERGE dbo.Products AS target
    USING dbo.ProductStaging AS source
    ON target.SKU = source.SKU
    WHEN MATCHED THEN
        UPDATE SET ProductName = source.ProductName
    WHEN NOT MATCHED THEN
        INSERT (ProductName, SKU) VALUES (source.ProductName, source.SKU);
END";

        var inventory = InventoryWith(new ProgrammableObjectMetadata
        {
            SchemaName = "dbo",
            ObjectName = "usp_ComplexProc",
            ObjectType = "SQL_STORED_PROCEDURE",
            SourceText = procSource,
            IsEncrypted = false
        });

        var result = _builder.BuildInventory([], inventory);

        result.Should().ContainSingle();
        var entry = result[0];
        entry.Name.Should().Be("usp_ComplexProc");
        entry.MaxRiskScore.Should().Be(4); // MERGE is risk 4
        entry.DetectedFeatures.Should().Contain("TOP");
        entry.DetectedFeatures.Should().Contain("MERGE");
        entry.ConversionCategories.Should().Contain("manual"); // from MERGE (risk 4)
    }

    [Fact]
    public void BuildInventory_MultipleProcsAndViews_AllListed()
    {
        var proc1 = @"CREATE PROCEDURE usp_Proc1 AS SELECT GETDATE()";
        var proc2 = @"CREATE PROCEDURE usp_Proc2 AS SELECT TOP 5 * FROM Orders";
        var view1 = @"CREATE VIEW vw_Orders AS SELECT * FROM Orders WITH (NOLOCK)";

        var inventory = InventoryWith(
            new ProgrammableObjectMetadata
            {
                SchemaName = "dbo", ObjectName = "usp_Proc1",
                ObjectType = "SQL_STORED_PROCEDURE", SourceText = proc1, IsEncrypted = false
            },
            new ProgrammableObjectMetadata
            {
                SchemaName = "dbo", ObjectName = "usp_Proc2",
                ObjectType = "SQL_STORED_PROCEDURE", SourceText = proc2, IsEncrypted = false
            },
            new ProgrammableObjectMetadata
            {
                SchemaName = "dbo", ObjectName = "vw_Orders",
                ObjectType = "VIEW", SourceText = view1, IsEncrypted = false
            });

        var result = _builder.BuildInventory([], inventory);

        result.Should().HaveCount(3);
        result.Should().Contain(e => e.Name == "usp_Proc1" && e.Type == "StoredProcedure");
        result.Should().Contain(e => e.Name == "usp_Proc2" && e.Type == "StoredProcedure");
        result.Should().Contain(e => e.Name == "vw_Orders" && e.Type == "View");

        var viewEntry = result.Single(e => e.Name == "vw_Orders");
        viewEntry.MaxRiskScore.Should().Be(4); // NOLOCK is risk 4
        viewEntry.DetectedFeatures.Should().Contain("NOLOCK");
    }
}
