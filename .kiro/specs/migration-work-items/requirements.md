# Requirements Document

## Introduction

The Migration Work Items feature extends the existing Migration Assessment Engine to generate actionable, developer-ready work item tickets from assessment results. The current engine produces a high-level assessment (migration readiness score, risk distribution, recommendations). This feature transforms that assessment data into a structured task list of individual remediation work items, each containing enough context and guidance for a developer unfamiliar with the codebase to understand the SQL Server construct, the PostgreSQL equivalent, and the specific steps needed to remediate the risk. The output enables direct import into project management tools (Jira, Azure DevOps) for sprint planning and execution tracking.

## Glossary

- **Work_Item_Generator**: The subsystem that transforms assessment results into structured, actionable work item tickets
- **Work_Item**: A single remediation task describing a specific SQL Server construct that requires modification for PostgreSQL compatibility, containing title, description, affected objects, remediation guidance, and effort estimates
- **Assessment_Report**: The JSON output produced by the existing Migration Assessment Engine containing analyzed statements, detected features, risk scores, and migration recommendations
- **Grouping_Strategy**: The algorithm that combines related analyzed statements and detected features into logical work items based on feature type and affected database objects
- **Remediation_Guidance**: The specific instructions within a work item that describe how to convert a SQL Server pattern to the PostgreSQL equivalent, including code examples derived from the actual SQL found in the assessed database
- **Affected_Object**: A database object (stored procedure, view, function, table, or batch) that contains a SQL Server construct requiring remediation
- **Priority_Score**: A numeric value derived from weighted risk that determines work item ordering, where higher values indicate higher priority
- **Acceptance_Criteria**: The verifiable conditions within a work item that define when the remediation is complete

## Requirements

### Requirement 1: Assessment Report Ingestion

**User Story:** As a migration engineer, I want the work item generator to consume the existing assessment JSON output, so that work items are derived directly from real analysis data without re-running the assessment.

#### Acceptance Criteria

1. WHEN provided a valid assessment JSON file path, THE Work_Item_Generator SHALL parse the file and extract all analyzed statements, detected features, risk scores, weighted risk values, and conversion categories
2. WHEN provided assessment data programmatically as an in-memory Assessment_Report object, THE Work_Item_Generator SHALL accept the object directly without requiring file serialization
3. IF the provided assessment JSON file does not exist at the specified path, THEN THE Work_Item_Generator SHALL report an error indicating the file path and that the file was not found
4. IF the provided assessment JSON file contains invalid JSON or does not conform to the assessment output schema, THEN THE Work_Item_Generator SHALL report a validation error indicating the specific schema violation encountered
5. IF the assessment JSON file contains zero analyzed statements and zero feature inventory entries, THEN THE Work_Item_Generator SHALL produce an empty work item list and report that no remediation work items are needed

### Requirement 2: Work Item Grouping

**User Story:** As a migration engineer, I want related issues grouped into logical work items, so that developers receive coherent tasks rather than one ticket per individual SQL statement occurrence.

#### Acceptance Criteria

1. WHEN processing analyzed statements, THE Work_Item_Generator SHALL group statements that share the same detected feature name and reside in the same database object (stored procedure, function, view, or batch) into a single work item
2. WHEN multiple statements share the same detected feature name but reside in different database objects, THE Work_Item_Generator SHALL create one work item per distinct database object for that feature
3. WHEN a single statement contains multiple detected features at different risk levels, THE Work_Item_Generator SHALL assign that statement to the work item for the highest-risk feature detected in the statement
4. WHEN a statement contains multiple detected features at the same risk level, THE Work_Item_Generator SHALL assign that statement to a work item for each detected feature, listing the statement as an affected location in each corresponding work item
5. WHEN the assessment contains server-level features from the feature inventory (SQL CLR, Service Broker, Linked Servers) with occurrence counts greater than zero, THE Work_Item_Generator SHALL create one work item per server-level feature category regardless of statement grouping
6. IF a statement has no associated database object name (ad hoc query without an owning object), THEN THE Work_Item_Generator SHALL group such statements by detected feature name into a single work item titled with the feature name and labeled as "Ad Hoc Queries"

### Requirement 3: Work Item Content Structure

**User Story:** As a developer, I want each work item to contain a professional ticket format with title, description, context, and remediation steps, so that I can understand and fix the issue without additional research.

#### Acceptance Criteria

1. THE Work_Item_Generator SHALL produce each work item containing: a title (maximum 120 characters), a description, the SQL Server pattern being used, the PostgreSQL equivalent pattern, a list of affected objects with their locations, a risk level (integer 1-5), an estimated effort range (minimum and maximum hours), and acceptance criteria for the remediation
2. WHEN generating the work item title, THE Work_Item_Generator SHALL format it as "[Risk N] Convert <feature_name> in <object_name>" where N is the risk level, feature_name is the SQL Server construct, and object_name is the affected database object or "Ad Hoc Queries"
3. WHEN generating the work item description, THE Work_Item_Generator SHALL include a plain-language explanation of why the SQL Server construct is incompatible with PostgreSQL, the number of occurrences found, and the business impact based on execution frequency
4. WHEN generating the SQL Server pattern section, THE Work_Item_Generator SHALL include at least one actual SQL code excerpt (up to 500 characters) from the analyzed statements that demonstrates the construct in context
5. WHEN generating the PostgreSQL equivalent section, THE Work_Item_Generator SHALL provide a code example showing the recommended PostgreSQL approach for the specific SQL Server construct identified in the work item
6. WHEN generating the affected objects list, THE Work_Item_Generator SHALL include for each affected location: the object name, the object type (stored procedure, function, view, trigger, or ad hoc), and the statement count within that object referencing the feature
7. WHEN generating acceptance criteria, THE Work_Item_Generator SHALL include at least two verifiable conditions: one confirming the SQL Server construct has been replaced, and one confirming the PostgreSQL equivalent produces correct results

### Requirement 4: Remediation Guidance Generation

**User Story:** As a developer unfamiliar with the codebase, I want specific "how to fix" instructions based on the actual SQL found in the database, so that I can remediate the issue without deep SQL Server expertise.

#### Acceptance Criteria

1. WHEN generating remediation guidance for Risk 2 features (TOP, ISNULL, GETDATE, LEN, CHARINDEX, PATINDEX, STUFF, DATEADD, DATEDIFF, DATEPART), THE Work_Item_Generator SHALL provide a direct syntax mapping showing the SQL Server function or construct and the equivalent PostgreSQL syntax
2. WHEN generating remediation guidance for Risk 3 features (TRY/CATCH, dynamic SQL, temporary tables, OUTPUT clause, CROSS APPLY, OUTER APPLY), THE Work_Item_Generator SHALL provide step-by-step conversion instructions that reference the specific procedural pattern found in the assessed database
3. WHEN generating remediation guidance for Risk 4 features (MERGE, table-valued parameters, global temp tables, locking hints, PIVOT, UNPIVOT), THE Work_Item_Generator SHALL provide a recommended PostgreSQL design pattern with an explanation of the architectural differences between the SQL Server approach and the PostgreSQL approach
4. WHEN generating remediation guidance for Risk 5 features (SQL CLR, Service Broker, Linked Servers, XML methods, OPENQUERY, OPENROWSET, FileStream, Memory Optimized), THE Work_Item_Generator SHALL provide a high-level migration strategy identifying PostgreSQL alternatives or third-party solutions and flag the item as requiring architectural review
5. THE Work_Item_Generator SHALL include in the remediation guidance at least one "before" code example taken from the actual assessed SQL and one "after" code example showing the PostgreSQL equivalent transformation
6. IF the detected feature does not have a known PostgreSQL equivalent mapping in the guidance knowledge base, THEN THE Work_Item_Generator SHALL indicate that manual analysis is required, reference the PostgreSQL documentation area most relevant to the construct, and assign the flag "requires-research"

### Requirement 5: Priority and Effort Calculation

**User Story:** As a project manager, I want work items prioritized by weighted impact and estimated effort, so that I can plan sprints and allocate resources effectively.

#### Acceptance Criteria

1. THE Work_Item_Generator SHALL calculate a Priority_Score for each work item as the sum of Weighted_Risk values (Risk_Score multiplied by execution frequency multiplied by business importance) across all statements grouped into that work item
2. THE Work_Item_Generator SHALL assign a priority label to each work item: "Critical" when Priority_Score is in the top 10 percent of all work items, "High" when in the 70th to 89th percentile, "Medium" when in the 30th to 69th percentile, and "Low" when below the 30th percentile
3. THE Work_Item_Generator SHALL estimate effort for each work item by multiplying the per-statement effort range (defined by risk level) by the number of affected statements, applying a complexity reduction factor of 0.7 for each additional statement beyond the first (recognizing that similar fixes in the same object require less incremental effort)
4. WHEN two work items have equal Priority_Score values, THE Work_Item_Generator SHALL order them by risk level descending, then by affected statement count descending
5. THE Work_Item_Generator SHALL include a total effort summary aggregating the minimum and maximum hours across all generated work items
6. IF a work item contains statements with different risk levels due to multi-feature grouping, THEN THE Work_Item_Generator SHALL use the highest risk level for effort estimation and priority calculation

### Requirement 6: JSON Output Format

**User Story:** As a downstream automation tool, I want work items in a structured JSON format, so that I can programmatically import them into Jira, Azure DevOps, or other project management systems.

#### Acceptance Criteria

1. THE Work_Item_Generator SHALL produce a JSON output file containing a metadata section with generation timestamp, source assessment file path, total work item count, and total estimated effort range
2. THE Work_Item_Generator SHALL produce each work item in the JSON output as an object containing: id (string, unique sequential identifier), title (string), description (string), sqlServerPattern (string with code excerpt), postgresEquivalent (string with code example), affectedObjects (array of objects with name, type, and statementCount), riskLevel (integer 1-5), priority (string: Critical, High, Medium, or Low), priorityScore (numeric), estimatedEffort (object with minHours and maxHours), acceptanceCriteria (array of strings), remediationGuidance (string), and tags (array of strings)
3. THE Work_Item_Generator SHALL include in the tags array for each work item: the risk level label ("risk-1" through "risk-5"), the feature category ("query-feature", "function-usage", "temporary-object", "transaction-feature", or "server-feature"), and the conversion category ("automatic", "semi-automatic", or "manual")
4. THE Work_Item_Generator SHALL order work items in the JSON output array by Priority_Score descending
5. THE Work_Item_Generator SHALL produce valid JSON that conforms to a published JSON schema distributed with the Work_Item_Generator
6. IF the Work_Item_Generator cannot write the JSON output file to the specified path, THEN THE Work_Item_Generator SHALL report an error indicating the file write failure and the target path

### Requirement 7: Markdown Output Format

**User Story:** As a migration engineer, I want a human-readable markdown report of work items, so that I can review the task list without specialized tooling and share it with stakeholders who do not use project management software.

#### Acceptance Criteria

1. WHEN the markdown output option is enabled, THE Work_Item_Generator SHALL produce a markdown file containing a summary section with total work item count, effort estimates, and risk distribution of work items by priority level
2. WHEN generating markdown output, THE Work_Item_Generator SHALL format each work item as a markdown section with heading (title), description paragraph, a fenced code block showing the SQL Server pattern, a fenced code block showing the PostgreSQL equivalent, a bullet list of affected objects, and a numbered list of acceptance criteria
3. WHEN generating markdown output, THE Work_Item_Generator SHALL organize work items under priority group headings (Critical, High, Medium, Low) with items sorted by Priority_Score descending within each group
4. WHEN generating markdown output, THE Work_Item_Generator SHALL include a table of contents at the top of the document with links to each priority group section
5. IF the markdown output path is not specified but markdown output is enabled, THEN THE Work_Item_Generator SHALL write the markdown file to the same directory as the JSON output with the filename "work-items.md"

### Requirement 8: Work Item Deduplication and Merging

**User Story:** As a migration engineer, I want the generator to avoid creating redundant tickets for the same logical issue, so that developers do not waste time on duplicate work.

#### Acceptance Criteria

1. WHEN two or more analyzed statements within the same database object produce identical detected feature names, THE Work_Item_Generator SHALL merge them into a single work item listing all statement locations rather than creating separate work items
2. WHEN merging statements into a single work item, THE Work_Item_Generator SHALL use the statement with the highest weighted risk as the primary example in the SQL Server pattern section
3. WHEN merging statements, THE Work_Item_Generator SHALL sum the execution frequencies of all merged statements to calculate the combined Priority_Score
4. THE Work_Item_Generator SHALL assign a unique identifier to each work item using the format "WI-{sequential_number}" where sequential_number starts at 001 and increments for each generated work item
5. IF the same database object appears in multiple work items due to containing multiple distinct feature types, THEN THE Work_Item_Generator SHALL include a cross-reference in each work item listing the related work item identifiers for the same object

### Requirement 9: Configuration and Extensibility

**User Story:** As a migration engineer, I want to configure grouping behavior and output options, so that I can tailor work item generation to my team's workflow preferences.

#### Acceptance Criteria

1. THE Work_Item_Generator SHALL accept a configuration specifying the output JSON file path, with a default value of "./work-items.json"
2. THE Work_Item_Generator SHALL accept a configuration specifying whether markdown output is enabled, with a default value of disabled
3. THE Work_Item_Generator SHALL accept a configuration specifying the markdown output file path, used only when markdown output is enabled
4. THE Work_Item_Generator SHALL accept a configuration specifying a minimum risk level filter (integer 1-5), causing the generator to produce work items only for statements at or above the specified risk level, with a default value of 1 (include all)
5. THE Work_Item_Generator SHALL accept a configuration specifying a maximum work item count limit, causing the generator to produce only the top N work items by Priority_Score, with a default of no limit
6. IF a configuration value is outside its valid range (risk level not 1-5, max count less than 1), THEN THE Work_Item_Generator SHALL report a validation error specifying the parameter name, the invalid value, and the valid range

### Requirement 10: Integration with Assessment Pipeline

**User Story:** As a migration engineer, I want work item generation available as both a standalone command and an optional pipeline stage, so that I can generate work items immediately after assessment or from previously saved assessment files.

#### Acceptance Criteria

1. THE Work_Item_Generator SHALL be invokable as a standalone CLI command that accepts an assessment JSON file path as input and produces work item output files
2. THE Work_Item_Generator SHALL be invokable as a pipeline stage within the existing Assessment_Engine, executing after the Report_Generator stage and receiving the Assessment_Report object directly
3. WHEN invoked as a pipeline stage, THE Work_Item_Generator SHALL not require the assessment to be serialized to disk before processing
4. WHEN invoked via CLI, THE Work_Item_Generator SHALL accept command-line arguments for: input assessment file path (required), output JSON path (optional), markdown output enabled flag (optional), markdown output path (optional), minimum risk level filter (optional), and maximum work item count (optional)
5. IF the Work_Item_Generator is invoked via CLI without the required input file path argument, THEN THE Work_Item_Generator SHALL display a usage message listing all available arguments with their descriptions and default values
