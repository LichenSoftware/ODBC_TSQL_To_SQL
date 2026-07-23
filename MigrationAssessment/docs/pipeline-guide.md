# Migration Validation Pipeline Guide

This guide walks through setting up and running the Migration Validation Pipeline — an automated system that runs the full Extract → Convert → Generate → Validate cycle across multiple SQL Server test databases, scores the results, and produces actionable diagnostics for improving the conversion engine.

## Prerequisites

- Docker Desktop installed and running (for SQL Server)
- .NET 8 SDK
- PowerShell 7+ (PowerShell Core)
- (Optional) A running PostgreSQL instance for live validation
- (Optional) `sqlcmd` CLI for running database setup scripts

## Overview

The pipeline validates the AI-Assisted Schema Conversion tool by:

1. Extracting schema objects from SQL Server test databases
2. Converting them to PostgreSQL DDL using the conversion engine
3. Generating consolidated DDL output
4. Validating the generated DDL against PostgreSQL syntax rules
5. Computing a Compatibility Score (target: 70%+)
6. Classifying failures into root cause categories to guide improvements

```
┌─────────┐    ┌─────────┐    ┌──────────┐    ┌──────────┐    ┌─────────────┐
│ Extract │ →  │ Convert │ →  │ Generate │ →  │ Validate │ →  │ Score/Report│
└─────────┘    └─────────┘    └──────────┘    └──────────┘    └─────────────┘
```

## Step 1: Set Up Test Databases

The pipeline includes 5 test databases, each stressing different SQL Server features:

| Database | Focus | Setup Script |
|----------|-------|-------------|
| AssessmentTestDB | General features (Risk 1–5) | `scripts/setup-test-database.sql` |
| ProcedureComplexityDB | Cursors, nested TRY/CATCH, TVPs, OUTPUT params | `scripts/setup-procedure-complexity-db.sql` |
| ViewsTriggerDB | Indexed views, INSTEAD OF triggers, APPLY operators | `scripts/setup-views-triggers-db.sql` |
| TypesAndCLRDB | Table types, alias types with rules, CLR stubs | `scripts/setup-types-clr-db.sql` |
| CrossSchemaAdvancedDB | Multi-schema deps, partitioning, RLS, temporal tables | `scripts/setup-cross-schema-advanced-db.sql` |

### Start SQL Server

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong!Pass123" \
  -p 1433:1433 --name sql-assessment \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

### Run All Setup Scripts

```bash
# Copy scripts into the container
docker cp "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts" sql-assessment:/tmp/scripts/

# Run each setup script
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/scripts/setup-test-database.sql -C
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/scripts/setup-procedure-complexity-db.sql -C
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/scripts/setup-views-triggers-db.sql -C
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/scripts/setup-types-clr-db.sql -C
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/scripts/setup-cross-schema-advanced-db.sql -C
```

Or with local `sqlcmd`:

```bash
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\setup-test-database.sql"
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\setup-procedure-complexity-db.sql"
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\setup-views-triggers-db.sql"
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\setup-types-clr-db.sql"
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\setup-cross-schema-advanced-db.sql"
```

## Step 2: (Optional) Set Up PostgreSQL for Live Validation

For highest-confidence validation, set up a PostgreSQL instance:

```bash
docker run -e "POSTGRES_PASSWORD=postgres" \
  -p 5432:5432 --name pg-validation \
  -d postgres:16
```

Create a scratch database for validation:

```bash
docker exec pg-validation psql -U postgres -c "CREATE DATABASE validation_scratch;"
```

If you skip this step, the pipeline falls back to syntax-only validation (pattern-based checks that catch common issues but have lower confidence than running DDL against real PostgreSQL).

## Step 3: Run the Pipeline

### Single Database Mode

Run the pipeline for one database:

```powershell
.\Run-MigrationPipeline.ps1 -ConnectionString "Server=localhost;Database=ProcedureComplexityDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" -SessionName "procedure-complexity" -PgConnectionString "Host=localhost;Database=validation_scratch;Username=postgres;Password=postgres"

```

Without PostgreSQL (syntax-only mode):

```powershell
.\Run-MigrationPipeline.ps1 `
  -ConnectionString "Server=localhost;Database=ProcedureComplexityDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" `
  -SessionName "procedure-complexity" `
  -ValidationMode "syntax-only"
```

### Batch Mode (All 5 Databases)

Run the pipeline across all configured databases with a single command:

```powershell
.\Run-MigrationPipeline.ps1 `
  -BatchConfig "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\pipeline-config.json"
```

With live PostgreSQL validation:

```powershell
.\Run-MigrationPipeline.ps1 `
  -BatchConfig "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\pipeline-config.json" `
  -PgConnectionString "Host=localhost;Database=validation_scratch;Username=postgres;Password=postgres"
```

Batch mode processes each database sequentially. If one database fails (e.g., connection timeout), the pipeline logs the error and continues with the remaining databases.

### Rerun Failures Only

After a full run, re-convert only the objects that failed (preserving pass/skip results):

```powershell
.\Run-MigrationPipeline.ps1 `
  -ConnectionString "Server=localhost;Database=ProcedureComplexityDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" `
  -SessionName "procedure-complexity" `
  -RerunFailures
```

This is the main iteration loop: fix a rule/prompt, rerun failures, check if the score improved.

## Step 4: Read the Scoring Report

Reports are saved to `c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\pipeline-reports\` as timestamped JSON files:

```
pipeline-reports/
  scoring-report-20260722-143052.json
  scoring-report-20260722-151230.json
```

### Report Structure

```json
{
  "reportId": "a1b2c3d4-...",
  "timestamp": "2026-07-22T14:30:52.000Z",
  "totalElapsedSeconds": 145.3,
  "validationMode": "live-instance",
  "configHashes": { "type-mappings.json": "sha256-...", ... },
  "databases": [...],
  "aggregate": {
    "compatibilityScore": 72.5,
    "previousScore": 65.0,
    "delta": 7.5,
    "totalPass": 62,
    "totalFailSyntax": 12,
    "totalFailConvert": 8,
    "totalSkip": 3
  },
  "diagnostics": {
    "rootCauseCategories": [...],
    "topFailingTypes": [...]
  }
}
```

### Key Metrics

- **Compatibility Score**: `(pass) / (pass + fail-syntax + fail-convert) × 100`. Target is 70%+.
- **Delta**: Difference from previous run. Positive = improvement.
- **Per-database scores**: Each database has its own score so you can see which patterns are hardest.
- **Per-type breakdown**: Scores for Table, View, StoredProcedure, Function, Trigger individually.

### Batch Summary Table

After a batch run, the pipeline prints a summary table:

```
=== Batch Execution Summary ===
+------------------------+---------+------+------+---------+
| Database               | Objects | Pass | Fail | Score   |
+------------------------+---------+------+------+---------+
| AssessmentTestDB       |      18 |   14 |    4 |  77.8%  |
| ProcedureComplexityDB  |      18 |   11 |    7 |  61.1%  |
| ViewsTriggerDB         |      20 |   16 |    4 |  80.0%  |
| TypesAndCLRDB          |      22 |   15 |    7 |  68.2%  |
| CrossSchemaAdvancedDB  |      19 |   14 |    5 |  73.7%  |
+------------------------+---------+------+------+---------+
```

## Step 5: Use Diagnostics to Improve the Conversion Engine

The `diagnostics` section groups failures by root cause:

| Category | What It Means | Where to Fix |
|----------|--------------|--------------|
| type mapping gap | An unrecognized SQL Server data type | `config/type-mappings.json` |
| function mapping gap | An undefined function or operator | `config/function-mappings.json` |
| procedural pattern not handled | Error inside a PL/pgSQL function body | `config/prompts/*.md` templates |
| AI prompt deficiency | Conversion produced empty/placeholder output | `config/prompts/*.md` templates |
| dependency resolution failure | Reference to a missing prerequisite object | Schema extraction or ordering logic |

### Iteration Workflow

1. Run the batch pipeline → check the aggregate score
2. Look at `diagnostics.rootCauseCategories` (sorted by failure count)
3. Fix the highest-count category:
   - **Type mapping gap**: Add the missing type to `config/type-mappings.json`
   - **Function mapping gap**: Add the mapping to `config/function-mappings.json`
   - **Procedural pattern**: Improve the relevant prompt template in `config/prompts/`
   - **AI prompt deficiency**: Strengthen the prompt with more examples
4. Run with `-RerunFailures` to test your fix on just the previously-failed objects
5. Repeat until aggregate score reaches 70%+

### Change Detection

The pipeline automatically detects when you modify config or prompt files. It compares SHA-256 hashes between runs and re-converts affected object types:

- Editing `type-mappings.json` → re-converts all object types
- Editing `stored-procedure.v1.0.0.md` → re-converts only StoredProcedure objects
- Editing `view.v1.0.0.md` → re-converts only View objects

This happens automatically on the next pipeline run — no special flags needed.

## Command Reference

```
c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\Run-MigrationPipeline.ps1
  -ConnectionString <string>    SQL Server connection string (single-db mode)
  -SessionName <string>         Session identifier (required with -ConnectionString)
  -BatchConfig <path>           Path to pipeline-config.json (batch mode)
  -RerunFailures                Re-convert only previously failed objects
  -ValidationMode <string>      "live-instance" or "syntax-only" (default: auto-detect)
  -PgConnectionString <string>  PostgreSQL connection string for live validation
```

## Running the Tests

### Property-Based Tests (FsCheck/.NET)

```bash
dotnet test "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\tests\MigrationAssessment.Pipeline.PropertyTests"
```

These validate the correctness properties of the scoring, classification, validation, and pipeline logic with randomized inputs.

### Unit Tests (Pester/PowerShell)

```powershell
Invoke-Pester -Path "c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\tests\Pipeline.Tests" -Output Detailed
```

These test the PowerShell modules with known inputs and expected outputs.

## Pipeline Configuration

The batch config file (`c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\pipeline-config.json`) controls which databases to process:

```json
{
  "databases": [
    {
      "name": "MyNewTestDB",
      "connectionString": "Server=localhost;Database=MyNewTestDB;...",
      "sessionName": "my-new-test",
      "setupScript": "scripts/setup-my-new-test-db.sql"
    }
  ],
  "validation": {
    "pgConnectionString": "Host=localhost;Database=validation_scratch;...",
    "timeoutSeconds": 30,
    "fallbackToSyntaxOnly": true
  },
  "reporting": {
    "outputDirectory": "./pipeline-reports",
    "trackProgression": true
  }
}
```

To add a new test database:
1. Create a setup script in `c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\scripts\`
2. Add an entry to `c:\code\ODBC_TSQL_To_SQL\MigrationAssessment\pipeline-config.json`
3. Run the setup script against your SQL Server instance
4. Run the pipeline in batch mode

## Troubleshooting

### "Either -ConnectionString or -BatchConfig must be specified"

You must specify one of the two modes. Use `-ConnectionString` + `-SessionName` for a single database, or `-BatchConfig` for batch mode.

### Pipeline halts at "Extract" step

Check the SQL Server connection string. The database must be accessible and the user must have sufficient permissions (VIEW DEFINITION, VIEW SERVER STATE).

### All objects show "fail-syntax" in syntax-only mode

Syntax-only mode uses pattern-based checks that flag T-SQL remnants (NVARCHAR, IDENTITY, etc.). If the conversion engine isn't fully translating these, they'll all fail. Set up PostgreSQL for more accurate live-instance validation.

### Score shows "N/A" for a database

All objects in that database were classified as "skip" (not in the convertible set of Table, View, StoredProcedure, Function, Trigger). The database may only contain objects like Synonyms or Sequences that the pipeline doesn't validate.

### "No previous Scoring Report found" when using -RerunFailures

You need to run the pipeline at least once without `-RerunFailures` to produce an initial report. The rerun mode reads from the most recent report in `pipeline-reports/`.

### Batch mode shows "ERROR" for a database

That database failed completely (connection error or pipeline failure). The error is logged and the batch continues with remaining databases. Check the console output for the specific error message.

## File Layout

```
MigrationAssessment/
├── pipeline-config.json              # Batch configuration
├── pipeline-reports/                 # Generated scoring reports (JSON)
├── scripts/
│   ├── Run-MigrationPipeline.ps1     # Pipeline runner (main entry point)
│   ├── lib/
│   │   ├── Invoke-Scoring.ps1        # Scoring engine
│   │   ├── Invoke-DiagnosticsClassification.ps1  # Failure classifier
│   │   ├── Invoke-PgValidation.ps1   # PostgreSQL validator
│   │   └── Invoke-ReportGeneration.ps1  # Report serializer
│   ├── setup-test-database.sql       # AssessmentTestDB
│   ├── setup-procedure-complexity-db.sql
│   ├── setup-views-triggers-db.sql
│   ├── setup-types-clr-db.sql
│   └── setup-cross-schema-advanced-db.sql
├── tests/
│   ├── Pipeline.Tests/               # Pester unit tests
│   └── MigrationAssessment.Pipeline.PropertyTests/  # FsCheck property tests
└── docs/
    └── pipeline-guide.md             # This file
```
