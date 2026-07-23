# Requirements Document

## Introduction

This feature establishes a multi-database migration validation pipeline for the AI-Assisted Schema Conversion tool. The goal is to prove the tool reliably achieves 70%+ compatibility across diverse SQL Server databases by creating 4 additional test databases (each stressing different SQL Server features), automating the full Extract → Convert → Generate → Validate pipeline, and using failures from each database to iteratively improve the conversion engine's rules and prompts.

## Glossary

- **Pipeline**: The automated sequence of Extract → Convert → Generate → Validate steps executed against a single test database
- **Conversion_Engine**: The AI-Assisted Schema Conversion CLI tool that converts SQL Server schemas to PostgreSQL
- **Test_Database**: A SQL Server database created specifically to exercise particular SQL Server features for validation purposes
- **Compatibility_Score**: The percentage of schema objects that produce syntactically valid PostgreSQL DDL when validated against a PostgreSQL parser
- **Validation_Script**: A PowerShell or shell script that orchestrates the full pipeline for a given test database
- **Object_Pass_Rate**: The ratio of converted objects with status "converted" (confidence >= 0.7) to total convertible objects (excluding out-of-scope items)
- **Pipeline_Runner**: The automation script that executes the full Extract → Convert → Generate → Validate sequence for one or more test databases
- **Scoring_Report**: A JSON report produced by the pipeline that summarizes per-database and aggregate compatibility metrics

## Requirements

### Requirement 1: Diverse Test Database Creation

**User Story:** As a developer, I want 4 additional test databases each focusing on different SQL Server features, so that I can validate the conversion tool handles a broad range of real-world patterns.

#### Acceptance Criteria

1. THE Test_Database set SHALL include a database focused on stored procedures with complex control flow, containing at least one dedicated schema object for each of the following patterns: cursors, nested TRY/CATCH (minimum 2 levels deep), multiple result sets from a single procedure, table-valued parameters, and OUTPUT parameters with multiple assignments
2. THE Test_Database set SHALL include a database focused on complex views and triggers, containing at least one dedicated schema object for each of the following patterns: indexed views, INSTEAD OF triggers, multi-table triggers (affecting 2 or more tables), views with APPLY operators (CROSS APPLY or OUTER APPLY), and nested views referencing at least one other view
3. THE Test_Database set SHALL include a database focused on user-defined types and CLR-adjacent patterns, containing at least one dedicated schema object for each of the following patterns: table types used as procedure parameters, alias types with rules, computed columns with UDF references, schema-bound objects, and SQLCLR stubs (defined with EXTERNAL NAME referencing a placeholder assembly)
4. THE Test_Database set SHALL include a database focused on cross-schema references and advanced features, containing at least one dedicated schema object for each of the following patterns: multi-schema object dependencies (objects in 2 or more non-dbo schemas referencing each other), cross-database references, partitioned tables (with at least one partition function and scheme), row-level security (with a security predicate function), and temporal tables with system versioning
5. WHEN a Test_Database is created, THE Test_Database SHALL contain a minimum of 15 schema objects (tables, views, stored procedures, functions, and triggers combined), with at least 2 distinct object types represented
6. WHEN a Test_Database is created, THE Test_Database SHALL include seed data such that every stored procedure and view in that database returns at least one row (non-empty result set) and completes execution without raising an error at severity 16 or above when executed by a db_owner member on the source SQL Server instance
7. THE Test_Database creation scripts SHALL be idempotent by dropping and recreating the database if it already exists, such that executing the script two consecutive times against the same SQL Server instance produces an identical database without manual intervention between runs
8. WHEN a Test_Database creation script is executed against a SQL Server 2019 or later instance, THE script SHALL complete without compilation or runtime errors (severity 16 or above) and the resulting database SHALL be in ONLINE state

### Requirement 2: Automated Pipeline Execution

**User Story:** As a developer, I want to run the full Extract → Convert → Generate → Validate pipeline with a single command per database, so that I can iterate quickly without manual steps.

#### Acceptance Criteria

1. WHEN the Pipeline_Runner is invoked with a database connection string and session name, THE Pipeline_Runner SHALL execute the extract, convert, generate, and validate commands in sequence, proceeding to the next step only after the previous step exits with a zero exit code
2. IF any pipeline step returns a non-zero exit code or throws an unhandled exception, THEN THE Pipeline_Runner SHALL log the failure with the step name, error message, and elapsed time in seconds, then halt execution for that database
3. WHEN the generate step completes with a zero exit code, THE Pipeline_Runner SHALL invoke validation of the generated PostgreSQL DDL against a PostgreSQL syntax checker
4. WHEN all four pipeline steps (extract, convert, generate, validate) complete without halting, THE Pipeline_Runner SHALL produce a Scoring_Report in JSON format containing per-object pass/fail status and the aggregate Compatibility_Score
5. THE Pipeline_Runner SHALL accept a parameter to run against all configured test databases in sequence and produce a combined Scoring_Report
6. THE Pipeline_Runner SHALL record the total elapsed time in seconds for each pipeline run and include it in the Scoring_Report

### Requirement 3: Compatibility Scoring Definition

**User Story:** As a developer, I want a clear, measurable definition of "70%+ compatibility" so that I can objectively track progress and compare runs.

#### Acceptance Criteria

1. THE Scoring_Report SHALL calculate Compatibility_Score as: (number of objects classified "pass") divided by (total objects classified "pass" + "fail-syntax" + "fail-convert"), expressed as a percentage rounded to one decimal place
2. THE Scoring_Report SHALL classify each object validation result as one of: "pass" (valid PostgreSQL syntax confirmed by the Validation_Script), "fail-syntax" (PostgreSQL parser rejects the generated DDL), "fail-convert" (the Conversion_Engine step failed or returned an error for this object), or "skip" (object type is not in the convertible set of Table, View, StoredProcedure, Function, Trigger)
3. THE Scoring_Report SHALL report individual Compatibility_Scores per database and an aggregate Compatibility_Score across all databases, where the aggregate is calculated as (total "pass" objects across all databases) divided by (total "pass" + "fail-syntax" + "fail-convert" objects across all databases)
4. WHEN the aggregate Compatibility_Score falls below 70%, THE Scoring_Report SHALL list up to 5 failing object types ranked by failure count in descending order, where each entry includes the object type name and its failure count
5. THE Scoring_Report SHALL include a breakdown of scores by object type (Table, View, StoredProcedure, Function, Trigger) for each database, showing pass count, fail count, and Compatibility_Score per type
6. IF all objects in a database are classified as "skip" (zero convertible objects), THEN THE Scoring_Report SHALL report that database's Compatibility_Score as "N/A" and exclude it from the aggregate calculation

### Requirement 4: Failure-Driven Engine Improvement

**User Story:** As a developer, I want pipeline failures to produce actionable diagnostics that guide rule/prompt improvements, so that I can fix the conversion engine rather than hand-editing output.

#### Acceptance Criteria

1. WHEN an object fails validation, THE Scoring_Report SHALL include the specific PostgreSQL syntax error message, the line number where parsing failed, and the full generated DDL for that object
2. WHEN the pipeline completes, THE Scoring_Report SHALL group failures by root cause category (type mapping gap, function mapping gap, procedural pattern not handled, AI prompt deficiency, dependency resolution failure), where classification is determined by matching the error message and failed DDL context against the category definitions: "type mapping gap" when the error references an unrecognized data type, "function mapping gap" when the error references an undefined function or operator, "procedural pattern not handled" when the error occurs within a PL/pgSQL block body, "AI prompt deficiency" when conversion produced empty or placeholder output, and "dependency resolution failure" when the error references a missing prerequisite object
3. WHEN the Pipeline_Runner is invoked in "rerun-failures" mode, THE Pipeline_Runner SHALL identify objects with status "fail-syntax" or "fail-convert" from the most recent Scoring_Report for the specified session, re-convert only those objects, and preserve the existing conversion results for all other objects
4. WHEN a conversion rule file (type-mappings.json, function-mappings.json, schema-mappings.json) or prompt template file is modified (determined by file content hash change compared to the hash stored in the previous Scoring_Report), THE Pipeline_Runner SHALL re-convert objects of the types associated with the changed file on the next run, where prompt template files map to their corresponding object type (e.g., stored-procedure prompt maps to StoredProcedure objects) and mapping files apply to all object types
5. THE Scoring_Report SHALL track score progression by recording the Compatibility_Score and per-object pass/fail status for the current run alongside the scores from the immediately previous run for the same database, and SHALL display the numeric difference (delta) between current and previous Compatibility_Score per database
6. WHEN root cause grouping is complete, THE Scoring_Report SHALL rank the categories by failure count in descending order and include the count and list of affected object names for each category

### Requirement 5: Multi-Database Batch Execution

**User Story:** As a developer, I want to validate all 4 additional databases (plus the original) within a single batch execution, so that I can quickly assess overall tool quality after making changes.

#### Acceptance Criteria

1. THE Pipeline_Runner SHALL accept a configuration file listing all test databases with their connection strings, session names, and setup script paths
2. WHEN batch mode is invoked, THE Pipeline_Runner SHALL execute the pipeline for each configured database sequentially and collect results into a single combined Scoring_Report
3. WHEN batch execution completes, THE Pipeline_Runner SHALL print a summary table showing database name, object count, pass count, fail count, and Compatibility_Score for each database
4. IF a single database pipeline fails completely (e.g., connection failure), THEN THE Pipeline_Runner SHALL log the error for that database and continue with the remaining databases
5. THE Pipeline_Runner SHALL complete the full batch (5 databases) within 30 minutes under normal operating conditions (assumes local SQL Server, network access to Bedrock)

### Requirement 6: Validation Against PostgreSQL

**User Story:** As a developer, I want generated DDL validated against actual PostgreSQL syntax rules, so that "pass" means the DDL would execute successfully on a real PostgreSQL instance.

#### Acceptance Criteria

1. THE Validation_Script SHALL parse each generated DDL statement using a PostgreSQL syntax validator (either a running PostgreSQL instance or an offline parser) and classify the result as "pass" (DDL accepted without error) or "fail-syntax" (DDL rejected with a specific error message)
2. WHEN validation uses a live PostgreSQL instance, THE Validation_Script SHALL execute DDL within a transaction that is always rolled back (no persistent schema changes) and SHALL abort validation for a single statement if it does not complete within 30 seconds
3. IF a PostgreSQL instance is not available, THEN THE Validation_Script SHALL fall back to a syntax-only validation mode and record the validation_mode field in the Scoring_Report as "syntax-only" (as opposed to "live-instance") to indicate reduced confidence
4. WHEN a DDL statement contains a dependency on another object (e.g., a view referencing a table), THE Validation_Script SHALL create prerequisite objects within the same rolled-back transaction before validating the dependent object, so that dependency resolution errors are distinguished from syntax errors
5. THE Validation_Script SHALL report a per-object pass/fail result for every object regardless of whether other objects pass or fail, such that a failure in one object does not prevent validation of unrelated objects
6. IF a circular dependency is detected among DDL objects, THEN THE Validation_Script SHALL mark all objects in the cycle as "fail-syntax" with an error message indicating a circular dependency, and SHALL continue validating remaining objects not in the cycle
