namespace MigrationValidation.Runner;

/// <summary>
/// Contains all validation tests organized by category.
/// Each test exercises a specific database object in AssessmentTestDB.
/// </summary>
public static class TestCatalog
{
    public static List<ValidationTest> GetTests(string category)
    {
        return category.ToLowerInvariant() switch
        {
            "tables" => GetTableTests(),
            "views" => GetViewTests(),
            "functions" => GetFunctionTests(),
            "storedprocedures" => GetStoredProcedureTests(),
            "synonyms" => GetSynonymTests(),
            _ => []
        };
    }

    // =========================================================================
    // TABLE TESTS - CRUD operations on all tables
    // =========================================================================
    private static List<ValidationTest> GetTableTests() =>
    [
        // --- dbo.Categories ---
        new()
        {
            Name = "Categories - SELECT all",
            Category = "Tables",
            Sql = "SELECT CategoryID, CategoryName, ParentCategoryID FROM dbo.Categories",
            MinExpectedRows = 7
        },
        new()
        {
            Name = "Categories - Self-join (parent hierarchy)",
            Category = "Tables",
            Sql = """
                SELECT c.CategoryName, p.CategoryName AS ParentName
                FROM dbo.Categories c
                LEFT JOIN dbo.Categories p ON c.ParentCategoryID = p.CategoryID
                """,
            MinExpectedRows = 7
        },
        new()
        {
            Name = "Categories - INSERT and DELETE",
            Category = "Tables",
            Sql = """
                INSERT INTO dbo.Categories (CategoryName, ParentCategoryID) VALUES ('TestCategory', NULL);
                DELETE FROM dbo.Categories WHERE CategoryName = 'TestCategory';
                """,
            ExpectsResults = false
        },

        // --- dbo.Customers ---
        new()
        {
            Name = "Customers - SELECT all active",
            Category = "Tables",
            Sql = "SELECT CustomerID, FirstName, LastName, Email, CreatedAt, IsActive FROM dbo.Customers WHERE IsActive = 1",
            MinExpectedRows = 5
        },
        new()
        {
            Name = "Customers - Filter by email pattern",
            Category = "Tables",
            Sql = "SELECT CustomerID, Email FROM dbo.Customers WHERE Email LIKE '%example.com'",
            MinExpectedRows = 1
        },
        new()
        {
            Name = "Customers - INSERT, UPDATE, DELETE lifecycle",
            Category = "Tables",
            Sql = """
                INSERT INTO dbo.Customers (FirstName, LastName, Email) VALUES ('Migration', 'Test', 'migration.test@validation.com');
                UPDATE dbo.Customers SET FirstName = 'MigrationUpdated' WHERE Email = 'migration.test@validation.com';
                DELETE FROM dbo.Customers WHERE Email = 'migration.test@validation.com';
                """,
            ExpectsResults = false
        },

        // --- dbo.Products ---
        new()
        {
            Name = "Products - SELECT all",
            Category = "Tables",
            Sql = "SELECT ProductID, ProductName, SKU, Price, StockQuantity, CategoryID FROM dbo.Products",
            MinExpectedRows = 10
        },
        new()
        {
            Name = "Products - Filtered index scan (CategoryID IS NOT NULL)",
            Category = "Tables",
            Sql = "SELECT ProductID, ProductName, CategoryID FROM dbo.Products WHERE CategoryID IS NOT NULL",
            MinExpectedRows = 1
        },
        new()
        {
            Name = "Products - Aggregation",
            Category = "Tables",
            Sql = """
                SELECT CategoryID, COUNT(*) AS ProductCount, AVG(Price) AS AvgPrice, SUM(StockQuantity) AS TotalStock
                FROM dbo.Products
                GROUP BY CategoryID
                """,
            MinExpectedRows = 1
        },

        // --- dbo.Orders ---
        new()
        {
            Name = "Orders - SELECT all",
            Category = "Tables",
            Sql = "SELECT OrderID, CustomerID, OrderDate, TotalAmount, Status FROM dbo.Orders",
            MinExpectedRows = 10
        },
        new()
        {
            Name = "Orders - JOIN with Customers",
            Category = "Tables",
            Sql = """
                SELECT o.OrderID, c.FirstName + ' ' + c.LastName AS CustomerName, o.OrderDate, o.TotalAmount
                FROM dbo.Orders o
                INNER JOIN dbo.Customers c ON o.CustomerID = c.CustomerID
                WHERE o.Status = 'Completed'
                """,
            MinExpectedRows = 1
        },
        new()
        {
            Name = "Orders - Date filtering with ORDER BY DESC",
            Category = "Tables",
            Sql = "SELECT OrderID, OrderDate, TotalAmount FROM dbo.Orders WHERE OrderDate >= '2024-01-01' ORDER BY OrderDate DESC",
            MinExpectedRows = 1
        },

        // --- dbo.OrderItems ---
        new()
        {
            Name = "OrderItems - SELECT all",
            Category = "Tables",
            Sql = "SELECT OrderItemID, OrderID, ProductID, Quantity, UnitPrice FROM dbo.OrderItems",
            MinExpectedRows = 10
        },
        new()
        {
            Name = "OrderItems - Multi-table JOIN",
            Category = "Tables",
            Sql = """
                SELECT oi.OrderItemID, p.ProductName, oi.Quantity, oi.UnitPrice, (oi.Quantity * oi.UnitPrice) AS LineTotal
                FROM dbo.OrderItems oi
                INNER JOIN dbo.Products p ON oi.ProductID = p.ProductID
                INNER JOIN dbo.Orders o ON oi.OrderID = o.OrderID
                """,
            MinExpectedRows = 10
        },

        // --- dbo.ProductImportStaging ---
        new()
        {
            Name = "ProductImportStaging - SELECT all",
            Category = "Tables",
            Sql = "SELECT SKU, ProductName, Price, StockQuantity FROM dbo.ProductImportStaging",
            MinExpectedRows = 3
        },

        // --- dbo.OrderMetadata ---
        new()
        {
            Name = "OrderMetadata - SELECT all",
            Category = "Tables",
            Sql = "SELECT OrderID, MetadataXml FROM dbo.OrderMetadata",
            MinExpectedRows = 3
        },
        new()
        {
            Name = "OrderMetadata - XML value extraction",
            Category = "Tables",
            Sql = """
                SELECT OrderID,
                       MetadataXml.value('(/order/shipping/address)[1]', 'NVARCHAR(500)') AS ShippingAddress,
                       MetadataXml.value('(/order/shipping/method)[1]', 'NVARCHAR(100)') AS ShippingMethod
                FROM dbo.OrderMetadata
                """,
            MinExpectedRows = 3
        }
    ];

    // =========================================================================
    // VIEW TESTS
    // =========================================================================
    private static List<ValidationTest> GetViewTests() =>
    [
        // --- dbo.vw_RecentOrders ---
        new()
        {
            Name = "vw_RecentOrders - SELECT (TOP + ISNULL + string concat)",
            Category = "Views",
            Sql = "SELECT OrderID, CustomerName, OrderDate, TotalAmount FROM dbo.vw_RecentOrders",
            MinExpectedRows = 1
        },

        // --- dbo.vw_MonthlyCategoryRevenue ---
        new()
        {
            Name = "vw_MonthlyCategoryRevenue - SELECT (PIVOT)",
            Category = "Views",
            Sql = "SELECT CategoryName, Jan, Feb, Mar, Apr, May, Jun, Jul, Aug, Sep, Oct, Nov, Dec FROM dbo.vw_MonthlyCategoryRevenue",
            MinExpectedRows = 1
        }
    ];

    // =========================================================================
    // FUNCTION TESTS
    // =========================================================================
    private static List<ValidationTest> GetFunctionTests() =>
    [
        // --- dbo.fn_FormatCustomerName ---
        new()
        {
            Name = "fn_FormatCustomerName - Scalar function call",
            Category = "Functions",
            Sql = "SELECT dbo.fn_FormatCustomerName('John', 'Smith') AS FormattedName",
            MinExpectedRows = 1
        },
        new()
        {
            Name = "fn_FormatCustomerName - Used in SELECT with table data",
            Category = "Functions",
            Sql = """
                SELECT CustomerID, dbo.fn_FormatCustomerName(FirstName, LastName) AS FormattedName
                FROM dbo.Customers
                """,
            MinExpectedRows = 5
        },
        new()
        {
            Name = "fn_FormatCustomerName - NULL handling",
            Category = "Functions",
            Sql = "SELECT dbo.fn_FormatCustomerName(NULL, 'Smith') AS FormattedName",
            MinExpectedRows = 1
        }
    ];

    // =========================================================================
    // STORED PROCEDURE TESTS
    // =========================================================================
    private static List<ValidationTest> GetStoredProcedureTests() =>
    [
        // --- dbo.sp_GetTopCustomers ---
        new()
        {
            Name = "sp_GetTopCustomers - Default params (TOP N, ISNULL, DATEDIFF, GETDATE)",
            Category = "StoredProcedures",
            Sql = "sp_GetTopCustomers",
            IsStoredProcedure = true,
            Parameters = [new() { Key = "@TopN", Value = 5 }],
            MinExpectedRows = 1
        },

        // --- dbo.sp_ProcessOrder - TRY/CATCH, transactions, SCOPE_IDENTITY ---
        new()
        {
            Name = "sp_ProcessOrder - Successful order (TRY/CATCH, transaction, SCOPE_IDENTITY)",
            Category = "StoredProcedures",
            Sql = "sp_ProcessOrder",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@CustomerID", Value = 1 },
                new() { Key = "@ProductID", Value = 10 },  // USB-C Cable, high stock
                new() { Key = "@Quantity", Value = 1 },
                new() { Key = "@OrderID", IsOutput = true, DbType = System.Data.DbType.Int32 }
            ],
            ExpectsResults = false
        },
        new()
        {
            Name = "sp_ProcessOrder - Insufficient stock (error handling)",
            Category = "StoredProcedures",
            Sql = """
                BEGIN TRY
                    DECLARE @OID INT;
                    EXEC dbo.sp_ProcessOrder @CustomerID = 1, @ProductID = 1, @Quantity = 99999, @OrderID = @OID OUTPUT;
                    SELECT 'Should have thrown' AS Result;
                END TRY
                BEGIN CATCH
                    SELECT ERROR_MESSAGE() AS ErrorMessage, ERROR_NUMBER() AS ErrorNumber;
                END CATCH
                """,
            MinExpectedRows = 1
        },

        // --- dbo.sp_DynamicSearch - Dynamic SQL with sp_executesql ---
        new()
        {
            Name = "sp_DynamicSearch - Products by SKU (dynamic SQL)",
            Category = "StoredProcedures",
            Sql = "sp_DynamicSearch",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@TableName", Value = "Products" },
                new() { Key = "@FilterColumn", Value = "SKU" },
                new() { Key = "@FilterValue", Value = "PHONE-001" }
            ],
            MinExpectedRows = 1
        },
        new()
        {
            Name = "sp_DynamicSearch - Customers by Email (dynamic SQL)",
            Category = "StoredProcedures",
            Sql = "sp_DynamicSearch",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@TableName", Value = "Customers" },
                new() { Key = "@FilterColumn", Value = "Email" },
                new() { Key = "@FilterValue", Value = "john.smith@example.com" }
            ],
            MinExpectedRows = 1
        },

        // --- dbo.sp_BuildMonthlyReport - Temp tables ---
        new()
        {
            Name = "sp_BuildMonthlyReport - Jan 2024 (temp tables)",
            Category = "StoredProcedures",
            Sql = "sp_BuildMonthlyReport",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@Year", Value = 2024 },
                new() { Key = "@Month", Value = 1 }
            ],
            MinExpectedRows = 1
        },
        new()
        {
            Name = "sp_BuildMonthlyReport - Mar 2024 (temp tables)",
            Category = "StoredProcedures",
            Sql = "sp_BuildMonthlyReport",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@Year", Value = 2024 },
                new() { Key = "@Month", Value = 3 }
            ],
            MinExpectedRows = 1
        },

        // --- dbo.sp_UpsertProducts - MERGE statement ---
        // We seed staging with all existing SKUs that have order references to avoid FK violations
        // from the DELETE leg of the MERGE, then clean up after.
        new()
        {
            Name = "sp_UpsertProducts - MERGE upsert/delete (Risk Level 4)",
            Category = "StoredProcedures",
            Sql = """
                -- Ensure staging has all products that are referenced by OrderItems so DELETE leg won't violate FK
                DELETE FROM dbo.ProductImportStaging;
                INSERT INTO dbo.ProductImportStaging (SKU, ProductName, Price, StockQuantity)
                SELECT p.SKU, p.ProductName, p.Price, p.StockQuantity
                FROM dbo.Products p;

                -- Now update one row to test the MATCHED leg
                UPDATE dbo.ProductImportStaging SET Price = Price + 1.00 WHERE SKU = 'PHONE-001';

                -- Add a new row to test the NOT MATCHED BY TARGET leg
                INSERT INTO dbo.ProductImportStaging (SKU, ProductName, Price, StockQuantity)
                VALUES ('MERGE-TEST-001', 'Merge Test Product', 19.99, 10);

                -- Execute the MERGE
                EXEC dbo.sp_UpsertProducts;

                -- Verify the new product was inserted
                SELECT ProductID, ProductName, SKU, Price FROM dbo.Products WHERE SKU = 'MERGE-TEST-001';
                """,
            MinExpectedRows = 1
        },

        // --- dbo.sp_GetInventorySnapshot - NOLOCK hint ---
        new()
        {
            Name = "sp_GetInventorySnapshot - WITH (NOLOCK) query hint",
            Category = "StoredProcedures",
            Sql = "sp_GetInventorySnapshot",
            IsStoredProcedure = true,
            MinExpectedRows = 1
        },

        // --- dbo.sp_UpdateStockWithLock - UPDLOCK, ROWLOCK hints ---
        new()
        {
            Name = "sp_UpdateStockWithLock - WITH (UPDLOCK, ROWLOCK)",
            Category = "StoredProcedures",
            Sql = "sp_UpdateStockWithLock",
            IsStoredProcedure = true,
            Parameters =
            [
                new() { Key = "@ProductID", Value = 10 },
                new() { Key = "@NewQuantity", Value = 999 }
            ],
            ExpectsResults = false
        },

        // --- dbo.sp_SharedTempReport - Global temp tables ---
        new()
        {
            Name = "sp_SharedTempReport - Global temp table (##)",
            Category = "StoredProcedures",
            Sql = "sp_SharedTempReport",
            IsStoredProcedure = true,
            MinExpectedRows = 1
        },

        // --- dbo.sp_GetOrderShippingInfo - XML methods ---
        // The proc must have been created with QUOTED_IDENTIFIER ON for XML index queries.
        // We recreate it with the correct setting using sp_executesql which preserves session SET options.
        new()
        {
            Name = "sp_GetOrderShippingInfo - XML .value() and .query() (Risk Level 5)",
            Category = "StoredProcedures",
            Sql = """
                SET QUOTED_IDENTIFIER ON;
                SET ANSI_NULLS ON;

                IF OBJECT_ID('dbo.sp_GetOrderShippingInfo', 'P') IS NOT NULL
                    DROP PROCEDURE dbo.sp_GetOrderShippingInfo;
                """,
            ExpectsResults = false
        },
        new()
        {
            Name = "sp_GetOrderShippingInfo - Recreate with QUOTED_IDENTIFIER ON",
            Category = "StoredProcedures",
            Sql = """
                CREATE PROCEDURE dbo.sp_GetOrderShippingInfo
                    @OrderID INT
                AS
                BEGIN
                    SET NOCOUNT ON;
                    SELECT
                        o.OrderID,
                        m.MetadataXml.value('(/order/shipping/address)[1]', 'NVARCHAR(500)') AS ShippingAddress,
                        m.MetadataXml.value('(/order/shipping/method)[1]', 'NVARCHAR(100)') AS ShippingMethod,
                        m.MetadataXml.query('/order/items') AS ItemsXml
                    FROM dbo.Orders o
                    INNER JOIN dbo.OrderMetadata m ON o.OrderID = m.OrderID
                    WHERE o.OrderID = @OrderID;
                END;
                """,
            ExpectsResults = false
        },
        new()
        {
            Name = "sp_GetOrderShippingInfo - Execute XML .value() and .query() (Risk Level 5)",
            Category = "StoredProcedures",
            Sql = "EXEC dbo.sp_GetOrderShippingInfo @OrderID = 1;",
            MinExpectedRows = 1
        },

        // --- dbo.sp_GetExternalInventory - OPENQUERY / linked server ---
        new()
        {
            Name = "sp_GetExternalInventory - Linked server OPENQUERY reference (Risk Level 5)",
            Category = "StoredProcedures",
            Sql = "sp_GetExternalInventory",
            IsStoredProcedure = true,
            ExpectsResults = false  // Only PRINTs, doesn't actually execute the query
        }
    ];

    // =========================================================================
    // SYNONYM TESTS
    // =========================================================================
    private static List<ValidationTest> GetSynonymTests() =>
    [
        // --- dbo.syn_ActiveCustomers ---
        new()
        {
            Name = "syn_ActiveCustomers - SELECT through synonym",
            Category = "Synonyms",
            Sql = "SELECT CustomerID, FirstName, LastName, Email FROM dbo.syn_ActiveCustomers",
            MinExpectedRows = 5
        },
        new()
        {
            Name = "syn_ActiveCustomers - Filtered query through synonym",
            Category = "Synonyms",
            Sql = "SELECT CustomerID, Email FROM dbo.syn_ActiveCustomers WHERE IsActive = 1",
            MinExpectedRows = 1
        }
    ];
}
