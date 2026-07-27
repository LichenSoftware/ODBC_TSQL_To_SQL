# Migration Assessment Engine

A .NET CLI tool that analyzes a SQL Server database and produces a PostgreSQL migration readiness assessment with actionable work items. It connects to a live instance, collects workload and metadata, parses T-SQL using ScriptDom, scores migration risk per statement, and generates prioritized tasks with effort estimates and remediation guidance.

## What It Does

1. **Collects** data from Query Store, Extended Events, database metadata, and feature detection
2. **Analyzes** every captured T-SQL statement for PostgreSQL incompatibilities
3. **Scores** each statement on a 1–5 risk scale with weighted complexity
4. **Reports** a Migration Readiness Score (0–100) with effort estimates and recommendations
5. **Generates work items** — structured tasks for each incompatible feature, including SQL Server patterns, PostgreSQL equivalents, remediation steps, and acceptance criteria

## Risk Levels

| Level | Description | Examples |
|-------|-------------|----------|
| 1 | Standard SQL, no changes needed | Basic CRUD |
| 2 | Simple syntax translation | TOP, ISNULL, GETDATE |
| 3 | Procedural logic changes | TRY/CATCH, dynamic SQL |
| 4 | Significant redesign | MERGE, TVPs, lock hints |
| 5 | Architectural replacement | SQL CLR, Service Broker |

## Quick Start

```bash
# Run assessment
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True"

# Run assessment + generate work items
dotnet run --project src/MigrationAssessment.Cli -- \
  -c "Server=myserver;Database=mydb;Trusted_Connection=True;TrustServerCertificate=True" \
  --generate-work-items --work-item-markdown

# Generate work items from existing assessment
dotnet run --project src/MigrationAssessment.Cli -- \
  generate-work-items ./assessment-output.json --markdown --min-risk 3
```

## Output

**Assessment JSON** — Executive summary, risk distribution, object inventory, feature inventory, analyzed statements, migration recommendation, and effort estimates.

**Work Items (JSON + Markdown)** — Each item includes:
- Risk-ranked title and priority (Critical/High/Medium/Low)
- SQL Server code pattern and PostgreSQL equivalent
- Affected objects and occurrence counts
- Estimated effort (min/max hours) with confidence level
- Step-by-step remediation guidance
- Acceptance criteria

## Key Options

| Option | Description |
|--------|-------------|
| `-c` | SQL Server connection string (required) |
| `-o <path>` | Assessment output path (default: `./assessment-output.json`) |
| `--generate-work-items` | Enable work item generation |
| `--work-item-output <path>` | Work items JSON path |
| `--work-item-markdown` | Also produce Markdown report |
| `--work-item-min-risk <1-5>` | Filter by minimum risk level |
| `--work-item-max-count <n>` | Cap number of work items |

## Requirements

- .NET 8 SDK
- SQL Server login with `VIEW SERVER STATE` and `VIEW DEFINITION`
- Query Store enabled (optional, degrades gracefully)
- Extended Events session named `migration_assessment` (optional)

## Migration Recommendations

Based on the readiness score, the tool recommends one of:
- **Direct PostgreSQL Migration** (score ≥ 76, no Risk 5)
- **PostgreSQL with Compatibility Middleware** (score ≥ 51)
- **Partial Migration** (score ≥ 26)
- **Remain on SQL Server** (score < 26)
