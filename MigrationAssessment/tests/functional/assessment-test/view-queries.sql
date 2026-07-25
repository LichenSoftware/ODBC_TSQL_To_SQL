-- Functional tests: AssessmentTestDB - View queries
-- Validates that views are correctly translated and return expected results

-- test: Query vw_RecentOrders view
-- expect-rows: > 0
SELECT OrderID, CustomerName, OrderDate, TotalAmount FROM dbo.vw_RecentOrders;

-- test: Query vw_MonthlyCategoryRevenue view
-- expect-no-error
SELECT * FROM dbo.vw_MonthlyCategoryRevenue;

-- test: Execute sp_GetTopCustomers procedure
-- expect-rows: > 0
EXEC dbo.sp_GetTopCustomers @TopN = 5;

-- test: Execute sp_GetInventorySnapshot procedure
-- expect-rows: > 0
EXEC dbo.sp_GetInventorySnapshot;
