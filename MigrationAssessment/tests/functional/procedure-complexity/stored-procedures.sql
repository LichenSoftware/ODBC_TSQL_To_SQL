-- Functional tests: ProcedureComplexityDB - Stored procedure execution
-- Validates that stored procedures with complex control flow translate correctly

-- test: Execute multi-result-set dashboard
-- expect-rows: > 0
EXEC dbo.sp_MultiResultSetDashboard @DepartmentID = NULL;

-- test: Execute cursor-based department report
-- expect-rows: > 0
EXEC dbo.sp_CursorDepartmentReport;

-- test: Execute employee report - salary type
-- expect-rows: > 0
EXEC dbo.sp_EmployeeReport @ReportType = 'SALARY';

-- test: Execute employee report - default type
-- expect-rows: > 0
EXEC dbo.sp_EmployeeReport @ReportType = 'DEFAULT', @DepartmentID = 1;
