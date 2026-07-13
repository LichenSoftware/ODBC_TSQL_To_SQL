# Design Document

## Overview

The AI-Assisted Schema Conversion application is a .NET 8 command-line tool that converts Microsoft SQL Server database schemas to PostgreSQL. It follows the same clean architecture patterns established by the sibling MigrationAssessment solution: separate class library projects for Core (models/interfaces), domain logic, and infrastructure, composed via dependency injection in a CLI host.

The system is organized around a pipeline architecture where schema objects flow through discovery, classification, conversion (rule-based or AI-assisted), review, and output generation stages.

## Architecture

```
┌─────────────────────────────────────────────────────────────────────┐
│                         CLI Host (Program.cs)                        │
│            Configuration loading, DI setup, command parsing          │
└──────────────────────────────┬──────────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────────┐
│                      ConversionPipeline                              │
│   Orchestrates: Extract → Classify → Convert → Review → Output      │
└───┬──────────┬──────────┬──────────┬──────────┬─────────────────────┘
    │          │          │          │          │
    ▼          ▼          ▼          ▼          ▼
┌────────┐ ┌────────┐ ┌─────────┐ ┌────────┐ ┌──────────┐
│Schema  │ │Object  │ │Convert- │ │Review  │ │Script    │
│Extract-│ │Classi- │ │ers      │ │Manager │ │Generator │
│or      │ │fier    │ │         │ │        │ │          │
└────────┘ └────────┘ └────┬────┘ └────────┘ └──────────┘
                           │
              ┌────────────┼────────────┐
              ▼                         ▼
     ┌────────────────┐      ┌──────────────────┐
     │Rule-Based      │      │AI Converter      │
     │Converter       │      │                  │
     │                │      │  ┌────────────┐  │
     │ TypeMapper     │      │  │Bedrock     │  │
     │ FunctionMapper │      │  │Client      │  │
     │ TableConverter │      │  └────────────┘  │
     │ IndexConverter │      │  ┌────────────┐  │
     │ ViewConverter  │      │  │Prompt      │  │
     │ SequenceConv.  │      │  │Manager     │  │
     │ TypeConverter   │      │  └────────────┘  │
     │ SchemaConverter│      │  ┌────────────┐  │
     └────────────────┘      │  │Response    │  │
                             │  │Parser      │  │
                             │  └────────────┘  │
                             └──────────────────┘
                                      │
              ┌───────────────────────┼───────────────┐
              ▼                       ▼               ▼
     ┌────────────────┐    ┌──────────────┐  ┌────────────┐
     │Conversion      │    │Audit Log     │  │Conversion  │
     │Session Store   │    │Writer        │  │Report Gen  │
     └────────────────┘    └──────────────┘  └────────────┘
```

## Project Structure

```
AI-AssistedSchemaConversion/
├── SchemaConversion.slnx
├── src/
│   ├── SchemaConversion.Core/              # Models, interfaces, enums
│   ├── SchemaConversion.Extraction/        # Schema discovery from SQL Server
│   ├── SchemaConversion.RuleEngine/        # Deterministic converters
│   ├── SchemaConversion.AiEngine/          # AI-assisted conversion via Bedrock
│   ├── SchemaConversion.Orchestration/     # Pipeline, routing, session mgmt
│   ├── SchemaConversion.Reporting/         # Report and script generation
│   └── SchemaConversion.Cli/              # CLI host, DI, commands
├── tests/
│   ├── SchemaConversion.Core.Tests/
│   ├── SchemaConversion.Extraction.Tests/
│   ├── SchemaConversion.RuleEngine.Tests/
│   ├── SchemaConversion.AiEngine.Tests/
│   ├── SchemaConversion.Orchestration.Tests/
│   └── SchemaConversion.Reporting.Tests/
└── config/
    ├── type-mappings.json
    ├── function-mappings.json
    ├── schema-mappings.json
    └── prompts/
        ├── stored-procedure.v1.0.0.md
        ├── function.v1.0.0.md
        ├── trigger.v1.0.0.md
        ├── complex-object.v1.0.0.md
        └── view.v1.0.0.md
```

## Components and Interfaces

### SchemaConversion.Core

The Core project contains all models, interfaces, and enums shared across the solution. It has no dependencies on other projects or external packages beyond logging abstractions.

#### Key Models

```csharp
namespace SchemaConversion.Core.Models;

public enum SchemaObjectType
{
    Table, View, StoredProcedure, Function, Trigger,
    Index, Constraint, Sequence, UserDefinedType,
    Synonym, Schema, Permission
}

public enum ConversionStatus
{
    Pending, Converted, Flagged, Failed, OutOfScope, ManuallyReviewed
}

public enum ConversionMethod
{
    RuleBased, AiAssisted, Manual
}

public sealed record SchemaObject
{
    public required string Name { get; init; }
    public required string SchemaName { get; init; }
    public required SchemaObjectType ObjectType { get; init; }
    public required string SourceDefinition { get; init; }
    public required string SourceDefinitionHash { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}

public sealed record ConversionResult
{
    public required string ObjectName { get; init; }
    public required string SchemaName { get; init; }
    public required SchemaObjectType ObjectType { get; init; }
    public required ConversionStatus Status { get; init; }
    public required ConversionMethod Method { get; init; }
    public string? GeneratedDdl { get; init; }
    public string? WrapperDdl { get; init; }
    public double? ConfidenceScore { get; init; }
    public IReadOnlyList<string> Assumptions { get; init; } = [];
    public IReadOnlyList<ManualReviewFlag> ReviewFlags { get; init; } = [];
    public IReadOnlyList<CompatibilityNote> CompatibilityNotes { get; init; } = [];
    public string? PromptTemplateVersion { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record ManualReviewFlag
{
    public required string Reason { get; init; }
    public string? CodeSection { get; init; }
    public string? SuggestedAlternative { get; init; }
}

public sealed record CompatibilityNote
{
    public required string Category { get; init; }  // e.g., "NullHandling", "Locking", "Collation"
    public required string Description { get; init; }
}

public sealed record ConversionSessionEntry
{
    public required SchemaObject Source { get; init; }
    public required ConversionResult Result { get; init; }
    public DateTimeOffset ConvertedAt { get; init; }
    public bool IsManuallyEdited { get; init; }
}
```

#### Key Interfaces

```csharp
namespace SchemaConversion.Core.Interfaces;

public interface ISchemaExtractor
{
    Task<IReadOnlyList<SchemaObject>> ExtractAsync(
        SchemaExtractionOptions options, CancellationToken ct);
}

public interface IObjectClassifier
{
    ClassificationResult Classify(SchemaObject obj);
}

public interface IRuleBasedConverter
{
    ConversionResult Convert(SchemaObject obj, ConversionContext context);
}

public interface IAiConverter
{
    Task<ConversionResult> ConvertAsync(
        SchemaObject obj, ConversionContext context, CancellationToken ct);
}

public interface IConversionSessionStore
{
    Task<ConversionSession> LoadOrCreateAsync(string sessionId, CancellationToken ct);
    Task SaveEntryAsync(string sessionId, ConversionSessionEntry entry, CancellationToken ct);
    Task<ConversionSessionEntry?> GetEntryAsync(
        string sessionId, string schemaName, string objectName, CancellationToken ct);
    Task<IReadOnlyList<ConversionSessionEntry>> GetAllEntriesAsync(
        string sessionId, CancellationToken ct);
}

public interface IAuditLogWriter
{
    Task WriteAsync(AuditLogEntry entry, CancellationToken ct);
}

public interface IConversionReportGenerator
{
    Task<ConversionReport> GenerateAsync(
        string sessionId, IReadOnlyList<ConversionSessionEntry> entries, CancellationToken ct);
}

public interface IScriptGenerator
{
    Task GenerateAsync(
        IReadOnlyList<ConversionSessionEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct);
}
```

### SchemaConversion.Extraction

Responsible for connecting to SQL Server or parsing DDL files and producing a dependency-ordered list of `SchemaObject` instances.

#### Components

- **SqlServerSchemaExtractor** — Implements `ISchemaExtractor`. Uses `Microsoft.Data.SqlClient` to query system catalog views. Builds a dependency graph by querying `sys.sql_expression_dependencies`.
- **DdlFileSchemaExtractor** — Implements `ISchemaExtractor`. Parses `.sql` files using `Microsoft.SqlServer.TransactSql.ScriptDom` (the T-SQL parser from Microsoft) to extract individual object definitions and infer dependencies from object references.
- **DependencyGraphBuilder** — Performs topological sort of schema objects. Detects circular dependencies and returns cycle information.

#### Dependency Graph

```csharp
public sealed class DependencyGraphBuilder
{
    /// <summary>
    /// Returns objects in dependency order (dependencies first).
    /// If cycles exist, returns the cycle members separately.
    /// </summary>
    public DependencyOrderResult GetProcessingOrder(IReadOnlyList<SchemaObject> objects);
}

public sealed record DependencyOrderResult
{
    public required IReadOnlyList<SchemaObject> Ordered { get; init; }
    public required IReadOnlyList<IReadOnlyList<SchemaObject>> Cycles { get; init; }
}
```

### SchemaConversion.RuleEngine

Contains all deterministic conversion logic. Each converter handles a specific object type.

#### Components

- **TypeMapper** — Loads `type-mappings.json` at startup. Provides `MapType(SqlServerType) → PostgresType` with support for precision/scale propagation. Adds CHECK constraints where needed (e.g., TINYINT range).
- **FunctionMapper** — Loads `function-mappings.json` at startup. Provides `MapFunction(functionName, args) → PostgresExpression`. Handles style codes for CONVERT/CAST.
- **ExpressionTranslator** — Walks T-SQL expression trees (parsed by ScriptDom) and applies TypeMapper + FunctionMapper. Returns translated expression or signals "cannot translate" to trigger AI fallback.
- **TableConverter** — Converts CREATE TABLE statements using TypeMapper and ExpressionTranslator for defaults/computed columns. Implements `IRuleBasedConverter` for `SchemaObjectType.Table`.
- **ConstraintConverter** — Converts PK, FK, UNIQUE, CHECK constraints. Uses ExpressionTranslator for CHECK expressions.
- **IndexConverter** — Converts indexes including filtered indexes (partial indexes) and clustered index handling.
- **SequenceConverter** — Converts SEQUENCE objects.
- **ViewConverter** — Converts views whose SELECT body can be fully translated by ExpressionTranslator.
- **SchemaConverter** — Generates CREATE SCHEMA statements and applies Schema_Mapping_Table.
- **UserDefinedTypeConverter** — Converts alias types to DOMAINs, table types to composite types.
- **SynonymConverter** — Converts synonyms to views.
- **PermissionConverter** — Converts GRANT/REVOKE; flags DENY.

#### Configuration File Formats

**type-mappings.json:**
```json
{
  "mappings": [
    {
      "sqlServerType": "INT",
      "postgresType": "INTEGER",
      "preservePrecision": false,
      "additionalConstraint": null
    },
    {
      "sqlServerType": "TINYINT",
      "postgresType": "SMALLINT",
      "preservePrecision": false,
      "additionalConstraint": "CHECK ({column} >= 0 AND {column} <= 255)"
    },
    {
      "sqlServerType": "MONEY",
      "postgresType": "NUMERIC(19,4)",
      "preservePrecision": false,
      "additionalConstraint": null
    },
    {
      "sqlServerType": "DATETIME2",
      "postgresType": "TIMESTAMP({precision})",
      "preservePrecision": true,
      "maxPrecision": 6,
      "additionalConstraint": null
    }
  ]
}
```

**function-mappings.json:**
```json
{
  "mappings": [
    {
      "sqlServerFunction": "GETDATE",
      "postgresExpression": "CURRENT_TIMESTAMP",
      "argCount": 0
    },
    {
      "sqlServerFunction": "ISNULL",
      "postgresExpression": "COALESCE({0}, {1})",
      "argCount": 2
    },
    {
      "sqlServerFunction": "LEN",
      "postgresExpression": "LENGTH({0})",
      "argCount": 1
    },
    {
      "sqlServerFunction": "CHARINDEX",
      "postgresExpression": "POSITION({0} IN {1})",
      "argCount": 2
    },
    {
      "sqlServerFunction": "NEWID",
      "postgresExpression": "gen_random_uuid()",
      "argCount": 0
    }
  ],
  "styleCodes": {
    "101": { "format": "MM/DD/YYYY", "toCharPattern": "MM/DD/YYYY" },
    "103": { "format": "DD/MM/YYYY", "toCharPattern": "DD/MM/YYYY" },
    "120": { "format": "YYYY-MM-DD HH:MI:SS", "toCharPattern": "YYYY-MM-DD HH24:MI:SS" }
  }
}
```

### SchemaConversion.AiEngine

Handles all AI-assisted conversion via Amazon Bedrock.

#### Components

- **BedrockClient** — Implements communication with Amazon Bedrock using `AWSSDK.BedrockRuntime`. Handles retries with exponential backoff, timeout management, and structured response parsing. Authenticates via standard AWS credential chain.
- **PromptManager** — Loads versioned prompt templates from the `config/prompts/` directory. Constructs the full prompt by injecting the schema object definition, Type_Mapping_Ruleset context, and conversion instructions into the template.
- **AiResponseParser** — Parses the structured JSON response from the LLM. Validates response schema (DDL, confidence, assumptions, review areas). Returns parsed result or signals malformed response for retry.
- **AiConverterService** — Implements `IAiConverter`. Coordinates PromptManager → BedrockClient → AiResponseParser → AuditLogWriter. Applies confidence threshold check.

#### Structured AI Response Format

The LLM is instructed (via the system prompt in the Prompt_Template) to respond with:

```json
{
  "ddl": "CREATE OR REPLACE FUNCTION ...",
  "wrapperDdl": null,
  "confidence": 0.85,
  "assumptions": [
    "Assumed @StartDate parameter is always non-null based on usage pattern"
  ],
  "reviewAreas": [
    {
      "codeSection": "Lines 15-22: Dynamic SQL construction",
      "reason": "Complex string interpolation may not preserve all edge cases"
    }
  ],
  "compatibilityNotes": [
    {
      "category": "ErrorHandling",
      "description": "RAISERROR severity levels do not map directly to PostgreSQL exception handling"
    }
  ]
}
```

#### Prompt Template Structure

Each prompt template is a Markdown file with YAML frontmatter:

```markdown
---
version: "1.0.0"
category: "stored-procedure"
model_instructions: "system"
---

You are a database migration expert converting SQL Server stored procedures to PostgreSQL.

## Rules
1. If the procedure returns a result set, produce a PostgreSQL FUNCTION returning TABLE.
2. If the procedure only performs DML without returning rows, produce a PostgreSQL PROCEDURE.
3. Preserve all parameter names, types, and default values.
4. Map data types according to this mapping: {type_mapping_context}
5. Preserve transaction control, error handling, and business logic.
6. Generate wrapper objects if the calling interface changes.

## Source Object
```sql
{source_definition}
```

## Required Output Format
Respond ONLY with valid JSON matching this schema:
{response_schema}
```

#### Retry and Error Handling

```csharp
public sealed class BedrockClientOptions
{
    public required string ModelId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxRetryAttempts { get; init; } = 3;
    public double Temperature { get; init; } = 0.2;
    public int MaxOutputTokens { get; init; } = 8192;
}
```

Retry policy: exponential backoff starting at 2 seconds, doubling each attempt (2s, 4s, 8s). Retries on:
- HTTP 429 (throttling)
- HTTP 5xx (server error)
- Timeout
- Malformed response (does not parse as valid JSON matching expected schema)

### SchemaConversion.Orchestration

Contains the pipeline coordinator, object classifier, session management, and dependency resolution.

#### Components

- **ConversionPipeline** — The main orchestrator. Implements the full conversion workflow: extract → classify → order → convert → persist. Handles parallel execution of independent objects and sequential execution of dependent objects.
- **ObjectClassifier** — Implements `IObjectClassifier`. Uses object type and a quick syntax scan (checking for SQL Server-specific keywords in views) to determine routing. Supports manual override via configuration.
- **ConversionSessionStore** — Implements `IConversionSessionStore`. Persists session state as a directory of JSON files (one per object) for efficient random-access reads/writes.
- **SessionChangeDetector** — Compares source definition hashes against stored session hashes to identify new/modified objects for incremental processing.

#### Pipeline Flow

```csharp
public sealed class ConversionPipeline
{
    public async Task<ConversionPipelineResult> ExecuteAsync(
        ConversionPipelineOptions options, CancellationToken ct)
    {
        // 1. Extract schema objects from source
        var objects = await _extractor.ExtractAsync(options.Extraction, ct);

        // 2. Load or create session, detect changes
        var session = await _sessionStore.LoadOrCreateAsync(options.SessionId, ct);
        var objectsToProcess = _changeDetector.GetObjectsRequiringProcessing(
            objects, session, options.Filters);

        // 3. Build dependency graph, get processing order
        var order = _dependencyGraph.GetProcessingOrder(objectsToProcess);

        // 4. Handle circular dependencies with placeholder strategy
        foreach (var cycle in order.Cycles)
            await HandleCycleAsync(cycle, session, ct);

        // 5. Process objects in dependency order
        foreach (var obj in order.Ordered)
        {
            var classification = _classifier.Classify(obj);
            var result = classification.Method switch
            {
                ConversionMethod.RuleBased => ConvertRuleBased(obj),
                ConversionMethod.AiAssisted => await ConvertAiAsync(obj, ct),
                _ => throw new InvalidOperationException()
            };

            // Fallback: if rule-based fails, try AI
            if (result.Status == ConversionStatus.Failed
                && classification.Method == ConversionMethod.RuleBased)
            {
                result = await ConvertAiAsync(obj, ct);
            }

            await _sessionStore.SaveEntryAsync(options.SessionId,
                new ConversionSessionEntry { Source = obj, Result = result }, ct);

            _progress.Report(objectsToProcess.Count, processedCount);
        }

        // 6. Generate report
        return await GenerateResultAsync(options.SessionId, ct);
    }
}
```

#### Session Storage Format

Sessions are stored as a directory structure:

```
sessions/
└── {session-id}/
    ├── session.json          # Session metadata (created, last modified, filters)
    ├── objects/
    │   ├── dbo.Customers.Table.json
    │   ├── dbo.GetOrders.StoredProcedure.json
    │   └── sales.OrderTotal.Function.json
    └── audit/
        └── audit-001.jsonl   # Audit log entries (JSON Lines)
```

Each object file contains the full `ConversionSessionEntry` serialized as JSON, enabling random-access reads and writes without loading the full session.

### SchemaConversion.Reporting

Generates conversion reports and executable output scripts.

#### Components

- **ConversionReportGenerator** — Implements `IConversionReportGenerator`. Aggregates all session entries into a structured JSON report with per-object details, summary statistics, and compatibility notes.
- **ScriptGenerator** — Implements `IScriptGenerator`. Produces dependency-ordered PostgreSQL DDL scripts. Supports multiple output modes (consolidated, per-schema, per-type, per-object).
- **ScriptOrderResolver** — Reorders converted DDL statements based on the dependency graph. Inserts CREATE SCHEMA first, then types, tables, indexes, functions/procedures, triggers, views, permissions.

#### Output Script Ordering

```
1. CREATE SCHEMA statements
2. CREATE DOMAIN / CREATE TYPE (user-defined types)
3. CREATE SEQUENCE
4. CREATE TABLE (with constraints inline)
5. CREATE INDEX
6. CREATE FUNCTION / CREATE PROCEDURE
7. CREATE TRIGGER (after trigger functions)
8. CREATE VIEW
9. Wrapper objects (functions/views for compatibility)
10. GRANT / REVOKE (permissions)
11. COMMENT ON (extended properties)
```

#### Report JSON Structure

```json
{
  "sessionId": "conv-2026-07-04-001",
  "generatedAt": "2026-07-04T14:30:00Z",
  "summary": {
    "totalObjects": 450,
    "byStatus": { "converted": 410, "flagged": 25, "failed": 5, "outOfScope": 10 },
    "byMethod": { "ruleBased": 380, "aiAssisted": 55, "manual": 5 },
    "byType": { "Table": 120, "StoredProcedure": 85, "View": 60, ... },
    "progressPercent": 91.1
  },
  "objects": [
    {
      "name": "GetCustomerOrders",
      "schema": "dbo",
      "type": "StoredProcedure",
      "method": "AiAssisted",
      "status": "Converted",
      "confidence": 0.92,
      "promptTemplateVersion": "1.0.0",
      "assumptions": ["Assumed result set always returns exactly 5 columns"],
      "reviewFlags": [],
      "compatibilityNotes": [
        { "category": "Locking", "description": "NOLOCK hints removed..." }
      ],
      "generatedDdl": "CREATE OR REPLACE FUNCTION ..."
    }
  ],
  "compatibilityNotes": [
    { "category": "NullHandling", "description": "PostgreSQL string || NULL = NULL..." },
    { "category": "Collation", "description": "Default collation differs..." }
  ],
  "flaggedObjects": [
    { "name": "ProcessBatch", "schema": "dbo", "reason": "Global temp table ##BatchQueue" }
  ]
}
```

### SchemaConversion.Cli

The CLI host provides the user interface. Uses `System.CommandLine` for argument parsing.

#### Commands

```
schema-convert extract     --connection <conn> | --files <path>
                           --output <session-dir>

schema-convert convert     --session <session-dir>
                           [--schema <name>] [--type <type>] [--objects <list>]
                           [--force-ai <object>] [--force-rules <object>]
                           [--concurrency <n>]

schema-convert rerun       --session <session-dir>
                           --objects <list>

schema-convert review      --session <session-dir>
                           [--flagged-only]

schema-convert edit        --session <session-dir>
                           --object <name>
                           --file <edited-ddl-file>

schema-convert approve     --session <session-dir>
                           --objects <list> | --all

schema-convert generate    --session <session-dir>
                           --output <dir>
                           [--mode consolidated|per-schema|per-type|per-object]

schema-convert report      --session <session-dir>
                           --output <report.json>
```

## Data Models

### Core Domain Models

| Model | Purpose | Key Fields |
|-------|---------|------------|
| `SchemaObject` | Represents a discovered source database object | Name, SchemaName, ObjectType, SourceDefinition, SourceDefinitionHash, DependsOn |
| `ConversionResult` | Outcome of converting a single object | ObjectName, Status, Method, GeneratedDdl, WrapperDdl, ConfidenceScore, Assumptions, ReviewFlags, CompatibilityNotes |
| `ConversionSessionEntry` | Persisted state for one object in a session | Source (SchemaObject), Result (ConversionResult), ConvertedAt, IsManuallyEdited |
| `ManualReviewFlag` | Marker for human review | Reason, CodeSection, SuggestedAlternative |
| `CompatibilityNote` | Behavioral difference documentation | Category, Description |
| `AuditLogEntry` | Record of a single AI interaction | SessionId, ObjectName, ObjectType, PromptTemplateVersion, FullPrompt, ModelId, FullResponse, Timestamp, RetryAttempt, IsError |
| `ConversionReport` | Aggregated report of all conversion results | SessionId, GeneratedAt, Summary, Objects, CompatibilityNotes, FlaggedObjects |
| `TypeMappingRule` | Single entry in type-mappings.json | SqlServerType, PostgresType, PreservePrecision, MaxPrecision, AdditionalConstraint |
| `FunctionMappingRule` | Single entry in function-mappings.json | SqlServerFunction, PostgresExpression, ArgCount |
| `ClassificationResult` | Output of object classification | Method (RuleBased/AiAssisted), Reason |
| `DependencyOrderResult` | Output of dependency graph resolution | Ordered (list), Cycles (list of lists) |

### Persistence Formats

**Session metadata (`session.json`):**
```json
{
  "sessionId": "conv-2026-07-04-001",
  "createdAt": "2026-07-04T10:00:00Z",
  "lastModifiedAt": "2026-07-04T14:30:00Z",
  "sourceType": "connection",
  "sourceIdentifier": "[masked]",
  "totalObjectCount": 450,
  "filters": { "schemas": ["dbo", "sales"], "types": null, "objects": null }
}
```

**Object entry (`objects/{schema}.{name}.{type}.json`):**
```json
{
  "source": {
    "name": "GetCustomerOrders",
    "schemaName": "dbo",
    "objectType": "StoredProcedure",
    "sourceDefinition": "CREATE PROCEDURE dbo.GetCustomerOrders ...",
    "sourceDefinitionHash": "a1b2c3d4...",
    "dependsOn": ["dbo.Customers", "dbo.Orders"]
  },
  "result": {
    "objectName": "GetCustomerOrders",
    "schemaName": "dbo",
    "objectType": "StoredProcedure",
    "status": "Converted",
    "method": "AiAssisted",
    "generatedDdl": "CREATE OR REPLACE FUNCTION ...",
    "wrapperDdl": null,
    "confidenceScore": 0.92,
    "assumptions": [],
    "reviewFlags": [],
    "compatibilityNotes": [],
    "promptTemplateVersion": "1.0.0",
    "errorMessage": null
  },
  "convertedAt": "2026-07-04T12:15:30Z",
  "isManuallyEdited": false
}
```

**Audit log entry (one JSON line in `.jsonl` file):**
```json
{"sessionId":"conv-2026-07-04-001","objectName":"GetCustomerOrders","objectType":"StoredProcedure","promptTemplateVersion":"1.0.0","fullPrompt":"...","modelId":"anthropic.claude-sonnet-4-20250514-v1:0","fullResponse":"...","timestamp":"2026-07-04T12:15:28.123Z","retryAttempt":0,"isError":false}
```

## Data Flow

### Conversion Flow for a Single Object

```
SchemaObject
    │
    ▼
ObjectClassifier.Classify()
    │
    ├── RuleBased ──────────────────► IRuleBasedConverter.Convert()
    │                                        │
    │                                        ├── Success → ConversionResult (Converted)
    │                                        │
    │                                        └── Cannot Convert → Reclassify as AI
    │                                                    │
    ▼                                                   ▼
AiAssisted ──────────────────────────► IAiConverter.ConvertAsync()
                                              │
                                              ▼
                                     PromptManager.BuildPrompt()
                                              │
                                              ▼
                                     BedrockClient.InvokeAsync()
                                              │
                                     ┌────────┴────────┐
                                     │                 │
                                     ▼                 ▼
                              Success Response    Error/Timeout
                                     │                 │
                                     ▼                 ▼
                              AiResponseParser    Retry (up to max)
                                     │                 │
                              ┌──────┴──────┐          ▼
                              │             │     All retries failed
                              ▼             ▼          │
                         Valid JSON    Malformed        ▼
                              │        (retry)    ConversionResult (Failed)
                              ▼                   + ManualReviewFlag
                     ConversionResult
                     (check confidence)
                              │
                     ┌────────┴────────┐
                     │                 │
                     ▼                 ▼
              confidence >= 0.7   confidence < 0.7
                     │                 │
                     ▼                 ▼
              Status: Converted   Status: Flagged
                                  + ManualReviewFlag
```

### Session Persistence Flow

```
ConversionResult produced
    │
    ▼
ConversionSessionStore.SaveEntryAsync()
    │
    ▼
Serialize ConversionSessionEntry to JSON
    │
    ▼
Write to: sessions/{id}/objects/{schema}.{name}.{type}.json
    │
    ▼
(If AI) AuditLogWriter.WriteAsync()
    │
    ▼
Append JSON line to: sessions/{id}/audit/audit-{seq}.jsonl
```

## Technology Stack

| Component | Technology | Rationale |
|-----------|-----------|-----------|
| Runtime | .NET 8 | Matches MigrationAssessment, LTS |
| Language | C# 12 | Latest features, nullable reference types |
| T-SQL Parsing | Microsoft.SqlServer.TransactSql.ScriptDom | Official Microsoft T-SQL parser, AST-based |
| SQL Server Connectivity | Microsoft.Data.SqlClient | Standard SQL Server driver |
| AWS Bedrock | AWSSDK.BedrockRuntime | Official AWS SDK for .NET |
| DI Container | Microsoft.Extensions.DependencyInjection | Standard .NET DI |
| Configuration | Microsoft.Extensions.Configuration | JSON config files, env vars |
| Logging | Microsoft.Extensions.Logging | ILogger abstraction |
| CLI Framework | System.CommandLine | Modern .NET CLI parsing |
| JSON Serialization | System.Text.Json | High-performance, built-in |
| Hashing | SHA256 (System.Security.Cryptography) | Source definition change detection |
| Testing | xUnit + NSubstitute | Matches MigrationAssessment patterns |

## Configuration

### appsettings.json

```json
{
  "Bedrock": {
    "ModelId": "anthropic.claude-sonnet-4-20250514-v1:0",
    "Region": "us-east-1",
    "TimeoutSeconds": 120,
    "MaxRetryAttempts": 3,
    "Temperature": 0.2,
    "MaxOutputTokens": 8192,
    "ConfidenceThreshold": 0.7
  },
  "Conversion": {
    "TypeMappingsPath": "config/type-mappings.json",
    "FunctionMappingsPath": "config/function-mappings.json",
    "SchemaMappingsPath": "config/schema-mappings.json",
    "PromptTemplatesPath": "config/prompts/",
    "DefaultIdentifierCasing": "lower",
    "ForceQuotedIdentifiers": false,
    "MaxConcurrentAiRequests": 3
  },
  "AuditLog": {
    "MaxFileSizeBytes": 104857600,
    "Directory": "sessions/{sessionId}/audit/"
  },
  "Output": {
    "Mode": "consolidated",
    "IncludeComments": true,
    "UseIfNotExists": true
  }
}
```

## Key Design Decisions

### 1. T-SQL Parsing via ScriptDom

The Rule_Based_Converter uses `Microsoft.SqlServer.TransactSql.ScriptDom` to parse T-SQL into an AST rather than using regex or string manipulation. This provides:
- Reliable identification of object boundaries in multi-statement scripts
- Accurate expression tree traversal for function/type translation
- Correct handling of quoted identifiers, comments, and whitespace
- Detection of SQL Server-specific syntax for classification decisions

### 2. File-Based Session Storage (not database)

Sessions are stored as JSON files on disk rather than in a database because:
- No external database dependency for a conversion tool
- Easy to inspect, version control, and share sessions
- Supports random-access via individual object files
- Survives application restarts without additional infrastructure
- Portable across environments

### 3. JSON Lines for Audit Logs

Audit logs use JSON Lines format (one JSON object per line) because:
- Append-only writes without loading/rewriting the full file
- Streaming reads for large logs
- Simple rotation by file size
- Each line is independently parseable

### 4. Confidence-Based Auto-Flagging

AI responses include a self-assessed confidence score. Objects below the configurable threshold (default 0.7) are automatically flagged for manual review. This provides:
- Consistent quality gates regardless of object complexity
- Reduced human review burden (only low-confidence items need attention)
- Configurable strictness per deployment

### 5. Fallback from Rule-Based to AI

When the Rule_Based_Converter encounters a construct it cannot handle (unrecognized function, complex expression), it signals failure and the pipeline automatically reroutes to the AI_Converter. This ensures:
- No silent failures in rule-based conversion
- Graceful degradation to AI for edge cases
- The rule engine stays simple and deterministic

### 6. Placeholder Strategy for Circular Dependencies

Circular dependencies (e.g., View A references Function B which references View A) are handled by:
1. Creating all objects in the cycle with placeholder bodies (`CREATE FUNCTION ... RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql`)
2. Converting them in alphabetical order
3. Replacing placeholders with `CREATE OR REPLACE`

This avoids dependency deadlocks while producing valid PostgreSQL output.

### 7. Identifier Casing Strategy

By default, all identifiers are emitted as lowercase (PostgreSQL convention). Quoted identifiers are only used when:
- Two objects differ only in case (name collision avoidance)
- The user explicitly enables `ForceQuotedIdentifiers` in configuration
- An object name is a PostgreSQL reserved word

This minimizes application friction since most SQL Server apps use case-insensitive identifiers.

## Security Considerations

1. **Credential handling** — AWS credentials use the standard SDK credential chain (env vars, profiles, IAM roles). Never stored in config files.
2. **Connection strings** — Masked in all log/report output. Only held in memory during extraction.
3. **Path traversal** — All file path inputs are canonicalized and validated against allowed directories.
4. **AI prompt injection** — Source SQL definitions are passed as data context within the prompt template, not as instructions. The system prompt explicitly constrains output format.
5. **Sensitive data in DDL** — The tool processes schema definitions, not row data. If source DDL contains embedded secrets (unlikely but possible in defaults), they would appear in output scripts. A warning is logged if potential secrets are detected in source definitions.

## Error Handling

| Scope | Behavior |
|-------|----------|
| Single object conversion failure | Log error, mark object as Failed, continue pipeline |
| AI timeout/error (retryable) | Exponential backoff retry up to MaxRetryAttempts |
| AI malformed response | Retry (counts toward limit), then mark as Failed |
| Session persistence failure | Halt pipeline, preserve last good state, report error |
| Configuration validation failure | Fail fast at startup with descriptive error |
| Source extraction failure | Halt pipeline, report error (no partial state to corrupt) |
| Circular dependency | Apply placeholder strategy, log cycle detection |

All exceptions are caught at the per-object level within the pipeline. The pipeline itself only halts on infrastructure failures (disk full, session corruption) or explicit cancellation. Individual object failures never crash the pipeline.

Error messages are structured and include: the object name, the failure stage (extraction, classification, conversion, persistence), and actionable context (e.g., "Function DATEDIFF_BIG not found in Function_Mapping_Ruleset").

## Correctness Properties

### Property 1: Determinism
Given the same source schema and configuration (type-mappings, function-mappings, schema-mappings), the Rule_Based_Converter SHALL produce byte-identical output across multiple runs.

**Validates: Requirements 2, 3, 4, 18, 19**

### Property 2: Idempotency
Running the pipeline twice with no source changes SHALL produce no new conversion results (all objects already in session with matching hashes are skipped).

**Validates: Requirements 13.1, 13.3**

### Property 3: Dependency Integrity
Output scripts SHALL never reference an object that has not been defined earlier in the same script or in a preceding script in the ordered output set.

**Validates: Requirements 10.4, 16.2**

### Property 4: Session Consistency
After each object is persisted, the session on disk represents a valid state that can be loaded and resumed without data loss.

**Validates: Requirements 13.1, 13.5, NFR 4**

### Property 5: Audit Completeness
Every AI invocation (successful or failed) SHALL have a corresponding audit log entry. The count of audit entries SHALL equal the total number of Bedrock API calls made.

**Validates: Requirements 20.1, 20.5**

### Property 6: Hash Stability
The source definition hash function (SHA-256 of the normalized source DDL) SHALL produce consistent results across application restarts and environments.

**Validates: Requirements 13.3**

### Property 7: No Silent Data Loss
Every Schema_Object discovered during extraction SHALL appear in the final Conversion_Report with a status, even if that status is "out-of-scope" or "failed".

**Validates: Requirements 15.1, 15.3**

## Testing Strategy

### Unit Tests (per project)

| Project | Test Focus | Approach |
|---------|-----------|----------|
| SchemaConversion.RuleEngine | TypeMapper, FunctionMapper, ExpressionTranslator, individual converters | Input SQL → expected PostgreSQL DDL assertions. Golden file tests for complex tables. |
| SchemaConversion.AiEngine | PromptManager template rendering, AiResponseParser validation, BedrockClient retry logic | Mock HTTP responses. Test structured response parsing. Test malformed response detection. |
| SchemaConversion.Orchestration | ObjectClassifier routing, ConversionPipeline flow, SessionChangeDetector hash comparison | Mock ISchemaExtractor, IRuleBasedConverter, IAiConverter. Verify routing decisions and session state. |
| SchemaConversion.Extraction | DependencyGraphBuilder topological sort, cycle detection | In-memory SchemaObject graphs with known dependency structures. |
| SchemaConversion.Reporting | Report JSON structure, script ordering, IF NOT EXISTS generation | Golden file assertions against known session entries. |

### Integration Tests

- **End-to-end pipeline test** — Feed a known SQL Server DDL script through the full pipeline with a mocked Bedrock endpoint. Verify session files, audit log, report, and output scripts.
- **Type mapping exhaustiveness** — Verify all SQL Server types listed in Requirement 18 have entries in `type-mappings.json` and produce valid PostgreSQL output.
- **Function mapping exhaustiveness** — Verify all functions listed in Requirement 19 have entries in `function-mappings.json` and produce valid PostgreSQL expressions.
- **Dependency ordering** — Feed schemas with known dependency chains (including cycles) and verify output script ordering is valid.

### Contract Tests (AI Layer)

- Verify that prompt templates produce prompts parseable by the model (format checks, not semantic).
- Verify that mocked AI responses matching the expected schema are correctly parsed into ConversionResult.
- Verify that responses missing required fields are rejected and trigger retry.

## Traceability Matrix

| Requirement | Primary Component(s) |
|-------------|---------------------|
| Req 1: Source Schema Acquisition | SchemaConversion.Extraction |
| Req 2: Table/Column Conversion | SchemaConversion.RuleEngine (TableConverter, TypeMapper) |
| Req 3: Constraint/Index Conversion | SchemaConversion.RuleEngine (ConstraintConverter, IndexConverter) |
| Req 4: Sequence/View Conversion | SchemaConversion.RuleEngine (SequenceConverter, ViewConverter) |
| Req 5: Schema/Namespace Handling | SchemaConversion.RuleEngine (SchemaConverter) |
| Req 6: User-Defined Types | SchemaConversion.RuleEngine (UserDefinedTypeConverter) |
| Req 7: Stored Procedure Conversion | SchemaConversion.AiEngine (AiConverterService) |
| Req 8: Function/Trigger Conversion | SchemaConversion.AiEngine (AiConverterService) |
| Req 9: Complex Object Conversion | SchemaConversion.AiEngine (AiConverterService) |
| Req 10: Routing/Orchestration | SchemaConversion.Orchestration (ConversionPipeline, ObjectClassifier) |
| Req 11: Bedrock Integration | SchemaConversion.AiEngine (BedrockClient) |
| Req 12: Prompt Versioning | SchemaConversion.AiEngine (PromptManager) |
| Req 13: Incremental Conversion | SchemaConversion.Orchestration (ConversionSessionStore, SessionChangeDetector) |
| Req 14: Manual Review | SchemaConversion.Cli (review/edit/approve commands) |
| Req 15: Reporting | SchemaConversion.Reporting (ConversionReportGenerator) |
| Req 16: Output Scripts | SchemaConversion.Reporting (ScriptGenerator, ScriptOrderResolver) |
| Req 17: Compatibility | SchemaConversion.RuleEngine + AiEngine (Wrapper generation) |
| Req 18: Data Type Mapping | SchemaConversion.RuleEngine (TypeMapper, type-mappings.json) |
| Req 19: Expression Translation | SchemaConversion.RuleEngine (FunctionMapper, ExpressionTranslator) |
| Req 20: Audit/Traceability | SchemaConversion.Orchestration (AuditLogWriter) |
| Req 21: Permissions | SchemaConversion.RuleEngine (PermissionConverter) |
| NFR 1: Performance | SchemaConversion.Orchestration (parallel processing, concurrency limits) |
| NFR 2: Scalability | SchemaConversion.Orchestration (file-per-object session store) |
| NFR 3: Security | All components (credential handling, path validation) |
| NFR 4: Reliability | SchemaConversion.Orchestration (per-object persistence, crash recovery) |
| NFR 5: Maintainability | Config files (type-mappings.json, function-mappings.json) |
