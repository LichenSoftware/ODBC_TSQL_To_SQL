# DataMigrator

A command-line tool that moves data from SQL Server to PostgreSQL, leveraging the session metadata produced by the AI-AssistedSchemaConversion project.

## How It Works

1. Reads the session JSON files to discover tables and their dependency order
2. Parses column names from the source T-SQL DDL definitions
3. Connects to both SQL Server (source) and PostgreSQL (destination)
4. Disables FK triggers on the target to avoid constraint violations during load
5. Copies data table-by-table in dependency order using batched inserts
6. Re-enables FK triggers after all data is loaded
7. Reseeds identity/serial sequences based on max values

## Usage

```bash
dotnet run -- \
  --source "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" \
  --target "Host=localhost;Port=5432;Database=AssessmentTestDB;Username=postgres;Password=Sage@123" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5"
```

## Options

| Option | Default | Description |
|--------|---------|-------------|
| `--source` | (required) | SQL Server connection string |
| `--target` | (required) | PostgreSQL connection string |
| `--session` | (required) | Path to the session directory containing the `objects/` folder |
| `--batch-size` | 1000 | Number of rows per batch insert transaction |
| `--tables` | (all) | Specific tables to migrate (e.g., `dbo.Orders dbo.Customers`) |
| `--disable-fk` | true | Disable FK triggers during migration |
| `--reseed` | true | Reseed identity sequences after migration |
| `--truncate` | false | Truncate target tables before migrating (CASCADE) |

## Prerequisites

- The target PostgreSQL database must already have the schema applied (tables created)
- The session must have been extracted and converted (table JSON files must exist)
- SQL Server must be accessible from this machine
- PostgreSQL must be accessible from this machine

## Example: Migrate specific tables

```bash
dotnet run -- \
  --source "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" \
  --target "Host=localhost;Port=5432;Database=AssessmentTestDB;Username=postgres;Password=Sage@123" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5" \
  --tables dbo.Customers dbo.Orders
```

## Example: Fresh re-migration with truncate

```bash
dotnet run -- \
  --source "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" \
  --target "Host=localhost;Port=5432;Database=AssessmentTestDB;Username=postgres;Password=Sage@123" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5" \
  --truncate
```
