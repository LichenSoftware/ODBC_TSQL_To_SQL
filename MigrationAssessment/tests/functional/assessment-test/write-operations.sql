-- Functional tests: AssessmentTestDB - INSERT/UPDATE operations
-- Validates that DML write operations work correctly after migration

-- test: Insert a new category
-- expect-no-error
INSERT INTO dbo.Categories (CategoryName, ParentCategoryID) VALUES ('Test Category', NULL);

-- test: Insert a new customer
-- expect-no-error
INSERT INTO dbo.Customers (FirstName, LastName, Email) VALUES ('Func', 'TestUser', 'func.test@example.com');

-- test: Update product stock quantity
-- expect-no-error
UPDATE dbo.Products SET StockQuantity = StockQuantity + 1 WHERE SKU = 'PHONE-001';

-- test: Update order status
-- expect-no-error
UPDATE dbo.Orders SET Status = 'Completed' WHERE Status = 'Pending';

-- test: Verify inserted customer exists
-- expect-rows: > 0
SELECT CustomerID, FirstName, LastName FROM dbo.Customers WHERE Email = 'func.test@example.com';

-- test: Delete test customer
-- expect-no-error
DELETE FROM dbo.Customers WHERE Email = 'func.test@example.com';
