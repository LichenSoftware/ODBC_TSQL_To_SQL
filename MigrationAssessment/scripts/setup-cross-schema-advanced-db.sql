-- Migration Assessment Engine - CrossSchemaAdvancedDB Setup
-- Creates a database focused on cross-schema references and advanced SQL Server features.
-- Exercises: multi-schema dependencies, cross-database references, partitioned tables,
-- row-level security, and temporal tables with system versioning.
--
-- Usage:
--   sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i setup-cross-schema-advanced-db.sql

USE master;
GO

-- Drop if exists for idempotent reruns
IF DB_ID('CrossSchemaAdvancedDB') IS NOT NULL
BEGIN
    ALTER DATABASE CrossSchemaAdvancedDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE CrossSchemaAdvancedDB;
END
GO

CREATE DATABASE CrossSchemaAdvancedDB;
GO

USE CrossSchemaAdvancedDB;
GO

-- =============================================================================
-- SCHEMAS: Create non-dbo schemas for multi-schema dependencies
-- =============================================================================

CREATE SCHEMA sales;
GO

CREATE SCHEMA inventory;
GO

CREATE SCHEMA security;
GO

-- =============================================================================
-- TABLES: Base tables across multiple schemas
-- =============================================================================

-- 1. sales.Customers
CREATE TABLE sales.Customers (
    CustomerID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerName NVARCHAR(200) NOT NULL,
    Email NVARCHAR(255) NOT NULL,
    Region NVARCHAR(50) NOT NULL DEFAULT 'US',
    CreatedAt DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- 2. inventory.Products
CREATE TABLE inventory.Products (
    ProductID INT IDENTITY(1,1) PRIMARY KEY,
    ProductName NVARCHAR(200) NOT NULL,
    SKU NVARCHAR(50) NOT NULL UNIQUE,
    UnitPrice DECIMAL(10,2) NOT NULL,
    StockLevel INT NOT NULL DEFAULT 0,
    CategoryCode NVARCHAR(20) NOT NULL DEFAULT 'GENERAL'
);
GO

-- 3. sales.Orders - references inventory.Products (cross-schema FK)
CREATE TABLE sales.Orders (
    OrderID INT IDENTITY(1,1) PRIMARY KEY,
    CustomerID INT NOT NULL REFERENCES sales.Customers(CustomerID),
    ProductID INT NOT NULL REFERENCES inventory.Products(ProductID),
    Quantity INT NOT NULL,
    OrderTotal DECIMAL(12,2) NOT NULL,
    OrderDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending'
);
GO

-- 4. inventory.StockMovements - references sales.Orders (cross-schema FK back)
CREATE TABLE inventory.StockMovements (
    MovementID INT IDENTITY(1,1) PRIMARY KEY,
    ProductID INT NOT NULL REFERENCES inventory.Products(ProductID),
    OrderID INT NULL REFERENCES sales.Orders(OrderID),
    QuantityChange INT NOT NULL,
    MovementType NVARCHAR(20) NOT NULL, -- 'Sale', 'Restock', 'Adjustment'
    MovementDate DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
);
GO

-- =============================================================================
-- PARTITIONED TABLE: Orders partitioned by date range
-- =============================================================================

-- 5. Partition function - quarterly boundaries
CREATE PARTITION FUNCTION pf_OrderDateRange (DATETIME2)
AS RANGE RIGHT FOR VALUES (
    '2024-01-01', '2024-04-01', '2024-07-01', '2024-10-01'
);
GO

-- 6. Partition scheme - all to PRIMARY (single filegroup for simplicity)
CREATE PARTITION SCHEME ps_OrderDateRange
AS PARTITION pf_OrderDateRange
ALL TO ([PRIMARY]);
GO

-- 7. sales.OrdersPartitioned - partitioned table
CREATE TABLE sales.OrdersPartitioned (
    OrderID INT IDENTITY(1,1),
    CustomerID INT NOT NULL,
    ProductID INT NOT NULL,
    Quantity INT NOT NULL,
    OrderTotal DECIMAL(12,2) NOT NULL,
    OrderDate DATETIME2 NOT NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    CONSTRAINT PK_OrdersPartitioned PRIMARY KEY (OrderID, OrderDate)
) ON ps_OrderDateRange(OrderDate);
GO

-- =============================================================================
-- TEMPORAL TABLE: Employee records with system versioning
-- =============================================================================

-- 8. dbo.Employees - temporal table with system versioning
CREATE TABLE dbo.Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeName NVARCHAR(200) NOT NULL,
    Department NVARCHAR(100) NOT NULL,
    Salary DECIMAL(10,2) NOT NULL,
    SysStartTime DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
    SysEndTime DATETIME2 GENERATED ALWAYS AS ROW END NOT NULL,
    PERIOD FOR SYSTEM_TIME (SysStartTime, SysEndTime)
) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.EmployeesHistory));
GO

-- =============================================================================
-- ROW-LEVEL SECURITY: Restrict sales data by region
-- =============================================================================

-- 9. security.UserRegionMapping - maps users to allowed regions
CREATE TABLE security.UserRegionMapping (
    UserName NVARCHAR(128) NOT NULL,
    Region NVARCHAR(50) NOT NULL,
    PRIMARY KEY (UserName, Region)
);
GO

-- 10. security.fn_RegionPredicate - security predicate function
CREATE FUNCTION security.fn_RegionPredicate(@Region NVARCHAR(50))
RETURNS TABLE
WITH SCHEMABINDING
AS
RETURN (
    SELECT 1 AS fn_result
    WHERE @Region IN (
        SELECT Region FROM security.UserRegionMapping
        WHERE UserName = USER_NAME()
    )
    OR USER_NAME() = 'dbo'
    OR IS_MEMBER('db_owner') = 1
);
GO

-- 11. Security policy on sales.Customers
CREATE SECURITY POLICY security.RegionFilter
ADD FILTER PREDICATE security.fn_RegionPredicate(Region) ON sales.Customers
WITH (STATE = ON, SCHEMABINDING = ON);
GO

-- =============================================================================
-- CROSS-SCHEMA VIEWS & PROCEDURES: Objects referencing multiple schemas
-- =============================================================================

-- 12. sales.vw_OrderDetails - joins sales and inventory schemas
CREATE VIEW sales.vw_OrderDetails
AS
    SELECT
        o.OrderID,
        c.CustomerName,
        c.Region,
        p.ProductName,
        p.SKU,
        o.Quantity,
        o.OrderTotal,
        o.OrderDate,
        o.Status
    FROM sales.Orders o
    INNER JOIN sales.Customers c ON o.CustomerID = c.CustomerID
    INNER JOIN inventory.Products p ON o.ProductID = p.ProductID;
GO

-- 13. inventory.vw_StockSummary - references inventory and sales schemas
CREATE VIEW inventory.vw_StockSummary
AS
    SELECT
        p.ProductID,
        p.ProductName,
        p.StockLevel,
        ISNULL(SUM(sm.QuantityChange), 0) AS TotalMovements,
        COUNT(DISTINCT sm.OrderID) AS RelatedOrderCount
    FROM inventory.Products p
    LEFT JOIN inventory.StockMovements sm ON p.ProductID = sm.ProductID
    GROUP BY p.ProductID, p.ProductName, p.StockLevel;
GO

-- 14. sales.sp_PlaceOrder - cross-schema procedure (sales writes to inventory)
CREATE PROCEDURE sales.sp_PlaceOrder
    @CustomerID INT,
    @ProductID INT,
    @Quantity INT
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @UnitPrice DECIMAL(10,2);
    DECLARE @Stock INT;

    -- Read from inventory schema
    SELECT @UnitPrice = UnitPrice, @Stock = StockLevel
    FROM inventory.Products
    WHERE ProductID = @ProductID;

    IF @Stock < @Quantity
    BEGIN
        RAISERROR('Insufficient stock for ProductID %d', 16, 1, @ProductID);
        RETURN;
    END

    DECLARE @Total DECIMAL(12,2) = @UnitPrice * @Quantity;

    BEGIN TRANSACTION;

    -- Insert into sales schema
    INSERT INTO sales.Orders (CustomerID, ProductID, Quantity, OrderTotal, Status)
    VALUES (@CustomerID, @ProductID, @Quantity, @Total, 'Confirmed');

    DECLARE @OrderID INT = SCOPE_IDENTITY();

    -- Write to inventory schema (cross-schema dependency)
    UPDATE inventory.Products
    SET StockLevel = StockLevel - @Quantity
    WHERE ProductID = @ProductID;

    INSERT INTO inventory.StockMovements (ProductID, OrderID, QuantityChange, MovementType)
    VALUES (@ProductID, @OrderID, -@Quantity, 'Sale');

    COMMIT TRANSACTION;

    SELECT @OrderID AS NewOrderID, @Total AS OrderTotal;
END;
GO

-- 15. inventory.sp_RestockProduct - inventory procedure referencing its own schema
CREATE PROCEDURE inventory.sp_RestockProduct
    @ProductID INT,
    @Quantity INT
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE inventory.Products
    SET StockLevel = StockLevel + @Quantity
    WHERE ProductID = @ProductID;

    INSERT INTO inventory.StockMovements (ProductID, OrderID, QuantityChange, MovementType)
    VALUES (@ProductID, NULL, @Quantity, 'Restock');

    SELECT p.ProductID, p.ProductName, p.StockLevel AS NewStockLevel
    FROM inventory.Products p
    WHERE p.ProductID = @ProductID;
END;
GO

-- 16. sales.sp_CustomerOrderHistory - cross-schema with aggregation
CREATE PROCEDURE sales.sp_CustomerOrderHistory
    @CustomerID INT
AS
BEGIN
    SET NOCOUNT ON;

    SELECT
        c.CustomerName,
        c.Region,
        p.ProductName,
        o.Quantity,
        o.OrderTotal,
        o.OrderDate,
        o.Status
    FROM sales.Orders o
    INNER JOIN sales.Customers c ON o.CustomerID = c.CustomerID
    INNER JOIN inventory.Products p ON o.ProductID = p.ProductID
    WHERE o.CustomerID = @CustomerID
    ORDER BY o.OrderDate DESC;
END;
GO

-- 17. Cross-database reference procedure (wrapped to avoid runtime error)
CREATE PROCEDURE sales.sp_GetExternalCustomerData
AS
BEGIN
    SET NOCOUNT ON;

    -- Cross-database reference pattern using three-part naming
    -- In production this would reference another database on the same server
    DECLARE @SQL NVARCHAR(MAX);
    SET @SQL = N'
        SELECT CustomerID, CustomerName, Email
        FROM ExternalCRM.dbo.Customers
        WHERE IsActive = 1';

    -- Print the cross-database query for detection purposes
    -- Actual execution would fail without ExternalCRM database
    PRINT 'Cross-database reference detected:';
    PRINT @SQL;

    -- Return local data as fallback so the procedure produces results
    SELECT CustomerID, CustomerName, Email
    FROM sales.Customers;
END;
GO

-- 18. inventory.fn_GetProductValue - scalar function used cross-schema
CREATE FUNCTION inventory.fn_GetProductValue(@ProductID INT)
RETURNS DECIMAL(12,2)
AS
BEGIN
    DECLARE @Value DECIMAL(12,2);
    SELECT @Value = UnitPrice * StockLevel
    FROM inventory.Products
    WHERE ProductID = @ProductID;
    RETURN ISNULL(@Value, 0);
END;
GO

-- 19. sales.vw_HighValueOrders - view using cross-schema function
CREATE VIEW sales.vw_HighValueOrders
AS
    SELECT
        o.OrderID,
        c.CustomerName,
        o.OrderTotal,
        o.OrderDate,
        inventory.fn_GetProductValue(o.ProductID) AS CurrentProductInventoryValue
    FROM sales.Orders o
    INNER JOIN sales.Customers c ON o.CustomerID = c.CustomerID
    WHERE o.OrderTotal > 100;
GO

-- =============================================================================
-- SEED DATA
-- =============================================================================

-- Security user-region mappings (allow dbo full access)
INSERT INTO security.UserRegionMapping (UserName, Region) VALUES
('dbo', 'US'),
('dbo', 'EU'),
('dbo', 'APAC');
GO

-- Customers across regions
INSERT INTO sales.Customers (CustomerName, Email, Region) VALUES
('Contoso Ltd', 'orders@contoso.com', 'US'),
('Fabrikam Inc', 'sales@fabrikam.com', 'US'),
('Northwind Traders', 'info@northwind.com', 'EU'),
('Adventure Works', 'purchasing@adventureworks.com', 'EU'),
('Tailspin Toys', 'orders@tailspin.com', 'APAC');
GO

-- Products
INSERT INTO inventory.Products (ProductName, SKU, UnitPrice, StockLevel, CategoryCode) VALUES
('Widget Alpha', 'WGT-001', 25.99, 500, 'WIDGETS'),
('Widget Beta', 'WGT-002', 34.99, 300, 'WIDGETS'),
('Gadget Pro', 'GDG-001', 149.99, 100, 'GADGETS'),
('Gadget Lite', 'GDG-002', 79.99, 250, 'GADGETS'),
('Component X', 'CMP-001', 9.99, 1000, 'COMPONENTS'),
('Component Y', 'CMP-002', 14.99, 800, 'COMPONENTS');
GO

-- Orders (cross-schema references)
INSERT INTO sales.Orders (CustomerID, ProductID, Quantity, OrderTotal, OrderDate, Status) VALUES
(1, 1, 10, 259.90, '2024-01-15', 'Completed'),
(1, 3, 2, 299.98, '2024-02-20', 'Completed'),
(2, 2, 5, 174.95, '2024-03-10', 'Shipped'),
(3, 4, 3, 239.97, '2024-04-05', 'Completed'),
(4, 5, 50, 499.50, '2024-05-12', 'Completed'),
(5, 6, 20, 299.80, '2024-07-01', 'Processing'),
(2, 1, 8, 207.92, '2024-08-15', 'Pending'),
(3, 3, 1, 149.99, '2024-09-22', 'Completed');
GO

-- Stock movements
INSERT INTO inventory.StockMovements (ProductID, OrderID, QuantityChange, MovementType, MovementDate) VALUES
(1, 1, -10, 'Sale', '2024-01-15'),
(3, 2, -2, 'Sale', '2024-02-20'),
(2, 3, -5, 'Sale', '2024-03-10'),
(4, 4, -3, 'Sale', '2024-04-05'),
(5, 5, -50, 'Sale', '2024-05-12'),
(6, 6, -20, 'Sale', '2024-07-01'),
(1, NULL, 100, 'Restock', '2024-06-01'),
(3, NULL, 50, 'Restock', '2024-06-15');
GO

-- Partitioned orders (seed into different partitions)
INSERT INTO sales.OrdersPartitioned (CustomerID, ProductID, Quantity, OrderTotal, OrderDate, Status) VALUES
(1, 1, 10, 259.90, '2023-11-01', 'Completed'),
(2, 2, 5, 174.95, '2024-01-15', 'Completed'),
(3, 3, 2, 299.98, '2024-04-20', 'Completed'),
(4, 4, 3, 239.97, '2024-07-10', 'Shipped'),
(5, 5, 50, 499.50, '2024-10-05', 'Pending');
GO

-- Employees (temporal table seed)
INSERT INTO dbo.Employees (EmployeeName, Department, Salary) VALUES
('Alice Johnson', 'Engineering', 95000.00),
('Bob Smith', 'Sales', 72000.00),
('Carol Williams', 'Engineering', 105000.00),
('Dave Brown', 'Marketing', 68000.00);
GO

-- Update employees to generate history records
UPDATE dbo.Employees SET Salary = 100000.00 WHERE EmployeeID = 1;
UPDATE dbo.Employees SET Department = 'Management', Salary = 85000.00 WHERE EmployeeID = 2;
GO

-- =============================================================================
-- EXERCISE QUERIES - Verify all views and procedures return non-empty results
-- =============================================================================

-- Cross-schema view
SELECT * FROM sales.vw_OrderDetails;
GO

-- Inventory summary view
SELECT * FROM inventory.vw_StockSummary;
GO

-- High-value orders view
SELECT * FROM sales.vw_HighValueOrders;
GO

-- Place order procedure (cross-schema write)
EXEC sales.sp_PlaceOrder @CustomerID = 1, @ProductID = 2, @Quantity = 3;
GO

-- Restock procedure
EXEC inventory.sp_RestockProduct @ProductID = 1, @Quantity = 200;
GO

-- Customer order history
EXEC sales.sp_CustomerOrderHistory @CustomerID = 1;
GO

-- Cross-database reference (prints SQL, returns local fallback)
EXEC sales.sp_GetExternalCustomerData;
GO

-- Partitioned table query
SELECT * FROM sales.OrdersPartitioned WHERE OrderDate >= '2024-01-01' AND OrderDate < '2024-04-01';
GO

-- Temporal table - current data
SELECT * FROM dbo.Employees;
GO

-- Temporal table - historical data
SELECT * FROM dbo.Employees FOR SYSTEM_TIME ALL ORDER BY EmployeeID, SysStartTime;
GO

-- Cross-schema function
SELECT inventory.fn_GetProductValue(1) AS ProductValue;
GO

-- Row-level security is transparent - query goes through the predicate
SELECT * FROM sales.Customers;
GO

-- =============================================================================
-- SUMMARY
-- =============================================================================

PRINT '=============================================';
PRINT 'Test database setup complete!';
PRINT 'Database: CrossSchemaAdvancedDB';
PRINT '';
PRINT 'Features included:';
PRINT '  - Multi-schema dependencies (sales, inventory, security)';
PRINT '  - Cross-schema foreign keys and procedures';
PRINT '  - Cross-database reference (three-part name pattern)';
PRINT '  - Partitioned table with partition function and scheme';
PRINT '  - Row-level security with predicate function and policy';
PRINT '  - Temporal table with system versioning';
PRINT '';
PRINT 'Schema objects (19 total):';
PRINT '  Tables: 7 (sales.Customers, inventory.Products, sales.Orders,';
PRINT '           inventory.StockMovements, sales.OrdersPartitioned,';
PRINT '           dbo.Employees, security.UserRegionMapping)';
PRINT '  Views: 3 (sales.vw_OrderDetails, inventory.vw_StockSummary,';
PRINT '            sales.vw_HighValueOrders)';
PRINT '  Procedures: 5 (sales.sp_PlaceOrder, inventory.sp_RestockProduct,';
PRINT '                  sales.sp_CustomerOrderHistory,';
PRINT '                  sales.sp_GetExternalCustomerData)';
PRINT '  Functions: 2 (security.fn_RegionPredicate, inventory.fn_GetProductValue)';
PRINT '  Security Policy: 1 (security.RegionFilter)';
PRINT '  Partition Function: 1 (pf_OrderDateRange)';
PRINT '  Partition Scheme: 1 (ps_OrderDateRange)';
PRINT '=============================================';
GO
