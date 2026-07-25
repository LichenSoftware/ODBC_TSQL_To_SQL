-- Functional tests: TypesAndCLRDB - Queries exercising various data types
-- Validates that user-defined types, computed columns, and schema-bound objects work

-- test: Query employees with alias type columns
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, Email, Phone, Salary, HireDate FROM dbo.Employees;

-- test: Query product inventory with computed column
-- expect-rows: > 0
SELECT InventoryID, ProductName, QuantityOnHand, UnitCost, TotalValue FROM dbo.ProductInventory;

-- test: Query orders table
-- expect-rows: > 0
SELECT OrderID, CustomerName, OrderDate, TotalAmount FROM dbo.Orders;

-- test: Query order lines table
-- expect-rows: > 0
SELECT LineID, OrderID, ProductID, Quantity, UnitPrice FROM dbo.OrderLines;

-- test: Verify computed column calculates correctly
-- expect-rows: > 0
SELECT ProductName, QuantityOnHand, UnitCost, TotalValue
FROM dbo.ProductInventory
WHERE TotalValue > 0;
