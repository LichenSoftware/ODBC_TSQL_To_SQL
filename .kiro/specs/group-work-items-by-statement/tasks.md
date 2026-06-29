# Implementation Plan: Group Work Items by Statement

## Overview

Refactor the work item generation pipeline in `MigrationAssessment.WorkItems` to group analyzed statements by `(SqlTextHash, DatabaseObjectName)` instead of `(FeatureName, DatabaseObjectName)`. This consolidates multi-feature SQL statements into single work items, updates all downstream generators (title, description, remediation, effort) to handle multiple features, and maintains backward compatibility with existing single-feature overloads.

## Tasks

- [x] 1. Extend data models with multi-feature support
  - [x] 1.1 Add `DetectedFeatures` and `MaxRiskLevel` properties to `StatementGroup`
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/Models/StatementGroup.cs`
    - Add `public required IReadOnlyList<string> DetectedFeatures { get; init; }` property
    - Keep existing `FeatureName` property (now represents the primary/highest-risk feature)
    - Ensure `MaxRiskLevel` is already `required` (it is) — no change needed there
    - _Requirements: 1.3, 1.4, 1.5, 9.1, 9.2, 9.3_

  - [x] 1.2 Add `DetectedFeatures` and `RelatedFeatures` properties to `WorkItem`
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/Models/WorkItem.cs`
    - Add `public IReadOnlyList<string> DetectedFeatures { get; init; } = [];`
    - Add `public IReadOnlyList<string> RelatedFeatures { get; init; } = [];`
    - _Requirements: 3.1, 3.2_

- [x] 2. Implement statement-based grouping in `StatementGrouper`
  - [x] 2.1 Change grouping key from `(FeatureName, ObjectName)` to `(SqlTextHash, ObjectName)`
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/StatementGrouper.cs`
    - Add a private `ComputeHash(string sqlText)` method using SHA-256
    - Replace the `Dictionary<(string FeatureName, string? DatabaseObjectName), ...>` with `Dictionary<(string SqlHash, string? DatabaseObjectName), ...>`
    - Collect all distinct feature names from statements sharing the same hash+object into `DetectedFeatures`
    - Set `FeatureName` to the highest-risk feature from `DetectedFeatures`
    - Set `MaxRiskLevel` to `max(GetFeatureRiskLevel(f) for f in DetectedFeatures)`
    - Remove the `DetermineFeatureAssignments` method (no longer needed — all features are collected)
    - Keep `GetFeatureRiskLevel` as-is (still needed for risk lookup)
    - Keep `CreateServerLevelGroups` unchanged (server-level groups remain isolated)
    - Populate the new `DetectedFeatures` property on each `StatementGroup`
    - _Requirements: 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 2.1, 2.2, 2.3_

  - [x] 2.2 Write property test: statement-based grouping reduces work item count (Property 1)
    - **Property 1: Statement-based grouping reduces work item count**
    - **Validates: Requirements 1**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/StatementGrouperPropertyTests.cs`
    - Generate arbitrary `AnalyzedStatement` lists with multiple features per statement
    - Assert that statement-based grouping produces ≤ groups than would be produced by feature-based grouping

  - [x] 2.3 Write property test: MaxRiskLevel equals highest feature risk (Property 6)
    - **Property 6: MaxRiskLevel equals highest feature risk**
    - **Validates: Requirements 1.5**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/StatementGrouperPropertyTests.cs`
    - For any `StatementGroup` produced, verify `MaxRiskLevel == max(GetFeatureRiskLevel(f) for f in DetectedFeatures)`

  - [x] 2.4 Write property test: server-level features remain isolated (Property 7)
    - **Property 7: Server-level features remain isolated**
    - **Validates: Requirements 2**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/StatementGrouperPropertyTests.cs`
    - For any input with server-level features, verify each produces a separate group with `IsServerLevelFeature = true`

- [x] 3. Checkpoint - Verify grouper changes
  - Ensure all tests pass, ask the user if questions arise.

- [x] 4. Update `TitleGenerator` for multi-feature titles
  - [x] 4.1 Add multi-feature `GenerateTitle` overload
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/TitleGenerator.cs`
    - Add new overload: `public string GenerateTitle(IReadOnlyList<string> detectedFeatures, string? objectName, int riskLevel)`
    - Single feature: `"[Risk N] Convert {featureName} in {objectName}"`
    - Multiple features: `"[Risk N] Convert {count} features in {objectName}"`
    - Retain existing single-feature overload — refactor it to delegate to the new multi-feature overload
    - Maintain 120 char max with truncation logic
    - Use `"Ad Hoc Queries"` when objectName is null/whitespace
    - _Requirements: 4.1, 4.2, 4.3, 4.4, 8.2_

  - [x] 4.2 Write property test: title format reflects feature count (Property 5)
    - **Property 5: Title format reflects feature count**
    - **Validates: Requirements 4.1, 4.2**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/ContentGenerationPropertyTests.cs`
    - For any work item, if `DetectedFeatures.Count == 1` then title contains the feature name; if `Count > 1` then title contains `"{count} features"`

- [x] 5. Update `DescriptionGenerator` for multi-feature descriptions
  - [x] 5.1 Add multi-feature `GenerateDescription` overload
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/DescriptionGenerator.cs`
    - Add new overload: `public string GenerateDescription(IReadOnlyList<string> detectedFeatures, int riskLevel, int occurrenceCount, long totalExecutionCount, string? objectName)`
    - List all detected features in the description text
    - Include occurrence count, total execution count, and business impact level
    - Include risk level explanation
    - Retain existing single-feature overload — refactor it to delegate to the new multi-feature overload
    - _Requirements: 5.1, 5.2, 5.3, 8.3_

- [x] 6. Update `RemediationGuidanceGenerator` for multi-feature guidance
  - [x] 6.1 Add multi-feature `GenerateGuidance` overload
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/RemediationGuidanceGenerator.cs`
    - Add new overload: `public (string Guidance, bool RequiresResearch) GenerateGuidance(IReadOnlyList<string> detectedFeatures, string primarySqlText)`
    - Produce one `### {FeatureName}` section per feature in `detectedFeatures`
    - Include SQL example once at the top (not duplicated per feature)
    - For each feature: include incompatibility explanation, PostgreSQL equivalent, and remediation steps from the knowledge base
    - If a feature has no known mapping: indicate manual analysis required, set `RequiresResearch = true`
    - Retain existing single-feature overload — refactor it to delegate to the new multi-feature overload
    - _Requirements: 6.1, 6.2, 6.3, 6.4, 6.5, 8.4_

  - [x] 6.2 Write property test: remediation guidance covers all detected features (Property 3)
    - **Property 3: Remediation guidance covers all detected features**
    - **Validates: Requirements 6.1**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/ContentGenerationPropertyTests.cs`
    - For any work item with N features in `DetectedFeatures`, verify guidance contains exactly N `###`-headed sections

- [x] 7. Update `EffortEstimator` for multi-feature effort calculation
  - [x] 7.1 Add multi-feature `EstimateEffort` overload to `IEffortEstimator` and `EffortEstimator`
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/IEffortEstimator.cs`
    - Add interface method: `HourRange EstimateEffort(IReadOnlyList<string> detectedFeatures, int statementCount);`
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/EffortEstimator.cs`
    - Implement: sum per-feature efforts using each feature's own risk level from `GetFeatureRiskLevel`
    - Apply geometric reduction factor (0.7) per-feature independently based on `statementCount`
    - Add a static/internal `GetFeatureRiskLevel` method (or reference the one in StatementGrouper)
    - Retain existing `EstimateEffort(int riskLevel, int statementCount)` overload
    - _Requirements: 7.1, 7.2, 7.3, 7.4, 8.5_

  - [x] 7.2 Write property test: effort is sum of per-feature efforts (Property 4)
    - **Property 4: Effort is sum of per-feature efforts**
    - **Validates: Requirements 7.1**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/PriorityAndEffortPropertyTests.cs`
    - For any list of features and statement count, verify `EstimateEffort(features, count)` equals `sum(EstimateEffort(risk(f), count) for f in features)`

- [x] 8. Checkpoint - Verify generator changes
  - Ensure all tests pass, ask the user if questions arise.

- [x] 9. Wire multi-feature flow in `WorkItemGeneratorService`
  - [x] 9.1 Update `BuildWorkItem` to use multi-feature overloads and populate new properties
    - Modify `MigrationAssessment/src/MigrationAssessment.WorkItems/WorkItemGeneratorService.cs`
    - In `BuildWorkItem`, read `group.DetectedFeatures` from the `StatementGroup`
    - Call `_titleGenerator.GenerateTitle(detectedFeatures, objectName, riskLevel)` instead of single-feature overload
    - Call `_descriptionGenerator.GenerateDescription(detectedFeatures, riskLevel, occurrenceCount, totalExecutionCount, objectName)` instead of single-feature overload
    - Call `_guidanceGenerator.GenerateGuidance(detectedFeatures, dedupGroup.PrimarySqlPattern)` instead of single-feature overload
    - Call `_effortEstimator.EstimateEffort(detectedFeatures, occurrenceCount)` instead of single-feature overload
    - Set `DetectedFeatures = group.DetectedFeatures` on the output `WorkItem`
    - Set `RelatedFeatures = group.DetectedFeatures.Distinct().ToList()` on the output `WorkItem`
    - _Requirements: 3.3, 3.4_

  - [x] 9.2 Write property test: no duplicate SqlServerPattern across work items (Property 2)
    - **Property 2: No duplicate SqlServerPattern across work items**
    - **Validates: Requirements 9.4**
    - Add to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/WorkItemDeduplicatorPropertyTests.cs`
    - For any valid set of statements and feature detection results, verify no two output work items share the same `SqlServerPattern`

- [x] 10. Verify backward compatibility
  - [x] 10.1 Add unit tests verifying single-feature overloads still delegate correctly
    - Add tests to `MigrationAssessment/tests/MigrationAssessment.WorkItems.Tests/ContentGenerationUnitTests.cs`
    - Test `TitleGenerator.GenerateTitle(string, string?, int)` delegates to multi-feature overload and produces same output
    - Test `DescriptionGenerator.GenerateDescription(string, ...)` delegates to multi-feature overload
    - Test `RemediationGuidanceGenerator.GenerateGuidance(string, string)` delegates to multi-feature overload
    - Test `IEffortEstimator.EstimateEffort(int, int)` still works independently
    - Verify existing `IStatementGrouper` interface signatures are unchanged
    - _Requirements: 8.1, 8.2, 8.3, 8.4, 8.5_

- [x] 11. Final checkpoint - Ensure all tests pass
  - Ensure all tests pass, ask the user if questions arise.

## Notes

- Tasks marked with `*` are optional and can be skipped for faster MVP
- Each task references specific requirements for traceability
- Checkpoints ensure incremental validation
- Property tests validate universal correctness properties from the design document
- Unit tests validate specific examples and edge cases
- The design uses C# (.NET 8) with xUnit and FsCheck for property-based testing
- All changes are scoped to the `MigrationAssessment.WorkItems` project and its test project
- The `StatementGrouper.GetFeatureRiskLevel` method will be needed by `EffortEstimator` — consider extracting to a shared static utility or making it `internal` accessible via `InternalsVisibleTo`

## Task Dependency Graph

```json
{
  "waves": [
    { "id": 0, "tasks": ["1.1", "1.2"] },
    { "id": 1, "tasks": ["2.1", "4.1", "5.1", "6.1", "7.1"] },
    { "id": 2, "tasks": ["2.2", "2.3", "2.4", "4.2", "6.2", "7.2"] },
    { "id": 3, "tasks": ["9.1"] },
    { "id": 4, "tasks": ["9.2", "10.1"] }
  ]
}
```
