-- Migration Assessment Engine - Test Database Setup
-- Creates a database with varied SQL Server features to exercise all 5 risk levels.
-- Run against: Server=localhost;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True
--
-- Usage:
--   sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i setup-test-database.sql

USE master;
GO

-- Drop if exists for idempotent reruns
IF DB_ID('AssessmentTestDB') IS NOT NULL
BEGIN
    ALTER DATABASE AssessmentTestDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE AssessmentTestDB;
END
GO

CREATE DATABASE AssessmentTestDB;
GO

USE AssessmentTestDB;
GO

-- =============================================================================
-- Enable Query Store (captures executed queries for the assessment tool)
-- =============================================================================
ALTER DATABASE AssessmentTestDB SET QUERY_STORE = ON;
ALTER DATABASE AssessmentTestDB SET QUERY_STORE (
    OPERATION_MODE = READ_WRITE,
    CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 30),
    DATA_FLUSH_INTERVAL_SECONDS = 60,
    MAX_STORAGE_SIZE_MB = 100
);
GO

-- =============================================================================
-- SCHEMA: Risk Level 1 - Standard SQL (trivial to migrate)
-- =============================================================================

CREATE TABLE dbo.Categories (
    CategoryID INT IDENTITY(1,1) PRIMARY KEY,
    CategoryName NVARCHAR(100) NOT NULL,
    ParentCategoryID INT NULL
);
GO

ALTER TABLE dbo.Categories ADD CONSTRAINT FK_Categories_Parent
    FOREIGN KEY (ParentCategoryID) REFERENCES dbo.Categories(CategoryID);
GO

CREATE TABLE dbo.Customers (
    CustomerID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(255) NOT NULL UNIQUE,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
    IsActive BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.Products (
    ProductID INT IDENTITY(1,1) PRIMARY KEY,
    ProductName NVARCHAR(200) NOT NULL,
    SKU NVARCHAR(50) NOT NULL UNIQUE,
    Price DECIMAL(10, 2) NOT NULL,
    StockQuantity INT NOT NULL DEFAULT 0,
    CategoryID INT NULL REFERENCES dbo.Categories(CategoryID)
);
GO

CREATE TABLE dbo.Orders (
    OrderID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID INT NOT NULL REFERENCES dbo.Customers(CustomerID),
    OrderDate DATETIME2 NOT NULL DEFAULT GETDATE(),
    TotalAmount DECIMAL(12, 2) NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending'
);
GO

CREATE TABLE dbo.OrderItems (
    OrderItemID INT IDENTITY(1,1) PRIMARY KEY,
    OrderID INT NOT NULL REFERENCES dbo.Orders(OrderID),
    ProductID INT NOT NULL REFERENCES dbo.Products(ProductID),
    Quantity INT NOT NULL,
    UnitPrice DECIMAL(10, 2) NOT NULL
);
GO

-- Indexes
CREATE NONCLUSTERED INDEX IX_Orders_CustomerID ON dbo.Orders(CustomerID);
CREATE NONCLUSTERED INDEX IX_Orders_OrderDate ON dbo.Orders(OrderDate DESC)
    INCLUDE (CustomerID, TotalAmount);
CREATE NONCLUSTERED INDEX IX_Products_Category ON dbo.Products(CategoryID)
    WHERE CategoryID IS NOT NULL;
GO

-- =============================================================================
-- SCHEMA: Risk Level 2 - Simple translations (TOP, ISNULL, GETDATE, etc.)
-- =============================================================================

CREATE VIEW dbo.vw_RecentOrders AS
    SELECT TOP 100
        o.OrderID,
        c.FirstName + ' ' + c.LastName AS CustomerName,
        o.OrderDate,
        ISNULL(o.TotalAmount, 0) AS TotalAmount
    FROM dbo.Orders o
    INNER JOIN dbo.Customers c ON o.CustomerID = c.CustomerID
    ORDER BY o.OrderDate DESC;
GO

CREATE PROCEDURE dbo.sp_GetTopCustomers
    @TopN INT = 10
AS
BEGIN
    SELECT TOP (@TopN)
        c.CustomerID,
        c.FirstName + ' ' + c.LastName AS FullName,
        COUNT(o.OrderID) AS OrderCount,
        ISNULL(SUM(o.TotalAmount), 0) AS TotalSpent,
        DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) AS DaysSinceLastOrder
    FROM dbo.Customers c
    LEFT JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
    GROUP BY c.CustomerID, c.FirstName, c.LastName
    ORDER BY TotalSpent DESC;
END;
GO

CREATE FUNCTION dbo.fn_FormatCustomerName(@FirstName NVARCHAR(100), @LastName NVARCHAR(100))
RETURNS NVARCHAR(201)
AS
BEGIN
    RETURN ISNULL(@LastName, '') + ', ' + ISNULL(@FirstName, '');
END;
GO

-- =============================================================================
-- SCHEMA: Risk Level 3 - Procedural changes (TRY/CATCH, dynamic SQL, temp tables)
-- =============================================================================

CREATE PROCEDURE dbo.sp_ProcessOrder
    @CustomerID INT,
    @ProductID INT,
    @Quantity INT,
    @OrderID INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        BEGIN TRANSACTION;

        -- Check stock
        DECLARE @CurrentStock INT;
        SELECT @CurrentStock = StockQuantity FROM dbo.Products WHERE ProductID = @ProductID;

        IF @CurrentStock < @Quantity
            THROW 50001, 'Insufficient stock', 1;

        -- Create order
        DECLARE @Price DECIMAL(10,2);
        SELECT @Price = Price FROM dbo.Products WHERE ProductID = @ProductID;

        INSERT INTO dbo.Orders (CustomerID, TotalAmount, Status)
        VALUES (@CustomerID, @Price * @Quantity, 'Processing');

        SET @OrderID = SCOPE_IDENTITY();

        INSERT INTO dbo.OrderItems (OrderID, ProductID, Quantity, UnitPrice)
        VALUES (@OrderID, @ProductID, @Quantity, @Price);

        -- Update stock
        UPDATE dbo.Products
        SET StockQuantity = StockQuantity - @Quantity
        WHERE ProductID = @ProductID;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        THROW;
    END CATCH;
END;
GO

CREATE PROCEDURE dbo.sp_DynamicSearch
    @TableName NVARCHAR(128),
    @FilterColumn NVARCHAR(128),
    @FilterValue NVARCHAR(256)
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @SQL NVARCHAR(MAX);
    SET @SQL = N'SELECT * FROM ' + QUOTENAME(@TableName) +
               N' WHERE ' + QUOTENAME(@FilterColumn) + N' = @Value';

    EXEC sp_executesql @SQL, N'@Value NVARCHAR(256)', @Value = @FilterValue;
END;
GO

CREATE PROCEDURE dbo.sp_BuildMonthlyReport
    @Year INT,
    @Month INT
AS
BEGIN
    SET NOCOUNT ON;

    -- Temp table usage
    CREATE TABLE #MonthlyStats (
        CustomerID INT,
        OrderCount INT,
        TotalRevenue DECIMAL(12,2)
    );

    INSERT INTO #MonthlyStats
    SELECT
        o.CustomerID,
        COUNT(*),
        SUM(o.TotalAmount)
    FROM dbo.Orders o
    WHERE YEAR(o.OrderDate) = @Year AND MONTH(o.OrderDate) = @Month
    GROUP BY o.CustomerID;

    -- Return results joined with customer info
    SELECT
        c.FirstName + ' ' + c.LastName AS CustomerName,
        ms.OrderCount,
        ms.TotalRevenue
    FROM #MonthlyStats ms
    INNER JOIN dbo.Customers c ON ms.CustomerID = c.CustomerID
    ORDER BY ms.TotalRevenue DESC;

    DROP TABLE #MonthlyStats;
END;
GO

-- =============================================================================
-- SCHEMA: Risk Level 4 - Significant redesign (MERGE, locking hints, PIVOT)
-- =============================================================================

CREATE TABLE dbo.ProductImportStaging (
    SKU NVARCHAR(50) NOT NULL,
    ProductName NVARCHAR(200) NOT NULL,
    Price DECIMAL(10, 2) NOT NULL,
    StockQuantity INT NOT NULL
);
GO

CREATE PROCEDURE dbo.sp_UpsertProducts
AS
BEGIN
    SET NOCOUNT ON;

    MERGE dbo.Products AS target
    USING dbo.ProductImportStaging AS source
    ON target.SKU = source.SKU
    WHEN MATCHED THEN
        UPDATE SET
            ProductName = source.ProductName,
            Price = source.Price,
            StockQuantity = source.StockQuantity
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (ProductName, SKU, Price, StockQuantity)
        VALUES (source.ProductName, source.SKU, source.Price, source.StockQuantity)
    WHEN NOT MATCHED BY SOURCE THEN
        DELETE;
END;
GO

CREATE PROCEDURE dbo.sp_GetInventorySnapshot
AS
BEGIN
    SET NOCOUNT ON;

    -- Uses NOLOCK hint (dirty reads for performance)
    SELECT
        p.ProductID,
        p.ProductName,
        p.StockQuantity,
        p.Price
    FROM dbo.Products p WITH (NOLOCK)
    WHERE p.StockQuantity > 0
    ORDER BY p.StockQuantity ASC;
END;
GO

CREATE PROCEDURE dbo.sp_UpdateStockWithLock
    @ProductID INT,
    @NewQuantity INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE p
    SET p.StockQuantity = @NewQuantity
    FROM dbo.Products p WITH (UPDLOCK, ROWLOCK)
    WHERE p.ProductID = @ProductID;
END;
GO

CREATE VIEW dbo.vw_MonthlyCategoryRevenue AS
    SELECT CategoryName, [1] AS Jan, [2] AS Feb, [3] AS Mar, [4] AS Apr,
           [5] AS May, [6] AS Jun, [7] AS Jul, [8] AS Aug,
           [9] AS Sep, [10] AS Oct, [11] AS Nov, [12] AS Dec
    FROM (
        SELECT
            c.CategoryName,
            MONTH(o.OrderDate) AS OrderMonth,
            oi.Quantity * oi.UnitPrice AS Revenue
        FROM dbo.OrderItems oi
        INNER JOIN dbo.Orders o ON oi.OrderID = o.OrderID
        INNER JOIN dbo.Products p ON oi.ProductID = p.ProductID
        INNER JOIN dbo.Categories c ON p.CategoryID = c.CategoryID
    ) src
    PIVOT (
        SUM(Revenue) FOR OrderMonth IN ([1],[2],[3],[4],[5],[6],[7],[8],[9],[10],[11],[12])
    ) pvt;
GO

CREATE PROCEDURE dbo.sp_SharedTempReport
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE ##GlobalOrderSummary (
        OrderDate DATE,
        TotalOrders INT,
        TotalRevenue DECIMAL(12,2)
    );

    INSERT INTO ##GlobalOrderSummary
    SELECT
        CAST(OrderDate AS DATE),
        COUNT(*),
        SUM(TotalAmount)
    FROM dbo.Orders
    GROUP BY CAST(OrderDate AS DATE);

    SELECT * FROM ##GlobalOrderSummary ORDER BY OrderDate DESC;

    DROP TABLE ##GlobalOrderSummary;
END;
GO

-- =============================================================================
-- SCHEMA: Risk Level 5 - Architectural features (XML, linked server references)
-- =============================================================================

CREATE TABLE dbo.OrderMetadata (
    OrderID INT NOT NULL REFERENCES dbo.Orders(OrderID),
    MetadataXml XML NOT NULL,
    CONSTRAINT PK_OrderMetadata PRIMARY KEY (OrderID)
);
GO

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
GO

CREATE PRIMARY XML INDEX IX_OrderMetadata_Xml ON dbo.OrderMetadata(MetadataXml);
GO

CREATE PROCEDURE dbo.sp_GetExternalInventory
AS
BEGIN
    SET NOCOUNT ON;

    -- This references a linked server pattern that the feature detector will find
    -- In a real environment this would query a remote server
    DECLARE @SQL NVARCHAR(MAX) = N'
        SELECT * FROM OPENQUERY(RemoteWarehouse,
            ''SELECT ProductCode, AvailableStock FROM Inventory WHERE AvailableStock > 0'')';

    -- Just print for testing - actual execution would fail without the linked server
    PRINT @SQL;
END;
GO

-- Synonym (detected by metadata collector)
CREATE SYNONYM dbo.syn_ActiveCustomers FOR dbo.Customers;
GO

-- =============================================================================
-- SEED DATA - Populate tables so queries return results
-- =============================================================================

-- Categories
INSERT INTO dbo.Categories (CategoryName, ParentCategoryID) VALUES
('Electronics', NULL),
('Clothing', NULL),
('Books', NULL),
('Electronics > Phones', 1),
('Electronics > Laptops', 1),
('Clothing > Mens', 2),
('Clothing > Womens', 2);
GO

-- Products
INSERT INTO dbo.Products (ProductName, SKU, Price, StockQuantity, CategoryID) VALUES
('iPhone 15', 'PHONE-001', 999.99, 50, 4),
('Galaxy S24', 'PHONE-002', 899.99, 75, 4),
('MacBook Pro', 'LAPTOP-001', 2499.99, 25, 5),
('ThinkPad X1', 'LAPTOP-002', 1799.99, 30, 5),
('Blue Jeans', 'CLOTH-001', 49.99, 200, 6),
('Summer Dress', 'CLOTH-002', 79.99, 150, 7),
('SQL Server Internals', 'BOOK-001', 59.99, 100, 3),
('PostgreSQL Up & Running', 'BOOK-002', 44.99, 80, 3),
('Wireless Earbuds', 'AUDIO-001', 129.99, 300, 1),
('USB-C Cable', 'CABLE-001', 12.99, 1000, 1);
GO

-- Customers
INSERT INTO dbo.Customers (FirstName, LastName, Email) VALUES
('John', 'Smith', 'john.smith@example.com'),
('Jane', 'Doe', 'jane.doe@example.com'),
('Bob', 'Johnson', 'bob.j@example.com'),
('Alice', 'Williams', 'alice.w@example.com'),
('Charlie', 'Brown', 'charlie.b@example.com');
GO

-- Orders
INSERT INTO dbo.Orders (CustomerID, OrderDate, TotalAmount, Status) VALUES
(1, '2024-01-15', 999.99, 'Completed'),
(1, '2024-02-20', 129.99, 'Completed'),
(2, '2024-01-22', 2499.99, 'Completed'),
(2, '2024-03-10', 49.99, 'Shipped'),
(3, '2024-02-01', 1799.99, 'Completed'),
(3, '2024-03-15', 79.99, 'Processing'),
(4, '2024-01-05', 59.99, 'Completed'),
(4, '2024-02-28', 899.99, 'Completed'),
(5, '2024-03-01', 12.99, 'Pending'),
(5, '2024-03-20', 44.99, 'Pending');
GO

-- Order Items
INSERT INTO dbo.OrderItems (OrderID, ProductID, Quantity, UnitPrice) VALUES
(1, 1, 1, 999.99),
(2, 9, 1, 129.99),
(3, 3, 1, 2499.99),
(4, 5, 1, 49.99),
(5, 4, 1, 1799.99),
(6, 6, 1, 79.99),
(7, 7, 1, 59.99),
(8, 2, 1, 899.99),
(9, 10, 1, 12.99),
(10, 8, 1, 44.99);
GO

-- Order Metadata (XML)
INSERT INTO dbo.OrderMetadata (OrderID, MetadataXml) VALUES
(1, '<order><shipping><address>123 Main St, Seattle, WA</address><method>Express</method></shipping><items><item sku="PHONE-001" qty="1"/></items></order>'),
(3, '<order><shipping><address>456 Oak Ave, Portland, OR</address><method>Standard</method></shipping><items><item sku="LAPTOP-001" qty="1"/></items></order>'),
(5, '<order><shipping><address>789 Pine Rd, Denver, CO</address><method>Express</method></shipping><items><item sku="LAPTOP-002" qty="1"/></items></order>');
GO

-- Staging data for MERGE test
INSERT INTO dbo.ProductImportStaging (SKU, ProductName, Price, StockQuantity) VALUES
('PHONE-001', 'iPhone 15 Pro', 1099.99, 60),
('PHONE-003', 'Pixel 8', 699.99, 40),
('CABLE-001', 'USB-C Cable 2m', 14.99, 1200);
GO

-- =============================================================================
-- EXERCISE QUERIES - Run statements so Query Store captures them
-- =============================================================================

-- Risk 1: Standard SQL
SELECT * FROM dbo.Customers WHERE IsActive = 1;
SELECT c.FirstName, c.LastName, o.OrderDate, o.TotalAmount
FROM dbo.Customers c INNER JOIN dbo.Orders o ON c.CustomerID = o.CustomerID
WHERE o.Status = 'Completed';
INSERT INTO dbo.Customers (FirstName, LastName, Email) VALUES ('Test', 'User', 'test@example.com');
DELETE FROM dbo.Customers WHERE Email = 'test@example.com';
GO

-- Risk 2: Simple translations
SELECT TOP 5 * FROM dbo.Products ORDER BY Price DESC;
SELECT ProductName, ISNULL(CategoryID, 0) AS CatID FROM dbo.Products;
SELECT GETDATE() AS CurrentTime, DATEDIFF(DAY, '2024-01-01', GETDATE()) AS DaysThisYear;
SELECT CHARINDEX('Phone', ProductName) AS Pos FROM dbo.Products WHERE ProductName LIKE '%Phone%';
SELECT FirstName + ' ' + LastName AS FullName FROM dbo.Customers;
GO

-- Risk 3: Procedural (execute stored procs with TRY/CATCH, dynamic SQL)
DECLARE @NewOrderID INT;
EXEC dbo.sp_ProcessOrder @CustomerID = 1, @ProductID = 1, @Quantity = 1, @OrderID = @NewOrderID OUTPUT;
GO

EXEC dbo.sp_DynamicSearch @TableName = 'Products', @FilterColumn = 'SKU', @FilterValue = 'PHONE-001';
GO

EXEC dbo.sp_BuildMonthlyReport @Year = 2024, @Month = 3;
GO

EXEC dbo.sp_GetTopCustomers @TopN = 5;
GO

-- Risk 4: MERGE, locking hints
EXEC dbo.sp_UpsertProducts;
GO

EXEC dbo.sp_GetInventorySnapshot;
GO

EXEC dbo.sp_UpdateStockWithLock @ProductID = 1, @NewQuantity = 45;
GO

SELECT * FROM dbo.vw_MonthlyCategoryRevenue;
GO

EXEC dbo.sp_SharedTempReport;
GO

-- Risk 5: XML methods
EXEC dbo.sp_GetOrderShippingInfo @OrderID = 1;
GO

SELECT
    OrderID,
    MetadataXml.value('(/order/shipping/method)[1]', 'NVARCHAR(100)') AS Method
FROM dbo.OrderMetadata;
GO

EXEC dbo.sp_GetExternalInventory;
GO

-- =============================================================================
-- Force Query Store to flush captured data
-- =============================================================================
EXEC sp_query_store_flush_db;
GO

PRINT '=============================================';
PRINT 'Test database setup complete!';
PRINT 'Database: AssessmentTestDB';
PRINT 'Query Store: Enabled (READ_WRITE)';
PRINT '';
PRINT 'Features included:';
PRINT '  Risk 1: Standard CRUD, joins, basic queries';
PRINT '  Risk 2: TOP, ISNULL, GETDATE, DATEDIFF, string concat';
PRINT '  Risk 3: TRY/CATCH, dynamic SQL, temp tables, SCOPE_IDENTITY';
PRINT '  Risk 4: MERGE, NOLOCK/UPDLOCK/ROWLOCK, PIVOT, global temp tables';
PRINT '  Risk 5: XML columns/indexes/methods, OPENQUERY reference';
PRINT '';
PRINT 'Run the assessment with:';
PRINT '  dotnet run --project src/MigrationAssessment.Cli -- -c "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True"';
PRINT '=============================================';
GO
