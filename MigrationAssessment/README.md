# Migration Assessment Engine

A command-line tool that analyzes a SQL Server database and produces a PostgreSQL migration readiness assessment with actionable work items. It connects to a live SQL Server instance, collects workload and metadata information, parses captured T-SQL using ScriptDom, scores each statement's migration risk, generates a comprehensive report in JSON format, and creates prioritized work items with effort estimates, remediation guidance, and acceptance criteria for every incompatible feature that needs to be addressed.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Network access to the target SQL Server instance
- A SQL Server login with the following permissions:
  - `VIEW SERVER STATE` (for Query Store and Extended Events)
  - `VIEW DEFINITION` on the target database
  - `SELECT` on `msdb.dbo.sysjobs` and `msdb.dbo.sysjobsteps` (for Agent Job detection)

## Building

From the `MigrationAssessment/` directory:

```bash
dotnet build
```

To publish a self-contained executable:

```bash
dotnet publish src/MigrationAssessment.Cli -c Release -o ./publish
```

## Running

### Basic usage

```bash
dotnet run --project src/MigrationAssessment.Cli -- -c "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True"
```

### With all options

```bash
dotnet run --project src/MigrationAssessment.Cli -- \
  --connection-string "Server=myserver;Database=mydb;User Id=sa;Password=secret;TrustServerCertificate=True" \
  --output ./reports/assessment.json \
  --business-importance 2.5
```

### Using the published executable

```bash
./publish/MigrationAssessment.Cli -c "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True"
```

## Command-Line Options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--connection-string` | `-c` | SQL Server connection string (required) | — |
| `--output` | `-o` | Path for the JSON output file | `./assessment-output.json` |
| `--business-importance` | `-b` | Default business importance multiplier (1.0–5.0) | `1.0` |
| `--help` | `-h` | Show help message | — |

The connection string can also be passed as the first positional argument:

```bash
dotnet run --project src/MigrationAssessment.Cli -- "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True"
```

## What It Collects

The engine gathers data from four independent sources. If any source is unavailable, the assessment continues with the remaining sources.

| Source | What it captures |
|--------|-----------------|
| **Query Store** | Executed SQL statements with execution count, duration, CPU, and logical reads |
| **Extended Events** | Ad hoc SQL, stored procedure calls, dynamic SQL, temp table DDL, TRY/CATCH usage |
| **Database Metadata** | Tables, columns, indexes, constraints, foreign keys, views, procedures, functions, synonyms |
| **Feature Detection** | SQL CLR, Service Broker, Agent Jobs, CDC, Change Tracking, Replication, Linked Servers, Full Text Search, FileStream, XML Indexes, Temporal Tables, Memory-Optimized Tables, Partitioning |

### Data source requirements

- **Query Store**: Must be enabled on the target database (READ_WRITE or READ_ONLY state). If disabled, a warning is logged and the engine continues.
- **Extended Events**: Requires an active session named `migration_assessment`. If no session is found, a warning is logged and the engine continues.
- **Metadata/Features**: Requires `VIEW DEFINITION` permission. Individual feature categories that can't be queried due to permissions are reported as "inaccessible" in the output.

## Output

The engine produces a single JSON file (default: `./assessment-output.json`) containing:

### Executive Summary

- **Migration Readiness Score** (0–100): Overall migration feasibility
- **Classification**: One of Excellent Candidate (90–100), Good Candidate (76–89), Moderate Candidate (51–75), High Risk (26–50), Not Recommended (0–25)
- **Risk Distribution**: Count and percentage of statements at each risk level

### Risk Levels

| Level | Description | Examples | Estimated Effort |
|-------|-------------|----------|-----------------|
| 1 – Trivial | Standard SQL, no extensions | Basic CRUD | 0–5 min |
| 2 – Low | Simple syntax translations | TOP, ISNULL, GETDATE | 5–30 min |
| 3 – Moderate | Procedural changes needed | TRY/CATCH, dynamic SQL, identity | 30 min – 4 hr |
| 4 – High | Significant redesign required | MERGE, TVPs, locking hints | 4–40 hr |
| 5 – Critical | Architectural replacement | SQL CLR, Service Broker, Linked Servers | 40+ hr |

### Migration Recommendation

One of:
- **Direct PostgreSQL Migration** — Score ≥ 76, no Risk 5 statements
- **PostgreSQL Migration with Compatibility Middleware** — Score ≥ 51, few Risk 5 statements
- **Partial Migration** — Score ≥ 26, significant Risk 4/5 presence
- **Remain on SQL Server** — Score < 26, extensive dependencies

### JSON Schema

```json
{
  "assessmentMetadata": {
    "generatedAt": "2024-01-15T10:30:00Z",
    "engineVersion": "1.0.0"
  },
  "executiveSummary": {
    "migrationReadinessScore": 72,
    "classification": "Moderate Candidate - Significant Work Required",
    "totalStatements": 1500,
    "riskDistribution": { "1": 800, "2": 400, "3": 200, "4": 80, "5": 20 }
  },
  "objectInventory": [
    { "objectType": "Table", "objectName": "Orders", "schemaName": "dbo" }
  ],
  "featureInventory": [
    { "featureName": "SQL CLR", "occurrenceCount": 2 }
  ],
  "analyzedStatements": [
    {
      "statementText": "SELECT TOP 10 ...",
      "riskScore": 2,
      "weightedRisk": 200.0,
      "conversionCategory": "automatic",
      "detectedFeatures": ["TOP"]
    }
  ],
  "migrationRecommendation": {
    "recommendation": "PostgreSQL Migration with Compatibility Middleware",
    "reasoning": "...",
    "migrationReadinessScore": 72
  },
  "effort": {
    "schemaConversion": { "minHours": 10, "maxHours": 50 },
    "codeConversion": { "minHours": 40, "maxHours": 400 },
    "testing": { "minHours": 60, "maxHours": 600 },
    "dataMigration": { "minHours": 20, "maxHours": 80 },
    "performanceTuning": { "minHours": 16, "maxHours": 64 },
    "totalClassification": "Large"
  }
}
```

### Conversion Categories

Statements are categorized for downstream tooling:

| Category | Risk Levels | Meaning |
|----------|-------------|---------|
| `automatic` | 1–2 | Can be converted programmatically |
| `semi-automatic` | 3 | Requires human review after automated conversion |
| `manual` | 4–5 | Must be manually rewritten or redesigned |

## Connection and Error Handling

- **Connection retry**: 3 attempts with 5-second delays and 30-second timeout per attempt
- **Query timeout**: 120 seconds per collection query
- **Graceful degradation**: Individual source failures are logged and the assessment continues
- **All-sources failure**: If every data source fails, the engine exits with code 1 (no empty assessments produced)
- **Statement resilience**: Unparseable or failing statements are assigned Risk 3 and processing continues

## Exit Codes

| Code | Meaning |
|------|---------|
| 0 | Assessment completed successfully |
| 1 | Fatal error (connection exhausted, all sources failed, or output write failure) |

## Setting Up Extended Events

To get the most comprehensive assessment, create an Extended Events session on the target server:

```sql
CREATE EVENT SESSION [migration_assessment] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(
    ACTION(sqlserver.database_name, sqlserver.username, sqlserver.sql_text)),
ADD EVENT sqlserver.rpc_completed(
    ACTION(sqlserver.database_name, sqlserver.username, sqlserver.sql_text)),
ADD EVENT sqlserver.sp_statement_completed(
    ACTION(sqlserver.database_name, sqlserver.username))
ADD TARGET package0.ring_buffer(SET max_memory = 51200)
WITH (MAX_DISPATCH_LATENCY = 5 SECONDS);

ALTER EVENT SESSION [migration_assessment] ON SERVER STATE = START;
```

This session captures SQL batches, RPC calls, and stored procedure statements into a ring buffer. The engine reads from this buffer during collection.

## Work Item Generation

The tool can generate structured work items from an assessment report, creating actionable tasks for each incompatible SQL Server feature that needs to be addressed during migration. Work items include titles, descriptions, risk levels, effort estimates, PostgreSQL conversion examples, remediation guidance, and acceptance criteria.

### Integrated Mode (during assessment)

Generate work items as part of the assessment pipeline:

```bash
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True" \
  --generate-work-items
```

With all options:

```bash
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=myserver;Database=mydb;User Id=sa;Password=secret;TrustServerCertificate=True" \
  --output ./reports/assessment.json \
  --generate-work-items \
  --work-item-output ./reports/work-items.json \
  --work-item-markdown \
  --work-item-markdown-output ./reports/work-items.md \
  --work-item-min-risk 3 \
  --work-item-max-count 20
```

### Standalone Mode (from existing assessment)

Generate work items from a previously-saved assessment JSON file without re-running the assessment:

```bash
dotnet run --project src/MigrationAssessment.Cli -- generate-work-items ./assessment-output.json
```

With options:

```bash
dotnet run --project src/MigrationAssessment.Cli -- generate-work-items ./assessment-output.json \
  --output ./work-items.json \
  --markdown \
  --markdown-output ./work-items.md \
  --min-risk 3 \
  --max-items 10
```

### Work Item Command-Line Options

#### Integrated mode flags (used with `-c`)

| Option | Description | Default |
|--------|-------------|---------|
| `--generate-work-items` | Enable work item generation after assessment | Off |
| `--work-item-output <path>` | JSON output path | `./work-items.json` |
| `--work-item-markdown` | Enable Markdown report | Off |
| `--work-item-markdown-output <path>` | Markdown output path | Same dir as JSON |
| `--work-item-min-risk <1-5>` | Minimum risk level filter | `1` |
| `--work-item-max-count <n>` | Maximum number of work items | Unlimited |

#### Standalone mode (`generate-work-items` verb)

| Argument/Option | Description | Default |
|-----------------|-------------|---------|
| `<input-file-path>` | Path to assessment JSON file (required) | — |
| `--output <path>` | JSON output path | `./work-items.json` |
| `--markdown` | Enable Markdown output | Off |
| `--markdown-output <path>` | Markdown output path | Same dir as JSON |
| `--min-risk <1-5>` | Minimum risk level filter | `1` |
| `--max-items <count>` | Maximum work items to generate | Unlimited |

### Output

Work items are written as both JSON and (optionally) Markdown. Each work item includes:

- **ID and title** — e.g., `WI-001: [Risk 5] Convert XML_METHOD in sp_GetOrderShippingInfo`
- **Description** — What the incompatibility is and where it occurs
- **SQL Server pattern** — The original T-SQL code
- **PostgreSQL equivalent** — Suggested converted code with TODO annotations
- **Risk level and priority** — Scored and ranked (Critical, High, Medium, Low)
- **Estimated effort** — Min/max hours with confidence level
- **Affected objects** — Which stored procedures, views, or ad hoc queries are impacted
- **Remediation guidance** — Step-by-step conversion instructions
- **Acceptance criteria** — Definition of done for each work item
- **Tags** — For filtering (e.g., `risk-4`, `transaction-feature`, `manual`)
- **Related work item IDs** — Cross-references between related items

The JSON output includes a metadata section with total counts, effort rollups, confidence breakdown, and validation results.

## Running Tests

```bash
# All unit tests (no SQL Server required)
dotnet test

# Filter out integration tests if added later
dotnet test --filter "Category!=Integration"
```

## Project Structure

```
MigrationAssessment/
├── MigrationAssessment.slnx
├── src/
│   ├── MigrationAssessment.Core/          # Models, interfaces, enumerations
│   ├── MigrationAssessment.Collectors/    # Query Store, XE, Metadata, Feature collectors
│   ├── MigrationAssessment.Analysis/      # ScriptDom parser, AST visitor, risk scorer
│   ├── MigrationAssessment.Reporting/     # Report generator, JSON writer
│   └── MigrationAssessment.Cli/           # Console entry point, DI, pipeline orchestrator
└── tests/
    ├── MigrationAssessment.Core.Tests/
    ├── MigrationAssessment.Collectors.Tests/
    ├── MigrationAssessment.Analysis.Tests/
    └── MigrationAssessment.Reporting.Tests/
```
