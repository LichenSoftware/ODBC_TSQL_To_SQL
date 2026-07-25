-- Functional tests: ProcedureComplexityDB - OUTPUT parameter tests
-- Validates that procedures with OUTPUT parameters work correctly

-- test: Get department stats with OUTPUT parameters
-- expect-rows: > 0
DECLARE @EmpCount INT, @TotalSal DECIMAL(12,2), @AvgSal DECIMAL(10,2);
DECLARE @MinSal DECIMAL(10,2), @MaxSal DECIMAL(10,2), @BudgetRem DECIMAL(12,2);
EXEC dbo.sp_GetDepartmentStats @DepartmentID = 1,
    @EmployeeCount = @EmpCount OUTPUT,
    @TotalSalary = @TotalSal OUTPUT,
    @AvgSalary = @AvgSal OUTPUT,
    @MinSalary = @MinSal OUTPUT,
    @MaxSalary = @MaxSal OUTPUT,
    @BudgetRemaining = @BudgetRem OUTPUT;
SELECT @EmpCount AS EmployeeCount, @TotalSal AS TotalSalary, @AvgSal AS AvgSalary;

-- test: Process queue with OUTPUT counts
-- expect-rows: > 0
DECLARE @Processed INT, @Errors INT;
EXEC dbo.sp_ProcessQueueWithErrorHandling @MaxItems = 5,
    @ProcessedCount = @Processed OUTPUT,
    @ErrorCount = @Errors OUTPUT;
SELECT @Processed AS Processed, @Errors AS Errors;

-- test: Promote employee with OUTPUT parameters
-- expect-rows: > 0
DECLARE @WasPromoted BIT, @NewSalary DECIMAL(10,2), @Msg NVARCHAR(500);
EXEC dbo.sp_PromoteEmployee @EmployeeID = 2, @SalaryIncrease = 5.0,
    @WasPromoted = @WasPromoted OUTPUT,
    @NewSalary = @NewSalary OUTPUT,
    @PromotionMessage = @Msg OUTPUT;
SELECT @WasPromoted AS Promoted, @NewSalary AS NewSalary, @Msg AS Message;
