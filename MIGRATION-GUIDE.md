# SQL Server to PostgreSQL Migration Guide

A complete end-to-end guide for migrating a Microsoft SQL Server database to PostgreSQL using the Migris Technology toolchain. This document covers every step from initial assessment through final validation.

## Overview

The migration pipeline consists of seven tools, run in sequence:

```
┌──────────────────────┐     ┌────────────────────────────┐     ┌─────────────────────┐
│ 1. MigrationAssessment│────▶│ 2. AI-AssistedSchemaConv.  │────▶│ 3. ConversionReviewer│
└──────────────────────┘     └────────────────────────────┘     └─────────────────────┘
                                                                          │
┌──────────────────────┐     ┌────────────────────────────┐              │
│ 7. MigrationValidation│◀────│ 6. PgPassthrough           │◀─────────────┤
└──────────────────────┘     └────────────────────────────┘              │
                                       ▲                                  │
┌──────────────────────┐               │                    ┌────────────▼────────┐
│ 5. DataMigrator      │───────────────┘                    │ 4. MappingGenerator │
└──────────────────────┘                                    └─────────────────────┘
```

| Step | Tool | Purpose |
|------|------|---------|
| 1 | **MigrationAssessment** | Analyze the source database and produce a readiness report |
| 2 | **AI-AssistedSchemaConversion** | Convert SQL Server DDL to PostgreSQL DDL |
| 3 | **ConversionReviewer** | Review, edit, and apply the converted DDL to PostgreSQL |
| 4 | **MappingGenerator** | Generate procedure-mapping metadata for PgPassthrough |
| 5 | **DataMigrator** | Copy data from SQL Server to PostgreSQL |
| 6 | **PgPassthrough** | Provide a TDS-compatible endpoint so existing apps connect unchanged |
| 7 | **MigrationValidation** | Verify the migration by running T-SQL tests against both endpoints |

---

## Prerequisites

- **.NET 8 SDK** — all tools are .NET 8 console/web applications
- **SQL Server** — source database with appropriate permissions
- **PostgreSQL** — destination server (14+ recommended)
- **AWS Credentials** — required for AI-assisted conversion (Amazon Bedrock access)
- **Network access** to both database servers from the machine running the tools

All commands below assume a workspace root of `c:\code\ODBC_TSQL_To_SQL`. Adjust paths if your layout differs.

---

## Step 1: Migration Assessment

**Tool:** `MigrationAssessment`  
**Purpose:** Connect to the live SQL Server, analyze workloads and schema, score migration risk, and produce a JSON report with effort estimates and recommendations.

### 1.1 Build

```bash
cd MigrationAssessment
dotnet build
```

### 1.2 (Optional) Set Up Extended Events for Deeper Analysis

For the most comprehensive assessment, create an Extended Events session on your SQL Server:

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

Let this session run during normal business hours to capture representative workload data before running the assessment.

### 1.3 Run the Assessment

```bash
dotnet run --project src/MigrationAssessment.Cli -- ^
  --connection-string "Server=localhost;Database=MyDatabase;User Id=sa;Password=YourPass;TrustServerCertificate=True" ^
  --output ./reports/assessment.json ^
  --business-importance 1.5
```

### 1.4 Interpret the Results

Open `assessment.json` and review:

| Section | What to Look For |
|---------|-----------------|
| `executiveSummary.migrationReadinessScore` | Score 0–100. Above 75 is a good candidate. |
| `executiveSummary.riskDistribution` | Count of statements at each risk level (1–5) |
| `featureInventory` | SQL Server features in use (CLR, Service Broker, etc.) |
| `migrationRecommendation` | Direct migration, middleware-assisted, or remain on SQL Server |
| `effort` | Estimated hours by category |

**Decision point:** If the score is below 26 or the tool recommends "Remain on SQL Server," evaluate whether the blocking features can be redesigned before proceeding.

### 1.5 Address Issues

Use the feature inventory and risk-5 statements to identify objects that need redesign or removal before conversion. Common pre-migration cleanup:

- Remove or replace SQL CLR dependencies
- Replace Linked Server queries with application-level data access
- Simplify Service Broker patterns into application queuing
- Document FILESTREAM usage for alternative storage strategy

---

## Step 2: AI-Assisted Schema Conversion

**Tool:** `AI-AssistedSchemaConversion`  
**Purpose:** Extract all schema objects from SQL Server (or DDL files), convert them to PostgreSQL DDL using deterministic rules and AI, and produce a session containing the converted results.

### 2.1 Build

```bash
cd AI-AssistedSchemaConversion
dotnet build
```

### 2.2 Configure (Optional)

Edit `appsettings.json` to adjust:

| Setting | Default | Notes |
|---------|---------|-------|
| `Bedrock.ModelId` | `anthropic.claude-sonnet-4-20250514-v1:0` | Amazon Bedrock model for AI conversion |
| `Bedrock.Region` | `us-east-1` | AWS region |
| `Conversion.ConfidenceThreshold` | `0.7` | Below this score, objects are flagged for review |
| `Conversion.DefaultConcurrency` | `4` | Parallel conversion threads |

### 2.3 Extract Schema Objects

**From a live database:**

```bash
dotnet run --project src/SchemaConversion.Cli -- extract ^
  --connection "Server=localhost;Database=MyDatabase;User Id=sa;Password=YourPass;TrustServerCertificate=True" ^
  --output ./sessions/my-migration
```

**From DDL script files:**

```bash
dotnet run --project src/SchemaConversion.Cli -- extract ^
  --files ./my-sql-scripts ^
  --output ./sessions/my-migration
```

### 2.4 Convert to PostgreSQL

```bash
dotnet run --project src/SchemaConversion.Cli -- convert --session ./sessions/my-migration
```

The tool automatically routes each object:
- **Tables, indexes, constraints, sequences** → deterministic rule-based conversion
- **Stored procedures, functions, triggers, complex views** → AI-assisted via Amazon Bedrock

Watch the summary line:
```
Converted: 45  Flagged: 3  Failed: 2
```

### 2.5 Review Flagged Objects

```bash
dotnet run --project src/SchemaConversion.Cli -- review --session ./sessions/my-migration --flagged-only
```

For each flagged object, you can:
- Accept it as-is (if the generated DDL looks correct despite low confidence)
- Edit and re-apply with `schema-convert edit --session ... --object dbo.MyProc --file ./fixed.sql`
- Re-run with forced method: `--force-ai` or `--force-rules`

### 2.6 Approve and Generate Output

```bash
# Approve all (or selectively approve specific objects)
dotnet run --project src/SchemaConversion.Cli -- approve --session ./sessions/my-migration --all

# Generate consolidated DDL
dotnet run --project src/SchemaConversion.Cli -- generate ^
  --session ./sessions/my-migration ^
  --output ./output/scripts ^
  --mode consolidated
```

### 2.7 Generate a Report (Optional)

```bash
dotnet run --project src/SchemaConversion.Cli -- report ^
  --session ./sessions/my-migration ^
  --output ./output/report.json
```

---

## Step 3: Conversion Reviewer

**Tool:** `ConversionReviewer`  
**Purpose:** A visual web interface for reviewing the AI-generated DDL side-by-side with the original T-SQL, editing it, and applying scripts directly to your PostgreSQL database in dependency order.

### 3.1 Configure

Edit `ConversionReviewer/src/ConversionReviewer/appsettings.json`:

```json
{
  "SessionsPath": "..\\..\\..\\AI-AssistedSchemaConversion\\sessions",
  "ConnectionStrings": {
    "TargetPostgres": "Host=localhost;Port=5432;Database=MyDatabase;Username=postgres;Password=YourPass"
  }
}
```

| Setting | Description |
|---------|-------------|
| `SessionsPath` | Path to the AI-AssistedSchemaConversion sessions folder |
| `TargetPostgres` | Connection string to your destination PostgreSQL database |

### 3.2 Create the Target Database

Before applying scripts, create an empty database on PostgreSQL:

```sql
CREATE DATABASE "MyDatabase";
```

### 3.3 Run the Reviewer

```bash
cd ConversionReviewer
dotnet run --project src/ConversionReviewer
```

Open **http://localhost:5100** in your browser.

### 3.4 Review and Apply

1. **Select your session** from the dropdown (e.g., `my-migration`)
2. **Browse objects** in the grid — filter by type (Table, View, Function, etc.)
3. **Review** each object side-by-side: source T-SQL on the left, generated PostgreSQL on the right
4. **Edit** any DDL that needs adjustment — changes save back to the session JSON
5. **Apply** scripts individually or use **Batch Apply** to execute all pending scripts in dependency order
6. **Check status** — green checkmarks indicate successfully applied scripts

The reviewer applies objects in topological dependency order:
1. Tables with no dependencies
2. Tables with foreign key dependencies
3. Functions
4. Views
5. Stored Procedures (as PostgreSQL functions)
6. Triggers
7. Synonyms (as views)

**Tip:** If a script fails, fix the DDL in the editor, save, and re-apply. The error message from PostgreSQL is displayed in the UI to help diagnose the issue.

---

## Step 4: Mapping Generator

**Tool:** `MappingGenerator`  
**Purpose:** Read the conversion session and generate a `procedure-mappings.json` file that tells PgPassthrough how to translate `EXEC dbo.MyProc @param` calls into PostgreSQL function calls at runtime.

### 4.1 Run

```bash
cd MappingGenerator
dotnet run -- ^
  --session ..\AI-AssistedSchemaConversion\sessions\my-migration ^
  --output ..\PgPassthrough\src\PgPassthrough.Server\procedure-mappings.json
```

### 4.2 What It Produces

The output file maps each SQL Server stored procedure/function to its PostgreSQL equivalent:

```json
{
  "description": "Custom translation mappings generated from schema conversion session...",
  "generatedAt": "2025-01-15T10:30:00Z",
  "mappings": [
    {
      "sourceSchema": "dbo",
      "sourceName": "sp_GetTopCustomers",
      "sourceType": "StoredProcedure",
      "postgresSchema": "public",
      "postgresName": "sp_gettop_customers",
      "postgresType": "function",
      "callStyle": "SELECT",
      "returnsTable": true,
      "parameters": [
        { "postgresName": "p_top_count", "postgresType": "INTEGER", "position": 1 }
      ]
    }
  ]
}
```

PgPassthrough uses this at runtime to correctly route:
- `EXEC dbo.sp_GetTopCustomers @TopCount = 10` → `SELECT * FROM public.sp_gettop_customers(10)`

### 4.3 Verify

Check the console output for each mapping line:
```
  ✓ dbo.sp_GetTopCustomers → public.sp_gettop_customers (SELECT)
  ✓ dbo.fn_FormatCustomerName → public.fn_formatcustomername (SELECT)
```

If a procedure is missing, ensure it was successfully converted in step 2 (status `converted` or `flagged`).

---

## Step 5: Data Migration

**Tool:** `DataMigrator`  
**Purpose:** Copy data from SQL Server to PostgreSQL, respecting table dependencies, and reseed identity sequences.

### 5.1 Prerequisites

- The PostgreSQL schema **must already be applied** (step 3 completed)
- Both SQL Server and PostgreSQL must be accessible from this machine

### 5.2 Run

```bash
cd DataMigrator
dotnet run -- ^
  --source "Server=localhost;Database=MyDatabase;User Id=sa;Password=YourPass;TrustServerCertificate=True" ^
  --target "Host=localhost;Port=5432;Database=MyDatabase;Username=postgres;Password=YourPass" ^
  --session "../AI-AssistedSchemaConversion/sessions/my-migration"
```

### 5.3 Options

| Option | Default | Description |
|--------|---------|-------------|
| `--source` | (required) | SQL Server connection string |
| `--target` | (required) | PostgreSQL connection string |
| `--session` | (required) | Path to the conversion session directory |
| `--batch-size` | 1000 | Rows per batch insert |
| `--tables` | all | Specific tables to migrate (e.g., `dbo.Orders dbo.Customers`) |
| `--disable-fk` | true | Disable FK triggers during load |
| `--reseed` | true | Reset identity sequences to max(id)+1 after migration |
| `--truncate` | false | Truncate target tables before migrating (use for re-runs) |

### 5.4 What Happens

1. Reads session JSON files to discover tables and dependency order
2. Disables foreign key triggers on PostgreSQL (avoids constraint violations during load)
3. Copies data table-by-table in dependency order using batched inserts
4. Re-enables foreign key triggers
5. Reseeds identity/serial sequences based on max values

### 5.5 Re-running

If you need to re-migrate (schema changes, data corrections):

```bash
dotnet run -- ^
  --source "Server=localhost;Database=MyDatabase;User Id=sa;Password=YourPass;TrustServerCertificate=True" ^
  --target "Host=localhost;Port=5432;Database=MyDatabase;Username=postgres;Password=YourPass" ^
  --session "../AI-AssistedSchemaConversion/sessions/my-migration" ^
  --truncate
```

The `--truncate` flag clears target tables (with CASCADE) before loading fresh data.

---

## Step 6: PgPassthrough Setup

**Tool:** `PgPassthrough`  
**Purpose:** A TDS protocol middleware that lets existing SQL Server client applications connect to PostgreSQL without any application changes. Clients use their existing SQL Server ODBC/ADO.NET drivers, and PgPassthrough translates T-SQL to PostgreSQL on the fly.

### 6.1 Configure

Edit `PgPassthrough/src/PgPassthrough.Server/appsettings.json`:

```json
{
  "PgPassthrough": {
    "Port": 11433,
    "BindAddress": "0.0.0.0",
    "MaxConcurrentSessions": 100,
    "EnableQueryLogging": true,
    "Backend": {
      "Host": "localhost",
      "Port": 5432,
      "Database": "MyDatabase",
      "Username": "postgres",
      "Password": "YourPass",
      "MinPoolSize": 2,
      "MaxPoolSize": 50,
      "ConnectionTimeoutSeconds": 30,
      "CommandTimeoutSeconds": 30,
      "SslMode": false
    },
    "Cache": {
      "MaxEntries": 10000
    }
  },
  "TdsServer": {
    "AllowedLogins": [
      {
        "Username": "sa",
        "Password": "YourStrong!Pass123"
      }
    ]
  }
}
```

Key settings:

| Setting | Description |
|---------|-------------|
| `PgPassthrough.Port` | Port that TDS clients connect to (use something other than 1433 to avoid conflict) |
| `Backend.*` | Connection details for the PostgreSQL database |
| `TdsServer.AllowedLogins` | Credentials that client applications will use to authenticate |
| `Cache.MaxEntries` | LRU translation cache size (reduces re-parsing overhead) |

### 6.2 Deploy the Procedure Mappings

Ensure the `procedure-mappings.json` from step 4 is in the PgPassthrough.Server directory:

```
PgPassthrough/src/PgPassthrough.Server/procedure-mappings.json
```

This file was output directly there if you used the command in step 4.

### 6.3 Build and Run

```bash
cd PgPassthrough
dotnet run --project src/PgPassthrough.Server
```

You should see:
```
info: PgPassthrough[0] TDS listener started on 0.0.0.0:11433
```

### 6.4 Point Your Application

Update your application's connection string from:
```
Server=sql-server-host;Database=MyDatabase;User Id=sa;Password=YourPass;
```

To:
```
Server=pgpassthrough-host,11433;Database=MyDatabase;User Id=sa;Password=YourStrong!Pass123;Encrypt=False;
```

The application continues to use its existing SQL Server ODBC driver. PgPassthrough handles all translation transparently.

### 6.5 How It Works

```
Your App (ODBC/ADO.NET) ──TDS──▶ PgPassthrough ──PostgreSQL──▶ PostgreSQL
                                      │
                                      ├─ Parses T-SQL into an AST
                                      ├─ Translates AST to PostgreSQL SQL
                                      ├─ Executes against Npgsql connection pool
                                      └─ Encodes results as TDS token stream back to client
```

PgPassthrough handles:
- `SELECT TOP N` → `SELECT ... LIMIT N`
- `GETDATE()` → `CURRENT_TIMESTAMP`
- `ISNULL(a,b)` → `COALESCE(a,b)`
- `EXEC dbo.MyProc @p = 1` → `SELECT * FROM public.my_proc(1)` (via procedure mappings)
- Schema remapping (`dbo.` → `public.`)
- Transaction management (BEGIN TRAN / COMMIT / ROLLBACK)
- And many more T-SQL constructs

---

## Step 7: Migration Validation

**Tool:** `MigrationValidation`  
**Purpose:** Run a suite of T-SQL test scripts against both the original SQL Server and the PgPassthrough endpoint to verify functional equivalence.

### 7.1 Configure

Edit `MigrationValidation/src/MigrationValidation.Runner/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=localhost;Database=MyDatabase;User Id=sa;Password=YourPass;TrustServerCertificate=True",
    "PgPassthrough": "Server=localhost,11433;Database=MyDatabase;User Id=sa;Password=YourStrong!Pass123;Encrypt=False"
  },
  "ActiveConnection": "SqlServer"
}
```

### 7.2 Establish a Baseline

Run against the original SQL Server first:

```bash
cd MigrationValidation
dotnet run --project src/MigrationValidation.Runner -- --connection SqlServer --verbose
```

This confirms all test scripts pass against the source and gives you expected row counts.

### 7.3 Validate the Migration

With PgPassthrough running (step 6), run the same tests against the migrated database:

```bash
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --verbose
```

### 7.4 Run by Category

```bash
# Tables only
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --category Tables

# Views only
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --category Views

# Stored Procedures only
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --category StoredProcedures

# Functions only
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --category Functions
```

### 7.5 Interpret Results

```
┌─────────────┬───────────────┐
│ Metric      │ Value         │
├─────────────┼───────────────┤
│ Total Tests │ 39            │
│ Passed      │ 39            │
│ Failed      │ 0             │
│ Pass Rate   │ 100.0%        │
│ Target      │ PgPassthrough │
└─────────────┴───────────────┘
```

- **100% pass rate** — migration is validated, the application can be switched over
- **Failures** — review the error messages, which typically indicate:
  - Translation gaps in PgPassthrough (T-SQL syntax not yet handled)
  - Schema differences (missing objects, naming mismatches)
  - Data issues (empty tables, sequence misalignment)

### 7.6 Resolving Failures

| Symptom | Likely Cause | Fix |
|---------|-------------|-----|
| "relation does not exist" | Schema mismatch (dbo vs public) | Check PgPassthrough schema mapping or re-apply DDL |
| "function does not exist" | Missing procedure mapping | Re-run MappingGenerator (step 4) |
| "Expected N rows, got 0" | Data not migrated | Re-run DataMigrator (step 5) |
| Query syntax error | T-SQL construct not translated | Check PgPassthrough logs; may need a code fix |

---

## Complete Workflow Checklist

```
□ Step 1: Run MigrationAssessment
    □ Set up Extended Events (optional but recommended)
    □ Run assessment and review report
    □ Address any blocking issues (Risk 5 items)

□ Step 2: Run AI-Assisted Schema Conversion
    □ Extract schema objects
    □ Convert all objects
    □ Review flagged items
    □ Approve results
    □ Generate output DDL

□ Step 3: Apply Schema with Conversion Reviewer
    □ Create empty PostgreSQL database
    □ Launch Conversion Reviewer
    □ Review and edit DDL as needed
    □ Batch apply all scripts in dependency order
    □ Verify all scripts applied successfully

□ Step 4: Generate Procedure Mappings
    □ Run MappingGenerator
    □ Verify output placed in PgPassthrough.Server directory

□ Step 5: Migrate Data
    □ Run DataMigrator
    □ Verify row counts match source

□ Step 6: Configure and Start PgPassthrough
    □ Set backend connection details
    □ Configure allowed logins
    □ Ensure procedure-mappings.json is in place
    □ Start PgPassthrough and confirm TDS listener is active

□ Step 7: Validate Migration
    □ Run validation against SQL Server (baseline)
    □ Run validation against PgPassthrough
    □ Compare results — target 100% pass rate
    □ Resolve any failures and re-validate
```

---

## Troubleshooting

### AWS Credentials (Step 2)

The AI-AssistedSchemaConversion tool requires AWS credentials for Amazon Bedrock access. Configure via:
- Environment variables: `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY`
- Shared credentials file: `~/.aws/credentials`
- IAM instance role (EC2/ECS)

### PgPassthrough Connection Issues (Step 6)

- **"does not support encryption"** — Add `Encrypt=False` to the client connection string
- **Connection refused on 11433** — Ensure PgPassthrough is running and the port matches
- **Authentication failed** — Verify `TdsServer.AllowedLogins` matches the credentials in your connection string

### Data Integrity (Step 5)

If row counts don't match after migration:
1. Check the DataMigrator console output for errors
2. Run with `--truncate` for a clean re-migration
3. Verify no FK constraint violations in the PostgreSQL logs

### Performance

- Increase `--batch-size` for DataMigrator if tables are large (e.g., `--batch-size 5000`)
- Increase PgPassthrough `Cache.MaxEntries` for workloads with many distinct queries
- Increase `Backend.MaxPoolSize` if PgPassthrough handles many concurrent connections

---

## Project Locations

| Tool | Directory |
|------|-----------|
| MigrationAssessment | `MigrationAssessment/` |
| AI-AssistedSchemaConversion | `AI-AssistedSchemaConversion/` |
| ConversionReviewer | `ConversionReviewer/` |
| MappingGenerator | `MappingGenerator/` |
| DataMigrator | `DataMigrator/` |
| PgPassthrough | `PgPassthrough/` |
| MigrationValidation | `MigrationValidation/` |
