# Implementation Plan: Migration Work Items Generator

## Overview

Implement the Migration Work Items Generator as a new C# (.NET 8) project `MigrationAssessment.WorkItems` that transforms assessment results into structured, actionable work item tickets. The generator groups related statements by feature name and database object, calculates priority scores and effort estimates, generates professional ticket content with remediation guidance, and outputs both JSON and optional Markdown formats. It integrates into the existing assessment pipeline as an optional stage and provides a standalone CLI verb.

## Tasks

- [x] 1. Set up project structure and core models
  - [x] 1.1 Create WorkItems project and test projects
    - Create `src/MigrationAssessment.WorkItems/` project targeting .NET 8
    - Create `tests/MigrationAssessment.WorkItems.Tests/` test project
    - Create `tests/MigrationAssessment.WorkItems.Integration.Tests/` integration test project
    - Add project reference to `MigrationAssessment.Core` for shared models
    - Add NuGet references: `Microsoft.Extensions.Logging.Abstractions`, `System.Text.Json`
    - Add test NuGet references: `xunit`, `FluentAssertions`, `FsCheck.Xunit`, `NSubstitute`
    - Add solution references in `MigrationAssessment.slnx`
    - _Requirements: 10.1, 10.2 (project infrastructure)_

  - [x] 1.2 Define work item models and configuration
    - Implement `WorkItem` record with all required fields (Id, Title, Description, SqlServerPattern, PostgresEquivalent, AffectedObjects, RiskLevel, Priority, PriorityScore, EstimatedEffort, AcceptanceCriteria, RemediationGuidance, Tags, RelatedWorkItemIds)
    - Implement `AffectedObject` record with Name, Type, StatementCount
    - Implement `HourRange` record with MinHours and MaxHours
    - Implement `WorkItemResult` record with WorkItems list, Metadata, Succeeded flag, ErrorMessage
    - Implement `WorkItemMetadata` record with GeneratedAt, SourceAssessmentPath, TotalWorkItemCount, TotalEstimatedEffort
    - Implement `WorkItemConfiguration` record with OutputJsonPath, MarkdownEnabled, MarkdownOutputPath, MinimumRiskLevel, MaxWorkItemCount
    - Implement `StatementGroup` record with FeatureName, DatabaseObjectName, DatabaseObjectType, Statements, IsServerLevelFeature, MaxRiskLevel
    - _Requirements: 3.1, 6.2, 9.1, 9.2, 9.3, 9.4, 9.5_

  - [x] 1.3 Define interfaces for work item generation
    - Implement `IWorkItemGenerator` interface with `GenerateWorkItems` and `GenerateFromFileAsync` methods
    - Implement `IStatementGrouper` interface with `GroupStatements` method
    - Implement `IPriorityCalculator` interface with `CalculatePriorityScore` and `AssignPriorityLabels` methods
    - Implement `IEffortEstimator` interface with `EstimateEffort` and `CalculateTotalEffort` methods
    - Implement `IRemediationKnowledgeBase` interface with `GetGuidance` and `HasGuidance` methods
    - Implement `RemediationEntry` record with PostgresEquivalent, RemediationSteps, IncompatibilityExplanation, RiskLevel, RequiresArchitecturalReview, PostgresDocReference
    - Implement `IWorkItemJsonWriter` and `IWorkItemMarkdownWriter` interfaces
    - _Requirements: 1.1, 1.2, 4.1, 4.2, 4.3, 4.4, 10.2_

- [x] 2. Implement statement grouping and deduplication
  - [x] 2.1 Implement StatementGrouper
    - Filter statements by minimum risk level from configuration
    - For each statement, extract (FeatureName, DatabaseObject) pairs from detected features
    - Apply multi-feature assignment rule: if different risk levels, assign to highest-risk feature group only; if same risk level, assign to all matching groups
    - Group by key `(FeatureName, DatabaseObjectName)` — one group per unique key
    - Handle ad hoc statements (no owning object) by grouping under `(FeatureName, null)` with label "Ad Hoc Queries"
    - Create server-level work item groups from FeatureDetectionResult entries with occurrence count > 0
    - _Requirements: 2.1, 2.2, 2.3, 2.4, 2.5, 2.6_

  - [x] 2.2 Implement WorkItemDeduplicator
    - Merge statements within the same database object that produce identical feature names into a single work item listing all statement locations
    - Select the statement with the highest WeightedRisk as the primary example for SQL Server pattern
    - Sum execution frequencies of all merged statements for combined PriorityScore calculation
    - Build cross-reference map: for each database object appearing in multiple work items, record related work item IDs
    - Assign sequential unique identifiers in format "WI-{sequential_number}" starting at 001
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5_

  - [x] 2.3 Write property tests for statement grouping
    - **Property 1: Grouping key uniqueness** — verify at most one work item per unique (FeatureName, DatabaseObjectName) pair
    - **Property 2: Multi-feature highest-risk assignment** — verify statements with features at different risk levels appear only in the highest-risk group
    - **Property 3: Same-risk multi-feature inclusion** — verify statements with same-risk features appear in all matching groups
    - **Property 4: Server-level feature coverage** — verify exactly N server-level work items for N features with count > 0
    - **Validates: Requirements 2.1, 2.2, 2.3, 2.4, 2.5, 2.6, 8.1**

  - [x] 2.4 Write property tests for deduplication
    - **Property 8: Primary example is highest weighted risk** — verify SQL pattern example is sourced from the statement with highest WeightedRisk
    - **Property 16: Work item ID uniqueness and format** — verify all IDs unique, match pattern `WI-\d{3,}`, sequential from WI-001
    - **Property 17: Cross-references for shared objects** — verify each object in K>1 work items has K-1 related IDs in each work item
    - **Validates: Requirements 8.2, 8.4, 8.5**

- [x] 3. Implement priority and effort calculation
  - [x] 3.1 Implement PriorityCalculator
    - Calculate PriorityScore as sum of WeightedRisk values across all statements in a work item
    - Sort all work items by PriorityScore descending
    - Assign percentile-based priority labels: Critical (top 10%), High (70th-89th percentile), Medium (30th-69th percentile), Low (below 30th percentile)
    - Implement tie-breaking: equal PriorityScore ordered by risk level descending, then statement count descending
    - Handle edge case: single work item gets "Critical" label
    - _Requirements: 5.1, 5.2, 5.4_

  - [x] 3.2 Implement EffortEstimator
    - Define per-statement base effort ranges by risk level: Risk 1 (0, 0.08h), Risk 2 (0.08, 0.5h), Risk 3 (0.5, 4h), Risk 4 (4, 40h), Risk 5 (40, 80h)
    - Apply geometric series formula: `TotalHours = Base × (1 - 0.7^N) / 0.3` for N statements
    - Use highest risk level in the group when statements have mixed risk levels
    - Implement `CalculateTotalEffort` summing min and max hours across all work items
    - _Requirements: 5.3, 5.5, 5.6_

  - [x] 3.3 Write property tests for priority and effort
    - **Property 9: Priority score equals sum of weighted risks** — verify PriorityScore = Σ(WeightedRisk) for all statements in group
    - **Property 10: Percentile-based priority labels** — verify labels match percentile thresholds
    - **Property 11: Effort estimation geometric series** — verify effort formula produces correct values for given risk level and count
    - **Property 12: Total effort equals sum of parts** — verify total min/max = sum of individual min/max
    - **Property 13: Output ordering by priority** — verify descending PriorityScore with correct tie-breaking
    - **Validates: Requirements 5.1, 5.2, 5.3, 5.4, 5.5, 5.6**

- [x] 4. Checkpoint - Ensure grouping, priority, and effort logic compiles and tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 5. Implement content generation layer
  - [x] 5.1 Implement TitleGenerator and DescriptionGenerator
    - Generate titles in format `[Risk N] Convert <feature_name> in <object_name>` with max 120 characters (truncate object name if needed)
    - Generate descriptions with plain-language explanation of incompatibility, occurrence count, and business impact based on execution frequency
    - _Requirements: 3.2, 3.3_

  - [x] 5.2 Implement RemediationKnowledgeBase
    - Create static knowledge base keyed by feature name
    - Risk 2 entries: Direct syntax mappings for TOP, ISNULL, GETDATE, LEN, CHARINDEX, PATINDEX, STUFF, DATEADD, DATEDIFF, DATEPART
    - Risk 3 entries: Step-by-step conversion instructions for TRY/CATCH, dynamic SQL, temporary tables, OUTPUT clause, CROSS APPLY, OUTER APPLY
    - Risk 4 entries: Design pattern recommendations for MERGE, table-valued parameters, global temp tables, locking hints, PIVOT, UNPIVOT
    - Risk 5 entries: Migration strategies for SQL CLR, Service Broker, Linked Servers, XML methods, OPENQUERY, OPENROWSET, FileStream, Memory Optimized
    - Include PostgreSQL documentation references for each entry
    - Handle unknown features by returning null (triggers "requires-research" flag)
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 4.6_

  - [x] 5.3 Implement RemediationGuidanceGenerator
    - Look up remediation entry from knowledge base by feature name
    - Include "before" code example from actual assessed SQL (highest WeightedRisk statement, max 500 chars)
    - Include "after" code example from knowledge base PostgreSQL equivalent
    - If no known mapping exists, indicate manual analysis required, reference PostgreSQL docs, assign "requires-research" flag
    - _Requirements: 4.5, 4.6_

  - [x] 5.4 Implement AcceptanceCriteriaGenerator
    - Generate at least two verifiable conditions per work item
    - First criterion: confirm SQL Server construct has been replaced
    - Second criterion: confirm PostgreSQL equivalent produces correct results
    - Add additional criteria based on feature complexity (Risk 4-5 get extra verification steps)
    - _Requirements: 3.7_

  - [x] 5.5 Write property tests for content generation
    - **Property 5: Work item structural completeness** — verify all generated work items contain required non-empty fields with correct constraints
    - **Property 6: Title format conformance** — verify titles match pattern `[Risk R] Convert F in O` and ≤120 chars
    - **Property 7: SQL pattern sourced from input** — verify sqlServerPattern is substring of an input statement's SqlText
    - **Validates: Requirements 3.1, 3.2, 3.4, 3.6, 3.7**

  - [x] 5.6 Write unit tests for content generation
    - Test knowledge base returns entries for all known features (TOP, ISNULL, MERGE, XML methods, etc.)
    - Test title truncation for long feature/object names
    - Test "requires-research" flag for unknown features
    - Test acceptance criteria always contains at least 2 items
    - _Requirements: 3.2, 3.7, 4.1, 4.2, 4.3, 4.4, 4.6_

- [x] 6. Checkpoint - Ensure content generation compiles and tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 7. Implement input layer (JSON ingestion)
  - [x] 7.1 Implement AssessmentJsonReader
    - Parse assessment JSON file and extract analyzed statements, detected features, risk scores, weighted risk values, and conversion categories
    - Validate JSON structure against expected assessment output schema
    - Return error with file path if file not found
    - Return validation error with specific schema violation if JSON is invalid or non-conformant
    - Return empty work item list with informational message if zero statements and zero feature inventory entries
    - _Requirements: 1.1, 1.3, 1.4, 1.5_

  - [x] 7.2 Implement pipeline integration entry point
    - Accept in-memory AssessmentReport and AnalyzedStatement collection directly without file serialization
    - Implement `IWorkItemGenerator.GenerateWorkItems` method wiring all processing components together
    - _Requirements: 1.2, 10.2, 10.3_

  - [x] 7.3 Write property tests for input validation
    - **Property 20: Invalid input produces validation error** — verify non-JSON or schema-non-conformant strings produce failed result with non-empty error message
    - **Validates: Requirements 1.3, 1.4**

  - [x] 7.4 Write unit tests for JSON ingestion
    - Test parsing the existing `test-assessment.json` file successfully
    - Test file-not-found error message
    - Test invalid JSON syntax error
    - Test valid JSON but missing required fields error
    - Test empty assessment produces empty work item list
    - _Requirements: 1.1, 1.3, 1.4, 1.5_

- [x] 8. Implement output layer (JSON and Markdown writers)
  - [x] 8.1 Implement WorkItemJsonWriter
    - Serialize WorkItemResult to JSON conforming to published schema
    - Include metadata section with generation timestamp, source path, total count, total effort
    - Serialize each work item with all required fields including tags array
    - Order work items by PriorityScore descending in output array
    - Handle file write errors and return error with target path
    - Create output directory if it does not exist
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 6.6_

  - [x] 8.2 Implement WorkItemMarkdownWriter
    - Generate summary section with total count, effort estimates, risk distribution by priority
    - Generate table of contents with links to priority group sections
    - Organize work items under priority group headings (Critical, High, Medium, Low)
    - Format each work item with title heading, description, fenced code blocks for SQL patterns, bullet list of affected objects, numbered acceptance criteria
    - Default output path: same directory as JSON with filename "work-items.md"
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 7.5_

  - [x] 8.3 Write property tests for output layer
    - **Property 14: JSON schema validation** — verify serialized JSON validates against published schema for any valid input
    - **Property 15: Tags completeness** — verify tags contain risk label, feature category, and conversion category
    - **Property 18: Risk level filter enforcement** — verify all output work items have riskLevel ≥ configured minimum
    - **Property 19: Maximum count limit enforcement** — verify output contains at most L items, being the top L by PriorityScore
    - **Validates: Requirements 6.1, 6.2, 6.3, 6.5, 9.4, 9.5**

  - [x] 8.4 Write unit tests for output formatting
    - Test JSON output validates against the published schema
    - Test Markdown output contains expected sections and structure
    - Test file write error handling
    - Test default Markdown path when not specified
    - _Requirements: 6.5, 6.6, 7.1, 7.2, 7.5_

- [x] 9. Checkpoint - Ensure input/output layers compile and tests pass
  - Ensure all tests pass, ask the user if questions arise.

- [x] 10. Implement CLI integration and pipeline wiring
  - [x] 10.1 Implement WorkItemGenerator orchestrator
    - Wire all components: StatementGrouper → WorkItemDeduplicator → PriorityCalculator → EffortEstimator → TitleGenerator → DescriptionGenerator → RemediationGuidanceGenerator → AcceptanceCriteriaGenerator
    - Implement `GenerateFromFileAsync` reading JSON then processing
    - Implement `GenerateWorkItems` processing in-memory data directly
    - Apply configuration: minimum risk level filter, maximum work item count limit
    - Validate configuration and report errors for invalid values
    - _Requirements: 9.4, 9.5, 9.6, 10.2, 10.3_

  - [x] 10.2 Implement CLI verb for work item generation
    - Add `generate-work-items` command to existing CLI with arguments: input file path (required), output JSON path (optional), markdown enabled flag (optional), markdown output path (optional), minimum risk level (optional), maximum work item count (optional)
    - Display usage message when required arguments are missing
    - Configure DI container with all work item generation services
    - Invoke generator and write outputs based on configuration
    - _Requirements: 10.1, 10.4, 10.5_

  - [x] 10.3 Integrate into existing AssessmentPipeline
    - Add work item generation as an optional pipeline stage after ReportGenerator
    - Pass AssessmentReport and AnalyzedStatements directly to IWorkItemGenerator without disk serialization
    - Gate execution on a configuration flag (opt-in)
    - _Requirements: 10.2, 10.3_

  - [x] 10.4 Write unit tests for CLI and pipeline integration
    - Test CLI argument parsing for all argument combinations
    - Test missing required argument shows usage message
    - Test configuration validation error messages
    - Test pipeline stage executes only when enabled
    - _Requirements: 10.1, 10.4, 10.5, 9.6_

- [x] 11. Final checkpoint - Ensure all tests pass and end-to-end flow works
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document (20 properties total)
- Unit tests validate specific examples and edge cases
- The implementation uses C# (.NET 8) with xUnit, FsCheck.Xunit, FluentAssertions, and NSubstitute
- The existing `test-assessment.json` file can be used for integration testing of the JSON ingestion layer
- The new project follows the same pattern as existing projects (`MigrationAssessment.Analysis`, `MigrationAssessment.Reporting`) with a separate project per pipeline stage

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1"] },
    { "id": 1, "tasks": ["1.2", "1.3"] },
    { "id": 2, "tasks": ["2.1", "2.2"] },
    { "id": 3, "tasks": ["2.3", "2.4", "3.1", "3.2"] },
    { "id": 4, "tasks": ["3.3", "5.1", "5.2"] },
    { "id": 5, "tasks": ["5.3", "5.4"] },
    { "id": 6, "tasks": ["5.5", "5.6", "7.1", "7.2"] },
    { "id": 7, "tasks": ["7.3", "7.4", "8.1", "8.2"] },
    { "id": 8, "tasks": ["8.3", "8.4"] },
    { "id": 9, "tasks": ["10.1"] },
    { "id": 10, "tasks": ["10.2", "10.3"] },
    { "id": 11, "tasks": ["10.4"] }
  ]
}
```
