-- Functional tests: ProcedureComplexityDB - Cursor-based procedure calls
-- Validates that cursor-based procedures translate and execute correctly

-- test: Execute cursor salary adjustment
-- expect-rows: > 0
EXEC dbo.sp_CursorSalaryAdjustment @AdjustmentPercent = 2.0, @DepartmentID = 2;

-- test: Verify salary history recorded
-- expect-rows: > 0
SELECT EmployeeID, OldSalary, NewSalary, Reason FROM dbo.SalaryHistory;

-- test: Execute nested try-catch transfer with valid data
-- expect-no-error
EXEC dbo.sp_NestedTryCatchTransfer @FromDeptID = 1, @ToDeptID = 2, @Amount = 5000.00, @ApprovedBy = 'TestAdmin';

-- test: Verify budget transfer recorded
-- expect-rows: > 0
SELECT TransferID, FromDepartmentID, ToDepartmentID, Amount, Status FROM dbo.BudgetTransfers;
