# Testing Guide: Migration Assessment Engine with Docker

This guide walks through setting up a local SQL Server instance in Docker, seeding a test database, and running the Migration Assessment Engine against it.

## Prerequisites

- Docker Desktop installed and running
- .NET 8 SDK
- (Optional) `sqlcmd` CLI tool for running SQL scripts directly

## Step 1: Start SQL Server in Docker

Pull and run the SQL Server 2022 container:

```bash
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=YourStrong!Pass123" \
  -p 1433:1433 --name sql-assessment \
  -d mcr.microsoft.com/mssql/server:2022-latest
```

Wait a few seconds for SQL Server to start, then verify it's running:

```bash
docker ps
```

You should see `sql-assessment` with status "Up".

To check SQL Server is accepting connections:

```bash
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -Q "SELECT @@VERSION" -C
```

## Step 2: Create and Seed the Test Database

The repository includes a setup script at `MigrationAssessment/scripts/setup-test-database.sql` that creates a database with features spanning all five risk levels.

### Option A: Run via Docker (no local sqlcmd needed)

```bash
docker cp MigrationAssessment/scripts/setup-test-database.sql sql-assessment:/tmp/setup.sql
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i /tmp/setup.sql -C
```

### Option B: Run with local sqlcmd

```bash
sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -i MigrationAssessment/scripts/setup-test-database.sql
```

### Option C: Run from Azure Data Studio or SSMS

Open `MigrationAssessment/scripts/setup-test-database.sql` and execute it against `localhost` with the sa credentials.

### What the script creates

The `AssessmentTestDB` database includes:

| Risk Level | Features Created |
|-----------|-----------------|
| 1 - Trivial | Customers, Products, Orders, OrderItems tables with standard CRUD, foreign keys, indexes |
| 2 - Low | Views with TOP, procedures using ISNULL, GETDATE, DATEDIFF, string concatenation |
| 3 - Moderate | Procedures with TRY/CATCH, dynamic SQL (sp_executesql), local temp tables (#), SCOPE_IDENTITY |
| 4 - High | MERGE upsert procedure, NOLOCK/UPDLOCK/ROWLOCK hints, PIVOT view, global temp tables (##) |
| 5 - Critical | XML column with XML index, .value()/.query() methods, OPENQUERY reference |

The script also:
- Enables Query Store in READ_WRITE mode
- Seeds sample data (customers, products, orders)
- Executes all procedures and queries so Query Store captures them
- Flushes the Query Store to disk

## Step 3: Build the Assessment Engine

From the repository root:

```bash
cd MigrationAssessment
dotnet build
```

## Step 4: Run the Assessment

```bash
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" \
  -o ./test-assessment.json
```

On Windows CMD (no backslash line continuations):

```cmd
dotnet run --project src\MigrationAssessment.Cli -- -c "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" -o ./test-assessment.json
```

### Command-line options

| Option | Short | Description | Default |
|--------|-------|-------------|---------|
| `--connection-string` | `-c` | SQL Server connection string | (required) |
| `--output` | `-o` | Output JSON file path | `./assessment-output.json` |
| `--business-importance` | `-b` | Business importance multiplier (1.0–5.0) | `1.0` |
| `--help` | `-h` | Show usage | — |

## Step 5: Review the Output

Open `test-assessment.json`. Key sections to check:

### Executive Summary

```json
"executiveSummary": {
  "migrationReadinessScore": 72,
  "classification": "Moderate Candidate - Significant Work Required",
  "totalStatements": 45,
  "riskDistribution": { "1": 15, "2": 12, "3": 10, "4": 5, "5": 3 }
}
```

The score ranges map to:
- 90–100: Excellent Candidate
- 76–89: Good Candidate
- 51–75: Moderate Candidate
- 26–50: High Risk
- 0–25: Not Recommended

### Analyzed Statements

Each captured statement shows its risk score and conversion category:

```json
"analyzedStatements": [
  {
    "statementText": "SELECT TOP 5 * FROM dbo.Products...",
    "riskScore": 2,
    "weightedRisk": 2.0,
    "conversionCategory": "automatic",
    "detectedFeatures": ["TOP"]
  }
]
```

Conversion categories:
- `automatic` (Risk 1–2): Can be converted programmatically
- `semi-automatic` (Risk 3): Needs human review after conversion
- `manual` (Risk 4–5): Requires manual rewrite

### Migration Recommendation

```json
"migrationRecommendation": {
  "recommendation": "PostgreSQL Migration with Compatibility Middleware",
  "reasoning": "Migration readiness score of 72 indicates moderate complexity..."
}
```

## Troubleshooting

### "Login failed for user 'sa'"

The container might still be starting. Wait 10 seconds and retry. Verify with:

```bash
docker logs sql-assessment
```

Look for "SQL Server is now ready for client connections."

### "Cannot open database 'AssessmentTestDB'"

The setup script hasn't been run yet, or it failed. Re-run Step 2 and check for error output.

### "Query Store is disabled"

The assessment will log a warning and continue using other data sources. If you want Query Store data, verify it's enabled:

```sql
SELECT actual_state_desc FROM sys.database_query_store_options;
```

It should show `READ_WRITE`.

### Connection timeout

Ensure port 1433 is mapped. Check with:

```bash
docker port sql-assessment
```

Should show `1433/tcp -> 0.0.0.0:1433`.

### Empty assessment (no statements analyzed)

This happens if Query Store hasn't captured any queries. Re-run the exercise queries section of the setup script:

```bash
docker exec sql-assessment /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "YourStrong!Pass123" -d AssessmentTestDB -Q "EXEC sp_query_store_flush_db" -C
```

## Cleanup

### Stop the container (preserves data)

```bash
docker stop sql-assessment
```

### Restart later

```bash
docker start sql-assessment
```

### Remove completely

```bash
docker rm -f sql-assessment
```

## Running Against a Real Database

To assess a production or staging SQL Server:

```bash
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=prod-sql.company.com;Database=MyApp;User Id=readonly_user;Password=...;TrustServerCertificate=True" \
  -o ./myapp-assessment.json \
  -b 3.0
```

Notes for real environments:
- Use a read-only account with `VIEW SERVER STATE` and `VIEW DEFINITION` permissions
- Set `-b` (business importance) higher for mission-critical databases to weight risk scores more heavily
- The tool only reads metadata and Query Store data — it does not modify the database
- Query timeouts are 120 seconds by default; large Query Stores may need more time
- If Extended Events data is desired, create the `migration_assessment` session (see README.md)

## Setting Up Extended Events (Optional)

For the most comprehensive assessment, create an Extended Events session before running workload:

```sql
USE AssessmentTestDB;
GO

CREATE EVENT SESSION [migration_assessment] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(
    ACTION(sqlserver.database_name, sqlserver.username, sqlserver.sql_text)),
ADD EVENT sqlserver.rpc_completed(
    ACTION(sqlserver.database_name, sqlserver.username, sqlserver.sql_text)),
ADD EVENT sqlserver.sp_statement_completed(
    ACTION(sqlserver.database_name, sqlserver.username))
ADD TARGET package0.ring_buffer(SET max_memory = 51200)
WITH (MAX_DISPATCH_LATENCY = 5 SECONDS);
GO

ALTER EVENT SESSION [migration_assessment] ON SERVER STATE = START;
GO
```

Then run your application workload, and afterwards run the assessment. The engine will read captured events from the ring buffer.

Note: Extended Events is optional. Query Store + Metadata collection is usually sufficient for a good assessment. XE adds visibility into ad hoc queries and dynamic SQL that Query Store might not capture.
