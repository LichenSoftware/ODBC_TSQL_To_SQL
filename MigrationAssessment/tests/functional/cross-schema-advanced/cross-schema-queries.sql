-- Functional tests: CrossSchemaAdvancedDB - Cross-schema queries
-- Validates that multi-schema references and cross-schema JOINs work

-- test: Query sales customers
-- expect-rows: > 0
SELECT CustomerID, CustomerName, Email, Region FROM sales.Customers;

-- test: Query inventory products
-- expect-rows: > 0
SELECT ProductID, ProductName, SKU, UnitPrice, StockLevel FROM inventory.Products;

-- test: Query cross-schema view vw_OrderDetails
-- expect-rows: > 0
SELECT OrderID, CustomerName, Region, ProductName, Quantity, OrderTotal FROM sales.vw_OrderDetails;

-- test: Query inventory stock summary view
-- expect-rows: > 0
SELECT ProductID, ProductName, StockLevel, TotalMovements FROM inventory.vw_StockSummary;

-- test: Query high value orders view with cross-schema function
-- expect-rows: > 0
SELECT OrderID, CustomerName, OrderTotal, CurrentProductInventoryValue FROM sales.vw_HighValueOrders;

-- test: Execute cross-schema function
-- expect-rows: > 0
SELECT inventory.fn_GetProductValue(1) AS ProductValue;
