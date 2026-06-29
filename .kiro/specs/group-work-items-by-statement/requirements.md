# Requirements Document

## Introduction

This feature changes the work item generation pipeline to group analyzed statements by unique SQL text (hash) + database object instead of by individual feature + database object. The result is that a single SQL statement using multiple incompatible features (e.g., `ISNULL`, `DATEDIFF`, `GETDATE`) produces one consolidated work item instead of multiple separate tickets. This reduces noise for developers, provides holistic remediation guidance, and maintains backward compatibility with existing interfaces.

## Glossary

- **StatementGrouper**: The component that clusters analyzed statements into logical groups, each of which becomes one work item.
- **StatementGroup**: A data model representing a cluster of statements sharing the same SQL text hash and database object.
- **WorkItem**: The output ticket record containing title, description, remediation guidance, effort estimate, and metadata.
- **DetectedFeatures**: The list of all SQL Server feature names found in a statement group's SQL text.
- **RelatedFeatures**: The distinct set of feature names associated with a work item, used for filtering.
- **TitleGenerator**: The component that produces the title string for a work item.
- **DescriptionGenerator**: The component that produces the description string for a work item.
- **RemediationGuidanceGenerator**: The component that produces multi-section remediation guidance for a work item.
- **EffortEstimator**: The component that calculates estimated effort (hour range) for a work item.
- **SqlTextHash**: A SHA-256 hash of a statement's SQL text, used as a grouping key.
- **ServerLevelFeature**: A feature detected at the server level (from FeatureDetectionResult) that always gets its own isolated work item.
- **MaxRiskLevel**: The highest risk level among all detected features in a statement group.
- **PrimaryFeature**: The feature with the highest risk level in a group, used as the `FeatureName` property for backward compatibility.

## Requirements

### Requirement 1: Statement-Based Grouping

**User Story:** As a developer reviewing migration work items, I want statements that use multiple incompatible features to produce a single consolidated work item, so that I have one ticket per SQL statement to fix rather than multiple fragmented tickets.

#### Acceptance Criteria

1. WHEN the StatementGrouper receives a list of analyzed statements, THE StatementGrouper SHALL group statements by the combination of (SqlTextHash, DatabaseObjectName) instead of (FeatureName, DatabaseObjectName)
2. WHEN multiple statements share the same SqlTextHash and DatabaseObjectName, THE StatementGrouper SHALL merge them into a single StatementGroup
3. WHEN a StatementGroup is created, THE StatementGrouper SHALL populate DetectedFeatures with all distinct feature names found across all statements in the group
4. WHEN a StatementGroup has multiple detected features, THE StatementGrouper SHALL set FeatureName to the feature with the highest risk level among DetectedFeatures
5. WHEN a StatementGroup is created, THE StatementGrouper SHALL set MaxRiskLevel to the maximum risk level among all features in DetectedFeatures
6. WHEN a statement has a RiskScore below the minimumRiskLevel parameter, THE StatementGrouper SHALL exclude that statement from all groups

### Requirement 2: Server-Level Feature Isolation

**User Story:** As a migration architect, I want server-level features to continue producing their own isolated work items, so that infrastructure-level incompatibilities remain clearly separated from statement-level issues.

#### Acceptance Criteria

1. WHEN the FeatureDetectionResult contains server-level features, THE StatementGrouper SHALL create a separate StatementGroup for each server-level feature with IsServerLevelFeature set to true
2. WHEN server-level StatementGroups are created, THE StatementGrouper SHALL NOT apply statement-based grouping logic to them
3. THE StatementGrouper SHALL append server-level groups to the output unchanged regardless of statement-based grouping results

### Requirement 3: WorkItem Model Extension

**User Story:** As a consumer of work item data, I want each work item to expose the full list of detected features, so that I can filter and categorize work items by any feature they involve.

#### Acceptance Criteria

1. THE WorkItem model SHALL include a DetectedFeatures property containing all feature names detected in the work item's statement group
2. THE WorkItem model SHALL include a RelatedFeatures property containing the distinct set of feature names for filtering purposes
3. WHEN a WorkItem is constructed, THE WorkItemGeneratorService SHALL populate DetectedFeatures from the StatementGroup's DetectedFeatures list
4. WHEN a WorkItem is constructed, THE WorkItemGeneratorService SHALL populate RelatedFeatures with the distinct values from DetectedFeatures

### Requirement 4: Multi-Feature Title Generation

**User Story:** As a developer scanning a work item list, I want titles to indicate whether a work item covers one or multiple features, so that I can quickly assess the scope of each ticket.

#### Acceptance Criteria

1. WHEN a work item has exactly one detected feature, THE TitleGenerator SHALL produce a title in the format "[Risk N] Convert {featureName} in {objectName}"
2. WHEN a work item has more than one detected feature, THE TitleGenerator SHALL produce a title in the format "[Risk N] Convert {count} features in {objectName}"
3. THE TitleGenerator SHALL limit title length to 120 characters maximum, truncating the object name with "..." if necessary
4. WHEN objectName is null or whitespace, THE TitleGenerator SHALL use "Ad Hoc Queries" as the object name

### Requirement 5: Multi-Feature Description Generation

**User Story:** As a developer reading a work item description, I want to see all affected features listed together with occurrence and execution data, so that I understand the full scope of what needs to change.

#### Acceptance Criteria

1. WHEN a work item has multiple detected features, THE DescriptionGenerator SHALL list all feature names in the description
2. THE DescriptionGenerator SHALL include the occurrence count, total execution count, and business impact level in the description
3. THE DescriptionGenerator SHALL include the risk level explanation in the description

### Requirement 6: Multi-Feature Remediation Guidance

**User Story:** As a developer remediating a SQL statement, I want the remediation guidance to cover all incompatible features in that statement in a single document, so that I can fix everything in one pass.

#### Acceptance Criteria

1. WHEN a work item has multiple detected features, THE RemediationGuidanceGenerator SHALL produce one heading section per feature in DetectedFeatures
2. THE RemediationGuidanceGenerator SHALL include the SQL Server example once at the top of the guidance, not duplicated per feature
3. WHEN a feature has a known PostgreSQL equivalent in the knowledge base, THE RemediationGuidanceGenerator SHALL include the incompatibility explanation, PostgreSQL equivalent, and remediation steps in that feature's section
4. WHEN a feature has no known PostgreSQL equivalent in the knowledge base, THE RemediationGuidanceGenerator SHALL indicate manual analysis is required for that feature
5. WHEN any feature in DetectedFeatures lacks a known mapping, THE RemediationGuidanceGenerator SHALL set RequiresResearch to true

### Requirement 7: Multi-Feature Effort Estimation

**User Story:** As a project manager reviewing effort estimates, I want the effort for a multi-feature work item to reflect the combined complexity of all features, so that sprint planning is accurate.

#### Acceptance Criteria

1. WHEN a work item has multiple detected features, THE EffortEstimator SHALL calculate effort as the sum of individual per-feature efforts
2. THE EffortEstimator SHALL use each feature's own risk level from the feature-risk map when calculating per-feature effort
3. THE EffortEstimator SHALL apply the geometric reduction factor (0.7) per-feature independently based on statement count
4. IF a feature name is not found in the feature-risk map, THEN THE EffortEstimator SHALL default to risk level 1

### Requirement 8: Backward Compatibility

**User Story:** As a maintainer of the codebase, I want existing single-feature interfaces to continue working, so that callers not yet updated to multi-feature APIs are not broken.

#### Acceptance Criteria

1. THE IStatementGrouper interface SHALL retain its existing method signatures without modification
2. THE TitleGenerator SHALL retain the existing single-feature GenerateTitle overload that delegates to the new multi-feature overload
3. THE DescriptionGenerator SHALL retain the existing single-feature GenerateDescription overload that delegates to the new multi-feature overload
4. THE RemediationGuidanceGenerator SHALL retain the existing single-feature GenerateGuidance overload that delegates to the new multi-feature overload
5. THE IEffortEstimator interface SHALL retain the existing EstimateEffort(int riskLevel, int statementCount) overload

### Requirement 9: Data Integrity Invariants

**User Story:** As a quality engineer, I want the system to maintain structural invariants on StatementGroup and WorkItem data, so that downstream consumers can rely on consistent data.

#### Acceptance Criteria

1. THE StatementGroup SHALL have DetectedFeatures containing at least one entry
2. THE StatementGroup FeatureName SHALL equal the highest-risk feature in DetectedFeatures
3. THE StatementGroup MaxRiskLevel SHALL equal the maximum of GetFeatureRiskLevel(f) for all f in DetectedFeatures
4. WHEN the pipeline produces work items, THE system SHALL NOT produce two work items with the same SqlServerPattern value
