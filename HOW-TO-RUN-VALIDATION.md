# How to Run the Migration Validation Suite

This document walks through running PgPassthrough and the MigrationValidation test suite to validate your SQL Server → PostgreSQL migration.

## Prerequisites

- .NET SDK installed
- SQL Server with `AssessmentTestDB` (source database)
- PostgreSQL with `AssessmentTestDB` (migrated database with schema and data applied)
- All terminals run from `c:\code\ODBC_TSQL_To_SQL`

## Step 1: Start PgPassthrough

PgPassthrough is a TDS protocol proxy that accepts SQL Server client connections and translates T-SQL to PostgreSQL on the fly.

**Open a terminal** and run:

```
cd c:\code\ODBC_TSQL_To_SQL\PgPassthrough
dotnet run --project src\PgPassthrough.Server
```

You should see:
```
info: PgPassthrough[0] TDS listener started on 0.0.0.0:11433
```

Leave this terminal running.

### PgPassthrough Configuration

Edit `PgPassthrough\src\PgPassthrough.Server\appsettings.json`:

| Setting | Current Value | Description |
|---------|--------------|-------------|
| `PgPassthrough.Port` | 11433 | TDS port clients connect to |
| `PgPassthrough.Backend.Host` | localhost | PostgreSQL host |
| `PgPassthrough.Backend.Port` | 5432 | PostgreSQL port |
| `PgPassthrough.Backend.Database` | AssessmentTestDB | Target PostgreSQL database |
| `PgPassthrough.Backend.Username` | postgres | PostgreSQL user |
| `PgPassthrough.Backend.Password` | Sage@123 | PostgreSQL password |
| `TdsServer.AllowedLogins[0].Username` | sa | Login accepted from TDS clients |
| `TdsServer.AllowedLogins[0].Password` | YourStrong!Pass123 | Password for TDS login |

## Step 2: Run the Validation Suite

**Open a second terminal** and run:

```
cd c:\code\ODBC_TSQL_To_SQL\MigrationValidation
dotnet run --project src\MigrationValidation.Runner -- --connection PgPassthrough
```

### Command Options

| Option | Description | Example |
|--------|-------------|---------|
| `--connection SqlServer` | Test against original SQL Server (baseline) | `-- --connection SqlServer` |
| `--connection PgPassthrough` | Test against migrated PostgreSQL via PgPassthrough | `-- --connection PgPassthrough` |
| `--category Tables` | Run only table tests | `-- --connection PgPassthrough --category Tables` |
| `--category Views` | Run only view tests | `-- --category Views` |
| `--category Functions` | Run only function tests | `-- --category Functions` |
| `--category StoredProcedures` | Run only stored procedure tests | `-- --category StoredProcedures` |
| `--category Synonyms` | Run only synonym tests | `-- --category Synonyms` |
| `--verbose` | Show row counts for each test | `-- --connection PgPassthrough --verbose` |

### Examples

Run all tests against PgPassthrough:
```
dotnet run --project src\MigrationValidation.Runner -- --connection PgPassthrough
```

Run all tests against original SQL Server (baseline comparison):
```
dotnet run --project src\MigrationValidation.Runner -- --connection SqlServer
```

Run only table tests with verbose output:
```
dotnet run --project src\MigrationValidation.Runner -- --connection PgPassthrough --category Tables --verbose
```

Run only stored procedure tests:
```
dotnet run --project src\MigrationValidation.Runner -- --connection PgPassthrough --category StoredProcedures
```

### MigrationValidation Configuration

Edit `MigrationValidation\src\MigrationValidation.Runner\appsettings.json`:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True",
    "PgPassthrough": "Server=localhost,11433;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;Encrypt=False"
  },
  "ActiveConnection": "SqlServer"
}
```

Note: The PgPassthrough connection uses `Encrypt=False` because PgPassthrough does not implement TLS.

## Step 3: Interpret Results

The test suite outputs a summary table:

```
┌─────────────┬───────────────┐
│ Metric      │ Value         │
├─────────────┼───────────────┤
│ Total Tests │ 39            │
│ Passed      │ 35            │
│ Failed      │ 4             │
│ Pass Rate   │ 89.7%         │
│ Target      │ PgPassthrough │
└─────────────┴───────────────┘
```

- **Passed**: The query executed and returned the expected number of rows
- **Failed**: Either the query errored or returned fewer rows than expected
- Failed tests list the specific error message to help diagnose translation or data issues

## Troubleshooting

### "does not support encryption"
Add `Encrypt=False` to the PgPassthrough connection string.

### "relation dbo.tablename does not exist"
PgPassthrough's translator needs to remap `dbo` schema references to `public`. Ensure you have the latest build of PgPassthrough with the schema mapping fix.

### Build fails with "file is locked"
PgPassthrough is still running. Stop it (Ctrl+C), then rebuild:
```
cd c:\code\ODBC_TSQL_To_SQL\PgPassthrough
dotnet build src\PgPassthrough.Server
```
Verify output shows "Build succeeded" with NO MSB3027 errors, then restart.

### "Expected at least N rows, got 0"
Check the PgPassthrough terminal output for PostgreSQL errors. Common causes:
- Schema mismatch (dbo vs public)
- Missing data (run the DataMigrator tool first)
- Translation not converting T-SQL syntax properly

## Full Workflow Summary

```
Terminal 1:                              Terminal 2:
─────────────────────────────────────    ─────────────────────────────────────
cd PgPassthrough                         cd MigrationValidation
dotnet run --project src\                dotnet run --project src\
  PgPassthrough.Server                     MigrationValidation.Runner --
                                           --connection PgPassthrough
[leave running]                          [view results]
```
