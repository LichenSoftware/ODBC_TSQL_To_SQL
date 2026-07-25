-- Functional tests: ViewsTriggerDB - View queries
-- Validates that various view patterns work correctly after migration

-- test: Query indexed view vw_DepartmentSummary
-- expect-rows: > 0
SELECT DepartmentID, DepartmentName, EmployeeCount, TotalSalary FROM dbo.vw_DepartmentSummary;

-- test: Query CROSS APPLY view vw_EmployeeTopSkills
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, TopSkillName, TopSkillLevel FROM dbo.vw_EmployeeTopSkills;

-- test: Query OUTER APPLY view vw_EmployeeProjectLoad
-- expect-rows: > 0
SELECT EmployeeID, FullName, DepartmentName, ProjectCount, TotalHoursAllocated FROM dbo.vw_EmployeeProjectLoad;

-- test: Query nested view vw_ActiveEmployees
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName, Salary FROM dbo.vw_ActiveEmployees;

-- test: Query nested view vw_ActiveEmployeeDetails
-- expect-rows: > 0
SELECT EmployeeID, FullName, DepartmentName, YearsOfService FROM dbo.vw_ActiveEmployeeDetails;

-- test: Query nested view vw_SeniorActiveEmployees
-- expect-rows: > 0
SELECT EmployeeID, FullName, DepartmentName, Salary FROM dbo.vw_SeniorActiveEmployees;

-- test: Query vw_ProjectOverview
-- expect-rows: > 0
SELECT ProjectID, ProjectName, DepartmentName, TeamSize, TotalHoursAllocated FROM dbo.vw_ProjectOverview;
