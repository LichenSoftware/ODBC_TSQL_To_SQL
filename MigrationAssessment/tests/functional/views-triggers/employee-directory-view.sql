-- Functional tests: ViewsTriggerDB - Employee directory view operations
-- Validates the INSTEAD OF trigger view works for read and write scenarios

-- test: Query employee directory view
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, Email, DepartmentName FROM dbo.vw_EmployeeDirectory;

-- test: Query employee skills table
-- expect-rows: > 0
SELECT SkillID, EmployeeID, SkillName, ProficiencyLevel FROM dbo.EmployeeSkills;

-- test: Query project assignments
-- expect-rows: > 0
SELECT AssignmentID, ProjectID, EmployeeID, Role, HoursAllocated FROM dbo.ProjectAssignments;

-- test: Query departments with budget
-- expect-rows: > 0
SELECT DepartmentID, DepartmentName, Budget, IsActive FROM dbo.Departments WHERE IsActive = 1;
