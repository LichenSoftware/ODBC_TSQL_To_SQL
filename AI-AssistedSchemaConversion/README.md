# AI-Assisted Schema Conversion Tool

A .NET 8 command-line tool that converts Microsoft SQL Server database schemas to PostgreSQL. It combines deterministic rule-based conversion for well-defined mappings with AI-assisted conversion (via Amazon Bedrock) for objects requiring semantic understanding.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Getting Started](#getting-started)
- [Configuration](#configuration)
- [Workflow Overview](#workflow-overview)
- [Commands](#commands)
- [Conversion Methods](#conversion-methods)
- [Type Mappings](#type-mappings)
- [Function Mappings](#function-mappings)
- [Schema Mappings](#schema-mappings)
- [Session Management](#session-management)
- [Output Formats](#output-formats)
- [Manual Review Workflow](#manual-review-workflow)
- [Customizing Prompt Templates](#customizing-prompt-templates)
- [Out of Scope](#out-of-scope)
- [Troubleshooting](#troubleshooting)

## Prerequisites

- .NET 8 SDK
- AWS credentials configured (for AI-assisted conversion via Amazon Bedrock)
  - Environment variables, IAM roles, shared credential files, or instance profiles
- Access to a SQL Server instance (if extracting from a live database) OR DDL script files

## Getting Started

### Build the tool

```bash
cd AI-AssistedSchemaConversion
dotnet build
```

### How to run it

This is a .NET console application. You have two options:

**Option A: Run directly from source (no install needed)**

Use `dotnet run` to compile and execute in one step. The `--` separator is required — everything after it becomes arguments to the tool itself (not to `dotnet`):

```bash
dotnet run --project src/SchemaConversion.Cli -- extract --files ./my-sql-scripts --output ./sessions/my-migration
```

This is the simplest approach during development. No installation step, no PATH changes.

**Option B: Publish as a standalone executable**

If you want a `schema-convert` command you can call from anywhere:

```bash
# Publish to a folder
dotnet publish src/SchemaConversion.Cli -c Release -o ./publish

# Now you can run it directly
./publish/SchemaConversion.Cli extract --files ./my-sql-scripts --output ./sessions/my-migration
```

Or install it as a .NET global tool (if you add tool packaging to the project):

```bash
dotnet tool install --global --add-source ./nupkg SchemaConversion.Cli
schema-convert extract --files ./my-sql-scripts --output ./sessions/my-migration
```

Most users will use **Option A** — just `dotnet run --project src/SchemaConversion.Cli -- <command>`.

### Quick Start — Converting DDL Files

```bash
cd AI-AssistedSchemaConversion

# 1. Extract objects from DDL files
dotnet run --project src/SchemaConversion.Cli -- extract --files ./my-sql-scripts --output ./sessions/my-migration

# 2. Convert all objects
dotnet run --project src/SchemaConversion.Cli -- convert --session ./sessions/my-migration

# 3. Review flagged objects
dotnet run --project src/SchemaConversion.Cli -- review --session ./sessions/my-migration --flagged-only

# 4. Generate PostgreSQL DDL scripts
dotnet run --project src/SchemaConversion.Cli -- generate --session ./sessions/my-migration --output ./output --mode consolidated
```

### Quick Start — Converting from a Live Database

```bash
cd AI-AssistedSchemaConversion

# 1. Extract objects from SQL Server
dotnet run --project src/SchemaConversion.Cli -- extract --connection "Server=myserver;Database=mydb;Trusted_Connection=True;" --output ./sessions/my-migration

# 2. Convert all objects
dotnet run --project src/SchemaConversion.Cli -- convert --session ./sessions/my-migration

# 3. Review flagged objects
dotnet run --project src/SchemaConversion.Cli -- review --session ./sessions/my-migration --flagged-only

# 4. Generate output scripts
dotnet run --project src/SchemaConversion.Cli -- generate --session ./sessions/my-migration --output ./output
```

## Configuration

All settings are in `appsettings.json`:

```json
{
  "Bedrock": {
    "ModelId": "anthropic.claude-sonnet-4-20250514-v1:0",
    "Region": "us-east-1",
    "Temperature": 0.2,
    "MaxOutputTokens": 8192,
    "Timeout": 120,
    "MaxRetryAttempts": 3
  },
  "Conversion": {
    "ConfidenceThreshold": 0.7,
    "DefaultConcurrency": 4,
    "SessionDirectory": "./sessions",
    "TypeMappingsFile": "./config/type-mappings.json",
    "FunctionMappingsFile": "./config/function-mappings.json",
    "SchemaMappingsFile": "./config/schema-mappings.json",
    "PromptTemplatesDirectory": "./config/prompts"
  },
  "AuditLog": {
    "MaxFileSizeBytes": 52428800,
    "Directory": "./sessions/{sessionId}/audit"
  },
  "Output": {
    "DefaultMode": "per-schema",
    "IncludeComments": true,
    "OutputDirectory": "./output"
  }
}
```

### Key Settings

| Setting | Description | Default |
|---------|-------------|---------|
| `Bedrock.ModelId` | Amazon Bedrock model to use for AI conversion | `anthropic.claude-sonnet-4-20250514-v1:0` |
| `Bedrock.Temperature` | LLM temperature (0.0–1.0, lower = more deterministic) | `0.2` |
| `Bedrock.Timeout` | Seconds to wait for AI response | `120` |
| `Bedrock.MaxRetryAttempts` | Retry count on failure (1–10) | `3` |
| `Conversion.ConfidenceThreshold` | AI confidence below this flags for manual review | `0.7` |
| `Conversion.DefaultConcurrency` | Parallel conversion threads | `4` |

## Workflow Overview

```
┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐     ┌──────────┐
│ Extract  │────▶│ Convert  │────▶│ Review   │────▶│ Approve  │────▶│ Generate │
└──────────┘     └──────────┘     └──────────┘     └──────────┘     └──────────┘
                       │                │
                       │                ▼
                       │          ┌──────────┐
                       │          │  Edit    │
                       │          └──────────┘
                       │                │
                       ▼                ▼
                 ┌──────────┐     ┌──────────┐
                 │  Rerun   │     │  Report  │
                 └──────────┘     └──────────┘
```

1. **Extract** — Discover schema objects from SQL Server or DDL files
2. **Convert** — Process objects using rule-based or AI-assisted conversion
3. **Review** — Inspect results, especially flagged items
4. **Edit** — Manually fix any AI-generated DDL that needs adjustment
5. **Approve** — Mark objects as reviewed and accepted
6. **Generate** — Produce final PostgreSQL DDL scripts
7. **Report** — Generate a JSON summary of the entire conversion

## Commands

### extract

Extract schema objects from a source and create a conversion session.

```bash
schema-convert extract --connection <connection-string> --output <session-dir>
schema-convert extract --files <ddl-directory> --output <session-dir>
```

| Option | Description |
|--------|-------------|
| `--connection` | SQL Server connection string (Windows or SQL auth) |
| `--files` | Path to directory containing `.sql` DDL files |
| `--output` | Directory where the session will be stored |

You must provide either `--connection` or `--files`, not both.

### convert

Convert extracted schema objects to PostgreSQL.

```bash
schema-convert convert --session <session-dir> [options]
```

| Option | Description | Default |
|--------|-------------|---------|
| `--session` | Session directory (required) | — |
| `--schema` | Filter: only convert objects in this schema | all |
| `--type` | Filter: only convert this object type (Table, View, StoredProcedure, etc.) | all |
| `--objects` | Filter: specific object names to convert | all |
| `--force-ai` | Force AI conversion for these object names | — |
| `--force-rules` | Force rule-based conversion for these object names | — |
| `--concurrency` | Max parallel conversions | 4 |

### rerun

Re-convert specific objects (replaces previous results).

```bash
schema-convert rerun --session <session-dir> --objects <name1> <name2> ...
```

### review

Display conversion results for human review.

```bash
schema-convert review --session <session-dir> [--flagged-only]
```

Output includes object name, status, method, confidence score, review flags, and assumptions.

### edit

Apply a manual edit to a converted object's DDL.

```bash
schema-convert edit --session <session-dir> --object <schema.name> --file <edited.sql>
```

After editing, the object is marked as "manually reviewed" and excluded from automatic reprocessing on future reruns.

### approve

Mark objects as approved for output generation.

```bash
schema-convert approve --session <session-dir> --objects <name1> <name2>
schema-convert approve --session <session-dir> --all
```

### generate

Produce PostgreSQL DDL scripts from the conversion results.

```bash
schema-convert generate --session <session-dir> --output <dir> [--mode <mode>]
```

| Mode | Description |
|------|-------------|
| `consolidated` | Single `migration.sql` file with all DDL |
| `per-schema` | One directory and file per schema |
| `per-type` | One file per object type (tables.sql, views.sql, etc.) |
| `per-object` | Numbered individual files per object |

### report

Generate a JSON conversion report with statistics and details.

```bash
schema-convert report --session <session-dir> --output <report.json>
```

## Conversion Methods

The tool automatically classifies each object and routes it to the appropriate converter:

### Rule-Based (deterministic)

Handles objects with well-defined, repeatable mappings:
- Tables and columns (type mapping, IDENTITY → GENERATED BY DEFAULT AS IDENTITY)
- Primary keys, foreign keys, unique constraints, check constraints
- Indexes (standard, unique, filtered/partial, clustered with compatibility note)
- Sequences
- Views (when all constructs have defined mapping rules)
- User-defined types (alias → DOMAIN, table types → composite TYPE)
- Synonyms (→ views)
- Schema definitions
- Permissions (GRANT/REVOKE; DENY flagged for review)

### AI-Assisted (via Amazon Bedrock)

Handles objects requiring semantic understanding:
- Stored procedures (→ PostgreSQL functions or procedures)
- User-defined functions (scalar, inline TVF, multi-statement TVF)
- Triggers (→ trigger functions + CREATE TRIGGER)
- Views with SQL Server-specific syntax
- Any rule-based object that fails and falls back to AI

### Fallback Behavior

If the rule-based converter encounters an unsupported pattern, the object is automatically reclassified and sent to the AI converter. You can also force routing with `--force-ai` or `--force-rules`.

## Type Mappings

The following SQL Server types are mapped deterministically:

| SQL Server | PostgreSQL | Notes |
|------------|-----------|-------|
| INT | INTEGER | |
| BIGINT | BIGINT | |
| SMALLINT | SMALLINT | |
| TINYINT | SMALLINT | + CHECK (col >= 0 AND col <= 255) |
| BIT | BOOLEAN | |
| DECIMAL(p,s) | NUMERIC(p,s) | Precision/scale preserved |
| NUMERIC(p,s) | NUMERIC(p,s) | Precision/scale preserved |
| MONEY | NUMERIC(19,4) | |
| SMALLMONEY | NUMERIC(10,4) | |
| FLOAT | DOUBLE PRECISION | |
| REAL | REAL | |
| DATETIME | TIMESTAMP(3) | |
| DATETIME2(n) | TIMESTAMP(n) | Precision capped at 6 |
| SMALLDATETIME | TIMESTAMP(0) | |
| DATE | DATE | |
| TIME(n) | TIME(n) | Precision capped at 6 |
| DATETIMEOFFSET(n) | TIMESTAMPTZ(n) | Precision capped at 6 |
| UNIQUEIDENTIFIER | UUID | |
| NVARCHAR(n) | VARCHAR(n) | NVARCHAR(MAX) → TEXT |
| VARCHAR(n) | VARCHAR(n) | VARCHAR(MAX) → TEXT |
| NCHAR(n) | CHAR(n) | |
| CHAR(n) | CHAR(n) | |
| TEXT / NTEXT | TEXT | |
| VARBINARY / BINARY / IMAGE | BYTEA | |
| XML | XML | |
| SQL_VARIANT | JSONB | Flagged for manual review |
| HIERARCHYID | — | Flagged; suggest ltree extension |
| GEOGRAPHY / GEOMETRY | — | Flagged; suggest PostGIS |

Mappings are defined in `config/type-mappings.json` and can be customized.

## Function Mappings

| SQL Server | PostgreSQL |
|------------|-----------|
| GETDATE() | CURRENT_TIMESTAMP |
| SYSDATETIME() | CURRENT_TIMESTAMP |
| GETUTCDATE() | (CURRENT_TIMESTAMP AT TIME ZONE 'UTC') |
| ISNULL(a, b) | COALESCE(a, b) |
| LEN(x) | LENGTH(x) |
| CHARINDEX(a, b) | POSITION(a IN b) |
| NEWID() | gen_random_uuid() |
| SCOPE_IDENTITY() | lastval() |
| DB_NAME() | current_database() |
| STUFF(s, i, l, r) | OVERLAY(s PLACING r FROM i FOR l) |
| STRING_AGG(x, d) | STRING_AGG(x, d) |
| OBJECT_ID(x) | to_regclass(x)::oid |

### Date Functions

**DATEDIFF(datepart, start, end)** maps to date subtraction with EXTRACT:
- DAY: `EXTRACT(DAY FROM (end::timestamp - start::timestamp))`
- MONTH: year/month arithmetic
- YEAR, HOUR, MINUTE, SECOND, WEEK all supported

**DATEADD(datepart, number, date)** maps to interval arithmetic:
- `date + INTERVAL 'number days/months/years/etc.'`

### CONVERT Style Codes

| Style Code | Format | PostgreSQL Pattern |
|-----------|--------|-------------------|
| 101 | MM/DD/YYYY | `TO_CHAR(expr, 'MM/DD/YYYY')` |
| 103 | DD/MM/YYYY | `TO_CHAR(expr, 'DD/MM/YYYY')` |
| 104 | DD.MM.YYYY | `TO_CHAR(expr, 'DD.MM.YYYY')` |
| 110 | MM-DD-YYYY | `TO_CHAR(expr, 'MM-DD-YYYY')` |
| 120 | YYYY-MM-DD HH:MI:SS | `TO_CHAR(expr, 'YYYY-MM-DD HH24:MI:SS')` |
| 121 | YYYY-MM-DD HH:MI:SS.MS | `TO_CHAR(expr, 'YYYY-MM-DD HH24:MI:SS.MS')` |
| 126 | ISO 8601 | `TO_CHAR(expr, 'YYYY-MM-DD"T"HH24:MI:SS')` |

CONVERT without a style code maps to `CAST(expr AS mapped_type)`.

Mappings are defined in `config/function-mappings.json` and can be customized.

## Schema Mappings

By default:
- `dbo` → `public`
- All other schemas preserve their name

Configure custom mappings in `config/schema-mappings.json`:

```json
{
  "defaultMappings": [
    { "sqlServerSchema": "dbo", "postgresSchema": "public" },
    { "sqlServerSchema": "sales", "postgresSchema": "sales" }
  ],
  "rules": {
    "unmappedBehavior": "preserve"
  }
}
```

## Session Management

Each conversion creates a session directory:

```
sessions/
└── my-migration/
    ├── session.json              # Session metadata
    ├── objects/
    │   ├── dbo.Customers.Table.json
    │   ├── dbo.GetOrders.StoredProcedure.json
    │   └── sales.OrderTotal.Function.json
    └── audit/
        └── audit-001.jsonl       # AI interaction audit log
```

### Incremental Processing

The tool detects changes by comparing SHA-256 hashes of source definitions. On subsequent runs:
- Unchanged objects are skipped
- Modified objects are reconverted
- New objects are added

### Change Detection

```bash
# Only new/modified objects get processed on re-run
schema-convert convert --session ./sessions/my-migration
```

### Filtering

```bash
# Convert only tables
schema-convert convert --session ./sessions/my-migration --type Table

# Convert only objects in the "sales" schema
schema-convert convert --session ./sessions/my-migration --schema sales

# Convert specific objects
schema-convert convert --session ./sessions/my-migration --objects "dbo.Customers" "dbo.Orders"
```

## Output Formats

### Consolidated Mode

A single `migration.sql` with all DDL in dependency order:

```sql
-- Schema Definitions
CREATE SCHEMA IF NOT EXISTS public;
CREATE SCHEMA IF NOT EXISTS sales;

-- User-Defined Types and Domains
CREATE DOMAIN public.email_address AS VARCHAR(255) ...

-- Sequences
CREATE SEQUENCE public.order_seq ...

-- Tables and Constraints
CREATE TABLE public.customers ( ... );

-- Indexes
CREATE INDEX ix_customers_email ON public.customers (email);

-- Functions and Procedures
CREATE OR REPLACE FUNCTION public.get_customer_orders(...) ...

-- Triggers
CREATE TRIGGER trg_audit_orders ...

-- Views
CREATE OR REPLACE VIEW public.active_orders AS ...

-- Wrapper Objects (compatibility layer)
CREATE OR REPLACE FUNCTION public.sp_get_orders_compat(...) ...

-- Permissions (GRANT / REVOKE)
GRANT SELECT ON public.customers TO app_reader;
```

### Per-Schema Mode

```
output/
├── public/
│   └── public.sql
└── sales/
    └── sales.sql
```

### Per-Type Mode

```
output/
├── schema.sql
├── table.sql
├── index.sql
├── function.sql
├── view.sql
└── permission.sql
```

### Per-Object Mode

```
output/
├── public/
│   ├── 0001_customers.sql
│   ├── 0002_orders.sql
│   └── 0003_get_customer_orders.sql
└── sales/
    └── 0004_order_total.sql
```

## Manual Review Workflow

Objects are flagged for manual review when:
- AI confidence score is below the threshold (default 0.7)
- The AI identifies areas it cannot confirm equivalence for
- Unsupported SQL Server features are encountered (global temp tables, CLR types, etc.)
- DENY permissions are encountered (no PostgreSQL equivalent)

### Review Process

```bash
# 1. See what needs attention
schema-convert review --session ./sessions/my-migration --flagged-only

# 2. Export an object's DDL, edit it manually
#    (copy the DDL from review output to a file, make corrections)

# 3. Apply your edited version
schema-convert edit --session ./sessions/my-migration --object dbo.GetCustomerOrders --file ./fixed-get-orders.sql

# 4. Approve reviewed objects
schema-convert approve --session ./sessions/my-migration --objects dbo.GetCustomerOrders

# 5. Or approve everything at once
schema-convert approve --session ./sessions/my-migration --all
```

## Customizing Prompt Templates

Prompt templates are in `config/prompts/` as Markdown files with YAML frontmatter:

```markdown
---
version: "1.0.0"
category: "stored-procedure"
model_instructions: "system"
---

You are a database migration expert converting SQL Server stored procedures to PostgreSQL.

## Rules
1. If the procedure returns a result set, produce a PostgreSQL FUNCTION returning TABLE.
2. ...

## Source Object
```sql
{source_definition}
```

## Required Output Format
Respond ONLY with valid JSON matching this schema:
{response_schema}
```

Available templates:
- `stored-procedure.v1.0.0.md`
- `function.v1.0.0.md`
- `trigger.v1.0.0.md`
- `complex-object.v1.0.0.md`
- `view.v1.0.0.md`

To update a template, edit the file and increment the version number. The audit log records which template version was used for each conversion, so results are traceable.

## Out of Scope

The following SQL Server features are not converted and will be recorded as out-of-scope in the report:

- Linked servers and distributed queries
- SQL Agent jobs and schedules
- Service Broker objects
- Filestream / FileTable
- Filegroup assignments
- Full-text indexes and catalogs
- Replication objects
- Database mail
- CLR assemblies and CLR-based objects
- Always Encrypted configurations
- Row-level security policies
- Data masking rules

## Troubleshooting

### "Type mappings file not found"

Ensure you are running the tool from the `AI-AssistedSchemaConversion` directory, or update the paths in `appsettings.json` to absolute paths.

### AI conversion returns low confidence

- Check the prompt template for the object category — you may need to add more specific rules
- Consider lowering `Conversion.ConfidenceThreshold` if the results look correct but the model is conservative
- Use `--force-rules` if the object is actually simple enough for deterministic conversion

### AWS credentials not found

The tool uses the standard AWS credential chain. Ensure one of the following is configured:
- `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` environment variables
- `~/.aws/credentials` file
- IAM role (if running on EC2/ECS)
- Instance profile

### Timeout errors on large stored procedures

Increase `Bedrock.Timeout` in `appsettings.json` (default 120 seconds). Complex procedures with many branches may need 180–240 seconds.

### Circular dependencies

The tool detects circular dependencies and handles them by:
1. Creating placeholder stubs first
2. Converting objects in dependency order
3. Replacing placeholders with full implementations using CREATE OR REPLACE

The report will note any cycles detected.

## Reporting

Generate a full JSON report of the conversion:

```bash
schema-convert report --session ./sessions/my-migration --output ./report.json
```

The report includes:
- Summary statistics (total objects, by status, by method, by type, progress %)
- Per-object details (DDL, confidence, assumptions, flags)
- Aggregated compatibility notes
- List of flagged objects requiring attention

Example summary output:

```json
{
  "sessionId": "my-migration",
  "summary": {
    "totalObjects": 450,
    "byStatus": { "converted": 410, "flagged": 25, "failed": 5, "outOfScope": 10 },
    "byMethod": { "ruleBased": 380, "aiAssisted": 55, "manual": 5 },
    "progressPercent": 91.1
  }
}
```
