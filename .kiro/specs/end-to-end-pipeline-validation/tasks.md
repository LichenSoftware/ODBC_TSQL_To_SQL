# Tasks

## Task 1: Add `fix` command to SchemaConversion.Cli

- [x] Add a new `BuildFixCommand()` method in `Program.cs` that accepts `--failed-ddl`, `--error`, `--source-tsql`, and `--max-attempts` (default: 2) options
- [x] Wire the command to invoke `BedrockFixService.RequestFixAsync()` in a loop: apply fix → if fails → resubmit with new error → repeat up to max-attempts
- [x] Apply the DDL against PostgreSQL (using a `--pg-connection` option) to determine success/failure at each attempt
- [x] Output JSON result to stdout: `{ "success": bool, "fixedDdl": "...", "attempts": int, "explanation": "...", "errors": [...] }`
- [x] Register the `fix` command on the root command in `Main()`
- [x] Add `BedrockFixService` and `Npgsql` to the DI container for the fix command path

**Requirements:** 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7

## Task 2: Create `Invoke-DdlApplication.ps1` PowerShell module

- [x] Create `MigrationAssessment/scripts/lib/Invoke-DdlApplication.ps1`
- [x] Implement `Invoke-DdlApplication` function accepting: `-DdlStatements` (array of objects with objectName, objectType, ddl, dependencies), `-PgConnectionString`, `-MaintenanceConnectionString`, `-DatabaseName`
- [x] Drop and recreate the destination database using the maintenance connection (connect to `postgres` db, `DROP DATABASE IF EXISTS`, `CREATE DATABASE`)
- [x] Build dependency graph and apply DDL in topological order (tables → views → functions → procedures → triggers)
- [x] For each object: execute DDL, record objectName, status (applied/failed), errorMessage, elapsedMs
- [x] Return array of results: `@{ objectName; status; errorMessage; elapsedMs }`
- [x] Handle connection failures gracefully — return error result without crashing pipeline

**Requirements:** 1.1, 1.2, 1.3, 1.4, 1.5

## Task 3: Create `Invoke-FixLoop.ps1` PowerShell module

- [x] Create `MigrationAssessment/scripts/lib/Invoke-FixLoop.ps1`
- [x] Implement `Invoke-FixLoop` function accepting: `-FailedObjects` (array with objectName, ddl, errorMessage, sourceTSql), `-PgConnectionString`, `-MaxAttempts` (default: 2), `-CliProjectPath`
- [x] For each failed object: invoke `dotnet run --project <CliProjectPath> -- fix --failed-ddl "..." --error "..." --source-tsql "..." --pg-connection "..." --max-attempts N`
- [x] Parse JSON output from the fix command
- [x] Return array of results: `@{ objectName; finalStatus (fixed/unfixable); attempts; fixedDdl; explanation; errors }`
- [x] Log each attempt number and status using `Write-PipelineLog`
- [x] Quote arguments containing spaces (reuse the same quoting pattern from `Invoke-PipelineStep`)

**Requirements:** 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7

## Task 4: Create `Invoke-DataMigration.ps1` PowerShell module

- [x] Create `MigrationAssessment/scripts/lib/Invoke-DataMigration.ps1`
- [x] Implement `Invoke-DataMigration` function accepting: `-SourceConnectionString`, `-TargetConnectionString`, `-SessionPath`, `-DataMigratorProjectPath`
- [x] Invoke DataMigrator as subprocess: `dotnet run --project <path> -- --source "..." --target "..." --session "..." --truncate --disable-fk --reseed`
- [x] Parse stdout to extract: tables succeeded, tables failed, total rows, elapsed time
- [x] Return: `@{ tablesSucceeded; tablesFailed; totalRows; elapsed; rawOutput }`
- [x] If no tables exist in session (Apply step skipped everything), return early with skipped status
- [x] Handle process timeout (120 second default) and capture stderr on failure

**Requirements:** 3.1, 3.2, 3.3, 3.4, 3.5

## Task 5: Create `Invoke-FunctionalTests.ps1` PowerShell module

- [x] Create `MigrationAssessment/scripts/lib/Invoke-FunctionalTests.ps1`
- [x] Implement `Invoke-FunctionalTests` function accepting: `-TestScriptDirectory`, `-PgPassthroughProjectPath`, `-PgPassthroughPort` (default: 11433), `-DestPgConnectionString`, `-TimeoutPerScript` (default: 30)
- [x] Start PgPassthrough.Server as background process configured to point at the destination PostgreSQL database
- [x] Poll TCP port until PgPassthrough is accepting connections (timeout: 15 seconds)
- [x] Discover test scripts from `TestScriptDirectory` (*.sql files)
- [x] For each test script: connect to PgPassthrough via SqlClient (TDS), execute T-SQL batch, parse assertions (see test script format below)
- [x] Record per-test results: scriptName, testName, status (pass/fail), errorMessage, elapsed
- [x] Stop PgPassthrough process after all tests complete
- [x] Return: `@{ total; passed; failed; results[] }`
- [x] If no test scripts found, return skipped status

**Test script assertion format:**
- `-- test: <name>` — names the test
- `-- expect-rows: > 0` / `-- expect-rows: 5` — assert row count
- `-- expect-value: <value>` — assert scalar result equals value
- `-- expect-no-error` — assert query executes without error

**Requirements:** 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7

## Task 6: Create `Invoke-EndToEndScoring.ps1` PowerShell module

- [x] Create `MigrationAssessment/scripts/lib/Invoke-EndToEndScoring.ps1`
- [x] Implement `Invoke-EndToEndScoring` function accepting: `-DdlResults`, `-FixResults`, `-DataMigrationResults`, `-FunctionalTestResults`, `-Weights` (hashtable: ddl=0.4, data=0.3, test=0.3)
- [x] Compute DDL_Rate: (applied + fixed) / total_objects
- [x] Compute Data_Rate: tables_migrated / total_tables
- [x] Compute Test_Rate: tests_passed / total_tests
- [x] Compute composite: (DDL_Weight × DDL_Rate) + (Data_Weight × Data_Rate) + (Test_Weight × Test_Rate) × 100
- [x] If functional tests were skipped, re-weight: DDL=57%, Data=43%
- [x] If data migration was skipped, return DDL rate only as E2E score
- [x] Return: `@{ endToEndScore; ddlRate; dataRate; testRate; appliedFirstTry; appliedAfterFix; unfixable }`

**Requirements:** 5.1, 5.2, 5.4, 5.5, 5.6

## Task 7: Integrate end-to-end steps into Run-MigrationPipeline.ps1

- [x] Add new parameters to the script: `-EndToEnd` (switch), `-MaxFixAttempts` (int, default: 2), `-DestPgConnectionString` (string), `-PgPassthroughPort` (int, default: 11433)
- [x] Dot-source the new lib modules: `Invoke-DdlApplication.ps1`, `Invoke-FixLoop.ps1`, `Invoke-DataMigration.ps1`, `Invoke-FunctionalTests.ps1`, `Invoke-EndToEndScoring.ps1`
- [x] After existing Step 4 (Validate), if `-EndToEnd` is enabled or `endToEnd` config is present:
  - Call `Invoke-DdlApplication` with DDL statements from the session
  - Collect failed objects, call `Invoke-FixLoop` for any failures
  - Call `Invoke-DataMigration` if at least one table applied/fixed
  - Call `Invoke-FunctionalTests` if data migration succeeded and test scripts exist
  - Call `Invoke-EndToEndScoring` to compute composite score
- [x] Add step result entries for: "apply", "fix-loop", "data-migration", "functional-tests"
- [x] When `endToEnd` config is absent and `-EndToEnd` switch is not set, skip all new steps (backward compatible)
- [x] Validate end-to-end configuration at startup: check destination connection string is provided, report clear errors for missing settings

**Requirements:** 6.1, 6.2, 6.3, 6.5, 1.5

## Task 8: Extend pipeline-config.json schema for end-to-end configuration

- [x] Add `endToEnd` section to pipeline-config.json with: `enabled`, `destinationConnectionString`, `maintenanceConnectionString`, `maxFixAttempts`, `pgPassthroughPath`, `pgPassthroughPort`, `testScriptDirectory`, `timeoutPerScript`, `scoring` (weights)
- [x] Add per-database overrides in `endToEnd.databases` object: `destinationDatabase`, `testScripts` path
- [x] Update `Invoke-SingleDatabasePipeline` to read per-database E2E config
- [x] Implement config validation logic that runs at pipeline startup

**Requirements:** 6.1, 6.2, 6.4, 6.5

## Task 9: Extend scoring report JSON with end-to-end section

- [x] Modify `Invoke-ReportGeneration.ps1` to accept end-to-end results
- [x] Add `endToEnd` section to report JSON: `enabled`, `endToEndScore`, `previousEndToEndScore`, `endToEndDelta`, `ddlApplication` (total, appliedFirstTry, appliedAfterFix, unfixable, rate), `fixLoop` (details per object), `dataMigration` (tables, rows, rate), `functionalTests` (total, passed, failed, rate, per-script results), `timing` (per-step elapsed)
- [x] When loading previous report for delta comparison, compare both Compatibility Score and End_To_End_Score
- [x] Include fix loop details: attempts, original error, final corrected DDL per object

**Requirements:** 7.1, 7.2, 7.3, 7.4, 5.2, 5.3, 5.5

## Task 10: Create initial functional test scripts for pipeline databases

- [x] Create directory structure: `MigrationAssessment/tests/functional/{database-name}/`
- [x] Write test scripts for AssessmentTestDB: basic SELECT queries, INSERT/UPDATE operations, view queries
- [x] Write test scripts for ProcedureComplexityDB: EXEC stored procedures, cursor-based proc calls, OUTPUT parameter tests
- [x] Write test scripts for ViewsTriggerDB: view queries, trigger-fire validation (INSERT that triggers audit)
- [x] Write test scripts for TypesAndCLRDB: queries exercising various data types
- [x] Write test scripts for CrossSchemaAdvancedDB: cross-schema queries, multi-schema JOINs
- [x] Each script uses the assertion format: `-- test:`, `-- expect-rows:`, `-- expect-value:`, `-- expect-no-error`

**Requirements:** 4.7, 4.1

## Task 11: Update pipeline-guide.md with end-to-end documentation

- [x] Add a new section "Step 5: End-to-End Validation" documenting the enhanced workflow
- [x] Document the `-EndToEnd` switch and related parameters
- [x] Document the `endToEnd` configuration section in pipeline-config.json
- [x] Document the test script assertion format
- [x] Document the End-to-End Score formula and interpretation
- [x] Add troubleshooting entries for: Bedrock unavailable, PgPassthrough won't start, destination database connection failures
- [x] Update the batch summary table example to show E2E scores

**Requirements:** 6.1, 6.3, 5.1
