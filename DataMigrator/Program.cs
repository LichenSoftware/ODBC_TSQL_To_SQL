using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DataMigrator;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<int> Main(string[] args)
    {
        var sourceOption = new Option<string>("--source", "SQL Server connection string") { IsRequired = true };
        var targetOption = new Option<string>("--target", "PostgreSQL connection string") { IsRequired = true };
        var sessionOption = new Option<string>("--session", "Path to session directory") { IsRequired = true };
        var batchSizeOption = new Option<int>("--batch-size", () => 1000, "Rows per batch insert");
        var tablesOption = new Option<string[]?>("--tables", "Specific tables to migrate (schema.name format)");
        var disableFkOption = new Option<bool>("--disable-fk", () => true, "Disable foreign keys during migration");
        var reseedOption = new Option<bool>("--reseed", () => true, "Reseed identity sequences after migration");
        var truncateOption = new Option<bool>("--truncate", () => false, "Truncate target tables before migrating");

        var rootCommand = new RootCommand("Data Migration Tool - moves data from SQL Server to PostgreSQL using session metadata")
        {
            sourceOption, targetOption, sessionOption, batchSizeOption,
            tablesOption, disableFkOption, reseedOption, truncateOption
        };

        rootCommand.SetHandler(async (context) =>
        {
            var source = context.ParseResult.GetValueForOption(sourceOption)!;
            var target = context.ParseResult.GetValueForOption(targetOption)!;
            var session = context.ParseResult.GetValueForOption(sessionOption)!;
            var batchSize = context.ParseResult.GetValueForOption(batchSizeOption);
            var tables = context.ParseResult.GetValueForOption(tablesOption);
            var disableFk = context.ParseResult.GetValueForOption(disableFkOption);
            var reseed = context.ParseResult.GetValueForOption(reseedOption);
            var truncate = context.ParseResult.GetValueForOption(truncateOption);

            var options = new MigrationOptions
            {
                SourceConnectionString = source,
                TargetConnectionString = target,
                SessionPath = session,
                BatchSize = batchSize,
                TableFilter = tables is { Length: > 0 } ? tables.ToHashSet(StringComparer.OrdinalIgnoreCase) : null,
                DisableForeignKeys = disableFk,
                ReseedSequences = reseed,
                TruncateBeforeMigrate = truncate
            };

            var exitCode = await RunMigrationAsync(options);
            context.ExitCode = exitCode;
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static async Task<int> RunMigrationAsync(MigrationOptions options)
    {
        var stopwatch = Stopwatch.StartNew();

        // 1. Load session and get tables in dependency order
        Console.WriteLine($"Loading session from: {options.SessionPath}");
        var tables = LoadTablesFromSession(options.SessionPath);

        if (options.TableFilter is not null)
        {
            tables = tables.Where(t => options.TableFilter.Contains($"{t.SourceSchema}.{t.SourceName}")).ToList();
        }

        Console.WriteLine($"Found {tables.Count} tables to migrate");

        if (tables.Count == 0)
        {
            Console.WriteLine("No tables to migrate.");
            return 0;
        }

        // 2. Test connections
        Console.Write("Testing SQL Server connection... ");
        if (!await TestSqlServerConnectionAsync(options.SourceConnectionString))
            return 1;
        Console.WriteLine("OK");

        Console.Write("Testing PostgreSQL connection... ");
        if (!await TestPostgresConnectionAsync(options.TargetConnectionString))
            return 1;
        Console.WriteLine("OK");

        // 3. Disable FK constraints if requested
        if (options.DisableForeignKeys)
        {
            Console.WriteLine("Disabling foreign key constraints on target...");
            await DisableForeignKeysAsync(options.TargetConnectionString, tables);
        }

        // 4. Migrate each table
        var totalRows = 0L;
        var failedTables = new List<string>();

        foreach (var table in tables)
        {
            try
            {
                var rows = await MigrateTableAsync(table, options);
                totalRows += rows;
                Console.WriteLine($"  ✓ {table.SourceSchema}.{table.SourceName} → {table.TargetSchema}.{table.TargetName}: {rows:N0} rows");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {table.SourceSchema}.{table.SourceName}: {ex.Message}");
                failedTables.Add($"{table.SourceSchema}.{table.SourceName}");
            }
        }

        // 5. Re-enable FK constraints
        if (options.DisableForeignKeys)
        {
            Console.WriteLine("Re-enabling foreign key constraints...");
            await EnableForeignKeysAsync(options.TargetConnectionString, tables);
        }

        // 6. Reseed identity sequences
        if (options.ReseedSequences)
        {
            Console.WriteLine("Reseeding identity sequences...");
            await ReseedSequencesAsync(options.TargetConnectionString, tables);
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"Migration complete:");
        Console.WriteLine($"  Tables: {tables.Count - failedTables.Count}/{tables.Count} succeeded");
        Console.WriteLine($"  Total rows: {totalRows:N0}");
        Console.WriteLine($"  Duration: {stopwatch.Elapsed}");

        if (failedTables.Count > 0)
        {
            Console.WriteLine($"  Failed: {string.Join(", ", failedTables)}");
            return 1;
        }

        return 0;
    }

    private static List<TableInfo> LoadTablesFromSession(string sessionPath)
    {
        var resolvedPath = Path.GetFullPath(sessionPath);
        var objectsPath = Path.Combine(resolvedPath, "objects");

        Console.WriteLine($"  Resolved session path: {resolvedPath}");
        Console.WriteLine($"  Looking for tables in: {objectsPath}");

        if (!Directory.Exists(objectsPath))
            throw new DirectoryNotFoundException($"Objects directory not found: {objectsPath}");

        var files = Directory.GetFiles(objectsPath, "*.Table.json");
        Console.WriteLine($"  Found {files.Length} table file(s)");

        var tables = new List<TableInfo>();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

            var source = root.GetProperty("source");
            var result = root.GetProperty("result");

            var sourceName = source.GetProperty("name").GetString()!;
            var sourceSchema = source.GetProperty("schemaName").GetString()!;
            var sourceDef = source.GetProperty("sourceDefinition").GetString()!;
            var dependsOn = new List<string>();
            if (source.TryGetProperty("dependsOn", out var deps))
            {
                foreach (var dep in deps.EnumerateArray())
                    dependsOn.Add(dep.GetString()!);
            }

            // Parse target schema and table name from generated DDL
            var generatedDdl = result.TryGetProperty("generatedDdl", out var ddl) ? ddl.GetString() ?? "" : "";
            var (targetSchema, targetName) = ExtractTargetSchemaAndTable(generatedDdl, sourceSchema, sourceName);

            // Extract columns from source DDL
            var columns = ExtractColumns(sourceDef);

            tables.Add(new TableInfo
            {
                SourceSchema = sourceSchema,
                SourceName = sourceName,
                TargetSchema = targetSchema,
                TargetName = targetName,
                Columns = columns,
                DependsOn = dependsOn
            });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  Warning: Failed to parse {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        // Topological sort by dependencies
        Console.WriteLine($"  Parsed {tables.Count} table(s) from JSON files");
        return TopologicalSort(tables);
    }

    private static (string Schema, string Table) ExtractTargetSchemaAndTable(string generatedDdl, string fallbackSchema, string fallbackName)
    {
        // Match: CREATE TABLE schema.tablename (
        var match = Regex.Match(generatedDdl, @"CREATE\s+TABLE\s+(\w+)\.(\w+)\s*\(", RegexOptions.IgnoreCase);
        if (match.Success)
        {
            return (match.Groups[1].Value, match.Groups[2].Value);
        }
        return (fallbackSchema.ToLowerInvariant(), fallbackName.ToLowerInvariant());
    }

    private static List<ColumnInfo> ExtractColumns(string sourceDef)
    {
        var columns = new List<ColumnInfo>();

        // Extract content between the outer parentheses of CREATE TABLE
        var parenStart = sourceDef.IndexOf('(');
        if (parenStart < 0) return columns;
        var body = sourceDef[(parenStart + 1)..];

        // Split by lines and process each column definition
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim().TrimEnd(',');

            // Match column definitions: [ColumnName] type ...
            var match = Regex.Match(trimmed, @"^\[(\w+)\]\s+(\w+)");
            if (!match.Success) continue;

            var colName = match.Groups[1].Value;

            // Skip CONSTRAINT lines (constraints start with CONSTRAINT keyword, not a column)
            if (trimmed.StartsWith("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                continue;

            var isIdentity = trimmed.Contains("IDENTITY", StringComparison.OrdinalIgnoreCase);
            var isXml = match.Groups[2].Value.Equals("xml", StringComparison.OrdinalIgnoreCase);

            columns.Add(new ColumnInfo
            {
                SourceName = colName,
                TargetName = colName.ToLowerInvariant(),
                IsIdentity = isIdentity,
                IsXml = isXml
            });
        }

        return columns;
    }

    private static List<TableInfo> TopologicalSort(List<TableInfo> tables)
    {
        var byName = tables.ToDictionary(
            t => $"{t.SourceSchema}.{t.SourceName}",
            t => t,
            StringComparer.OrdinalIgnoreCase);

        var sorted = new List<TableInfo>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(TableInfo table)
        {
            var key = $"{table.SourceSchema}.{table.SourceName}";
            if (visited.Contains(key)) return;
            if (visiting.Contains(key)) return; // break cycle

            visiting.Add(key);
            foreach (var dep in table.DependsOn)
            {
                if (byName.TryGetValue(dep, out var depTable))
                    Visit(depTable);
            }
            visiting.Remove(key);
            visited.Add(key);
            sorted.Add(table);
        }

        foreach (var table in tables.OrderBy(t => t.SourceName))
            Visit(table);

        return sorted;
    }

    private static async Task<long> MigrateTableAsync(TableInfo table, MigrationOptions options)
    {
        if (table.Columns.Count == 0)
            throw new InvalidOperationException("No columns found in source definition");

        var sourceColumns = string.Join(", ", table.Columns.Select(c => $"[{c.SourceName}]"));
        var targetColumns = string.Join(", ", table.Columns.Select(c => c.TargetName));
        var paramPlaceholders = string.Join(", ", table.Columns.Select((c, i) =>
            c.IsXml ? $"${i + 1}::xml" : $"${i + 1}"));

        var selectSql = $"SELECT {sourceColumns} FROM [{table.SourceSchema}].[{table.SourceName}]";
        var insertSql = $"INSERT INTO {table.TargetSchema}.{table.TargetName} ({targetColumns}) VALUES ({paramPlaceholders})";

        // Truncate if requested
        if (options.TruncateBeforeMigrate)
        {
            await using var truncConn = new NpgsqlConnection(options.TargetConnectionString);
            await truncConn.OpenAsync();
            await using var truncCmd = truncConn.CreateCommand();
            truncCmd.CommandText = $"TRUNCATE TABLE {table.TargetSchema}.{table.TargetName} CASCADE";
            await truncCmd.ExecuteNonQueryAsync();
        }

        // Override identity column behavior for PostgreSQL
        var hasIdentity = table.Columns.Any(c => c.IsIdentity);
        var overrideIdentity = hasIdentity ? " OVERRIDING SYSTEM VALUE" : "";
        if (hasIdentity)
        {
            insertSql = $"INSERT INTO {table.TargetSchema}.{table.TargetName} ({targetColumns}){overrideIdentity} VALUES ({paramPlaceholders})";
        }

        long rowCount = 0;

        await using var sourceConn = new SqlConnection(options.SourceConnectionString);
        await sourceConn.OpenAsync();

        await using var targetConn = new NpgsqlConnection(options.TargetConnectionString);
        await targetConn.OpenAsync();

        await using var reader = await new SqlCommand(selectSql, sourceConn) { CommandTimeout = 300 }.ExecuteReaderAsync();

        // Batch inserts using a transaction
        var batch = new List<object?[]>();

        while (await reader.ReadAsync())
        {
            var values = new object?[table.Columns.Count];
            for (int i = 0; i < table.Columns.Count; i++)
            {
                values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            batch.Add(values);

            if (batch.Count >= options.BatchSize)
            {
                await WriteBatchAsync(targetConn, insertSql, batch, table.Columns);
                rowCount += batch.Count;
                batch.Clear();
            }
        }

        // Write remaining rows
        if (batch.Count > 0)
        {
            await WriteBatchAsync(targetConn, insertSql, batch, table.Columns);
            rowCount += batch.Count;
        }

        return rowCount;
    }

    private static async Task WriteBatchAsync(
        NpgsqlConnection conn, string insertSql, List<object?[]> batch, List<ColumnInfo> columns)
    {
        await using var transaction = await conn.BeginTransactionAsync();

        foreach (var row in batch)
        {
            await using var cmd = new NpgsqlCommand(insertSql, conn, transaction);
            for (int i = 0; i < columns.Count; i++)
            {
                cmd.Parameters.Add(new NpgsqlParameter { Value = row[i] ?? DBNull.Value });
            }
            await cmd.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    private static async Task DisableForeignKeysAsync(string connectionString, List<TableInfo> tables)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table.TargetSchema}.{table.TargetName} DISABLE TRIGGER ALL";
            try { await cmd.ExecuteNonQueryAsync(); }
            catch { /* table might not exist yet */ }
        }
    }

    private static async Task EnableForeignKeysAsync(string connectionString, List<TableInfo> tables)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"ALTER TABLE {table.TargetSchema}.{table.TargetName} ENABLE TRIGGER ALL";
            try { await cmd.ExecuteNonQueryAsync(); }
            catch { /* ignore */ }
        }
    }

    private static async Task ReseedSequencesAsync(string connectionString, List<TableInfo> tables)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        foreach (var table in tables)
        {
            var identityCols = table.Columns.Where(c => c.IsIdentity).ToList();
            foreach (var col in identityCols)
            {
                try
                {
                    // Get the max value and reset the sequence
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = $@"
                        SELECT setval(
                            pg_get_serial_sequence('{table.TargetSchema}.{table.TargetName}', '{col.TargetName}'),
                            COALESCE((SELECT MAX({col.TargetName}) FROM {table.TargetSchema}.{table.TargetName}), 1)
                        )";
                    await cmd.ExecuteScalarAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"    Warning: Could not reseed {table.TargetName}.{col.TargetName}: {ex.Message}");
                }
            }
        }
    }

    private static async Task<bool> TestSqlServerConnectionAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> TestPostgresConnectionAsync(string connectionString)
    {
        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
            return false;
        }
    }
}

internal class MigrationOptions
{
    public required string SourceConnectionString { get; init; }
    public required string TargetConnectionString { get; init; }
    public required string SessionPath { get; init; }
    public int BatchSize { get; init; } = 1000;
    public HashSet<string>? TableFilter { get; init; }
    public bool DisableForeignKeys { get; init; } = true;
    public bool ReseedSequences { get; init; } = true;
    public bool TruncateBeforeMigrate { get; init; }
}

internal class TableInfo
{
    public required string SourceSchema { get; init; }
    public required string SourceName { get; init; }
    public required string TargetSchema { get; init; }
    public required string TargetName { get; init; }
    public required List<ColumnInfo> Columns { get; init; }
    public List<string> DependsOn { get; init; } = [];
}

internal class ColumnInfo
{
    public required string SourceName { get; init; }
    public required string TargetName { get; init; }
    public bool IsIdentity { get; init; }
    public bool IsXml { get; init; }
}
