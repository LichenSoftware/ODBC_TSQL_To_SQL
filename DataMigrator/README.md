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
  --source "Server=YOUR_SQL_SERVER;Database=YOUR_DB;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True" \
  --target "Host=YOUR_POSTGRES_HOST;Port=5432;Database=YOUR_DB;Username=postgres;Password=YOUR_PASSWORD" \
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
- Connection credentials configured via environment variables or secure credential management

## Configuration

### Using Environment Variables

Set the following environment variables before running:

```bash
export SQL_SERVER_HOST="your-sql-server"
export SQL_SERVER_USER="sa"
export SQL_SERVER_PASSWORD="your-password"
export POSTGRES_HOST="your-postgres-host"
export POSTGRES_USER="postgres"
export POSTGRES_PASSWORD="your-password"
```

Then use them in your connection strings:

```bash
dotnet run -- \
  --source "Server=$SQL_SERVER_HOST;Database=AssessmentTestDB;User Id=$SQL_SERVER_USER;Password=$SQL_SERVER_PASSWORD;TrustServerCertificate=True" \
  --target "Host=$POSTGRES_HOST;Port=5432;Database=AssessmentTestDB;Username=$POSTGRES_USER;Password=$POSTGRES_PASSWORD" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5"
```

### Using .env Files (Local Development)

Create a `.env` file in the DataMigrator directory (do NOT commit this file):

```
SQL_SERVER_HOST=localhost
SQL_SERVER_USER=sa
SQL_SERVER_PASSWORD=YourStrong!Pass123
POSTGRES_HOST=localhost
POSTGRES_USER=postgres
POSTGRES_PASSWORD=Sage@123
```

Then add `.env` to your `.gitignore`.

## Example: Migrate specific tables

```bash
dotnet run -- \
  --source "Server=YOUR_SQL_SERVER;Database=YOUR_DB;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True" \
  --target "Host=YOUR_POSTGRES_HOST;Port=5432;Database=YOUR_DB;Username=postgres;Password=YOUR_PASSWORD" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5" \
  --tables dbo.Customers dbo.Orders
```

## Example: Fresh re-migration with truncate

```bash
dotnet run -- \
  --source "Server=YOUR_SQL_SERVER;Database=YOUR_DB;User Id=sa;Password=YOUR_PASSWORD;TrustServerCertificate=True" \
  --target "Host=YOUR_POSTGRES_HOST;Port=5432;Database=YOUR_DB;Username=postgres;Password=YOUR_PASSWORD" \
  --session "../AI-AssistedSchemaConversion/sessions/my-migration5" \
  --truncate
```
