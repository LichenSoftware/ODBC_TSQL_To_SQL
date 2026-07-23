# Implementation Plan: Migration Validation Pipeline

## Overview

This plan implements a multi-database migration validation pipeline that automates the Extract → Convert → Generate → Validate cycle, computes compatibility scores, and produces actionable diagnostics. The implementation uses PowerShell for pipeline orchestration and .NET (C#/FsCheck) for property-based tests. Tasks build incrementally: test database scripts first, then core library modules (scoring, diagnostics, validation), then the pipeline runner, batch execution, and finally wiring and integration.

## Tasks

- [x] 1. Create test database setup scripts
  - [x] 1.1 Create ProcedureComplexityDB setup script
    - Create `MigrationAssessment/scripts/setup-procedure-complexity-db.sql`
    - Include idempotent DROP/CREATE database pattern matching existing `setup-test-database.sql`
    - Implement schema objects: cursor-based procedure, nested TRY/CATCH (2+ levels), multiple result set procedure, table-valued parameter procedure, OUTPUT parameter procedure
    - Include at minimum 15 objects total with at least 2 distinct types (tables + stored procedures)
    - Add seed data ensuring all procedures and views return non-empty results
    - _Requirements: 1.1, 1.5, 1.6, 1.7, 1.8_

  - [x] 1.2 Create ViewsTriggerDB setup script
    - Create `MigrationAssessment/scripts/setup-views-triggers-db.sql`
    - Use idempotent DROP/CREATE database pattern
    - Implement schema objects: indexed view, INSTEAD OF trigger, multi-table trigger (2+ tables), view with CROSS APPLY or OUTER APPLY, nested view referencing another view
    - Include at minimum 15 objects with tables, views, and triggers represented
    - Add seed data ensuring all views return non-empty results
    - _Requirements: 1.2, 1.5, 1.6, 1.7, 1.8_

  - [x] 1.3 Create TypesAndCLRDB setup script
    - Create `MigrationAssessment/scripts/setup-types-clr-db.sql`
    - Use idempotent DROP/CREATE database pattern
    - Implement schema objects: table type used as procedure parameter, alias type with rule, computed column with UDF reference, schema-bound object, SQLCLR stub with EXTERNAL NAME referencing placeholder assembly
    - Include at minimum 15 objects with types, tables, and procedures represented
    - Add seed data ensuring all procedures and views return non-empty results
    - _Requirements: 1.3, 1.5, 1.6, 1.7, 1.8_

  - [x] 1.4 Create CrossSchemaAdvancedDB setup script
    - Create `MigrationAssessment/scripts/setup-cross-schema-advanced-db.sql`
    - Use idempotent DROP/CREATE database pattern
    - Implement schema objects: multi-schema dependencies (2+ non-dbo schemas referencing each other), cross-database reference, partitioned table with partition function and scheme, row-level security with predicate function, temporal table with system versioning
    - Include at minimum 15 objects with multiple schemas and types represented
    - Add seed data ensuring all procedures and views return non-empty results
    - _Requirements: 1.4, 1.5, 1.6, 1.7, 1.8_

- [x] 2. Implement Scoring Engine
  - [x] 2.1 Create Scoring Engine module
    - Create `MigrationAssessment/scripts/lib/Invoke-Scoring.ps1`
    - Implement `Invoke-Scoring` function accepting `$ObjectResults` array and `$PreviousScores` hashtable
    - Classify each object as pass, fail-syntax, fail-convert, or skip
    - Compute per-database Compatibility_Score as `(pass) / (pass + fail-syntax + fail-convert) * 100` rounded to 1 decimal
    - Compute aggregate score across databases (excluding N/A databases where all objects are skip)
    - Generate per-type breakdowns (Table, View, StoredProcedure, Function, Trigger) with pass/fail counts and per-type score
    - Compute score deltas from previous run
    - Report "N/A" for databases with zero convertible objects
    - List top 5 failing types ranked by failure count when aggregate score < 70%
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 4.5_

  - [x] 2.2 Write property tests for Compatibility Score computation
    - Create .NET test project `MigrationAssessment/tests/MigrationAssessment.Pipeline.PropertyTests/`
    - Add FsCheck NuGet dependency
    - Implement property test for scoring formula with random object result sets
    - **Property 3: Compatibility Score Computation**
    - **Validates: Requirements 3.1, 3.3, 3.6**

  - [x] 2.3 Write property test for Object Classification Correctness
    - Implement FsCheck property test verifying each object is classified into exactly one status
    - Generate random objects with varying types and validation outcomes
    - **Property 4: Object Classification Correctness**
    - **Validates: Requirements 3.2**

  - [x] 2.4 Write property test for Per-Type Score Breakdown Consistency
    - Implement FsCheck property test verifying sum of per-type counts equals database totals
    - Verify per-type Compatibility_Score computation is correct for each type
    - **Property 5: Per-Type Score Breakdown Consistency**
    - **Validates: Requirements 3.5**

  - [x] 2.5 Write property test for Top Failing Types Below Threshold
    - Implement FsCheck property test generating result sets with aggregate < 70%
    - Verify up to 5 types are listed, ranked by failure count descending
    - **Property 6: Top Failing Types Below Threshold**
    - **Validates: Requirements 3.4**

  - [x] 2.6 Write property test for Score Progression Delta
    - Implement FsCheck property test with random current and previous scores
    - Verify delta = current − previous for each database
    - **Property 11: Score Progression Delta**
    - **Validates: Requirements 4.5**

- [x] 3. Implement Diagnostics Classifier
  - [x] 3.1 Create Diagnostics Classifier module
    - Create `MigrationAssessment/scripts/lib/Invoke-DiagnosticsClassification.ps1`
    - Implement `Invoke-DiagnosticsClassification` function accepting array of failed object results
    - Define regex patterns for 5 root cause categories: type mapping gap, function mapping gap, procedural pattern not handled, AI prompt deficiency, dependency resolution failure
    - Classify each failure into exactly one category via pattern matching
    - Group failures by category, rank by failure count descending
    - Include affected object names per category
    - Include error message, line number, and generated DDL for each failed object
    - _Requirements: 4.1, 4.2, 4.6_

  - [x] 3.2 Write property test for Failure Diagnostics Completeness
    - Implement FsCheck property test generating random failed objects with error messages
    - Verify every failed object has error message, line number, and DDL in the report
    - **Property 7: Failure Diagnostics Completeness**
    - **Validates: Requirements 4.1**

  - [x] 3.3 Write property test for Root Cause Classification
    - Implement FsCheck property test generating random error messages matching category patterns
    - Verify each failure is classified into exactly one category
    - Verify categories are ranked by failure count descending
    - **Property 8: Root Cause Classification**
    - **Validates: Requirements 4.2, 4.6**

- [x] 4. Implement PostgreSQL Validator
  - [x] 4.1 Create PostgreSQL Validator module
    - Create `MigrationAssessment/scripts/lib/Invoke-PgValidation.ps1`
    - Implement `Invoke-PgValidation` function accepting `$DdlStatements` array, optional `$PgConnectionString`, and `$TimeoutSeconds` (default 30)
    - Implement live-instance validation: execute DDL in a rolled-back transaction
    - Implement syntax-only fallback when no PG instance is available
    - Implement dependency resolution: create prerequisite objects within the same transaction before validating dependent objects
    - Implement topological sort for dependency ordering
    - Detect circular dependencies and mark all cycle members as "fail-syntax" with circular dependency error
    - Enforce 30-second timeout per statement
    - Return per-object results independently (one failure does not block others)
    - Record `validationMode` ("live-instance" or "syntax-only") in results
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [x] 4.2 Write property test for Validation Isolation
    - Implement FsCheck property test generating sets of DDL objects with random pass/fail outcomes
    - Verify each object receives an independent result regardless of other objects' outcomes
    - **Property 16: Validation Isolation**
    - **Validates: Requirements 6.5**

  - [x] 4.3 Write property test for Circular Dependency Detection
    - Implement FsCheck property test generating dependency graphs with and without cycles
    - Verify cycle members are marked fail-syntax with circular dependency message
    - Verify non-cycle objects are validated normally
    - **Property 17: Circular Dependency Detection**
    - **Validates: Requirements 6.6**

- [x] 5. Checkpoint - Ensure all core modules work
  - Ensure all tests pass, ask the user if questions arise.

- [x] 6. Implement Pipeline Runner
  - [x] 6.1 Create Pipeline Runner script
    - Create `MigrationAssessment/scripts/Run-MigrationPipeline.ps1`
    - Implement parameter parsing: `-ConnectionString`, `-SessionName`, `-BatchConfig`, `-RerunFailures`, `-ValidationMode`, `-PgConnectionString`
    - Implement sequential step execution: extract → convert → generate → validate via `dotnet run` against SchemaConversion.Cli
    - Track elapsed time per step and total
    - Halt on non-zero exit code with structured logging (step name, error message, elapsed seconds)
    - Produce Scoring Report JSON on successful completion by calling `Invoke-Scoring`, `Invoke-DiagnosticsClassification`, and `Invoke-PgValidation`
    - Record config file hashes (SHA-256) for type-mappings.json, function-mappings.json, schema-mappings.json, and prompt templates
    - Save report to `./pipeline-reports/` directory
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.6_

  - [x] 6.2 Implement rerun-failures mode
    - Read most recent Scoring Report for the specified session
    - Identify objects with status "fail-syntax" or "fail-convert"
    - Re-convert only those objects, preserving existing pass/skip results
    - Merge re-converted results into new Scoring Report
    - _Requirements: 4.3_

  - [x] 6.3 Implement change detection for rule/prompt files
    - Compare current SHA-256 hashes of conversion rule files and prompt templates against hashes stored in previous Scoring Report
    - When a file hash differs, re-convert objects of the associated type(s)
    - Prompt templates map to their corresponding object type; mapping files apply to all object types
    - _Requirements: 4.4_

  - [x] 6.4 Write property test for Selective Re-run of Failures
    - Implement FsCheck property test generating sessions with mixed pass/fail results
    - Verify only failed objects are re-converted and pass/skip objects are preserved
    - **Property 9: Selective Re-run of Failures**
    - **Validates: Requirements 4.3**

  - [x] 6.5 Write property test for Change Detection Triggers Re-conversion
    - Implement FsCheck property test generating hash comparison scenarios
    - Verify modified files trigger re-conversion of associated object types
    - **Property 10: Change Detection Triggers Re-conversion**
    - **Validates: Requirements 4.4**

- [x] 7. Implement Batch Execution
  - [x] 7.1 Create pipeline configuration file
    - Create `MigrationAssessment/pipeline-config.json`
    - Define entries for all 5 test databases with connection strings, session names, and setup script paths
    - Include validation settings (pgConnectionString, timeoutSeconds, fallbackToSyntaxOnly)
    - Include reporting settings (outputDirectory, trackProgression)
    - _Requirements: 5.1_

  - [x] 7.2 Implement Batch Orchestrator in Pipeline Runner
    - Add `-BatchConfig` parameter handling to `Run-MigrationPipeline.ps1`
    - Parse `pipeline-config.json` and iterate over each database entry sequentially
    - On connection failure or complete database failure, log error and continue with remaining databases
    - Produce combined Scoring Report with results from all databases
    - Print summary table at completion: database name, object count, pass count, fail count, Compatibility_Score
    - Show "ERROR" for databases that failed completely
    - Set exit code: non-zero if any database fails completely, zero if all produce a Scoring Report
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5_

  - [x] 7.3 Write property test for Batch Resilience on Database Failure
    - Implement FsCheck property test generating batch configurations with random failures
    - Verify failed databases are logged and remaining databases continue
    - **Property 13: Batch Resilience on Database Failure**
    - **Validates: Requirements 5.4**

  - [x] 7.4 Write property test for Batch Summary Table Completeness
    - Implement FsCheck property test generating completed batch results
    - Verify summary contains all required fields for each processed database
    - **Property 14: Batch Summary Table Completeness**
    - **Validates: Requirements 5.3**

- [x] 8. Checkpoint - Ensure pipeline executes end-to-end
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Create Report Generator and wire components together
  - [x] 9.1 Create Report Generator module
    - Create `MigrationAssessment/scripts/lib/Invoke-ReportGeneration.ps1`
    - Implement Scoring Report JSON serialization matching the schema defined in the design document
    - Include reportId (UUID), timestamp (ISO-8601), totalElapsedSeconds, validationMode, configHashes
    - Include per-database results with scores, byType breakdowns, and per-object details
    - Include aggregate scores with delta from previous run
    - Include diagnostics section with rootCauseCategories and topFailingTypes
    - Save reports to configurable output directory
    - Support loading previous report for delta computation
    - _Requirements: 2.4, 3.1, 3.3, 3.5, 4.1, 4.5_

  - [x] 9.2 Wire all modules into Pipeline Runner
    - Import `Invoke-Scoring`, `Invoke-DiagnosticsClassification`, `Invoke-PgValidation`, and `Invoke-ReportGeneration` into the Pipeline Runner
    - Connect pipeline step outputs to validation inputs
    - Connect validation outputs to scoring inputs
    - Connect scoring outputs and failed objects to diagnostics inputs
    - Connect all results to report generation
    - Verify end-to-end data flow from extract through to JSON report output
    - _Requirements: 2.1, 2.4, 3.1, 4.1, 4.2_

- [x] 10. Write Pester unit tests for core modules
  - [x] 10.1 Write Pester unit tests for Scoring Engine
    - Create `MigrationAssessment/tests/Pipeline.Tests/Invoke-Scoring.Tests.ps1`
    - Test scoring formula with known inputs (e.g., pass=7, fail-syntax=2, fail-convert=1 → 70.0%)
    - Test N/A handling for all-skip databases
    - Test per-type breakdown correctness
    - Test delta computation
    - _Requirements: 3.1, 3.3, 3.5, 3.6, 4.5_

  - [x] 10.2 Write Pester unit tests for Diagnostics Classifier
    - Create `MigrationAssessment/tests/Pipeline.Tests/Invoke-DiagnosticsClassification.Tests.ps1`
    - Test each regex category pattern with sample error messages
    - Test ranking by failure count
    - Test affected object names list
    - _Requirements: 4.1, 4.2, 4.6_

  - [x] 10.3 Write Pester unit tests for PostgreSQL Validator
    - Create `MigrationAssessment/tests/Pipeline.Tests/Invoke-PgValidation.Tests.ps1`
    - Test dependency ordering (topological sort)
    - Test circular dependency detection
    - Test timeout behavior
    - Test fallback mode selection
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [x] 10.4 Write Pester unit tests for Report Generator
    - Create `MigrationAssessment/tests/Pipeline.Tests/Invoke-ReportGeneration.Tests.ps1`
    - Test JSON structure matches expected schema
    - Test config hash computation
    - Test previous report loading for delta calculation
    - _Requirements: 2.4, 4.5_

- [x] 11. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties using FsCheck (.NET)
- Unit tests use Pester (PowerShell testing framework) for pipeline module testing
- The pipeline wraps the existing `SchemaConversion.Cli` (in `AI-AssistedSchemaConversion/src/SchemaConversion.Cli/`)
- Test database scripts follow the same idempotent pattern as the existing `setup-test-database.sql`
- All PowerShell library modules go in `MigrationAssessment/scripts/lib/` directory

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2", "1.3", "1.4"] },
    { "id": 1, "tasks": ["2.1", "3.1", "4.1"] },
    { "id": 2, "tasks": ["2.2", "2.3", "2.4", "2.5", "2.6", "3.2", "3.3", "4.2", "4.3"] },
    { "id": 3, "tasks": ["6.1", "7.1"] },
    { "id": 4, "tasks": ["6.2", "6.3", "9.1"] },
    { "id": 5, "tasks": ["6.4", "6.5", "7.2"] },
    { "id": 6, "tasks": ["7.3", "7.4", "9.2"] },
    { "id": 7, "tasks": ["10.1", "10.2", "10.3", "10.4"] }
  ]
}
```
