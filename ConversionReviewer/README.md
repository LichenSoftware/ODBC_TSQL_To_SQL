# Conversion Reviewer

A local Blazor Server web application for reviewing, editing, and applying the AI-generated schema conversion scripts produced by the AI-AssistedSchemaConversion project.

## Purpose

The AI-AssistedSchemaConversion project produces JSON files containing:
- Original T-SQL source DDL
- Converted PostgreSQL DDL
- Confidence scores, assumptions, review flags, and compatibility notes

These files are difficult to review manually. This app provides a visual interface to:

1. **Browse** all converted objects in a session, sorted by dependency order
2. **Review** source vs generated DDL side-by-side with syntax context
3. **Edit** the generated DDL before applying (changes saved back to JSON)
4. **Apply** scripts to the target PostgreSQL database in dependency order
5. **Track** which scripts have been applied and which have not
6. **Switch** between sessions (my-migration, my-migration2, my-migration3, etc.)

## Running

```bash
cd src/ConversionReviewer
dotnet run
```

Then open http://localhost:5100 in your browser.

## Configuration

Edit `src/ConversionReviewer/appsettings.json`:

```json
{
  "SessionsPath": "..\\..\\..\\..\\AI-AssistedSchemaConversion\\sessions",
  "ConnectionStrings": {
    "TargetPostgres": "Host=localhost;Port=5432;Database=assessmenttestdb;Username=postgres;Password=postgres"
  }
}
```

| Setting | Description |
|---------|-------------|
| `SessionsPath` | Path to the sessions folder (relative to project or absolute) |
| `ConnectionStrings:TargetPostgres` | PostgreSQL connection string for applying scripts |

## Features

### Dashboard
- Session selector dropdown
- Object grid showing all objects with type badges, confidence scores, apply status
- Filter by object type (Table, View, Function, StoredProcedure, Trigger, Synonym)
- Batch apply all pending scripts in dependency order
- Reset apply status for re-testing

### Review Page
- Side-by-side view: Source T-SQL (read-only) | Generated PostgreSQL (editable)
- Edit DDL directly — saves back to the JSON file with `isManuallyEdited: true`
- Apply individual script to the target database
- Review flags, assumptions, and compatibility notes displayed below
- Prev/Next navigation between objects

### Settings
- Test PostgreSQL connection
- View configured sessions path

## Dependency Ordering

Objects are applied in topological order based on the `dependsOn` field in each JSON file:
1. Tables with no dependencies first
2. Tables that depend on other tables
3. Functions
4. Views (which depend on tables)
5. Stored Procedures (which depend on tables/views)
6. Triggers
7. Synonyms

This ensures foreign key constraints and references are satisfied during application.

## Tracking Applied Scripts

When a script is applied (or fails), the JSON file is updated with:
- `appliedAt` — timestamp of the apply attempt
- `appliedSuccessfully` — true/false
- `applyError` — error message if failed

This persists across app restarts. Use "Reset All Status" on the dashboard to clear tracking for a fresh run.
