using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Extraction;

/// <summary>
/// Extracts schema objects from a live SQL Server instance by querying system catalog views.
/// </summary>
public sealed class SqlServerSchemaExtractor : ISchemaExtractor
{
    private readonly ILogger<SqlServerSchemaExtractor> _logger;

    public SqlServerSchemaExtractor(ILogger<SqlServerSchemaExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<SchemaObject>> ExtractAsync(
        SchemaExtractionOptions options, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException(
                "ConnectionString is required for SQL Server extraction.", nameof(options));
        }

        _logger.LogInformation("Beginning schema extraction from SQL Server.");

        var objects = new List<SchemaObject>();

        await using var connection = new SqlConnection(options.ConnectionString);

        try
        {
            await connection.OpenAsync(ct);
        }
        catch (SqlException ex)
        {
            // Never log connection string in exceptions
            _logger.LogError("Failed to connect to SQL Server: {Message}", ex.Message);
            throw new InvalidOperationException(
                "Failed to connect to SQL Server. Verify the connection string and network access.", ex);
        }

        _logger.LogInformation("Connected to SQL Server successfully.");

        var objectMetadata = await GetObjectMetadataAsync(connection, options, ct);
        var definitions = await GetObjectDefinitionsAsync(connection, ct);
        var tableDefinitions = await GetTableDefinitionsAsync(connection, ct);
        var synonymDefinitions = await GetSynonymDefinitionsAsync(connection, ct);
        var dependencies = await GetDependenciesAsync(connection, ct);

        // Merge table and synonym definitions into the main dictionary
        foreach (var (objectId, ddl) in tableDefinitions)
        {
            definitions.TryAdd(objectId, ddl);
        }

        foreach (var (objectId, ddl) in synonymDefinitions)
        {
            definitions.TryAdd(objectId, ddl);
        }

        foreach (var meta in objectMetadata)
        {
            var objectType = MapObjectType(meta.TypeCode);
            if (objectType is null)
            {
                continue;
            }

            var qualifiedName = $"{meta.SchemaName}.{meta.ObjectName}";
            var definition = definitions.GetValueOrDefault(meta.ObjectId, string.Empty);
            var hash = ComputeHash(definition);

            var deps = dependencies
                .Where(d => d.ReferencingId == meta.ObjectId)
                .Select(d => d.ReferencedName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            objects.Add(new SchemaObject
            {
                Name = meta.ObjectName,
                SchemaName = meta.SchemaName,
                ObjectType = objectType.Value,
                SourceDefinition = definition,
                SourceDefinitionHash = hash,
                DependsOn = deps
            });
        }

        _logger.LogInformation("Extracted {Count} schema objects.", objects.Count);

        return objects;
    }

    private static async Task<List<ObjectMetadata>> GetObjectMetadataAsync(
        SqlConnection connection, SchemaExtractionOptions options, CancellationToken ct)
    {
        var results = new List<ObjectMetadata>();

        var sql = """
            SELECT
                o.object_id,
                s.name AS schema_name,
                o.name AS object_name,
                o.type AS type_code
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'TR', 'SO', 'SN')
            """;

        if (options.IncludeSchemas is { Count: > 0 })
        {
            var schemaParams = string.Join(", ",
                options.IncludeSchemas.Select((_, i) => $"@schema{i}"));
            sql += $"\n  AND s.name IN ({schemaParams})";
        }

        sql += "\nORDER BY s.name, o.name";

        await using var cmd = new SqlCommand(sql, connection);

        if (options.IncludeSchemas is { Count: > 0 })
        {
            for (var i = 0; i < options.IncludeSchemas.Count; i++)
            {
                cmd.Parameters.AddWithValue($"@schema{i}", options.IncludeSchemas[i]);
            }
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new ObjectMetadata(
                ObjectId: reader.GetInt32(0),
                SchemaName: reader.GetString(1),
                ObjectName: reader.GetString(2),
                TypeCode: reader.GetString(3).Trim()));
        }

        return results;
    }

    private static async Task<Dictionary<int, string>> GetObjectDefinitionsAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var definitions = new Dictionary<int, string>();

        const string sql = """
            SELECT
                m.object_id,
                m.definition
            FROM sys.sql_modules m
            WHERE m.definition IS NOT NULL
            """;

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var objectId = reader.GetInt32(0);
            var definition = reader.GetString(1);
            definitions[objectId] = definition;
        }

        return definitions;
    }

    private static async Task<Dictionary<int, string>> GetTableDefinitionsAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var definitions = new Dictionary<int, string>();

        // Query all column info, constraints, identity, defaults, and computed columns for user tables
        const string columnsSql = """
            SELECT
                t.object_id,
                s.name AS schema_name,
                t.name AS table_name,
                c.column_id,
                c.name AS column_name,
                tp.name AS type_name,
                c.max_length,
                c.precision,
                c.scale,
                c.is_nullable,
                c.is_identity,
                ic.seed_value,
                ic.increment_value,
                dc.definition AS default_definition,
                cc.definition AS computed_definition,
                cc.is_persisted
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types tp ON c.user_type_id = tp.user_type_id
            LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY t.object_id, c.column_id
            """;

        var tableColumns = new Dictionary<int, TableInfo>();

        await using (var cmd = new SqlCommand(columnsSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var objectId = reader.GetInt32(0);

                if (!tableColumns.TryGetValue(objectId, out var tableInfo))
                {
                    tableInfo = new TableInfo(
                        SchemaName: reader.GetString(1),
                        TableName: reader.GetString(2),
                        Columns: []);
                    tableColumns[objectId] = tableInfo;
                }

                tableInfo.Columns.Add(new ColumnInfo(
                    ColumnId: reader.GetInt32(3),
                    Name: reader.GetString(4),
                    TypeName: reader.GetString(5),
                    MaxLength: reader.GetInt16(6),
                    Precision: reader.GetByte(7),
                    Scale: reader.GetByte(8),
                    IsNullable: reader.GetBoolean(9),
                    IsIdentity: reader.GetBoolean(10),
                    SeedValue: reader.IsDBNull(11) ? null : reader.GetValue(11),
                    IncrementValue: reader.IsDBNull(12) ? null : reader.GetValue(12),
                    DefaultDefinition: reader.IsDBNull(13) ? null : reader.GetString(13),
                    ComputedDefinition: reader.IsDBNull(14) ? null : reader.GetString(14),
                    IsPersisted: !reader.IsDBNull(15) && reader.GetBoolean(15)));
            }
        }

        // Get primary key and unique constraints
        const string constraintsSql = """
            SELECT
                i.object_id,
                i.name AS constraint_name,
                i.is_primary_key,
                i.is_unique_constraint,
                i.is_unique,
                i.type_desc,
                STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS column_list
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            INNER JOIN sys.tables t ON t.object_id = i.object_id
            WHERE t.is_ms_shipped = 0
              AND ic.is_included_column = 0
              AND (i.is_primary_key = 1 OR i.is_unique_constraint = 1)
            GROUP BY i.object_id, i.name, i.is_primary_key, i.is_unique_constraint, i.is_unique, i.type_desc
            """;

        var tableConstraints = new Dictionary<int, List<ConstraintInfo>>();

        await using (var cmd = new SqlCommand(constraintsSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var objectId = reader.GetInt32(0);

                if (!tableConstraints.TryGetValue(objectId, out var constraints))
                {
                    constraints = [];
                    tableConstraints[objectId] = constraints;
                }

                constraints.Add(new ConstraintInfo(
                    Name: reader.GetString(1),
                    IsPrimaryKey: reader.GetBoolean(2),
                    IsUniqueConstraint: reader.GetBoolean(3),
                    ColumnList: reader.GetString(6)));
            }
        }

        // Get foreign keys
        const string foreignKeysSql = """
            SELECT
                fk.parent_object_id,
                fk.name AS fk_name,
                rs.name + '.' + rt.name AS referenced_table,
                STRING_AGG(pc.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS parent_columns,
                STRING_AGG(rc.name, ', ') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS referenced_columns,
                fk.delete_referential_action_desc,
                fk.update_referential_action_desc
            FROM sys.foreign_keys fk
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            INNER JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            INNER JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            INNER JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            WHERE pt.is_ms_shipped = 0
            GROUP BY fk.parent_object_id, fk.name, rs.name, rt.name,
                     fk.delete_referential_action_desc, fk.update_referential_action_desc
            """;

        var tableForeignKeys = new Dictionary<int, List<ForeignKeyInfo>>();

        await using (var cmd = new SqlCommand(foreignKeysSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var objectId = reader.GetInt32(0);

                if (!tableForeignKeys.TryGetValue(objectId, out var fks))
                {
                    fks = [];
                    tableForeignKeys[objectId] = fks;
                }

                fks.Add(new ForeignKeyInfo(
                    Name: reader.GetString(1),
                    ReferencedTable: reader.GetString(2),
                    ParentColumns: reader.GetString(3),
                    ReferencedColumns: reader.GetString(4),
                    DeleteAction: reader.GetString(5),
                    UpdateAction: reader.GetString(6)));
            }
        }

        // Get check constraints
        const string checkConstraintsSql = """
            SELECT
                cc.parent_object_id,
                cc.name AS constraint_name,
                cc.definition
            FROM sys.check_constraints cc
            INNER JOIN sys.tables t ON t.object_id = cc.parent_object_id
            WHERE t.is_ms_shipped = 0
            """;

        var tableChecks = new Dictionary<int, List<CheckConstraintInfo>>();

        await using (var cmd = new SqlCommand(checkConstraintsSql, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var objectId = reader.GetInt32(0);

                if (!tableChecks.TryGetValue(objectId, out var checks))
                {
                    checks = [];
                    tableChecks[objectId] = checks;
                }

                checks.Add(new CheckConstraintInfo(
                    Name: reader.GetString(1),
                    Definition: reader.GetString(2)));
            }
        }

        // Build CREATE TABLE DDL for each table
        foreach (var (objectId, tableInfo) in tableColumns)
        {
            var ddl = BuildCreateTableDdl(
                tableInfo,
                tableConstraints.GetValueOrDefault(objectId),
                tableForeignKeys.GetValueOrDefault(objectId),
                tableChecks.GetValueOrDefault(objectId));

            definitions[objectId] = ddl;
        }

        return definitions;
    }

    private static string BuildCreateTableDdl(
        TableInfo table,
        List<ConstraintInfo>? constraints,
        List<ForeignKeyInfo>? foreignKeys,
        List<CheckConstraintInfo>? checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"CREATE TABLE [{table.SchemaName}].[{table.TableName}]");
        sb.AppendLine("(");

        // Columns
        for (var i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            sb.Append($"    [{col.Name}]");

            if (col.ComputedDefinition is not null)
            {
                sb.Append($" AS {col.ComputedDefinition}");
                if (col.IsPersisted)
                    sb.Append(" PERSISTED");
            }
            else
            {
                sb.Append($" {FormatDataType(col)}");

                if (col.IsIdentity)
                {
                    var seed = col.SeedValue ?? 1;
                    var increment = col.IncrementValue ?? 1;
                    sb.Append($" IDENTITY({seed},{increment})");
                }

                sb.Append(col.IsNullable ? " NULL" : " NOT NULL");

                if (col.DefaultDefinition is not null)
                {
                    sb.Append($" DEFAULT {col.DefaultDefinition}");
                }
            }

            var isLast = i == table.Columns.Count - 1
                         && (constraints is null || constraints.Count == 0)
                         && (foreignKeys is null || foreignKeys.Count == 0)
                         && (checks is null || checks.Count == 0);

            sb.AppendLine(isLast ? "" : ",");
        }

        // Primary key and unique constraints
        if (constraints is { Count: > 0 })
        {
            foreach (var constraint in constraints)
            {
                var type = constraint.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
                sb.AppendLine($"    CONSTRAINT [{constraint.Name}] {type} ({constraint.ColumnList}),");
            }
        }

        // Foreign keys
        if (foreignKeys is { Count: > 0 })
        {
            foreach (var fk in foreignKeys)
            {
                sb.Append($"    CONSTRAINT [{fk.Name}] FOREIGN KEY ({fk.ParentColumns}) REFERENCES {fk.ReferencedTable} ({fk.ReferencedColumns})");

                if (fk.DeleteAction != "NO_ACTION")
                    sb.Append($" ON DELETE {fk.DeleteAction.Replace('_', ' ')}");
                if (fk.UpdateAction != "NO_ACTION")
                    sb.Append($" ON UPDATE {fk.UpdateAction.Replace('_', ' ')}");

                sb.AppendLine(",");
            }
        }

        // Check constraints
        if (checks is { Count: > 0 })
        {
            foreach (var check in checks)
            {
                sb.AppendLine($"    CONSTRAINT [{check.Name}] CHECK {check.Definition},");
            }
        }

        // Remove trailing comma from the last constraint line
        var result = sb.ToString().TrimEnd();
        if (result.EndsWith(','))
        {
            result = result[..^1];
        }

        result += "\n);\n";

        return result;
    }

    private static string FormatDataType(ColumnInfo col)
    {
        var typeName = col.TypeName.ToUpperInvariant();

        return typeName switch
        {
            "NVARCHAR" or "NCHAR" => col.MaxLength == -1
                ? $"{col.TypeName}(MAX)"
                : $"{col.TypeName}({col.MaxLength / 2})",
            "VARCHAR" or "CHAR" or "VARBINARY" or "BINARY" => col.MaxLength == -1
                ? $"{col.TypeName}(MAX)"
                : $"{col.TypeName}({col.MaxLength})",
            "DECIMAL" or "NUMERIC" => $"{col.TypeName}({col.Precision},{col.Scale})",
            "FLOAT" => col.Precision == 53 ? col.TypeName : $"{col.TypeName}({col.Precision})",
            "DATETIME2" or "DATETIMEOFFSET" or "TIME" => col.Scale == 7
                ? col.TypeName
                : $"{col.TypeName}({col.Scale})",
            _ => col.TypeName
        };
    }

    private static async Task<Dictionary<int, string>> GetSynonymDefinitionsAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var definitions = new Dictionary<int, string>();

        const string sql = """
            SELECT
                syn.object_id,
                s.name AS schema_name,
                syn.name AS synonym_name,
                syn.base_object_name
            FROM sys.synonyms syn
            INNER JOIN sys.schemas s ON syn.schema_id = s.schema_id
            """;

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var objectId = reader.GetInt32(0);
            var schemaName = reader.GetString(1);
            var synonymName = reader.GetString(2);
            var baseObjectName = reader.GetString(3);

            var ddl = $"CREATE SYNONYM [{schemaName}].[{synonymName}] FOR {baseObjectName};\n";
            definitions[objectId] = ddl;
        }

        return definitions;
    }

    private static async Task<List<DependencyInfo>> GetDependenciesAsync(
        SqlConnection connection, CancellationToken ct)
    {
        var dependencies = new List<DependencyInfo>();

        const string sql = """
            SELECT
                d.referencing_id,
                COALESCE(rs.name, 'dbo') + '.' + d.referenced_entity_name AS referenced_name
            FROM sys.sql_expression_dependencies d
            LEFT JOIN sys.schemas rs ON d.referenced_schema_name = rs.name
            WHERE d.referenced_entity_name IS NOT NULL
            """;

        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            dependencies.Add(new DependencyInfo(
                ReferencingId: reader.GetInt32(0),
                ReferencedName: reader.GetString(1)));
        }

        return dependencies;
    }

    private static SchemaObjectType? MapObjectType(string typeCode)
    {
        return typeCode switch
        {
            "U" => SchemaObjectType.Table,
            "V" => SchemaObjectType.View,
            "P" => SchemaObjectType.StoredProcedure,
            "FN" or "IF" or "TF" => SchemaObjectType.Function,
            "TR" => SchemaObjectType.Trigger,
            "SO" => SchemaObjectType.Sequence,
            "SN" => SchemaObjectType.Synonym,
            _ => null
        };
    }

    private static string ComputeHash(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private sealed record ObjectMetadata(
        int ObjectId, string SchemaName, string ObjectName, string TypeCode);

    private sealed record DependencyInfo(int ReferencingId, string ReferencedName);

    private sealed record TableInfo(
        string SchemaName, string TableName, List<ColumnInfo> Columns);

    private sealed record ColumnInfo(
        int ColumnId, string Name, string TypeName,
        short MaxLength, byte Precision, byte Scale,
        bool IsNullable, bool IsIdentity,
        object? SeedValue, object? IncrementValue,
        string? DefaultDefinition,
        string? ComputedDefinition, bool IsPersisted);

    private sealed record ConstraintInfo(
        string Name, bool IsPrimaryKey, bool IsUniqueConstraint, string ColumnList);

    private sealed record ForeignKeyInfo(
        string Name, string ReferencedTable,
        string ParentColumns, string ReferencedColumns,
        string DeleteAction, string UpdateAction);

    private sealed record CheckConstraintInfo(string Name, string Definition);
}
