-- Functional tests: CrossSchemaAdvancedDB - Multi-schema JOINs
-- Validates that JOINs across schemas and cross-schema procedures work

-- test: Join sales.Orders with inventory.Products
-- expect-rows: > 0
SELECT o.OrderID, c.CustomerName, p.ProductName, o.Quantity, o.OrderTotal
FROM sales.Orders o
INNER JOIN sales.Customers c ON o.CustomerID = c.CustomerID
INNER JOIN inventory.Products p ON o.ProductID = p.ProductID;

-- test: Join inventory stock movements with orders
-- expect-rows: > 0
SELECT sm.MovementID, p.ProductName, sm.QuantityChange, sm.MovementType
FROM inventory.StockMovements sm
INNER JOIN inventory.Products p ON sm.ProductID = p.ProductID;

-- test: Execute cross-schema place order procedure
-- expect-no-error
EXEC sales.sp_PlaceOrder @CustomerID = 2, @ProductID = 1, @Quantity = 2;

-- test: Execute restock procedure
-- expect-no-error
EXEC inventory.sp_RestockProduct @ProductID = 1, @Quantity = 50;

-- test: Execute customer order history procedure
-- expect-rows: > 0
EXEC sales.sp_CustomerOrderHistory @CustomerID = 1;
