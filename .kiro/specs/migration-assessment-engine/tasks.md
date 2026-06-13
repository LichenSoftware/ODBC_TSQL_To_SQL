# Implementation Plan: Migration Assessment Engine

## Overview

Implement a C# (.NET 8) command-line Migration Assessment Engine that connects to SQL Server, collects workload data from Query Store, Extended Events, and metadata, parses T-SQL via ScriptDom, scores statements for PostgreSQL migration risk, and generates JSON + summary reports. The implementation follows the pipeline architecture defined in the design: Collection → Parsing → Analysis → Scoring → Reporting.

## Tasks

- [x] 1. Set up project structure and core interfaces
  - [x] 1.1 Create solution and project structure
    - Create `MigrationAssessment/` directory at solution root
    - Create projects: `MigrationAssessment.Cli`, `MigrationAssessment.Core`, `MigrationAssessment.Collectors`, `MigrationAssessment.Analysis`, `MigrationAssessment.Reporting`
    - Create test projects: `MigrationAssessment.Core.Tests`, `MigrationAssessment.Collectors.Tests`, `MigrationAssessment.Analysis.Tests`, `MigrationAssessment.Reporting.Tests`
    - Add NuGet references: `Microsoft.SqlServer.TransactSql.ScriptDom`, `Microsoft.Extensions.Logging`, `System.Text.Json`, `Microsoft.Data.SqlClient`
    - Add test NuGet references: `xunit`, `FluentAssertions`, `FsCheck.Xunit`, `NSubstitute`
    - _Requirements: All (project infrastructure)_

  - [x] 1.2 Define core models and enumerations
    - Implement `CollectedStatement`, `AnalyzedStatement`, `DetectedFeature` records
    - Implement `StatementSource`, `StatementClassification`, `FeatureCategory` enums
    - Implement `CollectionResult`, `CollectionOptions` records
    - Implement `AssessmentConfiguration` record with connection, retry, and output settings
    - _Requirements: 1.1, 1.2, 2.1, 2.6, 5.5_

  - [x] 1.3 Define collector and pipeline interfaces
    - Implement `IStatementCollector` interface with `CollectAsync` method
    - Implement `IRiskScorer`, `IWeightedComplexityCalculator`, `IMigrationReadinessScorer` interfaces
    - Implement `IReportGenerator` interface
    - Implement metadata models: `DatabaseObjectInventory`, `TableMetadata`, `AssessmentColumnMetadata`, `IndexMetadata`, `ConstraintMetadata`, `ForeignKeyMetadata`, `ProgrammableObjectMetadata`, `SynonymMetadata`
    - Implement feature detection models: `FeatureDetectionResult`, `DetectedServerFeature`, `InaccessibleFeature`
    - Implement report models: `AssessmentReport`, `ExecutiveSummary`, `RiskBreakdown`, `MigrationChallenge`, `MigrationEffortEstimate`, `MigrationRecommendation`, `CollectionFailure`
    - _Requirements: 3.1, 3.8, 4.14, 10.1, 10.4, 12.6_

- [x] 2. Implement data collection layer
  - [x] 2.1 Implement QueryStoreCollector
    - Query `sys.query_store_query_text`, `sys.query_store_plan`, `sys.query_store_runtime_stats`
    - Capture query_hash, execution count, avg duration, CPU, logical reads, plan_id, plan_hash
    - Detect Query Store state (READ_WRITE, READ_ONLY, ERROR, OFF) and log warnings
    - Implement 120-second query timeout with cancellation
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6_

  - [x] 2.2 Implement ExtendedEventsCollector
    - Query the active Extended Events session ring buffer or file target
    - Capture ad hoc SQL, stored procedure executions (up to 128 params, values truncated at 4000 chars), dynamic SQL, temp table DDL, TRY/CATCH batches
    - Preserve SQL text up to 65,536 characters without truncation
    - Implement batch processing for >100,000 events (batches of 10,000)
    - Log warning and continue if XE session is inactive
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 2.7, 2.8_

  - [x] 2.3 Implement MetadataCollector
    - Query `INFORMATION_SCHEMA` and `sys` catalog views for tables, columns, indexes, constraints, foreign keys, views, triggers, functions, stored procedures, synonyms
    - Exclude system schemas (sys, INFORMATION_SCHEMA, system-owned)
    - Handle encrypted/inaccessible object source text gracefully
    - Organize output by schema and object type
    - _Requirements: 3.1, 3.2, 3.3, 3.4, 3.5, 3.6, 3.7, 3.8_

  - [x] 2.4 Implement FeatureDetector
    - Query system views for each feature category: SQL CLR, Service Broker, Agent Jobs, CDC, Change Tracking, Replication, Linked Servers, Full Text Search, FileStream, XML Indexes, Temporal Tables, Memory Optimized, Partitioning
    - Report detailed inventory per category with properties
    - Report inaccessible features when permissions are insufficient
    - Report zero counts for categories with no instances
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.5, 4.6, 4.7, 4.8, 4.9, 4.10, 4.11, 4.12, 4.13, 4.14_

  - [ ]* 2.5 Write property tests for collection layer
    - **Property 1: Statement text preservation** — verify collected output text is identical to input text for strings 1-65,536 chars
    - **Property 2: Parameter value truncation** — verify output length equals min(inputLength, 4000)
    - **Property 3: Event batching invariant** — verify batch sizes ≤ 10,000 and total processed equals N
    - **Property 4: Feature category completeness** — verify all 13 categories present including zeros
    - **Property 5: Metadata organization by schema and type** — verify every object appears exactly once grouped by schema then type
    - **Validates: Requirements 2.6, 2.2, 2.8, 4.14, 3.8**

- [x] 3. Checkpoint - Ensure collection layer compiles and tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Implement statement analysis layer
  - [x] 4.1 Implement StatementParser using ScriptDom
    - Parse T-SQL using `TSql160Parser` from ScriptDom
    - Handle multi-statement batches (GO and semicolon splitting)
    - Record parse failures with statement text (first 1000 chars), error description, line/column
    - Classify each statement into one of: Select, Insert, Update, Delete, Merge, Ddl, Dcl, Tcl, Procedural, Unknown
    - _Requirements: 5.1, 5.2, 5.3, 5.4, 5.5, 5.6_

  - [x] 4.2 Implement StatementAnalyzer (AST Visitor)
    - Create `TSqlFragmentVisitor` subclass to walk the AST
    - Detect query features: TOP, OFFSET FETCH, MERGE, OUTPUT, CROSS/OUTER APPLY, PIVOT, UNPIVOT, dynamic SQL, OPENQUERY, OPENROWSET
    - Detect function usage: GETDATE, DATEADD, DATEDIFF, DATEPART, ISNULL, CHARINDEX, PATINDEX, STUFF, XML methods, JSON methods
    - Detect temporary objects: #temp, ##global temp, table variables, table-valued parameters
    - Detect transaction features: TRY/CATCH, explicit transactions, savepoints, locking hints (NOLOCK, ROWLOCK, UPDLOCK)
    - Record each feature occurrence with name, statement ID, line, and column
    - Handle partial analysis (AnalysisComplete = false) when unrecognized syntax is encountered
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [ ]* 4.3 Write property tests for statement analysis
    - **Property 6: Parse failure error structure** — verify failure records contain text (≥1000 chars), non-empty error, line ≥ 1, column ≥ 1
    - **Property 7: Batch splitting preserves statement count and order** — verify N statements produce N results with ordinals 1..N
    - **Property 8: Statement type classification completeness** — verify exactly one classification assigned from the defined set
    - **Property 9: Feature detection completeness** — verify N feature occurrences produce N records each with valid position
    - **Validates: Requirements 5.3, 5.4, 5.5, 5.6, 6.1, 6.2, 6.3, 6.4, 6.5**

  - [ ]* 4.4 Write unit tests for statement analysis
    - Test specific T-SQL samples for correct classification
    - Test known feature detection (e.g., TOP in SELECT, MERGE statement, NOLOCK hint)
    - Test parse failure for invalid SQL
    - Test multi-statement batch splitting
    - _Requirements: 5.1, 5.3, 5.4, 6.1, 6.2, 6.3, 6.4_

- [x] 5. Implement scoring layer
  - [x] 5.1 Implement RiskScorer
    - Build deterministic lookup table mapping detected features to risk levels 1-5
    - Risk 1: standard SQL (no extensions)
    - Risk 2: TOP, ISNULL, GETDATE, LEN, string `+` concat
    - Risk 3: TRY/CATCH, dynamic SQL, identity handling, CTE specifics
    - Risk 4: MERGE, TVPs, multi-statement TVFs, global temp, locking hints
    - Risk 5: SQL CLR, Service Broker, Linked Servers, Replication, FileStream, Memory Optimized
    - Assign max risk level among detected features; default 3 for unparseable statements
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5, 7.6, 7.7_

  - [x] 5.2 Implement WeightedComplexityCalculator
    - Calculate `WeightedRisk = RiskScore × ExecutionFrequency × BusinessImportance`
    - Default frequency = 1 when unavailable; default importance = 1.0 when unassigned
    - Rank statements by WeightedRisk descending, RiskScore descending as tiebreaker
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5_

  - [x] 5.3 Implement MigrationReadinessScorer
    - Calculate `MigrationReadinessScore = 100 × (1 - (SumOfWeightedRisks / MaxPossibleWeightedRisk))`
    - Ensure result is integer in range [0, 100]
    - Map score to classification: 90-100 "Excellent Candidate", 76-89 "Good Candidate", 51-75 "Moderate Candidate - Significant Work Required", 26-50 "High Risk", 0-25 "Not Recommended for Migration"
    - Return null score with "insufficient data" when zero statements and zero features
    - _Requirements: 9.1, 9.2, 9.3, 9.4, 9.5, 9.6, 9.7_

  - [ ]* 5.4 Write property tests for scoring layer
    - **Property 10: Risk score equals maximum feature risk level** — verify assigned score equals max of feature risks; 1 for no features; 3 for unparseable
    - **Property 11: Weighted risk formula correctness** — verify WeightedRisk = RiskScore × frequency × importance
    - **Property 12: Statement ranking order** — verify descending WeightedRisk with RiskScore as secondary sort
    - **Property 13: Migration readiness score range invariant** — verify score is integer in [0, 100]
    - **Property 14: Score-to-classification deterministic mapping** — verify correct classification for all boundary values
    - **Validates: Requirements 7.1-7.7, 8.1, 8.5, 9.1, 9.2-9.6**

  - [ ]* 5.5 Write unit tests for scoring layer
    - Test risk score assignment for known feature combinations
    - Test classification boundary values (25/26, 50/51, 75/76, 89/90)
    - Test weighted risk calculation with specific known values
    - Test null score for empty assessment
    - _Requirements: 7.1-7.7, 8.1, 9.1-9.7_

- [x] 6. Checkpoint - Ensure scoring layer compiles and tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement reporting layer
  - [x] 7.1 Implement ReportGenerator
    - Generate `ExecutiveSummary` with readiness score, total count, risk distribution (counts and percentages for levels 1-5)
    - Generate `RiskBreakdown` table
    - Generate `TopChallenges` (up to 10 items, ordered by WeightedRisk descending)
    - Generate `MigrationEffortEstimate` with hour ranges per category and total classification (Small/Medium/Large/Enterprise)
    - Generate `MigrationRecommendation` with reasoning referencing score, Risk 4/5 counts, and architectural features
    - Include failure summary listing each failed collection source
    - Handle edge case: zero statements produces score 0, zero counts, zero hours, Small classification
    - _Requirements: 10.1, 10.2, 10.3, 10.4, 10.5, 10.6, 10.7, 12.6_

  - [x] 7.2 Implement JsonReportWriter
    - Serialize `AssessmentReport` to JSON matching the published schema
    - Include objectInventory, featureInventory, analyzedStatements (with conversionCategory: automatic/semi-automatic/manual), migrationRecommendation
    - Categorize statements: Risk 1-2 = automatic, Risk 3 = semi-automatic, Risk 4-5 = manual
    - Handle file write errors (permissions, disk full, invalid path) with error reporting and non-zero exit
    - _Requirements: 11.1, 11.2, 11.3, 11.4, 11.5, 11.6, 11.7_

  - [ ]* 7.3 Write property tests for reporting layer
    - **Property 15: Report executive summary completeness** — verify score (or null), total count, risk distribution sums to total
    - **Property 16: Top challenges bounded and ordered** — verify at most 10 items ordered by WeightedRisk descending
    - **Property 17: Effort estimate consistency** — verify minHours ≤ maxHours and total classification matches ranges
    - **Property 18: JSON output schema validation** — verify serialized JSON contains all required sections with valid conversionCategory values
    - **Property 19: Failure summary completeness** — verify K failures produce K entries with source name and reason
    - **Validates: Requirements 10.1-10.5, 11.1-11.6, 12.6**

  - [ ]* 7.4 Write unit tests for reporting layer
    - Test report generation with zero statements (edge case)
    - Test effort classification boundaries (100/101, 500/501, 2000/2001)
    - Test JSON serialization round-trip for all model types
    - Test conversionCategory assignment based on risk score
    - _Requirements: 10.7, 10.5, 11.1-11.6_

- [x] 8. Implement CLI and pipeline orchestration
  - [x] 8.1 Implement AssessmentPipeline orchestrator
    - Wire up DI container with all services (collectors, analyzer, scorer, reporter)
    - Implement connection with retry logic (3 attempts, 5s delay, 30s timeout)
    - Run all collectors in parallel using `Task.WhenAll`
    - Feed collected statements through parser → analyzer → scorer → reporter
    - Track collector outcomes and build failure summary
    - Terminate with error if all sources fail (do not produce empty assessment)
    - _Requirements: 12.1, 12.2, 12.3, 12.4, 12.5, 12.6_

  - [x] 8.2 Implement CLI entry point
    - Parse command-line arguments for connection string, output path, and options
    - Configure `Microsoft.Extensions.Logging` with console provider
    - Set up DI and invoke pipeline
    - Return non-zero exit code on failure
    - _Requirements: 12.1, 12.2 (CLI is the user-facing entry point)_

  - [ ]* 8.3 Write property test for pipeline resilience
    - **Property 20: Analyzer pipeline resilience** — verify pipeline produces a result for every input (including adversarial/malformed) and never terminates early
    - **Validates: Requirements 12.4**

  - [ ]* 8.4 Write unit tests for pipeline orchestration
    - Test retry logic with mocked connection failures
    - Test parallel collector execution with mixed success/failure
    - Test all-sources-failed termination behavior
    - Test graceful degradation when individual sources fail
    - _Requirements: 12.1, 12.2, 12.3, 12.5, 12.6_

- [x] 9. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document (20 properties total)
- Unit tests validate specific examples and edge cases
- The implementation uses C# (.NET 8) with xUnit, FsCheck.Xunit, FluentAssertions, and NSubstitute
- Integration tests (requiring live SQL Server) are not included in this task list — they require environment setup and should be handled separately

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2", "1.3"] },
    { "id": 2, "tasks": ["2.1", "2.2", "2.3", "2.4"] },
    { "id": 3, "tasks": ["2.5", "4.1"] },
    { "id": 4, "tasks": ["4.2", "4.3", "4.4"] },
    { "id": 5, "tasks": ["5.1", "5.2"] },
    { "id": 6, "tasks": ["5.3", "5.4", "5.5"] },
    { "id": 7, "tasks": ["7.1", "7.2"] },
    { "id": 8, "tasks": ["7.3", "7.4"] },
    { "id": 9, "tasks": ["8.1"] },
    { "id": 10, "tasks": ["8.2", "8.3", "8.4"] }
  ]
}
```
