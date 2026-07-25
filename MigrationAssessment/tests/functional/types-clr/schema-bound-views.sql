-- Functional tests: TypesAndCLRDB - Schema-bound views and functions
-- Validates that schema-bound views and UDF references work after migration

-- test: Query schema-bound view vw_EmployeeSummary
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, Salary, YearsEmployed FROM dbo.vw_EmployeeSummary;

-- test: Query schema-bound view vw_EmployeeBonuses
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, Salary, BonusAmount FROM dbo.vw_EmployeeBonuses;

-- test: Execute inventory valuation procedure
-- expect-rows: > 0
EXEC dbo.sp_GetInventoryValuation;

-- test: Execute employee report procedure
-- expect-rows: > 0
EXEC dbo.sp_GetEmployeeReport @MinYears = 0;

-- test: Test CLR fallback regex function
-- expect-rows: > 0
SELECT dbo.fn_RegexMatchFallback('hello@world.com', '%@%') AS RegexResult;

-- test: Test CLR fallback JSON function
-- expect-no-error
SELECT dbo.fn_JsonParseFallback('{"name":"test","value":42}', '$.name') AS JsonResult;
