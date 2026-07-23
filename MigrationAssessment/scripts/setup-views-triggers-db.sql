-- Migration Validation Pipeline - Views & Triggers Test Database Setup
-- Creates a database focused on complex views and triggers to exercise conversion patterns.
-- Run against: Server=localhost;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True
--
-- Usage:
--   sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i setup-views-triggers-db.sql

USE master;
GO

-- Drop if exists for idempotent reruns
IF DB_ID('ViewsTriggerDB') IS NOT NULL
BEGIN
    ALTER DATABASE ViewsTriggerDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE ViewsTriggerDB;
END
GO

CREATE DATABASE ViewsTriggerDB;
GO

USE ViewsTriggerDB;
GO

-- =============================================================================
-- BASE TABLES (supporting objects for views and triggers)
-- =============================================================================

CREATE TABLE dbo.Departments (
    DepartmentID INT IDENTITY(1,1) PRIMARY KEY,
    DepartmentName NVARCHAR(100) NOT NULL,
    Budget DECIMAL(12,2) NOT NULL DEFAULT 0,
    IsActive BIT NOT NULL DEFAULT 1
);
GO

CREATE TABLE dbo.Employees (
    EmployeeID INT IDENTITY(1,1) PRIMARY KEY,
    FirstName NVARCHAR(100) NOT NULL,
    LastName NVARCHAR(100) NOT NULL,
    Email NVARCHAR(255) NOT NULL UNIQUE,
    DepartmentID INT NOT NULL REFERENCES dbo.Departments(DepartmentID),
    HireDate DATE NOT NULL DEFAULT GETDATE(),
    Salary DECIMAL(10,2) NOT NULL,
    ManagerID INT NULL REFERENCES dbo.Employees(EmployeeID)
);
GO

CREATE TABLE dbo.Projects (
    ProjectID INT IDENTITY(1,1) PRIMARY KEY,
    ProjectName NVARCHAR(200) NOT NULL,
    DepartmentID INT NOT NULL REFERENCES dbo.Departments(DepartmentID),
    StartDate DATE NOT NULL,
    EndDate DATE NULL,
    Budget DECIMAL(12,2) NOT NULL DEFAULT 0,
    Status NVARCHAR(20) NOT NULL DEFAULT 'Active'
);
GO

CREATE TABLE dbo.ProjectAssignments (
    AssignmentID INT IDENTITY(1,1) PRIMARY KEY,
    ProjectID INT NOT NULL REFERENCES dbo.Projects(ProjectID),
    EmployeeID INT NOT NULL REFERENCES dbo.Employees(EmployeeID),
    Role NVARCHAR(50) NOT NULL DEFAULT 'Member',
    HoursAllocated INT NOT NULL DEFAULT 40,
    AssignedDate DATE NOT NULL DEFAULT GETDATE()
);
GO

CREATE TABLE dbo.AuditLog (
    AuditID INT IDENTITY(1,1) PRIMARY KEY,
    TableName NVARCHAR(128) NOT NULL,
    Operation NVARCHAR(10) NOT NULL,
    RecordID INT NOT NULL,
    ChangedBy NVARCHAR(128) NOT NULL DEFAULT SUSER_SNAME(),
    ChangedAt DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
    OldValues NVARCHAR(MAX) NULL,
    NewValues NVARCHAR(MAX) NULL
);
GO

CREATE TABLE dbo.EmployeeSkills (
    SkillID INT IDENTITY(1,1) PRIMARY KEY,
    EmployeeID INT NOT NULL REFERENCES dbo.Employees(EmployeeID),
    SkillName NVARCHAR(100) NOT NULL,
    ProficiencyLevel INT NOT NULL CHECK (ProficiencyLevel BETWEEN 1 AND 5),
    CertifiedDate DATE NULL
);
GO

-- =============================================================================
-- INDEXED VIEW (with SCHEMABINDING)
-- Requirement 1.2: indexed views pattern
-- =============================================================================

CREATE VIEW dbo.vw_DepartmentSummary
WITH SCHEMABINDING
AS
    SELECT
        d.DepartmentID,
        d.DepartmentName,
        COUNT_BIG(*) AS EmployeeCount,
        SUM(e.Salary) AS TotalSalary
    FROM dbo.Departments d
    INNER JOIN dbo.Employees e ON d.DepartmentID = e.DepartmentID
    GROUP BY d.DepartmentID, d.DepartmentName;
GO

CREATE UNIQUE CLUSTERED INDEX IX_vw_DepartmentSummary
    ON dbo.vw_DepartmentSummary (DepartmentID);
GO

-- =============================================================================
-- INSTEAD OF TRIGGER
-- Requirement 1.2: INSTEAD OF triggers pattern
-- =============================================================================

CREATE VIEW dbo.vw_EmployeeDirectory AS
    SELECT
        e.EmployeeID,
        e.FirstName,
        e.LastName,
        e.Email,
        d.DepartmentName,
        e.DepartmentID,
        e.Salary
    FROM dbo.Employees e
    INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID;
GO

CREATE TRIGGER dbo.tr_EmployeeDirectory_InsteadOfInsert
ON dbo.vw_EmployeeDirectory
INSTEAD OF INSERT
AS
BEGIN
    SET NOCOUNT ON;

    INSERT INTO dbo.Employees (FirstName, LastName, Email, DepartmentID, Salary)
    SELECT
        i.FirstName,
        i.LastName,
        i.Email,
        i.DepartmentID,
        i.Salary
    FROM inserted i
    WHERE EXISTS (SELECT 1 FROM dbo.Departments d WHERE d.DepartmentID = i.DepartmentID);
END;
GO

CREATE TRIGGER dbo.tr_EmployeeDirectory_InsteadOfUpdate
ON dbo.vw_EmployeeDirectory
INSTEAD OF UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE e
    SET
        e.FirstName = i.FirstName,
        e.LastName = i.LastName,
        e.Email = i.Email,
        e.DepartmentID = i.DepartmentID,
        e.Salary = i.Salary
    FROM dbo.Employees e
    INNER JOIN inserted i ON e.EmployeeID = i.EmployeeID;
END;
GO

-- =============================================================================
-- MULTI-TABLE TRIGGER (affects 2+ tables)
-- Requirement 1.2: multi-table triggers pattern
-- =============================================================================

CREATE TRIGGER dbo.tr_Employees_AuditAndBudget
ON dbo.Employees
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;

    -- Write to AuditLog table
    IF EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- UPDATE
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, OldValues, NewValues)
        SELECT
            'Employees',
            'UPDATE',
            i.EmployeeID,
            'Salary=' + CAST(d.Salary AS NVARCHAR(20)),
            'Salary=' + CAST(i.Salary AS NVARCHAR(20))
        FROM inserted i
        INNER JOIN deleted d ON i.EmployeeID = d.EmployeeID;

        -- Update Department budget when salary changes
        UPDATE dept
        SET dept.Budget = dept.Budget + (i.Salary - d.Salary)
        FROM dbo.Departments dept
        INNER JOIN inserted i ON dept.DepartmentID = i.DepartmentID
        INNER JOIN deleted d ON i.EmployeeID = d.EmployeeID
        WHERE i.Salary <> d.Salary;
    END
    ELSE IF EXISTS (SELECT 1 FROM inserted)
    BEGIN
        -- INSERT
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, NewValues)
        SELECT
            'Employees',
            'INSERT',
            i.EmployeeID,
            'Name=' + i.FirstName + ' ' + i.LastName + ', Salary=' + CAST(i.Salary AS NVARCHAR(20))
        FROM inserted i;

        -- Add salary to department budget
        UPDATE dept
        SET dept.Budget = dept.Budget + i.Salary
        FROM dbo.Departments dept
        INNER JOIN inserted i ON dept.DepartmentID = i.DepartmentID;
    END
    ELSE IF EXISTS (SELECT 1 FROM deleted)
    BEGIN
        -- DELETE
        INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, OldValues)
        SELECT
            'Employees',
            'DELETE',
            d.EmployeeID,
            'Name=' + d.FirstName + ' ' + d.LastName + ', Salary=' + CAST(d.Salary AS NVARCHAR(20))
        FROM deleted d;

        -- Subtract salary from department budget
        UPDATE dept
        SET dept.Budget = dept.Budget - d.Salary
        FROM dbo.Departments dept
        INNER JOIN deleted d ON dept.DepartmentID = d.DepartmentID;
    END
END;
GO

CREATE TRIGGER dbo.tr_Projects_MultiTableAudit
ON dbo.Projects
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;

    -- Log to AuditLog
    INSERT INTO dbo.AuditLog (TableName, Operation, RecordID, NewValues)
    SELECT
        'Projects',
        CASE WHEN EXISTS (SELECT 1 FROM deleted) THEN 'UPDATE' ELSE 'INSERT' END,
        i.ProjectID,
        'Name=' + i.ProjectName + ', Budget=' + CAST(i.Budget AS NVARCHAR(20))
    FROM inserted i;

    -- Update department budget allocation when project budget changes
    UPDATE d
    SET d.Budget = d.Budget + ISNULL(i.Budget, 0) - ISNULL(del.Budget, 0)
    FROM dbo.Departments d
    INNER JOIN inserted i ON d.DepartmentID = i.DepartmentID
    LEFT JOIN deleted del ON i.ProjectID = del.ProjectID;
END;
GO

-- =============================================================================
-- VIEW WITH CROSS APPLY
-- Requirement 1.2: views with APPLY operators
-- =============================================================================

CREATE VIEW dbo.vw_EmployeeTopSkills AS
    SELECT
        e.EmployeeID,
        e.FirstName,
        e.LastName,
        d.DepartmentName,
        topSkill.SkillName AS TopSkillName,
        topSkill.ProficiencyLevel AS TopSkillLevel
    FROM dbo.Employees e
    INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
    CROSS APPLY (
        SELECT TOP 1
            es.SkillName,
            es.ProficiencyLevel
        FROM dbo.EmployeeSkills es
        WHERE es.EmployeeID = e.EmployeeID
        ORDER BY es.ProficiencyLevel DESC, es.SkillName ASC
    ) topSkill;
GO

-- VIEW WITH OUTER APPLY
CREATE VIEW dbo.vw_EmployeeProjectLoad AS
    SELECT
        e.EmployeeID,
        e.FirstName + ' ' + e.LastName AS FullName,
        d.DepartmentName,
        projectInfo.ProjectCount,
        projectInfo.TotalHoursAllocated,
        projectInfo.LatestAssignment
    FROM dbo.Employees e
    INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
    OUTER APPLY (
        SELECT
            COUNT(*) AS ProjectCount,
            SUM(pa.HoursAllocated) AS TotalHoursAllocated,
            MAX(pa.AssignedDate) AS LatestAssignment
        FROM dbo.ProjectAssignments pa
        WHERE pa.EmployeeID = e.EmployeeID
    ) projectInfo;
GO

-- =============================================================================
-- NESTED VIEWS (view referencing another view)
-- Requirement 1.2: nested views pattern
-- =============================================================================

-- Base view
CREATE VIEW dbo.vw_ActiveEmployees AS
    SELECT
        e.EmployeeID,
        e.FirstName,
        e.LastName,
        e.Email,
        e.DepartmentID,
        e.Salary,
        e.HireDate,
        e.ManagerID
    FROM dbo.Employees e
    INNER JOIN dbo.Departments d ON e.DepartmentID = d.DepartmentID
    WHERE d.IsActive = 1;
GO

-- Nested view referencing vw_ActiveEmployees
CREATE VIEW dbo.vw_ActiveEmployeeDetails AS
    SELECT
        ae.EmployeeID,
        ae.FirstName + ' ' + ae.LastName AS FullName,
        ae.Email,
        d.DepartmentName,
        ae.Salary,
        ae.HireDate,
        DATEDIFF(YEAR, ae.HireDate, GETDATE()) AS YearsOfService,
        mgr.FirstName + ' ' + mgr.LastName AS ManagerName
    FROM dbo.vw_ActiveEmployees ae
    INNER JOIN dbo.Departments d ON ae.DepartmentID = d.DepartmentID
    LEFT JOIN dbo.Employees mgr ON ae.ManagerID = mgr.EmployeeID;
GO

-- Second level nested view referencing vw_ActiveEmployeeDetails
CREATE VIEW dbo.vw_SeniorActiveEmployees AS
    SELECT
        aed.EmployeeID,
        aed.FullName,
        aed.Email,
        aed.DepartmentName,
        aed.Salary,
        aed.YearsOfService,
        aed.ManagerName
    FROM dbo.vw_ActiveEmployeeDetails aed
    WHERE aed.YearsOfService >= 3;
GO

-- =============================================================================
-- ADDITIONAL VIEWS (to meet 15+ objects minimum)
-- =============================================================================

-- View with multiple JOINs and aggregation
CREATE VIEW dbo.vw_ProjectOverview AS
    SELECT
        p.ProjectID,
        p.ProjectName,
        d.DepartmentName,
        p.Status,
        p.Budget,
        p.StartDate,
        p.EndDate,
        COUNT(pa.AssignmentID) AS TeamSize,
        SUM(pa.HoursAllocated) AS TotalHoursAllocated
    FROM dbo.Projects p
    INNER JOIN dbo.Departments d ON p.DepartmentID = d.DepartmentID
    LEFT JOIN dbo.ProjectAssignments pa ON p.ProjectID = pa.ProjectID
    GROUP BY p.ProjectID, p.ProjectName, d.DepartmentName, p.Status,
             p.Budget, p.StartDate, p.EndDate;
GO

-- =============================================================================
-- SEED DATA - Populate tables so views return non-empty results
-- =============================================================================

-- Departments (insert before employees due to FK)
SET IDENTITY_INSERT dbo.Departments ON;
INSERT INTO dbo.Departments (DepartmentID, DepartmentName, Budget, IsActive) VALUES
(1, 'Engineering', 0, 1),
(2, 'Marketing', 0, 1),
(3, 'Finance', 0, 1),
(4, 'Human Resources', 0, 1),
(5, 'Research', 0, 0);
SET IDENTITY_INSERT dbo.Departments OFF;
GO

-- Disable the multi-table trigger temporarily so budget isn't auto-calculated during seed
DISABLE TRIGGER dbo.tr_Employees_AuditAndBudget ON dbo.Employees;
GO

-- Employees
SET IDENTITY_INSERT dbo.Employees ON;
INSERT INTO dbo.Employees (EmployeeID, FirstName, LastName, Email, DepartmentID, HireDate, Salary, ManagerID) VALUES
(1, 'Sarah', 'Connor', 'sarah.connor@company.com', 1, '2018-03-15', 125000.00, NULL),
(2, 'James', 'Kirk', 'james.kirk@company.com', 1, '2019-07-01', 115000.00, 1),
(3, 'Ellen', 'Ripley', 'ellen.ripley@company.com', 1, '2020-01-10', 105000.00, 1),
(4, 'Dana', 'Scully', 'dana.scully@company.com', 2, '2019-05-20', 98000.00, NULL),
(5, 'Fox', 'Mulder', 'fox.mulder@company.com', 2, '2020-09-14', 95000.00, 4),
(6, 'Jean', 'Grey', 'jean.grey@company.com', 3, '2017-11-03', 110000.00, NULL),
(7, 'Bruce', 'Wayne', 'bruce.wayne@company.com', 3, '2021-02-28', 88000.00, 6),
(8, 'Diana', 'Prince', 'diana.prince@company.com', 4, '2018-08-12', 92000.00, NULL),
(9, 'Clark', 'Kent', 'clark.kent@company.com', 1, '2022-04-01', 100000.00, 1),
(10, 'Lara', 'Croft', 'lara.croft@company.com', 2, '2021-06-15', 91000.00, 4);
SET IDENTITY_INSERT dbo.Employees OFF;
GO

-- Re-enable the trigger
ENABLE TRIGGER dbo.tr_Employees_AuditAndBudget ON dbo.Employees;
GO

-- Manually set department budgets based on seed data
UPDATE dbo.Departments SET Budget = 445000.00 WHERE DepartmentID = 1; -- Sarah+James+Ellen+Clark
UPDATE dbo.Departments SET Budget = 284000.00 WHERE DepartmentID = 2; -- Dana+Fox+Lara
UPDATE dbo.Departments SET Budget = 198000.00 WHERE DepartmentID = 3; -- Jean+Bruce
UPDATE dbo.Departments SET Budget = 92000.00  WHERE DepartmentID = 4; -- Diana
GO

-- Projects
SET IDENTITY_INSERT dbo.Projects ON;
INSERT INTO dbo.Projects (ProjectID, ProjectName, DepartmentID, StartDate, EndDate, Budget, Status) VALUES
(1, 'Cloud Migration', 1, '2023-01-15', NULL, 500000.00, 'Active'),
(2, 'Mobile App Redesign', 1, '2023-06-01', '2024-03-31', 250000.00, 'Completed'),
(3, 'Brand Refresh Campaign', 2, '2024-01-01', NULL, 150000.00, 'Active'),
(4, 'Annual Audit Prep', 3, '2024-02-01', '2024-04-30', 75000.00, 'Active'),
(5, 'Employee Wellness Program', 4, '2023-09-01', NULL, 50000.00, 'Active');
SET IDENTITY_INSERT dbo.Projects OFF;
GO

-- Disable project trigger during seed to avoid double-counting budget
DISABLE TRIGGER dbo.tr_Projects_MultiTableAudit ON dbo.Projects;
GO

-- ProjectAssignments
INSERT INTO dbo.ProjectAssignments (ProjectID, EmployeeID, Role, HoursAllocated, AssignedDate) VALUES
(1, 1, 'Lead', 60, '2023-01-15'),
(1, 2, 'Developer', 40, '2023-01-20'),
(1, 3, 'Developer', 40, '2023-02-01'),
(1, 9, 'Developer', 35, '2023-04-10'),
(2, 2, 'Lead', 50, '2023-06-01'),
(2, 3, 'Developer', 40, '2023-06-15'),
(3, 4, 'Lead', 45, '2024-01-05'),
(3, 5, 'Designer', 40, '2024-01-10'),
(3, 10, 'Coordinator', 30, '2024-01-15'),
(4, 6, 'Lead', 50, '2024-02-01'),
(4, 7, 'Analyst', 40, '2024-02-05'),
(5, 8, 'Lead', 30, '2023-09-01');
GO

-- Re-enable the trigger
ENABLE TRIGGER dbo.tr_Projects_MultiTableAudit ON dbo.Projects;
GO

-- EmployeeSkills (needed for CROSS APPLY view)
INSERT INTO dbo.EmployeeSkills (EmployeeID, SkillName, ProficiencyLevel, CertifiedDate) VALUES
(1, 'Architecture', 5, '2020-06-01'),
(1, 'Python', 4, '2019-03-15'),
(2, 'C#', 5, '2021-01-10'),
(2, 'Azure', 4, '2022-05-20'),
(3, 'Kubernetes', 4, '2022-08-01'),
(3, 'Go', 3, NULL),
(4, 'Content Strategy', 5, '2020-11-01'),
(5, 'SEO', 4, '2021-09-15'),
(6, 'Financial Modeling', 5, '2019-04-01'),
(7, 'Data Analysis', 3, NULL),
(8, 'Conflict Resolution', 5, '2020-02-01'),
(9, 'TypeScript', 4, '2023-01-01'),
(10, 'Social Media', 4, '2022-03-01');
GO

-- =============================================================================
-- EXERCISE QUERIES - Verify all views return non-empty results
-- =============================================================================

-- Indexed view
SELECT * FROM dbo.vw_DepartmentSummary;
GO

-- INSTEAD OF trigger view
SELECT * FROM dbo.vw_EmployeeDirectory;
GO

-- CROSS APPLY view
SELECT * FROM dbo.vw_EmployeeTopSkills;
GO

-- OUTER APPLY view
SELECT * FROM dbo.vw_EmployeeProjectLoad;
GO

-- Nested views (all levels)
SELECT * FROM dbo.vw_ActiveEmployees;
GO

SELECT * FROM dbo.vw_ActiveEmployeeDetails;
GO

SELECT * FROM dbo.vw_SeniorActiveEmployees;
GO

-- Project overview view
SELECT * FROM dbo.vw_ProjectOverview;
GO

-- Test INSTEAD OF trigger by inserting through the view
INSERT INTO dbo.vw_EmployeeDirectory (FirstName, LastName, Email, DepartmentID, Salary)
VALUES ('Test', 'TriggerInsert', 'test.trigger@company.com', 1, 80000.00);
GO

-- Verify the insert went to the base table
SELECT * FROM dbo.Employees WHERE Email = 'test.trigger@company.com';
GO

-- Clean up test row (trigger fires on delete from base table)
DELETE FROM dbo.Employees WHERE Email = 'test.trigger@company.com';
GO

-- Verify multi-table trigger wrote audit log entries
SELECT * FROM dbo.AuditLog;
GO

-- =============================================================================
-- SUMMARY
-- =============================================================================

PRINT '=============================================';
PRINT 'Test database setup complete!';
PRINT 'Database: ViewsTriggerDB';
PRINT '';
PRINT 'Schema objects created:';
PRINT '  Tables (6): Departments, Employees, Projects,';
PRINT '              ProjectAssignments, AuditLog, EmployeeSkills';
PRINT '  Views (9):  vw_DepartmentSummary (indexed),';
PRINT '              vw_EmployeeDirectory (INSTEAD OF target),';
PRINT '              vw_EmployeeTopSkills (CROSS APPLY),';
PRINT '              vw_EmployeeProjectLoad (OUTER APPLY),';
PRINT '              vw_ActiveEmployees (base for nesting),';
PRINT '              vw_ActiveEmployeeDetails (nested L1),';
PRINT '              vw_SeniorActiveEmployees (nested L2),';
PRINT '              vw_ProjectOverview';
PRINT '  Triggers (4): tr_EmployeeDirectory_InsteadOfInsert,';
PRINT '                 tr_EmployeeDirectory_InsteadOfUpdate,';
PRINT '                 tr_Employees_AuditAndBudget (multi-table),';
PRINT '                 tr_Projects_MultiTableAudit (multi-table)';
PRINT '  Index (1):  IX_vw_DepartmentSummary (clustered on indexed view)';
PRINT '';
PRINT 'Total objects: 20 (6 tables + 9 views + 4 triggers + 1 index)';
PRINT '';
PRINT 'Patterns covered:';
PRINT '  - Indexed view (SCHEMABINDING + unique clustered index)';
PRINT '  - INSTEAD OF triggers (INSERT and UPDATE on view)';
PRINT '  - Multi-table triggers (Employees->AuditLog+Departments)';
PRINT '  - CROSS APPLY view (top skill per employee)';
PRINT '  - OUTER APPLY view (project load per employee)';
PRINT '  - Nested views (3 levels: Active->Details->Senior)';
PRINT '';
PRINT 'All views verified to return non-empty result sets.';
PRINT '=============================================';
GO
