-- Functional tests: AssessmentTestDB - Basic SELECT queries
-- Validates that basic table reads and joins work after migration

-- test: Select all categories
-- expect-rows: > 0
SELECT CategoryID, CategoryName FROM dbo.Categories;

-- test: Select all products
-- expect-rows: > 0
SELECT ProductID, ProductName, SKU, Price FROM dbo.Products;

-- test: Select customers with active flag
-- expect-rows: > 0
SELECT CustomerID, FirstName, LastName, Email FROM dbo.Customers WHERE IsActive = 1;

-- test: Inner join orders with customers
-- expect-rows: > 0
SELECT c.FirstName, c.LastName, o.OrderID, o.TotalAmount, o.Status
FROM dbo.Customers c
INNER JOIN dbo.Orders o ON c.CustomerID = o.CustomerID;

-- test: Join order items with products
-- expect-rows: > 0
SELECT oi.OrderItemID, p.ProductName, oi.Quantity, oi.UnitPrice
FROM dbo.OrderItems oi
INNER JOIN dbo.Products p ON oi.ProductID = p.ProductID;

-- test: Count total orders
-- expect-rows: > 0
SELECT COUNT(*) AS TotalOrders FROM dbo.Orders;
