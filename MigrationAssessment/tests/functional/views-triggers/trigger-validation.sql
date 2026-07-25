-- Functional tests: ViewsTriggerDB - Trigger fire validation
-- Validates that triggers fire correctly on INSERT/UPDATE operations

-- test: Insert via INSTEAD OF trigger view
-- expect-no-error
INSERT INTO dbo.vw_EmployeeDirectory (FirstName, LastName, Email, DepartmentID, Salary)
VALUES ('Trigger', 'TestUser', 'trigger.test@company.com', 1, 75000.00);

-- test: Verify INSTEAD OF trigger routed insert to base table
-- expect-rows: > 0
SELECT EmployeeID, FirstName, LastName FROM dbo.Employees WHERE Email = 'trigger.test@company.com';

-- test: Verify multi-table audit trigger logged the insert
-- expect-rows: > 0
SELECT AuditID, TableName, Operation, RecordID FROM dbo.AuditLog WHERE TableName = 'Employees' AND Operation = 'INSERT';

-- test: Update employee salary to trigger audit
-- expect-no-error
UPDATE dbo.Employees SET Salary = 80000.00 WHERE Email = 'trigger.test@company.com';

-- test: Verify audit log captured the update
-- expect-rows: > 0
SELECT AuditID, TableName, Operation, OldValues, NewValues FROM dbo.AuditLog WHERE TableName = 'Employees' AND Operation = 'UPDATE';

-- test: Clean up test employee
-- expect-no-error
DELETE FROM dbo.Employees WHERE Email = 'trigger.test@company.com';
