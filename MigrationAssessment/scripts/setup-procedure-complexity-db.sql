-- Migration Validation Pipeline - Procedure Complexity Test Database
-- Creates a database focused on stored procedures with complex control flow patterns.
-- Exercises: cursors, nested TRY/CATCH, multiple result sets, table-valued parameters, OUTPUT parameters.
--
-- Usage:
--   sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i setup-procedure-complexity-db.sql

USE master;
GO

-- Drop if exists for idempotent reruns
IF DB_ID('ProcedureComplexityDB') IS NOT NULL
BEGIN
    ALTER DATABASE ProcedureComplexityDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE ProcedureComplexityDB;
END
GO

CREATE DATABASE ProcedureComplexityDB;
GO

USE ProcedureComplexityDB;
GO

-- =============================================================================
-- TABLES: Supporting tables for procedure complexity patterns
-- =============================================================================

CREATE TABLE dbo.Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    DepartmentName NVARCHAR(100) NOT NULL,
    BudgetAmount DECIMAL(12,2) NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(255) NOT NULL UNIQUE,
    DepartmentID INT NOT NULL REFERENCES dbo.Departments(DepartmentID),
    Salary DECIMAL(10,2) NOT NULL,
    HireDate DATE NOT NULL DEFAULT GETDATE(),
    ManagerID INT NULL REFERENCES dbo.Employees(EmployeeID)
);
GO

CREATE TABLE dbo.AuditLog (
    AuditID INT IDENTITY(1,1) PRIMARY KEY,
    TableName NVARCHAR(128) NOT NULL,
    Operation NVARCHAR(20) NOT NULL,
    RecordID INT NOT NULL,
    ChangedBy NVARCHAR(128) NOT NULL DEFAULT SYSTEM_USER,
    ChangedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
    OldValues NVARCHAR(MAX) NULL,
    NewValues NVARCHAR(MAX) NULL
);
GO

CREATE TABLE dbo.SalaryHistory (
    HistoryID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL REFERENCES dbo.Employees(EmployeeID),
    OldSalary DECIMAL(10,2) NOT NULL,
    NewSalary DECIMAL(10,2) NOT NULL,
    ChangeDate DATETIME2 NOT NULL DEFAULT GETDATE(),
    Reason NVARCHAR(200) NULL
);
GO

CREATE TABLE dbo.ErrorLog (
    ErrorLogID INT IDENTITY(1,1) PRIMARY KEY,
    ErrorNumber INT NOT NULL,
    ErrorSeverity INT NOT NULL,
    ErrorState INT NOT NULL,
    ErrorProcedure NVARCHAR(128) NULL,
    ErrorLine INT NULL,
    ErrorMessage NVARCHAR(4000) NOT NULL,
    LoggedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);
GO

CREATE TABLE dbo.ProcessingQueue (
    QueueID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL REFERENCES dbo.Employees(EmployeeID),
    ActionType NVARCHAR(50) NOT NULL,
    Priority INT NOT NULL DEFAULT 5,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE(),
    ProcessedAt DATETIME2 NULL
);
GO

CREATE TABLE dbo.BudgetTransfers (
    TransferID INT IDENTITY(1,1) PRIMARY KEY,
    FromDepartmentID INT NOT NULL REFERENCES dbo.Departments(DepartmentID),
    ToDepartmentID INT NOT NULL REFERENCES dbo.Departments(DepartmentID),
    Amount DECIMAL(12,2) NOT NULL,
    TransferDate DATETIME2 NOT NULL DEFAULT GETDATE(),
    ApprovedBy NVARCHAR(128) NULL,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Pending'
);
GO

-- =============================================================================
-- TABLE TYPE: For table-valued parameter procedure
-- =============================================================================

CREATE TYPE dbo.EmployeeBatchType AS TABLE (
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(255) NOT NULL,
    DepartmentID INT NOT NULL,
    Salary DECIMAL(10,2) NOT NULL
);
GO

-- =============================================================================
-- VIEW: Summary view for validation
-- =============================================================================

CREATE VIEW dbo.vw_DepartmentSummary AS
    SELECT
        d.DepartmentID,
        d.DepartmentName,
        d.BudgetAmount,
        COUNT(e.EmployeeID) AS EmployeeCount,
        ISNULL(SUM(e.Salary), 0) AS TotalSalaries,
        ISNULL(AVG(e.Salary), 0) AS AvgSalary
    FROM dbo.Departments d
    LEFT JOIN dbo.Employees e ON d.DepartmentID = e.DepartmentID
    GROUP BY d.DepartmentID, d.DepartmentName, d.BudgetAmount;
GO

-- =============================================================================
-- PROCEDURE 1: Cursor-based processing
-- Iterates through employees and applies salary adjustments per department rules
-- =============================================================================

CREATE PROCEDURE dbo.sp_CursorSalaryAdjustment
    @AdjustmentPercent DECIMAL(5,2),
    @DepartmentID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EmpID INT;
    DECLARE @CurrentSalary DECIMAL(10,2);
    DECLARE @NewSalary DECIMAL(10,2);
    DECLARE @EmpDeptID INT;
    DECLARE @ProcessedCount INT = 0;

    DECLARE emp_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT EmployeeID, Salary, DepartmentID
        FROM dbo.Employees
        WHERE (@DepartmentID IS NULL OR DepartmentID = @DepartmentID)
          AND Salary > 0;

    OPEN emp_cursor;
    FETCH NEXT FROM emp_cursor INTO @EmpID, @CurrentSalary, @EmpDeptID;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        SET @NewSalary = @CurrentSalary * (1 + @AdjustmentPercent / 100.0);

        -- Cap salary at department budget / employee count
        DECLARE @DeptBudget DECIMAL(12,2);
        DECLARE @DeptCount INT;
        SELECT @DeptBudget = BudgetAmount FROM dbo.Departments WHERE DepartmentID = @EmpDeptID;
        SELECT @DeptCount = COUNT(*) FROM dbo.Employees WHERE DepartmentID = @EmpDeptID;

        IF @DeptCount > 0 AND @NewSalary > (@DeptBudget / @DeptCount)
            SET @NewSalary = @DeptBudget / @DeptCount;

        -- Record history
        INSERT INTO dbo.SalaryHistory (EmployeeID, OldSalary, NewSalary, Reason)
        VALUES (@EmpID, @CurrentSalary, @NewSalary, 'Annual adjustment ' + CAST(@AdjustmentPercent AS NVARCHAR(10)) + '%');

        -- Apply update
        UPDATE dbo.Employees SET Salary = @NewSalary WHERE EmployeeID = @EmpID;

        SET @ProcessedCount += 1;
        FETCH NEXT FROM emp_cursor INTO @EmpID, @CurrentSalary, @EmpDeptID;
    END;

    CLOSE emp_cursor;
    DEALLOCATE emp_cursor;

    SELECT @ProcessedCount AS EmployeesProcessed, @AdjustmentPercent AS AdjustmentPercent;
END;
GO

-- =============================================================================
-- PROCEDURE 2: Nested TRY/CATCH (2+ levels deep)
-- Processes budget transfer with inner validation and outer error handling
-- =============================================================================

CREATE PROCEDURE dbo.sp_NestedTryCatchTransfer
    @FromDeptID INT,
    @ToDeptID INT,
    @Amount DECIMAL(12,2),
    @ApprovedBy NVARCHAR(128)
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        -- Outer TRY: Transaction management
        BEGIN TRANSACTION;

        -- Validate departments exist
        IF NOT EXISTS (SELECT 1 FROM dbo.Departments WHERE DepartmentID = @FromDeptID)
            THROW 50001, 'Source department does not exist', 1;

        IF NOT EXISTS (SELECT 1 FROM dbo.Departments WHERE DepartmentID = @ToDeptID)
            THROW 50002, 'Target department does not exist', 1;

        BEGIN TRY
            -- Inner TRY: Budget validation
            DECLARE @CurrentBudget DECIMAL(12,2);
            SELECT @CurrentBudget = BudgetAmount FROM dbo.Departments WHERE DepartmentID = @FromDeptID;

            IF @CurrentBudget < @Amount
                THROW 50003, 'Insufficient budget in source department', 1;

            BEGIN TRY
                -- Innermost TRY: Actual transfer operations
                UPDATE dbo.Departments
                SET BudgetAmount = BudgetAmount - @Amount
                WHERE DepartmentID = @FromDeptID;

                UPDATE dbo.Departments
                SET BudgetAmount = BudgetAmount + @Amount
                WHERE DepartmentID = @ToDeptID;

                INSERT INTO dbo.BudgetTransfers (FromDepartmentID, ToDepartmentID, Amount, ApprovedBy, Status)
                VALUES (@FromDeptID, @ToDeptID, @Amount, @ApprovedBy, 'Completed');

            END TRY
            BEGIN CATCH
                -- Innermost CATCH: Log transfer failure
                INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, ErrorMessage)
                VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE(),
                        'Transfer operation failed: ' + ERROR_MESSAGE());
                THROW;
            END CATCH;

        END TRY
        BEGIN CATCH
            -- Inner CATCH: Log validation failure
            INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, ErrorMessage)
            VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE(),
                    'Budget validation failed: ' + ERROR_MESSAGE());
            THROW;
        END CATCH;

        COMMIT TRANSACTION;

        SELECT 'Transfer completed successfully' AS Result, @Amount AS TransferredAmount;

    END TRY
    BEGIN CATCH
        -- Outer CATCH: Rollback and log
        IF @@TRANCOUNT > 0
            ROLLBACK TRANSACTION;

        INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, ErrorMessage)
        VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE(),
                'Transaction rolled back: ' + ERROR_MESSAGE());

        -- Re-raise for caller
        THROW;
    END CATCH;
END;
GO

-- =============================================================================
-- PROCEDURE 3: Multiple result sets from a single procedure
-- Returns employee details, department summary, and processing queue status
-- =============================================================================

CREATE PROCEDURE dbo.sp_MultiResultSetDashboard
    @DepartmentID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Result Set 1: Employee listing
    SELECT
        e.EmployeeID,
        e.FirstName + ' ' + e.LastName AS FullName,
        e.Email,
        e.Salary,
        d.DepartmentName
    FROM dbo.Employees e
    INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
    WHERE (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID)
    ORDER BY e.LastName, e.FirstName;

    -- Result Set 2: Department budget summary
    SELECT
        d.DepartmentID,
        d.DepartmentName,
        d.BudgetAmount,
        COUNT(e.EmployeeID) AS HeadCount,
        ISNULL(SUM(e.Salary), 0) AS TotalSalary,
        d.BudgetAmount - ISNULL(SUM(e.Salary), 0) AS RemainingBudget
    FROM dbo.Departments d
    LEFT JOIN dbo.Employees e ON d.DepartmentID = e.DepartmentID
    WHERE (@DepartmentID IS NULL OR d.DepartmentID = @DepartmentID)
    GROUP BY d.DepartmentID, d.DepartmentName, d.BudgetAmount;

    -- Result Set 3: Pending processing queue items
    SELECT
        pq.QueueID,
        e.FirstName + ' ' + e.LastName AS EmployeeName,
        pq.ActionType,
        pq.Priority,
        pq.Status,
        pq.CreatedAt
    FROM dbo.ProcessingQueue pq
    INNER JOIN dbo.Employees e ON pq.EmployeeID = e.EmployeeID
    WHERE pq.Status = 'Pending'
      AND (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID)
    ORDER BY pq.Priority ASC, pq.CreatedAt ASC;

    -- Result Set 4: Recent audit trail
    SELECT TOP 10
        AuditID,
        TableName,
        Operation,
        RecordID,
        ChangedBy,
        ChangedAt
    FROM dbo.AuditLog
    ORDER BY ChangedAt DESC;
END;
GO

-- =============================================================================
-- PROCEDURE 4: Table-valued parameter procedure
-- Bulk-inserts employees from a TVP with validation
-- =============================================================================

CREATE PROCEDURE dbo.sp_BulkInsertEmployees
    @Employees dbo.EmployeeBatchType READONLY,
    @InsertedCount INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @InsertedCount = 0;

    -- Validate all departments exist
    IF EXISTS (
        SELECT 1 FROM @Employees e
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Departments d WHERE d.DepartmentID = e.DepartmentID)
    )
    BEGIN
        THROW 50010, 'One or more department IDs are invalid', 1;
    END;

    -- Validate no duplicate emails against existing data
    IF EXISTS (
        SELECT 1 FROM @Employees e
        INNER JOIN dbo.Employees existing ON existing.Email = e.Email
    )
    BEGIN
        THROW 50011, 'One or more email addresses already exist', 1;
    END;

    -- Insert from TVP
    INSERT INTO dbo.Employees (FirstName, LastName, Email, DepartmentID, Salary)
    SELECT FirstName, LastName, Email, DepartmentID, Salary
    FROM @Employees;

    SET @InsertedCount = @@ROWCOUNT;

    -- Log the bulk operation
    INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, NewValues)
    VALUES ('Employees', 'BULK_INSERT', @InsertedCount, 'Batch insert via TVP');

    SELECT @InsertedCount AS TotalInserted;
END;
GO

-- =============================================================================
-- PROCEDURE 5: OUTPUT parameters with multiple assignments
-- Computes department statistics and returns them via OUTPUT params
-- =============================================================================

CREATE PROCEDURE dbo.sp_GetDepartmentStats
    @DepartmentID INT,
    @EmployeeCount INT OUTPUT,
    @TotalSalary DECIMAL(12,2) OUTPUT,
    @AvgSalary DECIMAL(10,2) OUTPUT,
    @MinSalary DECIMAL(10,2) OUTPUT,
    @MaxSalary DECIMAL(10,2) OUTPUT,
    @BudgetRemaining DECIMAL(12,2) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    -- Validate department exists
    IF NOT EXISTS (SELECT 1 FROM dbo.Departments WHERE DepartmentID = @DepartmentID)
    BEGIN
        SET @EmployeeCount = 0;
        SET @TotalSalary = 0;
        SET @AvgSalary = 0;
        SET @MinSalary = 0;
        SET @MaxSalary = 0;
        SET @BudgetRemaining = 0;
        RETURN;
    END;

    -- Compute all statistics
    SELECT
        @EmployeeCount = COUNT(*),
        @TotalSalary = ISNULL(SUM(Salary), 0),
        @AvgSalary = ISNULL(AVG(Salary), 0),
        @MinSalary = ISNULL(MIN(Salary), 0),
        @MaxSalary = ISNULL(MAX(Salary), 0)
    FROM dbo.Employees
    WHERE DepartmentID = @DepartmentID;

    -- Compute remaining budget
    DECLARE @Budget DECIMAL(12,2);
    SELECT @Budget = BudgetAmount FROM dbo.Departments WHERE DepartmentID = @DepartmentID;
    SET @BudgetRemaining = @Budget - @TotalSalary;

    -- Return a result set as well
    SELECT
        @DepartmentID AS DepartmentID,
        @EmployeeCount AS EmployeeCount,
        @TotalSalary AS TotalSalary,
        @AvgSalary AS AvgSalary,
        @BudgetRemaining AS BudgetRemaining;
END;
GO

-- =============================================================================
-- PROCEDURE 6: Cursor with nested TRY/CATCH for queue processing
-- Combines cursor iteration with error handling per item
-- =============================================================================

CREATE PROCEDURE dbo.sp_ProcessQueueWithErrorHandling
    @MaxItems INT = 10,
    @ProcessedCount INT OUTPUT,
    @ErrorCount INT OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @ProcessedCount = 0;
    SET @ErrorCount = 0;

    DECLARE @QueueID INT;
    DECLARE @EmpID INT;
    DECLARE @Action NVARCHAR(50);

    DECLARE queue_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT TOP (@MaxItems) QueueID, EmployeeID, ActionType
        FROM dbo.ProcessingQueue
        WHERE Status = 'Pending'
        ORDER BY Priority ASC, CreatedAt ASC;

    OPEN queue_cursor;
    FETCH NEXT FROM queue_cursor INTO @QueueID, @EmpID, @Action;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        BEGIN TRY
            BEGIN TRY
                -- Inner validation
                IF NOT EXISTS (SELECT 1 FROM dbo.Employees WHERE EmployeeID = @EmpID)
                    THROW 50020, 'Employee not found for queue item', 1;

                -- Process the action
                UPDATE dbo.ProcessingQueue
                SET Status = 'Completed', ProcessedAt = GETDATE()
                WHERE QueueID = @QueueID;

                SET @ProcessedCount += 1;
            END TRY
            BEGIN CATCH
                -- Inner catch: mark as failed
                UPDATE dbo.ProcessingQueue
                SET Status = 'Failed', ProcessedAt = GETDATE()
                WHERE QueueID = @QueueID;

                INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, ErrorMessage)
                VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE(),
                        'Queue item ' + CAST(@QueueID AS NVARCHAR(10)) + ': ' + ERROR_MESSAGE());

                SET @ErrorCount += 1;
            END CATCH;
        END TRY
        BEGIN CATCH
            -- Outer catch: unexpected error
            INSERT INTO dbo.ErrorLog (ErrorNumber, ErrorSeverity, ErrorState, ErrorProcedure, ErrorLine, ErrorMessage)
            VALUES (ERROR_NUMBER(), ERROR_SEVERITY(), ERROR_STATE(), ERROR_PROCEDURE(), ERROR_LINE(),
                    'Unexpected queue processing error: ' + ERROR_MESSAGE());
            SET @ErrorCount += 1;
        END CATCH;

        FETCH NEXT FROM queue_cursor INTO @QueueID, @EmpID, @Action;
    END;

    CLOSE queue_cursor;
    DEALLOCATE queue_cursor;

    SELECT @ProcessedCount AS Processed, @ErrorCount AS Errors;
END;
GO

-- =============================================================================
-- PROCEDURE 7: Complex OUTPUT with conditional logic
-- Promotes employee with multiple output indicators
-- =============================================================================

CREATE PROCEDURE dbo.sp_PromoteEmployee
    @EmployeeID INT,
    @NewDepartmentID INT = NULL,
    @SalaryIncrease DECIMAL(5,2) = 10.0,
    @WasPromoted BIT OUTPUT,
    @NewSalary DECIMAL(10,2) OUTPUT,
    @PromotionMessage NVARCHAR(500) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;

    SET @WasPromoted = 0;
    SET @NewSalary = 0;
    SET @PromotionMessage = '';

    DECLARE @CurrentSalary DECIMAL(10,2);
    DECLARE @CurrentDeptID INT;
    DECLARE @EmpName NVARCHAR(201);

    SELECT @CurrentSalary = Salary, @CurrentDeptID = DepartmentID,
           @EmpName = FirstName + ' ' + LastName
    FROM dbo.Employees WHERE EmployeeID = @EmployeeID;

    IF @CurrentSalary IS NULL
    BEGIN
        SET @PromotionMessage = 'Employee not found';
        RETURN;
    END;

    SET @NewSalary = @CurrentSalary * (1 + @SalaryIncrease / 100.0);
    DECLARE @TargetDept INT = ISNULL(@NewDepartmentID, @CurrentDeptID);

    -- Check budget allows the promotion
    DECLARE @DeptBudget DECIMAL(12,2);
    DECLARE @DeptCurrentSalaries DECIMAL(12,2);
    SELECT @DeptBudget = BudgetAmount FROM dbo.Departments WHERE DepartmentID = @TargetDept;
    SELECT @DeptCurrentSalaries = ISNULL(SUM(Salary), 0) FROM dbo.Employees WHERE DepartmentID = @TargetDept AND EmployeeID <> @EmployeeID;

    IF (@DeptCurrentSalaries + @NewSalary) > @DeptBudget
    BEGIN
        SET @PromotionMessage = 'Promotion denied: would exceed department budget';
        SET @NewSalary = @CurrentSalary;
        RETURN;
    END;

    -- Apply promotion
    UPDATE dbo.Employees
    SET Salary = @NewSalary,
        DepartmentID = @TargetDept
    WHERE EmployeeID = @EmployeeID;

    INSERT INTO dbo.SalaryHistory (EmployeeID, OldSalary, NewSalary, Reason)
    VALUES (@EmployeeID, @CurrentSalary, @NewSalary, 'Promotion');

    SET @WasPromoted = 1;
    SET @PromotionMessage = @EmpName + ' promoted. Salary: ' + CAST(@CurrentSalary AS NVARCHAR(20)) + ' -> ' + CAST(@NewSalary AS NVARCHAR(20));

    SELECT @EmployeeID AS EmployeeID, @WasPromoted AS Promoted, @NewSalary AS NewSalary, @PromotionMessage AS Message;
END;
GO

-- =============================================================================
-- PROCEDURE 8: Another cursor pattern - department report generation
-- Uses cursor to iterate departments and build summary
-- =============================================================================

CREATE PROCEDURE dbo.sp_CursorDepartmentReport
AS
BEGIN
    SET NOCOUNT ON;

    CREATE TABLE #DeptReport (
        DepartmentName NVARCHAR(100),
        EmployeeCount INT,
        TotalSalary DECIMAL(12,2),
        BudgetUtilization DECIMAL(5,2)
    );

    DECLARE @DeptID INT;
    DECLARE @DeptName NVARCHAR(100);
    DECLARE @Budget DECIMAL(12,2);

    DECLARE dept_cursor CURSOR LOCAL FAST_FORWARD FOR
        SELECT DepartmentID, DepartmentName, BudgetAmount
        FROM dbo.Departments
        WHERE IsActive = 1;

    OPEN dept_cursor;
    FETCH NEXT FROM dept_cursor INTO @DeptID, @DeptName, @Budget;

    WHILE @@FETCH_STATUS = 0
    BEGIN
        DECLARE @EmpCount INT;
        DECLARE @TotalSal DECIMAL(12,2);

        SELECT @EmpCount = COUNT(*), @TotalSal = ISNULL(SUM(Salary), 0)
        FROM dbo.Employees
        WHERE DepartmentID = @DeptID;

        DECLARE @Utilization DECIMAL(5,2) = 0;
        IF @Budget > 0
            SET @Utilization = (@TotalSal / @Budget) * 100;

        INSERT INTO #DeptReport (DepartmentName, EmployeeCount, TotalSalary, BudgetUtilization)
        VALUES (@DeptName, @EmpCount, @TotalSal, @Utilization);

        FETCH NEXT FROM dept_cursor INTO @DeptID, @DeptName, @Budget;
    END;

    CLOSE dept_cursor;
    DEALLOCATE dept_cursor;

    SELECT * FROM #DeptReport ORDER BY BudgetUtilization DESC;

    DROP TABLE #DeptReport;
END;
GO

-- =============================================================================
-- PROCEDURE 9: Multiple result sets with conditional output
-- Returns different result sets based on parameters
-- =============================================================================

CREATE PROCEDURE dbo.sp_EmployeeReport
    @ReportType NVARCHAR(20),
    @DepartmentID INT = NULL
AS
BEGIN
    SET NOCOUNT ON;

    -- Result Set 1: Always returned - report header
    SELECT
        @ReportType AS ReportType,
        GETDATE() AS GeneratedAt,
        (SELECT COUNT(*) FROM dbo.Employees) AS TotalEmployees;

    -- Result Set 2: Conditional on report type
    IF @ReportType = 'SALARY'
    BEGIN
        SELECT
            e.FirstName + ' ' + e.LastName AS EmployeeName,
            e.Salary,
            d.DepartmentName,
            RANK() OVER (PARTITION BY e.DepartmentID ORDER BY e.Salary DESC) AS SalaryRank
        FROM dbo.Employees e
        INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
        WHERE (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID);
    END
    ELSE IF @ReportType = 'HISTORY'
    BEGIN
        SELECT
            e.FirstName + ' ' + e.LastName AS EmployeeName,
            sh.OldSalary,
            sh.NewSalary,
            sh.ChangeDate,
            sh.Reason
        FROM dbo.SalaryHistory sh
        INNER JOIN dbo.Employees e ON sh.EmployeeID = e.EmployeeID
        WHERE (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID)
        ORDER BY sh.ChangeDate DESC;
    END
    ELSE
    BEGIN
        SELECT
            e.EmployeeID,
            e.FirstName + ' ' + e.LastName AS EmployeeName,
            e.Email,
            d.DepartmentName,
            e.HireDate
        FROM dbo.Employees e
        INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
        WHERE (@DepartmentID IS NULL OR e.DepartmentID = @DepartmentID);
    END;
END;
GO

-- =============================================================================
-- SEED DATA - Populate tables so all procedures and views return results
-- =============================================================================

-- Departments
INSERT INTO dbo.Departments (DepartmentName, BudgetAmount, IsActive) VALUES
('Engineering', 500000.00, 1),
('Marketing', 300000.00, 1),
('Sales', 400000.00, 1),
('Human Resources', 200000.00, 1),
('Finance', 350000.00, 1);
GO

-- Employees
INSERT INTO dbo.Employees (FirstName, LastName, Email, DepartmentID, Salary, HireDate, ManagerID) VALUES
('Alice', 'Chen', 'alice.chen@company.com', 1, 95000.00, '2020-03-15', NULL),
('Bob', 'Martinez', 'bob.martinez@company.com', 1, 88000.00, '2021-06-01', 1),
('Carol', 'Williams', 'carol.williams@company.com', 1, 92000.00, '2019-11-20', 1),
('David', 'Kim', 'david.kim@company.com', 2, 75000.00, '2022-01-10', NULL),
('Eva', 'Johnson', 'eva.johnson@company.com', 2, 72000.00, '2022-04-22', 4),
('Frank', 'Brown', 'frank.brown@company.com', 3, 85000.00, '2020-08-05', NULL),
('Grace', 'Lee', 'grace.lee@company.com', 3, 78000.00, '2021-02-14', 6),
('Henry', 'Taylor', 'henry.taylor@company.com', 4, 68000.00, '2023-01-09', NULL),
('Irene', 'Davis', 'irene.davis@company.com', 5, 91000.00, '2019-07-30', NULL),
('Jack', 'Wilson', 'jack.wilson@company.com', 5, 82000.00, '2021-11-15', 9);
GO

-- Audit Log entries
INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, OldValues, NewValues) VALUES
('Employees', 'INSERT', 1, NULL, '{"FirstName":"Alice","LastName":"Chen"}'),
('Employees', 'INSERT', 2, NULL, '{"FirstName":"Bob","LastName":"Martinez"}'),
('Departments', 'UPDATE', 1, '{"BudgetAmount":400000}', '{"BudgetAmount":500000}'),
('Employees', 'UPDATE', 3, '{"Salary":85000}', '{"Salary":92000}');
GO

-- Salary History entries
INSERT INTO dbo.SalaryHistory (EmployeeID, OldSalary, NewSalary, ChangeDate, Reason) VALUES
(1, 85000.00, 95000.00, '2023-01-01', 'Annual review'),
(3, 85000.00, 92000.00, '2023-06-15', 'Promotion'),
(6, 78000.00, 85000.00, '2023-03-01', 'Market adjustment'),
(9, 85000.00, 91000.00, '2023-07-01', 'Annual review');
GO

-- Processing Queue entries
INSERT INTO dbo.ProcessingQueue (EmployeeID, ActionType, Priority, Status) VALUES
(1, 'Review', 3, 'Pending'),
(2, 'Training', 5, 'Pending'),
(4, 'Review', 2, 'Pending'),
(6, 'Certification', 4, 'Pending'),
(8, 'Onboarding', 1, 'Pending');
GO

-- Budget Transfers
INSERT INTO dbo.BudgetTransfers (FromDepartmentID, ToDepartmentID, Amount, ApprovedBy, Status) VALUES
(1, 2, 25000.00, 'CFO', 'Completed'),
(3, 4, 15000.00, 'VP Operations', 'Completed');
GO

-- =============================================================================
-- EXERCISE QUERIES - Execute procedures to verify they work and return results
-- =============================================================================

-- Exercise cursor-based procedure
EXEC dbo.sp_CursorSalaryAdjustment @AdjustmentPercent = 5.0, @DepartmentID = 1;
GO

-- Exercise nested TRY/CATCH (valid transfer)
EXEC dbo.sp_NestedTryCatchTransfer @FromDeptID = 1, @ToDeptID = 2, @Amount = 10000.00, @ApprovedBy = 'TestAdmin';
GO

-- Exercise multiple result sets
EXEC dbo.sp_MultiResultSetDashboard @DepartmentID = NULL;
GO

-- Exercise table-valued parameter procedure
DECLARE @NewEmployees dbo.EmployeeBatchType;
INSERT INTO @NewEmployees (FirstName, LastName, Email, DepartmentID, Salary) VALUES
('Karl', 'Miller', 'karl.miller@company.com', 1, 72000.00),
('Laura', 'Anderson', 'laura.anderson@company.com', 3, 69000.00);

DECLARE @Count INT;
EXEC dbo.sp_BulkInsertEmployees @Employees = @NewEmployees, @InsertedCount = @Count OUTPUT;
SELECT @Count AS BulkInsertedCount;
GO

-- Exercise OUTPUT parameter procedure
DECLARE @EmpCount INT, @TotalSal DECIMAL(12,2), @AvgSal DECIMAL(10,2);
DECLARE @MinSal DECIMAL(10,2), @MaxSal DECIMAL(10,2), @BudgetRem DECIMAL(12,2);
EXEC dbo.sp_GetDepartmentStats @DepartmentID = 1,
    @EmployeeCount = @EmpCount OUTPUT,
    @TotalSalary = @TotalSal OUTPUT,
    @AvgSalary = @AvgSal OUTPUT,
    @MinSalary = @MinSal OUTPUT,
    @MaxSalary = @MaxSal OUTPUT,
    @BudgetRemaining = @BudgetRem OUTPUT;
SELECT @EmpCount AS EmpCount, @TotalSal AS TotalSal, @AvgSal AS AvgSal, @BudgetRem AS BudgetRemaining;
GO

-- Exercise queue processing with nested TRY/CATCH
DECLARE @Processed INT, @Errors INT;
EXEC dbo.sp_ProcessQueueWithErrorHandling @MaxItems = 5, @ProcessedCount = @Processed OUTPUT, @ErrorCount = @Errors OUTPUT;
SELECT @Processed AS Processed, @Errors AS Errors;
GO

-- Exercise promotion procedure with OUTPUT
DECLARE @Promoted BIT, @NewSal DECIMAL(10,2), @Msg NVARCHAR(500);
EXEC dbo.sp_PromoteEmployee @EmployeeID = 2, @SalaryIncrease = 8.0,
    @WasPromoted = @Promoted OUTPUT, @NewSalary = @NewSal OUTPUT, @PromotionMessage = @Msg OUTPUT;
SELECT @Promoted AS Promoted, @NewSal AS NewSalary, @Msg AS Message;
GO

-- Exercise cursor department report
EXEC dbo.sp_CursorDepartmentReport;
GO

-- Exercise conditional multi-result set procedure
EXEC dbo.sp_EmployeeReport @ReportType = 'SALARY';
GO

EXEC dbo.sp_EmployeeReport @ReportType = 'HISTORY';
GO

-- Exercise view
SELECT * FROM dbo.vw_DepartmentSummary;
GO

-- =============================================================================
-- SUMMARY
-- =============================================================================

PRINT '=============================================';
PRINT 'Test database setup complete!';
PRINT 'Database: ProcedureComplexityDB';
PRINT '';
PRINT 'Schema objects created:';
PRINT '  Tables: 7 (Departments, Employees, AuditLog, SalaryHistory,';
PRINT '             ErrorLog, ProcessingQueue, BudgetTransfers)';
PRINT '  Types:  1 (EmployeeBatchType - table-valued parameter)';
PRINT '  Views:  1 (vw_DepartmentSummary)';
PRINT '  Stored Procedures: 9';
PRINT '    - sp_CursorSalaryAdjustment (cursor-based)';
PRINT '    - sp_NestedTryCatchTransfer (nested TRY/CATCH 3 levels)';
PRINT '    - sp_MultiResultSetDashboard (4 result sets)';
PRINT '    - sp_BulkInsertEmployees (table-valued parameter)';
PRINT '    - sp_GetDepartmentStats (6 OUTPUT parameters)';
PRINT '    - sp_ProcessQueueWithErrorHandling (cursor + nested TRY/CATCH)';
PRINT '    - sp_PromoteEmployee (OUTPUT with conditional logic)';
PRINT '    - sp_CursorDepartmentReport (cursor + temp table)';
PRINT '    - sp_EmployeeReport (multiple result sets, conditional)';
PRINT '';
PRINT 'Total objects: 18 (7 tables + 1 type + 1 view + 9 procedures)';
PRINT 'Complexity patterns covered:';
PRINT '  - Cursor-based processing';
PRINT '  - Nested TRY/CATCH (3 levels deep)';
PRINT '  - Multiple result sets from single procedure';
PRINT '  - Table-valued parameters';
PRINT '  - OUTPUT parameters with multiple assignments';
PRINT '=============================================';
GO
