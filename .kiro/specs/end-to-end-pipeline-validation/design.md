# Design Document: End-to-End Pipeline Validation

## Overview

Extends Run-MigrationPipeline.ps1 from a 4-step DDL-syntax pipeline to a 7-step end-to-end migration validation system. The enhanced pipeline exercises the full migration toolchain — AI-assisted schema conversion, AI-assisted error correction, data replication, and live T-SQL query execution — producing a composite score that reflects actual migration readiness.

## Architecture

```
┌─────────┐  ┌─────────┐  ┌──────────┐  ┌──────────┐  ┌───────┐  ┌──────────────┐  ┌─────────────────┐  ┌─────────────┐
│ Extract │→ │ Convert │→ │ Generate │→ │ Validate │→ │ Apply │→ │ Fix Loop (AI)│→ │ Data Migration  │→ │ Functional  │
│         │  │         │  │          │  │(syntax)  │  │ (DDL) │  │ (Bedrock)    │  │ (DataMigrator)  │  │ Tests       │
└─────────┘  └─────────┘  └──────────┘  └──────────┘  └───────┘  └──────────────┘  └─────────────────┘  └─────────────┘
                                                           │              │                  │                    │
                                                           ▼              ▼                  ▼                    ▼
                                                      PostgreSQL    AWS Bedrock         SQL Server →        PgPassthrough
                                                      (destination)  (Claude)           PostgreSQL           (TDS proxy)
```

Steps 1–4 remain unchanged (backward compatible). Steps 5–7 are gated by the presence of `endToEnd` configuration.

## Component Integration

### Existing Components Reused

| Component | Current Usage | Pipeline Integration |
|-----------|--------------|---------------------|
| `DatabaseApplyService` (ConversionReviewer) | Blazor UI button click | Extracted logic invoked programmatically via .NET CLI |
| `BedrockFixService` (ConversionReviewer) | Blazor UI "Fix with AI" button | Called in a loop from the pipeline for failed DDL |
| `DataMigrator` CLI | Manual invocation by user | Invoked as subprocess with --source, --target, --session |
| `PgPassthrough.Server` | Started manually, client connects via ODBC | Started as background process, test scripts executed via SqlClient |

### New Components

| Component | Type | Purpose |
|-----------|------|---------|
| `Invoke-DdlApplication.ps1` | PowerShell module (lib/) | Applies DDL to PostgreSQL with dependency ordering, fresh schema creation |
| `Invoke-FixLoop.ps1` | PowerShell module (lib/) | Orchestrates the AI fix cycle via a thin .NET CLI wrapper |
| `Invoke-DataMigration.ps1` | PowerShell module (lib/) | Invokes DataMigrator CLI as subprocess |
| `Invoke-FunctionalTests.ps1` | PowerShell module (lib/) | Starts PgPassthrough, runs test scripts, captures results |
| `Invoke-EndToEndScoring.ps1` | PowerShell module (lib/) | Computes composite End_To_End_Score |
| `SchemaConversion.Cli fix` command | .NET CLI command | Headless wrapper around BedrockFixService for pipeline use |

## Detailed Step Design

### Step 5: DDL Application

**Entry condition:** Validate step completed (regardless of syntax-only results). `endToEnd` configuration present.

**Process:**
1. Drop and recreate the destination database (or create a fresh schema) for isolation
2. Read DDL from session objects (using `Read-ValidationResults` from existing pipeline)
3. Apply objects in topological dependency order (tables first, then views/functions/procedures/triggers)
4. For each object:
   - Execute DDL against PostgreSQL via Npgsql
   - Record: objectName, status (applied/failed), errorMessage, elapsedMs
5. Objects that fail proceed to Step 5b (Fix Loop)

**Database isolation:** Each pipeline run drops the destination database and recreates it. This ensures no state leaks between runs.

```powershell
# Pseudocode
$conn = Connect-PostgreSQL -ConnectionString $maintenanceConnStr  # connect to 'postgres' db
Execute "DROP DATABASE IF EXISTS $destDbName"
Execute "CREATE DATABASE $destDbName"
```

### Step 5b: AI-Assisted Fix Loop

**Entry condition:** One or more DDL statements failed in Step 5.

**Process per failed object:**
1. Submit to BedrockFixService: failed DDL + PostgreSQL error + original T-SQL source
2. Receive corrected DDL
3. Re-apply corrected DDL to destination database
4. If still fails: repeat with new error (up to N=2 attempts by default)
5. Record: attempts taken, final status (fixed/unfixable), final DDL, explanation

**Implementation:** A new `fix` CLI command on SchemaConversion.Cli that wraps `BedrockFixService`:

```
dotnet run --project SchemaConversion.Cli -- fix \
  --failed-ddl "..." --error "..." --source-tsql "..." --max-attempts 2
```

Returns JSON: `{ "success": true/false, "fixedDdl": "...", "attempts": 2, "explanation": "..." }`

The pipeline invokes this CLI and parses the JSON output.

### Step 6: Data Migration

**Entry condition:** At least one table was successfully applied (or fixed) in Steps 5/5b.

**Process:**
1. Invoke DataMigrator as subprocess:
   ```
   dotnet run --project DataMigrator -- \
     --source "Server=...;Database=ProcedureComplexityDB;..." \
     --target "Host=...;Database=procedure_complexity_dest;..." \
     --session "sessions/procedure-complexity" \
     --truncate --disable-fk --reseed
   ```
2. Parse stdout for results (tables migrated, rows, failures)
3. Record: tablesSucceeded, tablesFailed, totalRows, elapsed

### Step 7: Functional Testing via PgPassthrough

**Entry condition:** Data migration completed with at least one table migrated.

**Process:**
1. Start PgPassthrough server as background process, pointed at the destination PostgreSQL database
2. Wait for PgPassthrough to be ready (poll TCP port)
3. Execute test scripts via SqlClient connecting to PgPassthrough (TDS protocol)
4. Each test script contains T-SQL queries + expected result assertions
5. Record per-script: name, status (pass/fail), errorMessage, elapsed
6. Stop PgPassthrough server

**Test script format:**
```sql
-- test: Select all departments
-- expect-rows: > 0
SELECT * FROM Departments;

-- test: Call stored procedure
-- expect-no-error
EXEC sp_GetDepartmentStats @DeptId = 1;

-- test: Verify row count
-- expect-value: 7
SELECT COUNT(*) FROM Employees;
```

**Test script discovery:** `tests/functional/{database-name}/*.sql` relative to MigrationAssessment root. Each database in the batch config can specify its own test directory.

## Scoring Model

### End-to-End Score Formula

```
E2E Score = (DDL_Weight × DDL_Rate) + (Data_Weight × Data_Rate) + (Test_Weight × Test_Rate)

Where:
  DDL_Rate  = (applied + fixed) / total_objects
  Data_Rate = tables_migrated / total_tables
  Test_Rate = tests_passed / total_tests

Default weights: DDL=40%, Data=30%, Tests=30%
```

### Report Extension

The existing scoring-report JSON is extended with:

```json
{
  "aggregate": { ... },
  "endToEnd": {
    "enabled": true,
    "endToEndScore": 85.2,
    "previousEndToEndScore": 72.0,
    "endToEndDelta": 13.2,
    "ddlApplication": {
      "total": 17,
      "appliedFirstTry": 11,
      "appliedAfterFix": 4,
      "unfixable": 2,
      "rate": 88.2
    },
    "fixLoop": {
      "totalAttempted": 6,
      "totalFixed": 4,
      "averageAttempts": 1.5,
      "objects": [
        { "name": "sp_X", "attempts": 2, "finalStatus": "fixed", "explanation": "..." }
      ]
    },
    "dataMigration": {
      "tablesTotal": 8,
      "tablesSucceeded": 7,
      "tablesFailed": 1,
      "totalRows": 15420,
      "rate": 87.5,
      "elapsed": 12.3
    },
    "functionalTests": {
      "total": 20,
      "passed": 17,
      "failed": 3,
      "rate": 85.0,
      "results": [
        { "script": "basic-queries.sql", "test": "Select all departments", "status": "pass" }
      ]
    },
    "timing": {
      "applyElapsed": 8.2,
      "fixLoopElapsed": 45.1,
      "dataMigrationElapsed": 12.3,
      "functionalTestElapsed": 6.7,
      "totalEndToEndElapsed": 72.3
    }
  }
}
```

## Configuration Schema

### pipeline-config.json Extension

```json
{
  "databases": [...],
  "validation": {...},
  "reporting": {...},
  "endToEnd": {
    "enabled": true,
    "destinationConnectionString": "Host=localhost;Database=validation_e2e;Username=postgres;Password=postgres",
    "maintenanceConnectionString": "Host=localhost;Database=postgres;Username=postgres;Password=postgres",
    "maxFixAttempts": 2,
    "pgPassthroughPath": "c:\\code\\ODBC_TSQL_To_SQL\\PgPassthrough\\src\\PgPassthrough.Server",
    "pgPassthroughPort": 11433,
    "testScriptDirectory": "./tests/functional",
    "timeoutPerScript": 30,
    "scoring": {
      "ddlWeight": 0.4,
      "dataWeight": 0.3,
      "testWeight": 0.3
    },
    "databases": {
      "ProcedureComplexityDB": {
        "destinationDatabase": "procedure_complexity_e2e",
        "testScripts": "./tests/functional/procedure-complexity"
      }
    }
  }
}
```

### CLI Parameters Extension

```
Run-MigrationPipeline.ps1
  ... (existing parameters) ...
  -EndToEnd                     Enable end-to-end validation mode
  -MaxFixAttempts <int>         Override fix loop max attempts (default: 2)
  -DestPgConnectionString <str> Destination PostgreSQL connection for DDL application
  -PgPassthroughPort <int>     Port for PgPassthrough (default: 11433)
```

## Implementation Strategy

### Phase 1: DDL Application + Fix Loop (Steps 5 + 5b)
- Add `fix` command to SchemaConversion.Cli
- Create `Invoke-DdlApplication.ps1` module
- Create `Invoke-FixLoop.ps1` module
- Integrate into Run-MigrationPipeline.ps1 gated by `-EndToEnd` flag

### Phase 2: Data Migration (Step 6)
- Create `Invoke-DataMigration.ps1` module
- Invoke DataMigrator CLI as subprocess
- Parse output and integrate results

### Phase 3: Functional Testing (Step 7)
- Define test script format and assertion syntax
- Create `Invoke-FunctionalTests.ps1` module
- Write initial test scripts for each database
- Start/stop PgPassthrough as part of pipeline

### Phase 4: Scoring + Reporting
- Create `Invoke-EndToEndScoring.ps1` module
- Extend report JSON schema
- Delta comparison with previous E2E scores

## Error Handling

- **PostgreSQL unavailable:** Skip all end-to-end steps; fall back to syntax-only scoring. Log clearly.
- **Bedrock unavailable:** Skip fix loop; record all failures as "unfixable". Continue to data migration (with only first-try-applied objects).
- **DataMigrator crash:** Record partial results; continue to functional tests (which may fail due to missing data).
- **PgPassthrough fails to start:** Skip functional tests; compute partial E2E score without test component (re-weight DDL=57%, Data=43%).
- **Timeout:** Each step has configurable timeout. Exceeded = recorded as failure for that item.

## Security Considerations

- Destination database is dropped/recreated each run — only use a dedicated test database, never production.
- Connection strings with passwords should use environment variables in CI; the pipeline-config.json supports `${ENV_VAR}` syntax for sensitive values.
- BedrockFixService calls use the same AWS credentials as the existing ConversionReviewer (IAM role or profile).

## Dependencies

- Existing: Npgsql assembly for PostgreSQL connections, AWS SDK for Bedrock
- Existing: DataMigrator CLI, PgPassthrough.Server, SchemaConversion.Cli
- New: `fix` command added to SchemaConversion.Cli
- Infrastructure: Docker (SQL Server), PostgreSQL instance, AWS Bedrock access
