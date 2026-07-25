# Requirements Document

## Introduction

Extend the Migration Validation Pipeline to perform end-to-end integration testing that mirrors the full human migration workflow. The current pipeline runs Extract → Convert → Generate → Validate (DDL syntax check only). This feature adds DDL application to a real PostgreSQL database, an AI-assisted iterative fix loop for application failures, data replication from SQL Server to PostgreSQL, functional testing via PgPassthrough, and runtime-based scoring. The goal is to produce a comprehensive validation score that reflects actual migration success — not just DDL correctness.

## Glossary

- **Pipeline**: The PowerShell orchestration script (Run-MigrationPipeline.ps1) that sequences migration steps
- **DDL_Applicator**: The component that executes generated PostgreSQL DDL against the destination database
- **Fix_Loop**: The iterative AI-assisted correction cycle that submits failed DDL + error messages to Bedrock for repair
- **ConversionReviewer**: The existing .NET tool that invokes AWS Bedrock to correct failed DDL scripts
- **DataMigrator**: The existing .NET CLI tool that replicates table data from SQL Server to PostgreSQL using session metadata
- **PgPassthrough**: The T-SQL-to-PostgreSQL proxy that allows running T-SQL test scripts against a PostgreSQL backend
- **Functional_Test_Runner**: The component that executes T-SQL test scripts through PgPassthrough and captures results
- **End_To_End_Score**: A composite metric reflecting DDL application success, data migration success, and functional test pass rate
- **Destination_Database**: The PostgreSQL database instance where DDL is applied and data is migrated
- **Source_Database**: The SQL Server database instance being migrated
- **Test_Script**: A T-SQL script containing queries and assertions that validate migrated database behavior
- **Fix_Attempt**: A single iteration of the AI-assisted correction cycle (submit error → receive fix → re-apply)
- **Session**: The working directory containing extracted objects, conversion results, and generated DDL for a database

## Requirements

### Requirement 1: DDL Application to Destination Database

**User Story:** As a migration engineer, I want the pipeline to apply generated DDL to a real PostgreSQL database, so that I can verify the schema actually creates successfully — not just that it passes syntax checks.

#### Acceptance Criteria

1. WHEN the Validate step completes, THE Pipeline SHALL execute a new Apply step that applies each generated DDL statement to the Destination_Database in dependency order
2. WHEN a DDL statement applies successfully, THE DDL_Applicator SHALL record the object name, elapsed time, and "applied" status in the step results
3. IF a DDL statement fails to apply, THEN THE DDL_Applicator SHALL capture the PostgreSQL error message and error position, and record a "failed" status for that object
4. THE DDL_Applicator SHALL create a fresh schema (or drop and recreate the target database) before applying DDL to ensure isolation between pipeline runs
5. WHEN a PostgreSQL connection is unavailable for the Destination_Database, THE Pipeline SHALL skip the Apply step and all subsequent end-to-end steps, falling back to syntax-only scoring

### Requirement 2: AI-Assisted Iterative Fix Loop

**User Story:** As a migration engineer, I want the pipeline to automatically attempt AI-powered corrections when DDL application fails, so that human intervention is eliminated for fixable errors.

#### Acceptance Criteria

1. WHEN a DDL statement fails to apply, THE Fix_Loop SHALL submit the failed DDL and the PostgreSQL error message to the ConversionReviewer BedrockFixService for correction
2. WHEN the ConversionReviewer returns a corrected DDL script, THE Fix_Loop SHALL re-apply the corrected script to the Destination_Database
3. IF the corrected DDL also fails to apply, THEN THE Fix_Loop SHALL repeat the submission with the new error message, up to a configurable maximum of Fix_Attempts (default: 2)
4. WHEN a Fix_Attempt succeeds, THE Fix_Loop SHALL record the number of attempts taken, the final working DDL, and the AI-provided explanation
5. IF all Fix_Attempts are exhausted without success, THEN THE Fix_Loop SHALL record the object as "unfixable" with the last error message and all attempted corrections
6. THE Fix_Loop SHALL include the original T-SQL source definition in each submission to the ConversionReviewer to provide conversion context
7. WHILE the Fix_Loop is processing an object, THE Pipeline SHALL log each attempt number, the error submitted, and whether the fix succeeded

### Requirement 3: Data Migration Step

**User Story:** As a migration engineer, I want the pipeline to replicate data from the SQL Server source to the PostgreSQL destination after DDL is applied, so that functional tests can run against realistic data.

#### Acceptance Criteria

1. WHEN the Apply step completes with at least one table successfully applied, THE Pipeline SHALL execute a DataMigration step that invokes the DataMigrator tool
2. THE DataMigrator SHALL receive the Source_Database connection string, the Destination_Database connection string, and the session path as parameters
3. WHEN a table fails to migrate, THE DataMigrator SHALL log the table name and error, and continue migrating remaining tables
4. WHEN data migration completes, THE Pipeline SHALL record the total rows migrated, the number of tables succeeded, the number of tables failed, and the elapsed time
5. IF no tables were successfully applied in the Apply step, THEN THE Pipeline SHALL skip the DataMigration step and record a "skipped" status

### Requirement 4: Functional Testing via PgPassthrough

**User Story:** As a migration engineer, I want the pipeline to execute T-SQL test scripts through PgPassthrough against the migrated database, so that I can verify the migration preserves runtime behavior — not just schema structure.

#### Acceptance Criteria

1. WHEN data migration completes, THE Pipeline SHALL execute a FunctionalTest step that runs T-SQL Test_Scripts through PgPassthrough against the Destination_Database
2. THE Functional_Test_Runner SHALL start the PgPassthrough server, configure it to point at the Destination_Database, and execute each Test_Script as a T-SQL batch
3. WHEN a Test_Script executes successfully and returns expected results, THE Functional_Test_Runner SHALL record the script name and "pass" status
4. IF a Test_Script fails (connection error, translation error, or unexpected result), THEN THE Functional_Test_Runner SHALL record the script name, "fail" status, and the error details
5. THE Functional_Test_Runner SHALL execute Test_Scripts with a configurable timeout per script (default: 30 seconds)
6. WHEN no Test_Scripts are configured for a database, THE Pipeline SHALL skip the FunctionalTest step and record a "skipped" status
7. THE Pipeline SHALL discover Test_Scripts from a configurable directory path per database (default: `tests/functional/{database-name}/`)

### Requirement 5: End-to-End Scoring

**User Story:** As a migration engineer, I want a composite score that reflects the success of the entire migration — schema application, data transfer, and runtime behavior — so that I can measure true migration readiness.

#### Acceptance Criteria

1. THE Pipeline SHALL compute an End_To_End_Score as a weighted composite: DDL Application rate (40%), Data Migration rate (30%), and Functional Test pass rate (30%)
2. WHEN all end-to-end steps complete, THE Pipeline SHALL include the End_To_End_Score alongside the existing Compatibility Score in the Scoring Report
3. THE Scoring Report SHALL include per-step breakdowns: DDL application results (pass/fail/fixed per object), data migration results (rows per table, failures), and functional test results (pass/fail per script)
4. WHEN the Fix_Loop repairs an object successfully, THE End_To_End_Score SHALL count that object as "applied" for scoring purposes (crediting the AI fix)
5. THE Scoring Report SHALL separately report "applied-first-try" and "applied-after-fix" counts to distinguish conversion quality from AI repair effectiveness
6. IF end-to-end steps are skipped (no PostgreSQL connection), THEN THE Pipeline SHALL report only the existing Compatibility Score and indicate that the End_To_End_Score is unavailable

### Requirement 6: Pipeline Configuration for End-to-End Mode

**User Story:** As a migration engineer, I want to configure the end-to-end pipeline behavior through the existing pipeline-config.json, so that I can control destination databases, fix loop limits, and test script locations without modifying code.

#### Acceptance Criteria

1. THE Pipeline SHALL accept a new configuration section `endToEnd` in pipeline-config.json that specifies the Destination_Database connection string, maximum Fix_Attempts, PgPassthrough executable path, and test script directories
2. WHEN the `endToEnd` section is absent from configuration, THE Pipeline SHALL execute only the existing Extract → Convert → Generate → Validate flow (backward compatible)
3. THE Pipeline SHALL accept an `-EndToEnd` switch parameter that enables end-to-end mode for single-database runs when a PgConnectionString is provided
4. WHEN running in batch mode, THE Pipeline SHALL support per-database destination connection strings in the `endToEnd.databases` array
5. THE Pipeline SHALL validate all end-to-end configuration values at startup and report clear error messages for missing or invalid settings before beginning execution

### Requirement 7: End-to-End Report Persistence

**User Story:** As a migration engineer, I want end-to-end results persisted in the same pipeline-reports directory with full detail, so that I can track progression over time and identify which AI fixes are durable.

#### Acceptance Criteria

1. THE Pipeline SHALL extend the existing scoring-report JSON format with an `endToEnd` section containing DDL application results, data migration results, functional test results, and the composite End_To_End_Score
2. WHEN computing delta scores between runs, THE Pipeline SHALL compare both the existing Compatibility Score and the End_To_End_Score against the previous report
3. THE Pipeline SHALL record Fix_Loop details for each fixed object: number of attempts, the original error, and the final corrected DDL
4. THE Pipeline SHALL include timing data for each end-to-end step (apply elapsed, fix loop elapsed, data migration elapsed, functional test elapsed)
