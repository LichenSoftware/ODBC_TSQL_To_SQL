# Schema Conversion - Migration TODO

## Run a Full Migration (my-migration4)

### Step 1: Extract (DONE)
```
dotnet run --project src/SchemaConversion.Cli -- extract --connection "Server=localhost;Database=AssessmentTestDB;User Id=sa;Password=YourStrong!Pass123;TrustServerCertificate=True" --output ./sessions/my-migration4
```

### Step 2: Convert
```
dotnet run --project src/SchemaConversion.Cli -- convert --session ./sessions/my-migration4
```

### Step 3: Generate DDL Scripts
```
dotnet run --project src/SchemaConversion.Cli -- generate --session ./sessions/my-migration4 --output ./sessions/my-migration4/output
```

### Step 4: Review & Apply
- Review the generated DDL in `./sessions/my-migration4/output`
- Verify all objects use consistent schema names (dbo → public)
- Execute the DDL on the destination PostgreSQL database

---

## Bug Fix Applied: Inconsistent Schema in Generated DDL

**Problem:** Rule-based conversions (tables, indexes, constraints) correctly mapped `dbo` → `public`, but AI-assisted conversions (stored procedures, functions, triggers, views) did not receive schema mapping context and produced inconsistent schemas (sometimes `dbo`, sometimes `public`).

**Fix (already applied):**
- [x] Added `BuildSchemaMappingContext()` method to `AiConverterService.cs`
- [x] Wired `{schema_mapping_context}` placeholder into `BuildPlaceholders()`
- [x] Added `## Schema Mappings` section to all 5 prompt templates
- [x] Added explicit conversion rule about schema mapping to each template
- [x] Verified build passes and all 212 tests pass

**Files changed:**
- `src/SchemaConversion.AiEngine/AiConverterService.cs`
- `config/prompts/stored-procedure.v1.0.0.md`
- `config/prompts/function.v1.0.0.md`
- `config/prompts/view.v1.0.0.md`
- `config/prompts/trigger.v1.0.0.md`
- `config/prompts/complex-object.v1.0.0.md`
