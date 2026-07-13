# Requirements Document

## Introduction

The AI-Assisted Schema Conversion application migrates Microsoft SQL Server databases to PostgreSQL. It combines deterministic rule-based conversion for well-defined mappings with AI-assisted conversion (via Amazon Bedrock) for objects requiring semantic understanding. The primary design goal is to minimize application code changes after migration by preserving calling patterns, naming conventions, and interface compatibility. This feature is a sibling project within the existing ODBC_TSQL_To_SQL repository alongside the MigrationAssessment solution.

## Glossary

- **Conversion_Engine**: The core orchestrator that routes database objects to either the Rule_Based_Converter or the AI_Converter based on object type and complexity classification.
- **Rule_Based_Converter**: The deterministic conversion subsystem that transforms SQL Server objects with well-defined mappings into PostgreSQL equivalents using repeatable, codified rules.
- **AI_Converter**: The subsystem that invokes an LLM hosted in Amazon Bedrock to convert SQL Server objects requiring semantic understanding into functionally equivalent PostgreSQL code.
- **Bedrock_Client**: The abstraction layer over the AWS SDK that communicates with Amazon Bedrock, supports model selection through configuration, and records all prompts and responses for auditing.
- **Conversion_Report**: A detailed output document that records each converted object, the conversion method used, confidence level, assumptions made, and any items flagged for manual review.
- **Schema_Object**: Any database object subject to conversion, including tables, indexes, constraints, views, stored procedures, functions, triggers, sequences, user-defined types, and synonyms.
- **Wrapper_Object**: A PostgreSQL function or view generated to preserve the original SQL Server invocation pattern when direct translation would change the calling interface.
- **Conversion_Session**: A unit of work representing the conversion of one or more schema objects, supporting incremental processing and reruns.
- **Audit_Log**: A persistent record of all AI prompts, responses, model identifiers, timestamps, and prompt template versions generated during AI-assisted conversion.
- **Manual_Review_Flag**: A marker applied to converted objects where the conversion confidence is below threshold or where assumptions were made that require human verification.
- **Type_Mapping_Ruleset**: The codified set of deterministic mappings from SQL Server data types to PostgreSQL data types used by the Rule_Based_Converter.
- **Function_Mapping_Ruleset**: The codified set of deterministic mappings from SQL Server built-in functions and expressions to PostgreSQL equivalents used by the Rule_Based_Converter.
- **Prompt_Template**: A versioned template used by the AI_Converter to construct prompts for each category of conversion (stored procedure, function, trigger, complex object, view).
- **Schema_Mapping_Table**: A configurable lookup that maps source SQL Server schema names to target PostgreSQL schema names.

## Out of Scope

The following SQL Server features are explicitly outside the scope of this application and SHALL NOT be converted. If encountered, the Conversion_Engine SHALL record the object in the Conversion_Report as out-of-scope.

- Linked servers and distributed queries
- SQL Agent jobs and schedules
- Service Broker objects (queues, contracts, services)
- Filestream and FileTable objects
- Filegroup assignments
- Full-text indexes and catalogs
- Replication objects
- Database mail configuration
- CLR assemblies and CLR-based objects (CLR stored procedures, CLR functions, CLR triggers, CLR user-defined types)
- Always Encrypted column configurations
- Row-level security policies
- Data masking rules

## Requirements

### Requirement 1: Source Schema Acquisition

**User Story:** As a database engineer, I want to specify a SQL Server source (connection string or DDL script files) and have the system extract all schema objects for conversion, so that I have a complete inventory before beginning conversion.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL accept as input either a live SQL Server connection string or a set of DDL script files containing SQL Server object definitions.
2. WHEN a connection string is provided, THE Conversion_Engine SHALL extract all schema object definitions from the specified database using SQL Server system catalog views (sys.objects, sys.columns, sys.types, sys.sql_modules, and related views).
3. WHEN DDL script files are provided, THE Conversion_Engine SHALL parse the files and extract individual object definitions.
4. THE Conversion_Engine SHALL produce an inventory of all discovered Schema_Objects including object name, schema name, object type, and dependency relationships before beginning conversion.
5. IF a connection string is provided using SQL Authentication, THE Conversion_Engine SHALL support both Windows Authentication and SQL Authentication.
6. THE Conversion_Engine SHALL NOT log or persist connection strings containing passwords in any output file, Conversion_Report, or Audit_Log.
7. IF the source extraction fails (connection error, permission denied, parse error), THEN THE Conversion_Engine SHALL halt processing and report an error message indicating the failure reason.

### Requirement 2: Rule-Based Table and Column Conversion

**User Story:** As a database engineer, I want tables and columns to be converted using deterministic rules, so that I get consistent, repeatable results for straightforward schema objects.

#### Acceptance Criteria

1. WHEN a SQL Server table definition is provided, THE Rule_Based_Converter SHALL produce a PostgreSQL CREATE TABLE statement that preserves the table name, column names, column order, and nullability.
2. WHEN a SQL Server data type is encountered, THE Rule_Based_Converter SHALL map the data type to a PostgreSQL equivalent using the Type_Mapping_Ruleset.
3. WHEN a SQL Server IDENTITY column is encountered, THE Rule_Based_Converter SHALL convert the column to use a PostgreSQL GENERATED BY DEFAULT AS IDENTITY clause preserving the seed and increment values.
4. WHEN a SQL Server DEFAULT constraint is encountered, THE Rule_Based_Converter SHALL translate the default expression to the PostgreSQL equivalent and apply it to the corresponding column. IF the default expression contains a function or pattern that cannot be deterministically translated using the Function_Mapping_Ruleset, THEN THE Rule_Based_Converter SHALL route the containing object to the AI_Converter.
5. WHEN a SQL Server computed column expression contains only operators and functions that have defined PostgreSQL equivalents in the Function_Mapping_Ruleset, THE Rule_Based_Converter SHALL convert the expression to a PostgreSQL GENERATED ALWAYS AS (expression) STORED column. IF the computed column expression contains functions or patterns without a defined PostgreSQL equivalent, THEN THE Rule_Based_Converter SHALL apply a Manual_Review_Flag to the column indicating the unsupported expression.
6. WHEN a SQL Server table uses a schema other than dbo, THE Rule_Based_Converter SHALL apply the Schema_Mapping_Table to determine the target PostgreSQL schema for the generated CREATE TABLE statement.

### Requirement 3: Rule-Based Constraint and Index Conversion

**User Story:** As a database engineer, I want primary keys, foreign keys, indexes, and constraints to be converted deterministically, so that referential integrity and performance characteristics are preserved.

#### Acceptance Criteria

1. WHEN a SQL Server PRIMARY KEY constraint is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL PRIMARY KEY constraint preserving the column list, column sort order (ASC/DESC), and constraint name.
2. WHEN a SQL Server FOREIGN KEY constraint is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL FOREIGN KEY constraint preserving the referenced table and schema, columns, ON DELETE action, and ON UPDATE action, defaulting to NO ACTION when no referential action is specified in the source.
3. WHEN a SQL Server UNIQUE constraint is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL UNIQUE constraint preserving the column list and constraint name.
4. WHEN a SQL Server CHECK constraint is encountered, THE Rule_Based_Converter SHALL translate the check expression to PostgreSQL syntax using the Function_Mapping_Ruleset and preserve the constraint name.
5. IF a CHECK constraint expression contains SQL Server-specific functions or patterns that cannot be deterministically translated using the Function_Mapping_Ruleset, THEN THE Rule_Based_Converter SHALL route the containing object to the AI_Converter for semantic translation.
6. WHEN a SQL Server index is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL CREATE INDEX statement preserving the index name, column list, column sort order (ASC/DESC), included columns (as covering index columns using the INCLUDE clause), and uniqueness.
7. WHEN a SQL Server filtered index is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL partial index with a WHERE clause that is functionally equivalent to the source filter predicate.
8. IF a filtered index WHERE clause contains expressions that cannot be deterministically translated, THEN THE Rule_Based_Converter SHALL route the index to the AI_Converter and apply a Manual_Review_Flag.
9. WHEN a SQL Server clustered index is encountered, THE Rule_Based_Converter SHALL produce a standard PostgreSQL B-tree index and record a compatibility note in the Conversion_Report indicating that physical row ordering (clustering) is not preserved in PostgreSQL.

### Requirement 4: Rule-Based Sequence and View Conversion

**User Story:** As a database engineer, I want sequences and straightforward views to be converted using deterministic rules, so that dependent application logic continues to work unchanged.

#### Acceptance Criteria

1. WHEN a SQL Server SEQUENCE object is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL CREATE SEQUENCE statement preserving the data type (mapped per the Type_Mapping_Ruleset), start value, increment, minimum value, maximum value, cycle behavior, and cache setting.
2. WHEN a SQL Server view is encountered whose body contains only constructs that the Rule_Based_Converter has a defined translation rule for (joins, subqueries, aggregates, CASE expressions, and functions listed in the Function_Mapping_Ruleset), THE Rule_Based_Converter SHALL produce a PostgreSQL CREATE VIEW statement with equivalent SELECT logic, preserving column aliases and column order.
3. IF a SQL Server view contains any function, operator, or syntax pattern for which no deterministic mapping rule exists in the Rule_Based_Converter's rulesets, THEN THE Rule_Based_Converter SHALL route the view to the AI_Converter for semantic translation and record the routed object name and the unmapped construct in the Conversion_Report.
4. WHEN a SQL Server view includes the WITH CHECK OPTION clause, THE Rule_Based_Converter SHALL preserve the WITH CHECK OPTION clause in the generated PostgreSQL CREATE VIEW statement.
5. WHEN a SQL Server view includes the WITH SCHEMABINDING option, THE Rule_Based_Converter SHALL omit the SCHEMABINDING directive (which has no PostgreSQL equivalent) and record a compatibility note in the Conversion_Report indicating the omission.
6. WHEN a SQL Server view references objects in a schema other than dbo, THE Rule_Based_Converter SHALL apply the Schema_Mapping_Table to update schema-qualified references in the generated view body.

### Requirement 5: Schema and Namespace Handling

**User Story:** As a database engineer, I want SQL Server schemas to be mapped to PostgreSQL schemas so that namespace isolation is preserved and cross-schema references remain valid.

#### Acceptance Criteria

1. THE Rule_Based_Converter SHALL map each SQL Server schema to a PostgreSQL schema with the same name by default.
2. THE Rule_Based_Converter SHALL generate CREATE SCHEMA statements for each schema encountered in the source database.
3. THE Conversion_Engine SHALL provide a configurable Schema_Mapping_Table to allow remapping source schema names to different target schema names.
4. WHEN objects reference other objects using schema-qualified names, THE Conversion_Engine SHALL update the schema prefix according to the Schema_Mapping_Table in all generated output.
5. WHEN a SQL Server synonym is encountered, THE Rule_Based_Converter SHALL produce a PostgreSQL view that selects all columns from the synonym's target object, preserving the synonym name as the view name.

### Requirement 6: User-Defined Type Conversion

**User Story:** As a database engineer, I want user-defined types (alias types, table types) to be converted so that dependent objects can reference them in PostgreSQL.

#### Acceptance Criteria

1. WHEN a SQL Server alias type (CREATE TYPE ... FROM base_type) is encountered, THE Rule_Based_Converter SHALL create a PostgreSQL DOMAIN with the equivalent base type (mapped per the Type_Mapping_Ruleset) and any associated constraints (NOT NULL, CHECK).
2. WHEN a SQL Server table type (CREATE TYPE ... AS TABLE) is encountered, THE Rule_Based_Converter SHALL create a PostgreSQL composite type (CREATE TYPE ... AS) with equivalent column definitions.
3. WHEN a SQL Server CLR user-defined type is encountered, THE Conversion_Engine SHALL apply a Manual_Review_Flag and record the CLR type in the Conversion_Report as requiring manual intervention (CLR objects are out of scope).
4. THE Conversion_Engine SHALL process user-defined types before any objects that reference them to satisfy dependency ordering.

### Requirement 7: AI-Assisted Stored Procedure Conversion

**User Story:** As a database engineer, I want stored procedures to be converted by AI with semantic understanding, so that business logic is preserved and application calling patterns remain unchanged.

#### Acceptance Criteria

1. WHEN a SQL Server stored procedure that returns a result set is provided, THE AI_Converter SHALL produce a PostgreSQL function that returns a TABLE or SETOF record type preserving the original result set's column names, column order, and data types mapped according to the Type_Mapping_Ruleset.
2. WHEN a SQL Server stored procedure that performs work without returning rows is provided, THE AI_Converter SHALL produce a PostgreSQL PROCEDURE using the CREATE PROCEDURE syntax.
3. WHEN the original stored procedure uses OUTPUT parameters, THE AI_Converter SHALL map each parameter that is read before being written to an INOUT parameter, and each parameter that is only written to an OUT parameter, in the PostgreSQL function or procedure.
4. WHEN a converted procedure or function changes the invocation method, parameter names, parameter types, parameter order, or return type relative to the original stored procedure, THE AI_Converter SHALL generate a Wrapper_Object that exposes the original interface so that existing application call sites require no modification.
5. THE AI_Converter SHALL produce PostgreSQL code that preserves the same conditional branching, looping, data modifications, transaction commit/rollback points, and exception handling paths as the original stored procedure. THE AI_Converter SHALL include a statement of equivalence assumptions in the Conversion_Report for each converted object.
6. WHEN the AI_Converter cannot determine functional equivalence for a portion of a stored procedure with high confidence, THE AI_Converter SHALL apply a Manual_Review_Flag to the converted object identifying the specific code section and the reason equivalence could not be confirmed.

### Requirement 8: AI-Assisted Function and Trigger Conversion

**User Story:** As a database engineer, I want user-defined functions and triggers to be converted by AI, so that computed logic and data integrity rules are maintained in PostgreSQL.

#### Acceptance Criteria

1. WHEN a SQL Server scalar user-defined function is provided, THE AI_Converter SHALL produce a PostgreSQL function that preserves the function name, all parameter names and data types, the return data type mapped per the Type_Mapping_Ruleset, and the computational logic.
2. WHEN a SQL Server inline table-valued function is provided, THE AI_Converter SHALL produce a PostgreSQL function that returns a TABLE type matching the original column names and data types. WHEN a SQL Server multi-statement table-valued function is provided, THE AI_Converter SHALL produce a PostgreSQL function that returns a TABLE or SETOF record type preserving the declared result column names and data types.
3. WHEN a SQL Server trigger is provided, THE AI_Converter SHALL produce a PostgreSQL trigger function and corresponding CREATE TRIGGER statement preserving the firing event (INSERT, UPDATE, DELETE), timing (BEFORE, AFTER, INSTEAD OF), and granularity (FOR EACH ROW or FOR EACH STATEMENT), and SHALL translate references to the INSERTED and DELETED pseudo-tables to PostgreSQL NEW and OLD row variables or transition tables as appropriate to the trigger granularity.
4. WHEN the SQL Server function or trigger uses SQL Server-specific patterns such as dynamic SQL, cursors, or temporary tables, THE AI_Converter SHALL rewrite the logic using PostgreSQL constructs while preserving the same input-to-output behavior as the original.
5. WHEN a converted function would change the application calling pattern (parameter order, parameter types, or return type), THE AI_Converter SHALL generate a Wrapper_Object that exposes the original invocation interface.

### Requirement 9: AI-Assisted Complex Object Conversion

**User Story:** As a database engineer, I want complex SQL Server objects like dynamic SQL, cursor-based logic, temporary tables, and error handling patterns to be intelligently translated, so that I do not have to manually rewrite every procedural element.

#### Acceptance Criteria

1. WHEN dynamic SQL (EXEC or sp_executesql) is encountered within a database object, THE AI_Converter SHALL translate the dynamic SQL pattern to PostgreSQL EXECUTE or format() equivalents, preserving parameterized inputs as format() arguments or USING clause parameters.
2. WHEN cursor-based logic is encountered, THE AI_Converter SHALL convert the cursor to a PostgreSQL FOR loop over a query, a PostgreSQL REFCURSOR, or a set-based rewrite, and SHALL record the conversion approach chosen and rationale in the Conversion_Report.
3. WHEN SQL Server TRY/CATCH error handling is encountered, THE AI_Converter SHALL convert the pattern to PostgreSQL BEGIN/EXCEPTION blocks, mapping SQL Server error functions (ERROR_NUMBER, ERROR_MESSAGE, ERROR_SEVERITY, ERROR_STATE) to PostgreSQL exception variables (SQLSTATE, SQLERRM) and preserving the original handling branches for each caught error category.
4. WHEN SQL Server transaction control statements (BEGIN TRAN, COMMIT, ROLLBACK, SAVE TRANSACTION) are encountered within a PROCEDURE, THE AI_Converter SHALL produce equivalent PostgreSQL transaction control using BEGIN, COMMIT, ROLLBACK, and SAVEPOINT.
5. IF SQL Server transaction control statements are encountered within an object that will be converted to a PostgreSQL FUNCTION (which does not support transaction control), THEN THE AI_Converter SHALL apply a Manual_Review_Flag indicating the transaction control incompatibility and SHALL suggest refactoring to a PROCEDURE or removing the in-function transaction control.
6. WHEN a local temporary table (#tablename) is encountered within a stored procedure or function, THE AI_Converter SHALL convert it to a PostgreSQL temporary table with appropriate lifetime (CREATE TEMPORARY TABLE ... ON COMMIT DROP or session-scoped as appropriate to the usage pattern).
7. WHEN a global temporary table (##tablename) is encountered, THE AI_Converter SHALL apply a Manual_Review_Flag because PostgreSQL does not support cross-session temporary tables, and SHALL suggest alternatives in the Conversion_Report.
8. WHEN a table variable (@tablename) is encountered, THE AI_Converter SHALL convert the table variable to a PostgreSQL temporary table or a record array as appropriate to the usage pattern.
9. WHEN SQL Server locking hints (NOLOCK, UPDLOCK, ROWLOCK, TABLOCK, HOLDLOCK) are encountered, THE AI_Converter SHALL remove the hints and record a compatibility note in the Conversion_Report explaining the locking behavior difference.
10. WHEN a SQL Server-specific feature with no direct PostgreSQL equivalent is encountered, THE AI_Converter SHALL apply a Manual_Review_Flag and include in the Conversion_Report the feature name, an explanation of the incompatibility, and at least one suggested PostgreSQL alternative or workaround.

### Requirement 10: Conversion Routing and Orchestration

**User Story:** As a database engineer, I want the system to automatically determine whether each object should be handled by rules or AI, so that I do not have to manually classify every object.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL classify each Schema_Object as either rule-based-convertible or ai-assisted-convertible using the following deterministic rules: tables, constraints, indexes, sequences, user-defined alias types, user-defined table types, synonyms, and views containing only constructs with defined mapping rules are classified as rule-based-convertible; stored procedures, functions, triggers, and views containing SQL Server-specific syntax without defined mapping rules are classified as ai-assisted-convertible.
2. WHEN a Schema_Object is classified as rule-based-convertible, THE Conversion_Engine SHALL route the object to the Rule_Based_Converter.
3. WHEN a Schema_Object is classified as ai-assisted-convertible, THE Conversion_Engine SHALL route the object to the AI_Converter.
4. THE Conversion_Engine SHALL process objects respecting dependency order so that referenced objects are converted before dependent objects.
5. IF the AI_Converter produces output that fails PostgreSQL syntax validation or does not contain a complete DDL statement for the target object, THEN THE Conversion_Engine SHALL mark the object with a Manual_Review_Flag and continue processing remaining objects.
6. IF the Rule_Based_Converter encounters an object it cannot convert due to an unrecognized pattern or unmapped construct, THEN THE Conversion_Engine SHALL reclassify the object as ai-assisted-convertible and route it to the AI_Converter.
7. IF a circular dependency is detected among Schema_Objects, THEN THE Conversion_Engine SHALL break the cycle by creating the objects without body definitions first (e.g., CREATE FUNCTION with a placeholder body), then converting them in dependency order, and finally replacing the placeholder bodies with the converted implementations using CREATE OR REPLACE.
8. THE Conversion_Engine SHALL allow manual override of the classification for any Schema_Object, enabling the user to force an object to be processed by either the Rule_Based_Converter or the AI_Converter regardless of automatic classification.

### Requirement 11: Amazon Bedrock Integration

**User Story:** As a platform operator, I want the AI layer to be abstracted behind a configuration-driven interface to Amazon Bedrock, so that I can switch models without modifying conversion logic.

#### Acceptance Criteria

1. THE Bedrock_Client SHALL communicate with Amazon Bedrock using the AWS SDK for .NET.
2. THE Bedrock_Client SHALL authenticate to AWS using standard AWS credential resolution (environment variables, IAM roles, shared credential files, instance profiles) without storing credentials in application configuration files.
3. THE Bedrock_Client SHALL allow the target model identifier to be specified through application configuration without requiring code changes.
4. WHEN an AI conversion request is made, THE Bedrock_Client SHALL record the full prompt, the prompt template version, the model identifier, the full response, and a UTC timestamp in the Audit_Log.
5. IF the Bedrock API returns an error or does not respond within a configurable timeout period (default: 120 seconds), THEN THE Bedrock_Client SHALL retry the request up to a configurable maximum number of attempts (default: 3, range: 1 to 10) with exponential backoff between retries, and if all attempts fail, mark the object with a Manual_Review_Flag.
6. THE Bedrock_Client SHALL support configurable parameters including temperature (range: 0.0 to 1.0), max output tokens (range: 1 to the maximum supported by the configured model), and system prompt content.
7. IF a required configuration value (model identifier) is missing or a configurable parameter value is outside its valid range, THEN THE Bedrock_Client SHALL reject the request at startup and report an error message indicating the invalid or missing configuration setting.
8. THE Bedrock_Client SHALL require AI responses to conform to a defined structured output format (JSON) containing at minimum: the generated PostgreSQL DDL, a self-assessed confidence score (0.0 to 1.0), a list of assumptions, and a list of areas requiring manual review.
9. IF an AI response does not conform to the expected structured output format, THEN THE Bedrock_Client SHALL retry the request (counting toward the maximum retry attempts) before marking the object with a Manual_Review_Flag.

### Requirement 12: AI Prompt Management and Versioning

**User Story:** As a platform operator, I want prompts sent to the AI to be versioned and managed so that conversion behavior is reproducible and can be improved over time.

#### Acceptance Criteria

1. THE AI_Converter SHALL use versioned Prompt_Templates for each category of conversion (stored procedure, function, trigger, complex object, view).
2. Each Prompt_Template SHALL include a version identifier (semantic versioning format: MAJOR.MINOR.PATCH).
3. THE Audit_Log SHALL record the Prompt_Template version identifier alongside the full prompt for each AI interaction.
4. THE Conversion_Engine SHALL allow Prompt_Templates to be updated through configuration files without code changes.
5. WHEN a Prompt_Template is updated, THE Conversion_Engine SHALL record the new version identifier so that conversions performed with different prompt versions can be distinguished in the Audit_Log and Conversion_Report.
6. THE AI_Converter SHALL instruct the LLM to include a self-assessed confidence score (0.0 to 1.0) in its structured response, where 1.0 indicates high certainty of functional equivalence and 0.0 indicates inability to convert. WHEN the reported confidence is below a configurable threshold (default: 0.7), THE Conversion_Engine SHALL automatically apply a Manual_Review_Flag to the object.

### Requirement 13: Incremental Conversion and Rerun Support

**User Story:** As a database engineer, I want to convert objects incrementally and rerun conversions, so that I can work through a large schema in manageable batches and refine results.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL persist Conversion_Session state including, for each processed Schema_Object, the object name, schema name, object type, conversion status (converted, flagged, or failed), conversion method used, the source definition hash, and the generated output, so that a conversion can be paused and resumed without reprocessing objects that have a status of converted or flagged.
2. WHEN a rerun is requested for a specific object, THE Conversion_Engine SHALL reconvert that object, replace the previous conversion result in the Conversion_Session with the new result, and update the object's conversion status accordingly.
3. WHEN new objects are added to the source schema or existing object definitions are modified after a partial conversion, THE Conversion_Engine SHALL detect changes by comparing the current source object definition hash against the hash stored in the Conversion_Session and process only objects that are new or whose definitions have changed.
4. THE Conversion_Engine SHALL support filtering conversion scope by schema name, object type, or explicit object list.
5. IF the Conversion_Engine fails to persist Conversion_Session state, THEN THE Conversion_Engine SHALL halt processing, retain the last successfully persisted state, and report an error message indicating the persistence failure and the last successfully persisted object.

### Requirement 14: Manual Review and Editing

**User Story:** As a database engineer, I want to review and edit AI-generated conversions before they are applied, so that I can verify correctness and make adjustments.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL present all converted objects for review before generating final output scripts, including for each object: the object name, object type, conversion method used, conversion status, the original SQL Server definition, and the generated PostgreSQL definition.
2. WHEN a converted object carries a Manual_Review_Flag, THE Conversion_Engine SHALL include the flag reason, the AI-reported confidence score (if applicable), and any AI-provided assumptions in the review output.
3. THE Conversion_Engine SHALL accept manual edits to converted object definitions and persist the edited versions in the Conversion_Session.
4. WHEN a manual edit is applied, THE Conversion_Engine SHALL mark the object as manually reviewed and exclude the object from automatic reprocessing on subsequent reruns unless the engineer explicitly requests reconversion of that object.
5. THE Conversion_Engine SHALL support approving, rejecting, or deferring each converted object individually during the review workflow.

### Requirement 15: Conversion Reporting

**User Story:** As a project manager, I want a detailed conversion report, so that I can track progress, identify risks, and plan manual intervention work.

#### Acceptance Criteria

1. THE Conversion_Report SHALL include for each converted object: the object name, schema name, object type, conversion method (rule-based or AI-assisted), conversion status (one of: converted, flagged, failed, out-of-scope), and the generated PostgreSQL DDL.
2. WHEN an object is converted by the AI_Converter, THE Conversion_Report SHALL include the confidence score (0.0 to 1.0) as reported by the AI, the Prompt_Template version used, any assumptions made as descriptive text entries, and any areas flagged for manual review.
3. THE Conversion_Report SHALL include a summary section with total object counts by type, counts by conversion method, counts by status (converted, flagged, failed, out-of-scope), and overall conversion progress percentage calculated as the number of objects with status "converted" divided by the total number of in-scope objects in the Conversion_Session multiplied by 100.
4. THE Conversion_Report SHALL list all objects carrying a Manual_Review_Flag with the reason for flagging.
5. THE Conversion_Report SHALL be generated in a structured format (JSON) to support programmatic consumption by downstream tools.
6. WHEN a Conversion_Session is completed or when explicitly requested by the user, THE Conversion_Engine SHALL generate the Conversion_Report reflecting the current state of all objects in the session.
7. THE Conversion_Report SHALL include a compatibility notes section listing behavioral differences between the SQL Server original and the PostgreSQL conversion, including at minimum: NULL handling variations, implicit type coercion differences, collation or sort-order changes, transaction isolation differences, locking behavior changes, and error-handling behavior changes.

### Requirement 16: Output Script Generation

**User Story:** As a database engineer, I want the conversion output to be a set of correctly-ordered, executable PostgreSQL DDL scripts, so that I can apply them to a target database.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL generate output as one or more PostgreSQL DDL script files containing all converted objects with status "converted" or "manually reviewed".
2. THE output scripts SHALL order statements so that objects are created after all objects they depend on, producing scripts that can be executed sequentially without dependency errors.
3. THE Conversion_Engine SHALL support generating a single consolidated script or separate scripts per schema, per object type, or per individual object, as specified by configuration.
4. THE output scripts SHALL include comments indicating the source object name, source schema, conversion method used, and any manual review flags or compatibility notes.
5. THE Conversion_Engine SHALL generate CREATE SCHEMA statements before any objects that reference those schemas.
6. THE output scripts SHALL use IF NOT EXISTS clauses or equivalent idempotent patterns where supported by PostgreSQL to allow safe re-execution.

### Requirement 17: Application Compatibility Preservation

**User Story:** As an application developer, I want the converted PostgreSQL schema to preserve calling interfaces and naming conventions, so that my application code requires minimal changes.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL emit all object names as lower-case unquoted identifiers by default. WHEN a source database contains two or more objects whose names differ only in case, THE Conversion_Engine SHALL apply quoted identifiers to preserve the distinction and record a compatibility note in the Conversion_Report. THE Conversion_Engine SHALL provide a configurable option to force quoted identifiers for all names when the application relies on case-sensitive identifier references.
2. WHEN a SQL Server stored procedure is converted, THE Conversion_Engine SHALL ensure the PostgreSQL equivalent can be invoked with the same parameter names, parameter order, data types, and default values as the original.
3. WHEN converting data types, THE Rule_Based_Converter SHALL choose the PostgreSQL type that preserves the same range, precision, and application-observable behavior as the SQL Server type according to the Type_Mapping_Ruleset.
4. WHERE a direct conversion would alter the calling interface of an object (defined as a change to the invocation name, parameter names, parameter order, parameter types, or return type), THE Conversion_Engine SHALL generate a Wrapper_Object that exposes the original interface and delegates to the converted implementation.
5. IF the Conversion_Engine cannot generate a Wrapper_Object to preserve the original interface, THEN THE Conversion_Engine SHALL apply a Manual_Review_Flag to the object with an explanation of the interface incompatibility.
6. WHEN SQL Server extended properties (sp_addextendedproperty) are encountered, THE Conversion_Engine SHALL convert them to PostgreSQL COMMENT ON statements where a direct mapping exists (MS_Description to COMMENT) and record other extended properties in the Conversion_Report.

### Requirement 18: Data Type Mapping

**User Story:** As a database engineer, I want a comprehensive and well-defined mapping of SQL Server data types to PostgreSQL data types, so that data fidelity is preserved.

#### Acceptance Criteria

1. THE Rule_Based_Converter SHALL map SQL Server integer types as follows: TINYINT to SMALLINT with an added CHECK constraint enforcing the range 0 to 255, SMALLINT to SMALLINT, INT to INTEGER, BIGINT to BIGINT.
2. THE Rule_Based_Converter SHALL map SQL Server DECIMAL and NUMERIC types to PostgreSQL NUMERIC preserving the declared precision and scale, SHALL map MONEY to NUMERIC(19,4), and SHALL map SMALLMONEY to NUMERIC(10,4).
3. THE Rule_Based_Converter SHALL map SQL Server FLOAT to PostgreSQL DOUBLE PRECISION and REAL to PostgreSQL REAL.
4. THE Rule_Based_Converter SHALL map SQL Server string types as follows: CHAR to CHAR, VARCHAR to VARCHAR, NCHAR to CHAR, NVARCHAR to VARCHAR, and TEXT and NTEXT to TEXT, preserving the declared length constraint on CHAR and VARCHAR mappings.
5. WHEN a SQL Server VARCHAR(MAX) or NVARCHAR(MAX) column is encountered, THE Rule_Based_Converter SHALL map the type to PostgreSQL TEXT.
6. WHEN a SQL Server VARBINARY(MAX) column is encountered, THE Rule_Based_Converter SHALL map the type to PostgreSQL BYTEA.
7. THE Rule_Based_Converter SHALL map SQL Server date and time types as follows: DATE to DATE, TIME to TIME, DATETIME to TIMESTAMP(3), DATETIME2 to TIMESTAMP preserving declared precision, SMALLDATETIME to TIMESTAMP(0), and DATETIMEOFFSET to TIMESTAMPTZ preserving declared precision, up to a maximum of 6 digits of fractional-seconds precision.
8. IF a SQL Server DATETIME2 or DATETIMEOFFSET column declares a fractional-seconds precision greater than 6, THEN THE Rule_Based_Converter SHALL map the type using a precision of 6 and record the precision reduction as a compatibility note in the Conversion_Report.
9. THE Rule_Based_Converter SHALL map SQL Server binary types (BINARY, VARBINARY, IMAGE) to PostgreSQL BYTEA type.
10. THE Rule_Based_Converter SHALL map SQL Server BIT type to PostgreSQL BOOLEAN type.
11. THE Rule_Based_Converter SHALL map SQL Server UNIQUEIDENTIFIER type to PostgreSQL UUID type.
12. THE Rule_Based_Converter SHALL map SQL Server XML type to PostgreSQL XML type.
13. THE Rule_Based_Converter SHALL map SQL Server SQL_VARIANT type by applying a Manual_Review_Flag (no direct PostgreSQL equivalent exists).
14. THE Rule_Based_Converter SHALL map SQL Server HIERARCHYID type by applying a Manual_Review_Flag (no direct PostgreSQL equivalent exists) and suggesting the ltree extension as an alternative in the Conversion_Report.
15. THE Rule_Based_Converter SHALL map SQL Server GEOGRAPHY and GEOMETRY types by applying a Manual_Review_Flag and suggesting PostGIS extension equivalents in the Conversion_Report.
16. IF a SQL Server data type has no defined mapping in the Type_Mapping_Ruleset, THEN THE Rule_Based_Converter SHALL apply a Manual_Review_Flag to the containing object and include the unmapped type in the Conversion_Report.

### Requirement 19: SQL Server Expression and Function Translation

**User Story:** As a database engineer, I want SQL Server built-in functions and expressions to be translated to PostgreSQL equivalents, so that views, defaults, and computed columns produce correct results.

#### Acceptance Criteria

1. WHEN a SQL Server built-in function is encountered in an expression, THE Rule_Based_Converter SHALL translate the function to the PostgreSQL equivalent according to the Function_Mapping_Ruleset that includes at minimum: GETDATE to CURRENT_TIMESTAMP, ISNULL to COALESCE, CONVERT and CAST to PostgreSQL CAST or formatting functions, COALESCE to COALESCE, NEWID to gen_random_uuid(), DATEDIFF to a date subtraction expression with EXTRACT, DATEADD to interval arithmetic, LEN to LENGTH, CHARINDEX to POSITION, GETUTCDATE to CURRENT_TIMESTAMP AT TIME ZONE 'UTC', SYSDATETIME to CLOCK_TIMESTAMP, SCOPE_IDENTITY to lastval(), @@IDENTITY to lastval(), OBJECT_ID to a catalog lookup, and DB_NAME to current_database().
2. WHEN a SQL Server CONVERT or CAST expression with a style code is encountered, THE Rule_Based_Converter SHALL translate the expression to a PostgreSQL equivalent by mapping the style code to a to_char or to_date format string for date and string conversions, or to a CAST expression for type-only conversions where no formatting is involved.
3. WHEN a SQL Server TOP clause is encountered in a view or inline expression, THE Rule_Based_Converter SHALL translate the clause to a PostgreSQL LIMIT clause and SHALL preserve any associated ORDER BY clause to maintain deterministic result ordering.
4. WHEN a SQL Server string concatenation using the plus operator is encountered, THE Rule_Based_Converter SHALL translate the expression to use the PostgreSQL concatenation operator (||).
5. IF a SQL Server string concatenation expression involves operands that may be NULL, THEN THE Rule_Based_Converter SHALL preserve SQL Server NULL propagation semantics in the translated PostgreSQL expression (in SQL Server, string + NULL = NULL; this is the default PostgreSQL || behavior).
6. IF a SQL Server expression contains a function not present in the Function_Mapping_Ruleset, or a pattern involving dynamic expressions, session-level settings, or undocumented style codes that have no single deterministic PostgreSQL equivalent, THEN THE Rule_Based_Converter SHALL route the containing object to the AI_Converter.
7. WHEN SQL Server system variables (@@ROWCOUNT, @@ERROR, @@TRANCOUNT, @@IDENTITY) are encountered within procedural code, THE AI_Converter SHALL map them to PostgreSQL equivalents (GET DIAGNOSTICS for ROW_COUNT, SQLSTATE for error state, transaction-level checks, lastval() for identity).

### Requirement 20: Audit and Traceability

**User Story:** As a compliance officer, I want all AI interactions to be fully auditable, so that I can verify what prompts were sent and what responses were received during the conversion process.

#### Acceptance Criteria

1. WHEN the AI_Converter processes an object, THE Audit_Log SHALL record the Conversion_Session identifier, the object name, object type, the Prompt_Template version, the full prompt sent to the model, the model identifier, and the full response received.
2. THE Audit_Log SHALL record a UTC timestamp with millisecond precision for each AI interaction.
3. THE Audit_Log SHALL be persisted as append-only file-based storage that survives application restarts, is not modified after initial write, and can be read and queried without loading the Conversion_Session.
4. THE Audit_Log SHALL be generated in a structured format (JSON Lines — one JSON object per line) to support programmatic analysis and streaming reads.
5. IF the Bedrock_Client receives an error response or timeout from the model, THEN THE Audit_Log SHALL record the failed interaction including the prompt sent, the model identifier, the error indication, and the retry attempt number.
6. THE Audit_Log SHALL NOT contain database credentials, connection strings, AWS credentials, or any other sensitive configuration values.
7. THE Audit_Log SHALL support a configurable maximum file size. WHEN the active log file reaches the configured limit, THE system SHALL rotate to a new file preserving all existing entries with a sequential file naming convention.

### Requirement 21: Permission and Security Object Handling

**User Story:** As a database engineer, I want database permissions and security objects to be addressed during conversion, so that access control is not silently lost.

#### Acceptance Criteria

1. WHEN SQL Server GRANT, DENY, or REVOKE statements are encountered in the source schema, THE Rule_Based_Converter SHALL produce equivalent PostgreSQL GRANT or REVOKE statements where a direct mapping exists.
2. WHEN SQL Server DENY statements are encountered (which have no direct PostgreSQL equivalent), THE Rule_Based_Converter SHALL apply a Manual_Review_Flag and record a compatibility note suggesting the use of explicit REVOKE or role-based access control patterns.
3. THE Conversion_Engine SHALL record all permission-related objects in the Conversion_Report regardless of whether they were successfully converted.
4. THE Conversion_Engine SHALL NOT include any sensitive credentials, passwords, or security keys in the generated output scripts, Conversion_Report, or Audit_Log.

## Non-Functional Requirements

### NFR 1: Performance

**User Story:** As a database engineer, I want the conversion tool to process schemas efficiently, so that large databases can be converted in a reasonable time frame.

#### Acceptance Criteria

1. THE Rule_Based_Converter SHALL convert a single table definition (up to 200 columns with constraints and indexes) within 2 seconds on the target hardware.
2. THE Conversion_Engine SHALL support parallel processing of independent Schema_Objects (objects with no dependency relationship) to reduce total conversion time.
3. THE Conversion_Engine SHALL report progress (objects completed / total objects) during long-running conversion sessions.
4. THE Conversion_Engine SHALL support configurable concurrency limits for AI requests to avoid exceeding Bedrock API rate limits.

### NFR 2: Scalability

**User Story:** As a database engineer, I want the tool to handle large enterprise schemas without failure, so that I can use it on production-scale databases.

#### Acceptance Criteria

1. THE Conversion_Engine SHALL support databases containing up to 10,000 Schema_Objects without degradation in correctness.
2. THE Conversion_Session persistence format SHALL support efficient random-access reads and updates for individual objects without requiring the entire session to be loaded into memory.
3. WHEN a single Schema_Object definition exceeds the configured model's context window, THE Conversion_Engine SHALL apply a Manual_Review_Flag indicating the object is too large for AI processing and SHALL suggest manual decomposition.

### NFR 3: Security

**User Story:** As a platform operator, I want the tool to handle credentials and sensitive data securely, so that security is not compromised during the conversion process.

#### Acceptance Criteria

1. THE Bedrock_Client SHALL authenticate to AWS using standard AWS credential resolution (environment variables, IAM roles, shared credential files, instance profiles) without storing credentials in application configuration files or source code.
2. IF a SQL Server connection string is used for source schema extraction, THE Conversion_Engine SHALL support Windows Authentication and SQL Authentication, and SHALL NOT persist connection strings containing passwords in any log, report, or session file.
3. THE Conversion_Engine SHALL NOT include sensitive data (passwords, tokens, keys) in generated output scripts, Conversion_Reports, Audit_Logs, or error messages.
4. THE Conversion_Engine SHALL validate all file path inputs to prevent path traversal attacks when reading source DDL files or writing output scripts.

### NFR 4: Reliability

**User Story:** As a database engineer, I want the tool to handle failures gracefully, so that partial progress is not lost and I can resume work.

#### Acceptance Criteria

1. IF the Conversion_Engine encounters an unhandled exception during processing of a single Schema_Object, THEN it SHALL log the error, mark the object as failed, and continue processing remaining objects.
2. THE Conversion_Session SHALL be persisted after each successfully converted object so that no more than one object's work is lost in the event of a crash.
3. IF the application terminates unexpectedly, THE Conversion_Engine SHALL detect the incomplete session on restart and offer to resume from the last persisted state.

### NFR 5: Maintainability

**User Story:** As a platform operator, I want the Type_Mapping_Ruleset and Function_Mapping_Ruleset to be maintainable without code changes, so that new mappings can be added as requirements evolve.

#### Acceptance Criteria

1. THE Type_Mapping_Ruleset SHALL be defined in an external configuration file (JSON or YAML) that can be modified without recompiling the application.
2. THE Function_Mapping_Ruleset SHALL be defined in an external configuration file (JSON or YAML) that can be modified without recompiling the application.
3. THE Conversion_Engine SHALL validate ruleset configuration files at startup and report any syntax errors or missing required fields before beginning processing.
