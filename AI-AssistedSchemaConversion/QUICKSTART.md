# Quick Start

Run all commands from the `AI-AssistedSchemaConversion` directory.

## 1. Extract schema from SQL Server

```
dotnet run --project src/SchemaConversion.Cli -- extract --connection "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" --output ./sessions/my-migration
```

Change `--output` to create a new session (e.g. `./sessions/my-migration2`).

## 2. Convert to PostgreSQL

```
dotnet run --project src/SchemaConversion.Cli -- convert --session ./sessions/my-migration
```

Results are written back into the session's `objects/` folder as JSON files.

## 3. Check results

Look at the final summary line:

```
Converted: 23  Flagged: 0  Failed: 3
```

- **Converted** — good to go, review the generated DDL
- **Flagged** — converted but confidence < 0.7, needs manual review
- **Failed** — could not convert, check the `errorMessage` in the object JSON

## 4. Generate output scripts (optional)

```
dotnet run --project src/SchemaConversion.Cli -- generate --session ./sessions/my-migration
```

## 5. Generate a report (optional)

```
dotnet run --project src/SchemaConversion.Cli -- report --session ./sessions/my-migration
```

## Tips

- Session folder holds all state. Delete it to start fresh.
- Re-running `convert` only reprocesses objects whose source hash changed.
- Concurrency defaults to 4 (set in `appsettings.json` → `Conversion.DefaultConcurrency`).
- Confidence threshold is 0.7 (set in `appsettings.json` → `Conversion.ConfidenceThreshold`).
