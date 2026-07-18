# Migration Validation Test Suite

Validates the success of the database migration from MS SQL Server to PostgreSQL (via PgPassthrough) by running T-SQL scripts that exercise every object in the `AssessmentTestDB` database.

## Purpose

After Migris Technology converts the database from MS SQL Server to PostgreSQL, this project allows you to:

1. Run the full test suite against the **original SQL Server** to establish a baseline
2. Switch the connection to the **PgPassthrough endpoint** (same TDS protocol, backed by PostgreSQL)
3. Compare results — if all scripts pass against both endpoints, the migration is validated

## Prerequisites

- .NET 8.0 SDK
- Access to the AssessmentTestDB on SQL Server (or PgPassthrough endpoint)
- The database must be set up using `../MigrationAssessment/scripts/setup-test-database.sql`

## Configuration

Edit `appsettings.json` to switch between endpoints:

```json
{
  "ConnectionStrings": {
    "SqlServer": "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True",
    "PgPassthrough": "Server=localhost,11433;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True"
  },
  "ActiveConnection": "SqlServer"
}
```

Change `ActiveConnection` to `"PgPassthrough"` to target the migrated database.

## Usage

```bash
# Run against SQL Server (baseline)
dotnet run --project src/MigrationValidation.Runner

# Run against PgPassthrough (post-migration validation)
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough

# Run specific category
dotnet run --project src/MigrationValidation.Runner -- --category StoredProcedures

# Run with verbose output
dotnet run --project src/MigrationValidation.Runner -- --verbose

# Combine options
dotnet run --project src/MigrationValidation.Runner -- --connection PgPassthrough --category Tables --verbose
```

### Options

| Flag | Description |
|------|-------------|
| `--connection SqlServer` | Override active connection (SqlServer or PgPassthrough) |
| `--category <name>` | Run only a specific category (Tables, Views, StoredProcedures, Functions, Synonyms, All) |
| `--verbose` | Show detailed query output including row counts |

## Test Categories

| Category | Objects Tested |
|----------|---------------|
| **Tables** | Categories, Customers, Products, Orders, OrderItems, ProductImportStaging, OrderMetadata |
| **Views** | vw_RecentOrders, vw_MonthlyCategoryRevenue, vw_OrderSummary (if exists) |
| **Stored Procedures** | sp_GetTopCustomers, sp_ProcessOrder, sp_DynamicSearch, sp_BuildMonthlyReport, sp_UpsertProducts, sp_GetInventorySnapshot, sp_UpdateStockWithLock, sp_SharedTempReport, sp_GetOrderShippingInfo, sp_GetExternalInventory |
| **Functions** | fn_FormatCustomerName, fn_GetCustomerTotal (if exists) |
| **Synonyms** | syn_ActiveCustomers |

## How It Works

Each test script:
1. Executes a T-SQL statement against the configured endpoint
2. Validates that results are returned without errors
3. Optionally compares row counts or specific values against expected baselines

The scripts use standard T-SQL syntax that both SQL Server and PgPassthrough should handle identically after migration.
