# Requirements Document

## Introduction

The Migration Assessment Engine is a C# (.NET 8) component that analyzes a Microsoft SQL Server environment and produces an objective migration readiness assessment for PostgreSQL. It collects data from Query Store, Extended Events, database metadata, and SQL Server-specific feature usage, then parses captured SQL statements into an AST, assigns risk scores, and generates a comprehensive report with a migration readiness score. The engine integrates with the existing ODBC_TSQL_To_SQL solution and produces machine-readable JSON output for downstream consumption by schema conversion and SQL translation middleware.

## Glossary

- **Assessment_Engine**: The top-level system that orchestrates data collection, statement analysis, risk scoring, and report generation
- **Data_Collector**: The subsystem responsible for gathering SQL statements, metadata, and feature usage from a SQL Server instance
- **Statement_Analyzer**: The subsystem that parses T-SQL statements into an AST and identifies PostgreSQL-incompatible constructs
- **Risk_Scorer**: The subsystem that assigns risk levels (1-5) to analyzed statements and computes weighted complexity scores
- **Report_Generator**: The subsystem that produces the executive summary, risk breakdown, and migration recommendations
- **AST**: Abstract Syntax Tree; a tree representation of parsed T-SQL statements
- **Query_Store**: A SQL Server feature that captures executed queries, execution statistics, and query plans
- **Extended_Events**: A SQL Server lightweight tracing system for capturing ad hoc SQL, procedure execution, and dynamic SQL
- **Risk_Score**: An integer from 1 to 5 indicating the difficulty of converting a SQL construct to PostgreSQL
- **Weighted_Risk**: The product of Risk_Score, execution frequency, and business importance for a given statement
- **Migration_Readiness_Score**: A value from 0 to 100 indicating overall suitability for PostgreSQL migration
- **Feature_Detection_Matrix**: A catalog of SQL Server-specific features and their occurrence counts in the assessed environment
- **ScriptDom**: Microsoft.SqlServer.TransactSql.ScriptDom; a .NET library for parsing T-SQL into an AST

## Requirements

### Requirement 1: Query Store Data Collection

**User Story:** As a database architect, I want the engine to collect executed SQL statements and their performance metrics from Query Store, so that I can assess real workload complexity.

#### Acceptance Criteria

1. WHEN connected to a SQL Server instance with Query Store enabled and in READ_WRITE or READ_ONLY state, THE Data_Collector SHALL retrieve all distinct SQL statement texts from Query Store, identified by unique query_hash, for the Query Store's configured retention period
2. WHEN retrieving Query Store data, THE Data_Collector SHALL capture execution count, average duration in milliseconds, CPU consumption in milliseconds, and logical reads (page count) for each statement
3. WHEN retrieving Query Store data, THE Data_Collector SHALL capture the execution plan identifier (plan_id) and plan_hash for each statement
4. IF Query Store is disabled on the target database, THEN THE Data_Collector SHALL log a warning indicating Query Store is unavailable and continue assessment using Extended Events and Database Metadata collection
5. IF Query Store is in an ERROR state on the target database, THEN THE Data_Collector SHALL log a warning indicating the error state and continue assessment using Extended Events and Database Metadata collection
6. IF the Query Store data retrieval query does not complete within 120 seconds, THEN THE Data_Collector SHALL terminate the query, log a timeout warning, and continue assessment using Extended Events and Database Metadata collection

### Requirement 2: Extended Events Data Collection

**User Story:** As a database architect, I want the engine to capture ad hoc SQL and procedural execution patterns via Extended Events, so that I can assess dynamic and procedural workloads not visible in Query Store alone.

#### Acceptance Criteria

1. WHEN an Extended Events session is configured and active, THE Data_Collector SHALL capture each ad hoc SQL statement executed against the database, recording the full SQL text, execution timestamp, database name, and executing principal
2. WHEN an Extended Events session is configured and active, THE Data_Collector SHALL capture each stored procedure execution including the fully qualified procedure name and up to 128 input parameters with their declared data types and values (truncating parameter values exceeding 4,000 characters)
3. WHEN an Extended Events session is configured and active, THE Data_Collector SHALL capture dynamic SQL execution via sp_executesql and EXEC(), recording the complete dynamically constructed SQL text separately from the outer calling statement
4. WHEN an Extended Events session is configured and active, THE Data_Collector SHALL capture each CREATE TABLE statement targeting #local or ##global temporary tables, recording the full DDL statement text and the calling context (stored procedure or batch identifier)
5. WHEN an Extended Events session is configured and active, THE Data_Collector SHALL capture TRY/CATCH block usage by recording the full statement text of each batch or procedure body that contains a TRY/CATCH construct
6. THE Data_Collector SHALL preserve captured SQL text without truncation up to 65,536 characters per statement to ensure the downstream AST parser can process complete statements
7. IF Extended Events data is unavailable or the configured session is not in a running state, THEN THE Data_Collector SHALL log a warning message indicating the unavailability reason and continue assessment using other available data sources without terminating
8. IF a single Extended Events collection cycle yields more than 100,000 events, THEN THE Data_Collector SHALL process events in batches of no more than 10,000 and report the total event count collected upon completion

### Requirement 3: Database Metadata Collection

**User Story:** As a database architect, I want the engine to inventory all database objects, so that I can understand the full scope of the migration.

#### Acceptance Criteria

1. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve metadata for all user-defined tables across all non-system schemas (excluding sys, INFORMATION_SCHEMA, and schemas owned by system principals), including column name, ordinal position, data type with precision/scale/max length, nullable status, identity specification, and computed column definitions
2. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve all index definitions including clustered, non-clustered, filtered, and columnstore indexes, capturing index columns, included columns, filter expressions, and fill factor
3. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve all constraint definitions including primary keys, unique constraints, check constraints with their expressions, and default constraints with their default value expressions
4. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve all foreign key relationships including parent table, parent columns, referenced table, referenced columns, update rule, and delete rule
5. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve the source text for all user-defined views, triggers, functions, and stored procedures across all non-system schemas
6. WHEN connected to a SQL Server instance, THE Data_Collector SHALL retrieve all synonym definitions including base object name and target object reference
7. IF a database object's source text is encrypted or inaccessible due to insufficient permissions, THEN THE Data_Collector SHALL record the object name and schema with an indication that the source text is unavailable and the reason for inaccessibility, and SHALL continue collecting metadata for remaining objects
8. WHEN metadata collection is complete, THE Data_Collector SHALL produce output in a structured machine-readable format that includes the full object inventory organized by schema and object type

### Requirement 4: SQL Server Feature Detection

**User Story:** As a database architect, I want the engine to detect SQL Server-specific features in use, so that I can identify architectural dependencies that complicate migration.

#### Acceptance Criteria

1. WHEN analyzing the target database, THE Data_Collector SHALL detect the presence of SQL CLR assemblies and report each assembly name, permission set, and referenced methods
2. WHEN analyzing the target database, THE Data_Collector SHALL detect Service Broker objects and report the name and state of each queue, service, and contract
3. WHEN analyzing the target database, THE Data_Collector SHALL detect SQL Agent jobs that reference the assessed database and report each job name, schedule status, and referenced database objects
4. WHEN analyzing the target database, THE Data_Collector SHALL detect Change Data Capture (CDC) and Change Tracking configurations and report each enabled table name and the configuration state (enabled or disabled)
5. WHEN analyzing the target database, THE Data_Collector SHALL detect replication configurations and report each publisher, subscriber, and article name with its replication type
6. WHEN analyzing the target database, THE Data_Collector SHALL detect Linked Server references in stored procedures and views and report the linked server name and the referencing object name for each occurrence
7. WHEN analyzing the target database, THE Data_Collector SHALL detect Full Text Search catalogs and indexes and report each catalog name and the table and columns associated with each full-text index
8. WHEN analyzing the target database, THE Data_Collector SHALL detect FileStream and FileTable objects and report each filegroup name and associated table name
9. WHEN analyzing the target database, THE Data_Collector SHALL detect XML indexes and report each index name, the parent table, and the XML column name
10. WHEN analyzing the target database, THE Data_Collector SHALL detect Temporal Tables (system-versioned tables) and report each table name, its history table name, and the period columns
11. WHEN analyzing the target database, THE Data_Collector SHALL detect Memory Optimized Tables and natively compiled stored procedures and report each object name and its durability setting
12. WHEN analyzing the target database, THE Data_Collector SHALL detect table and index partitioning schemes and report each partition scheme name, partition function name, and the number of partitions
13. IF the Data_Collector lacks sufficient permissions to query metadata for a specific feature category, THEN THE Data_Collector SHALL report that feature category as "inaccessible" with an indication of the required permission
14. WHEN feature detection is complete, THE Data_Collector SHALL report a count of detected instances for each feature category alongside the detailed inventory, including a count of zero for feature categories where no instances were found

### Requirement 5: AST-Based Statement Parsing

**User Story:** As a database architect, I want the engine to parse T-SQL statements into an AST using a proper parser, so that analysis is accurate and not subject to regex-based false positives.

#### Acceptance Criteria

1. THE Statement_Analyzer SHALL parse T-SQL statements using Microsoft.SqlServer.TransactSql.ScriptDom or an equivalent grammar-based parser
2. THE Statement_Analyzer SHALL NOT use regular expressions as the primary mechanism for identifying SQL constructs
3. WHEN a statement cannot be parsed, THE Statement_Analyzer SHALL record the parse failure including the original statement text, the error description returned by the parser, and the line number and column position of the first error
4. WHEN parsing a multi-statement batch separated by GO batch delimiters or semicolons, THE Statement_Analyzer SHALL split the batch and analyze each statement independently, preserving the ordinal position of each statement within the batch
5. THE Statement_Analyzer SHALL identify the statement type for each parsed statement, classifying it as one of: SELECT, INSERT, UPDATE, DELETE, MERGE, DDL, DCL, TCL (transaction control), or procedural
6. IF a parsed statement does not match any defined statement type classification, THEN THE Statement_Analyzer SHALL classify it as "Unknown" and include it in the parse results with its original text

### Requirement 6: Feature Detection in Parsed Statements

**User Story:** As a database architect, I want the engine to identify all SQL Server-specific query features in parsed statements, so that I can understand the conversion scope.

#### Acceptance Criteria

1. WHEN analyzing a parsed statement, THE Statement_Analyzer SHALL detect and record each occurrence of query features including TOP, OFFSET FETCH, MERGE, OUTPUT clause, CROSS APPLY, OUTER APPLY, PIVOT, UNPIVOT, dynamic SQL via EXEC(), OPENQUERY, and OPENROWSET, reporting for each occurrence the feature name, the source statement identifier, and the location within the statement
2. WHEN analyzing a parsed statement, THE Statement_Analyzer SHALL detect and record each occurrence of SQL Server function usage including GETDATE, DATEADD, DATEDIFF, DATEPART, ISNULL, CHARINDEX, PATINDEX, STUFF, XML methods, and JSON methods, reporting for each occurrence the function name, the source statement identifier, and the location within the statement
3. WHEN analyzing a parsed statement, THE Statement_Analyzer SHALL detect and record each occurrence of temporary object usage including #temp tables, ##global temp tables, table variables, and table-valued parameters, reporting for each occurrence the object type, the source statement identifier, and the location within the statement
4. WHEN analyzing a parsed statement, THE Statement_Analyzer SHALL detect and record each occurrence of transaction features including TRY/CATCH blocks, explicit transactions, savepoints, and locking hints (NOLOCK, ROWLOCK, UPDLOCK), reporting for each occurrence the feature name, the source statement identifier, and the location within the statement
5. WHEN a parsed statement contains multiple SQL Server-specific features, THE Statement_Analyzer SHALL detect and record each feature independently, producing one record per feature occurrence regardless of how many distinct features appear in the same statement
6. IF a parsed statement cannot be fully analyzed due to unrecognized syntax, THEN THE Statement_Analyzer SHALL record a partial analysis result containing any features successfully detected up to the point of failure, along with an indication that analysis was incomplete and the position where analysis stopped

### Requirement 7: Risk Score Assignment

**User Story:** As a database architect, I want each statement to receive a risk score from 1-5, so that I can prioritize conversion effort.

#### Acceptance Criteria

1. WHEN a statement contains only standard SQL compatible with PostgreSQL (basic SELECT, INSERT, UPDATE, DELETE without SQL Server extensions), THE Risk_Scorer SHALL assign Risk_Score 1 (Trivial, 0-5 minutes conversion)
2. WHEN a statement contains straightforward syntax translations (TOP, ISNULL, GETDATE, LEN, string concatenation with +), THE Risk_Scorer SHALL assign Risk_Score 2 (Low, 5-30 minutes conversion)
3. WHEN a statement contains procedural changes (TRY/CATCH, dynamic SQL, SQL Server-specific CTE syntax, identity handling), THE Risk_Scorer SHALL assign Risk_Score 3 (Moderate, 30 minutes to 4 hours conversion)
4. WHEN a statement contains constructs requiring significant redesign (MERGE, table-valued parameters, multi-statement table-valued functions, global temp tables, locking hints), THE Risk_Scorer SHALL assign Risk_Score 4 (High, 4-40 hours conversion)
5. WHEN a statement references architectural features (SQL CLR, Service Broker, Linked Servers, Replication, FileStream, Memory Optimized Tables), THE Risk_Scorer SHALL assign Risk_Score 5 (Critical, 40+ hours conversion)
6. WHEN a statement contains multiple risk factors, THE Risk_Scorer SHALL assign the highest applicable Risk_Score
7. IF a statement cannot be parsed by the Statement_Analyzer, THEN THE Risk_Scorer SHALL assign Risk_Score 3 (Moderate) as a default and flag the statement for manual review

### Requirement 8: Weighted Complexity Calculation

**User Story:** As a database architect, I want risk scores weighted by execution frequency and business importance, so that high-traffic workloads are prioritized.

#### Acceptance Criteria

1. THE Risk_Scorer SHALL calculate Weighted_Risk for each statement as: Risk_Score (integer 1-5) multiplied by execution frequency (integer 1 or greater) multiplied by business importance factor (numeric value from 1.0 to 5.0 inclusive, where 1.0 is lowest importance and 5.0 is highest importance)
2. IF execution frequency data is available from Query Store for a statement, THEN THE Risk_Scorer SHALL use the captured execution count as the frequency factor
3. IF execution frequency data is unavailable for a statement, THEN THE Risk_Scorer SHALL use a default frequency of 1
4. IF a business importance factor has not been assigned to a statement, THEN THE Risk_Scorer SHALL use a default business importance factor of 1.0
5. THE Risk_Scorer SHALL rank statements by Weighted_Risk in descending order, using Risk_Score as secondary sort (descending) for statements with equal Weighted_Risk values

### Requirement 9: Migration Readiness Score Calculation

**User Story:** As a database architect, I want a single 0-100 readiness score, so that I can quickly communicate migration feasibility to stakeholders.

#### Acceptance Criteria

1. THE Report_Generator SHALL compute a Migration_Readiness_Score as an integer from 0 to 100 by aggregating the weighted risk scores (risk level × execution frequency) across all assessed statements and features, where a score of 100 indicates all items are Risk 1 (trivial) and a score of 0 indicates all items are Risk 5 (critical)
2. WHEN the Migration_Readiness_Score is 90-100, THE Report_Generator SHALL classify the database as "Excellent Candidate"
3. WHEN the Migration_Readiness_Score is 76-89, THE Report_Generator SHALL classify the database as "Good Candidate"
4. WHEN the Migration_Readiness_Score is 51-75, THE Report_Generator SHALL classify the database as "Moderate Candidate - Significant Work Required"
5. WHEN the Migration_Readiness_Score is 26-50, THE Report_Generator SHALL classify the database as "High Risk"
6. WHEN the Migration_Readiness_Score is 0-25, THE Report_Generator SHALL classify the database as "Not Recommended for Migration"
7. IF the assessment contains zero analyzed statements and zero detected features, THEN THE Report_Generator SHALL not compute a Migration_Readiness_Score and SHALL indicate that insufficient data is available for scoring

### Requirement 10: Executive Summary Report

**User Story:** As a database architect, I want a comprehensive report with risk breakdown and migration recommendations, so that I can present findings to decision makers.

#### Acceptance Criteria

1. THE Report_Generator SHALL produce an Executive Summary containing the Migration_Readiness_Score (0-100 scale), total statement count, and risk level distribution showing the count and percentage of statements at each risk level (1 through 5)
2. THE Report_Generator SHALL produce a Risk Breakdown table showing the count of statements at each risk level (1 through 5)
3. THE Report_Generator SHALL produce a Top Migration Challenges section listing up to 10 items, ranking the most complex stored procedures, functions, views, and unsupported features in descending order of Weighted_Risk (Risk Score × Frequency × Business Importance)
4. THE Report_Generator SHALL produce an Estimated Migration Effort section with hour estimates for schema conversion, code conversion, testing, data migration, and performance tuning, where each category displays a numeric hour range (minimum and maximum)
5. THE Report_Generator SHALL produce a Migration Effort classification of Small (1-100 total hours), Medium (101-500 total hours), Large (501-2000 total hours), or Enterprise (greater than 2000 total hours) based on the sum of all category hour estimates
6. THE Report_Generator SHALL produce a Migration Recommendation selecting one of: Direct PostgreSQL Migration, PostgreSQL Migration with Compatibility Middleware, Partial Migration, or Remain on SQL Server, accompanied by reasoning that references the Migration_Readiness_Score, the count of Risk 4 and Risk 5 statements, and the presence of any SQL Server-specific features requiring architectural replacement
7. IF the assessment input contains zero analyzed statements, THEN THE Report_Generator SHALL produce the report with a Migration_Readiness_Score of 0, zero counts for all risk levels, zero hour estimates for all effort categories, and a classification of Small

### Requirement 11: Machine-Readable JSON Output

**User Story:** As a downstream system consumer, I want the assessment results in structured JSON, so that automated schema conversion and SQL translation tools can consume the findings.

#### Acceptance Criteria

1. THE Report_Generator SHALL produce a JSON output file containing the object inventory with each entry including the object type (table, view, trigger, function, stored procedure, synonym, index, constraint, foreign key), object name, and source schema
2. THE Report_Generator SHALL include in the JSON output a feature inventory listing each detected SQL Server-specific feature from the Feature_Detection_Matrix and its integer occurrence count
3. THE Report_Generator SHALL include in the JSON output the Risk_Score, Weighted_Risk, and original SQL statement text for each analyzed statement
4. THE Report_Generator SHALL include in the JSON output translation candidates for each analyzed statement categorized as automatic (Risk_Score 1-2), semi-automatic (Risk_Score 3), or manual (Risk_Score 4-5) conversion
5. THE Report_Generator SHALL include in the JSON output the migration recommendation (one of: Direct PostgreSQL Migration, PostgreSQL Migration with Compatibility Middleware, Partial Migration, or Remain on SQL Server) and the Migration_Readiness_Score
6. THE Report_Generator SHALL produce valid JSON that passes validation against the published JSON schema document distributed with the Assessment_Engine
7. IF the Report_Generator cannot write the JSON output file to the target path, THEN THE Report_Generator SHALL report an error indicating the file write failure and the target path

### Requirement 12: Connection and Error Handling

**User Story:** As a database architect, I want the engine to handle connection failures and partial data gracefully, so that the assessment completes even when some data sources are unavailable.

#### Acceptance Criteria

1. IF the Assessment_Engine cannot establish a connection to the target SQL Server instance within 30 seconds, THEN THE Assessment_Engine SHALL retry the connection up to 3 times with a 5-second delay between attempts before reporting the failure
2. IF all connection retry attempts are exhausted, THEN THE Assessment_Engine SHALL report the connection failure including the server address, error code, and error description, and terminate with a non-zero exit status
3. IF a data collection query fails or exceeds a 120-second timeout, THEN THE Data_Collector SHALL log the error with the query source name and error description, skip the failed collection, and continue with remaining data sources
4. IF the Statement_Analyzer encounters a statement that causes an unhandled exception, THEN THE Statement_Analyzer SHALL log the statement text (first 1000 characters), exception type, and exception message, and continue analyzing remaining statements
5. IF all data collection sources fail, THEN THE Assessment_Engine SHALL terminate and report that no data could be collected rather than producing an empty assessment
6. THE Assessment_Engine SHALL include in the final report a failure summary listing each skipped or failed collection by source name, the reason for failure, and the total count of successful versus failed collections
