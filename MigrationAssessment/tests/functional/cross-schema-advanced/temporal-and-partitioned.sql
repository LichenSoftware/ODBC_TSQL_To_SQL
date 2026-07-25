-- Functional tests: CrossSchemaAdvancedDB - Temporal tables and partitioned tables
-- Validates that advanced SQL Server features translate correctly

-- test: Query temporal table current data
-- expect-rows: > 0
SELECT EmployeeID, EmployeeName, Department, Salary FROM dbo.Employees;

-- test: Query temporal table history
-- expect-no-error
SELECT EmployeeID, EmployeeName, Department, Salary, SysStartTime, SysEndTime
FROM dbo.Employees FOR SYSTEM_TIME ALL
ORDER BY EmployeeID, SysStartTime;

-- test: Query partitioned orders table
-- expect-rows: > 0
SELECT OrderID, CustomerID, ProductID, Quantity, OrderTotal, OrderDate, Status
FROM sales.OrdersPartitioned;

-- test: Query partitioned table with date filter
-- expect-rows: > 0
SELECT OrderID, OrderTotal, OrderDate FROM sales.OrdersPartitioned
WHERE OrderDate >= '2024-01-01';
