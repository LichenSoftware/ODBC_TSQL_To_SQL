using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Collectors;

/// <summary>
/// Collects database object metadata from SQL Server sys catalog views.
/// Excludes system schemas and handles encrypted/inaccessible objects gracefully.
/// </summary>
public sealed class MetadataCollector : IMetadataCollector
{
    private readonly ILogger<MetadataCollector> _logger;

    public MetadataCollector(ILogger<MetadataCollector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DatabaseObjectInventory> CollectAsync(
        DbConnection connection,
        CollectionOptions options,
        CancellationToken ct)
    {
        var tables = new List<TableMetadata>();
        var indexes = new List<IndexMetadata>();
        var constraints = new List<ConstraintMetadata>();
        var foreignKeys = new List<ForeignKeyMetadata>();
        var programmableObjects = new List<ProgrammableObjectMetadata>();
        var synonyms = new List<SynonymMetadata>();

        int timeoutSeconds = (int)options.QueryTimeout.TotalSeconds;

        tables = await CollectTablesAndColumnsAsync(connection, timeoutSeconds, ct);
        indexes = await CollectIndexesAsync(connection, timeoutSeconds, ct);
        constraints = await CollectConstraintsAsync(connection, timeoutSeconds, ct);
        foreignKeys = await CollectForeignKeysAsync(connection, timeoutSeconds, ct);
        programmableObjects = await CollectProgrammableObjectsAsync(connection, timeoutSeconds, ct);
        synonyms = await CollectSynonymsAsync(connection, timeoutSeconds, ct);

        return new DatabaseObjectInventory
        {
            Tables = tables,
            Indexes = indexes,
            Constraints = constraints,
            ForeignKeys = foreignKeys,
            ProgrammableObjects = programmableObjects,
            Synonyms = synonyms
        };
    }

    private async Task<List<TableMetadata>> CollectTablesAndColumnsAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                c.name AS ColumnName,
                c.column_id AS OrdinalPosition,
                tp.name AS DataType,
                c.precision AS [Precision],
                c.scale AS Scale,
                c.max_length AS MaxLength,
                c.is_nullable AS IsNullable,
                c.is_identity AS IsIdentity,
                cc.definition AS ComputedDefinition
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = t.object_id
            INNER JOIN sys.types tp ON c.user_type_id = tp.user_type_id
            LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var tableDict = new Dictionary<(string Schema, string Table), List<ColumnMetadata>>();

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(reader.GetOrdinal("SchemaName"));
                var tableName = reader.GetString(reader.GetOrdinal("TableName"));
                var key = (schemaName, tableName);

                if (!tableDict.TryGetValue(key, out var columns))
                {
                    columns = new List<ColumnMetadata>();
                    tableDict[key] = columns;
                }

                columns.Add(new ColumnMetadata
                {
                    ColumnName = reader.GetString(reader.GetOrdinal("ColumnName")),
                    OrdinalPosition = reader.GetInt32(reader.GetOrdinal("OrdinalPosition")),
                    DataType = reader.GetString(reader.GetOrdinal("DataType")),
                    Precision = reader.IsDBNull(reader.GetOrdinal("Precision")) ? null : reader.GetByte(reader.GetOrdinal("Precision")),
                    Scale = reader.IsDBNull(reader.GetOrdinal("Scale")) ? null : reader.GetByte(reader.GetOrdinal("Scale")),
                    MaxLength = reader.IsDBNull(reader.GetOrdinal("MaxLength")) ? null : reader.GetInt16(reader.GetOrdinal("MaxLength")),
                    IsNullable = reader.GetBoolean(reader.GetOrdinal("IsNullable")),
                    IsIdentity = reader.GetBoolean(reader.GetOrdinal("IsIdentity")),
                    ComputedDefinition = reader.IsDBNull(reader.GetOrdinal("ComputedDefinition")) ? null : reader.GetString(reader.GetOrdinal("ComputedDefinition"))
                });
            }

            return tableDict
                .Select(kvp => new TableMetadata
                {
                    SchemaName = kvp.Key.Schema,
                    TableName = kvp.Key.Table,
                    Columns = kvp.Value
                })
                .OrderBy(t => t.SchemaName)
                .ThenBy(t => t.TableName)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect table and column metadata");
            return new List<TableMetadata>();
        }
    }

    private async Task<List<IndexMetadata>> CollectIndexesAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                s.name AS SchemaName,
                t.name AS TableName,
                i.name AS IndexName,
                i.type_desc AS IndexType,
                i.filter_definition AS FilterExpression,
                i.[fill_factor] AS [FillFactor],
                ic.key_ordinal AS KeyOrdinal,
                ic.is_included_column AS IsIncluded,
                c.name AS ColumnName
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND t.is_ms_shipped = 0
              AND i.name IS NOT NULL
            ORDER BY s.name, t.name, i.name, ic.key_ordinal";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var indexDict = new Dictionary<(string Schema, string Table, string Index), (string IndexType, string? Filter, int? FillFactor, List<string> KeyColumns, List<string> IncludedColumns)>();

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(reader.GetOrdinal("SchemaName"));
                var tableName = reader.GetString(reader.GetOrdinal("TableName"));
                var indexName = reader.GetString(reader.GetOrdinal("IndexName"));
                var key = (schemaName, tableName, indexName);

                if (!indexDict.TryGetValue(key, out var entry))
                {
                    var indexType = reader.GetString(reader.GetOrdinal("IndexType"));
                    var filterExpr = reader.IsDBNull(reader.GetOrdinal("FilterExpression")) ? null : reader.GetString(reader.GetOrdinal("FilterExpression"));
                    var fillFactorOrd = reader.GetOrdinal("FillFactor");
                    int? fillFactor = reader.IsDBNull(fillFactorOrd) ? null : Convert.ToInt32(reader.GetValue(fillFactorOrd));
                    if (fillFactor == 0) fillFactor = null; // SQL Server uses 0 to mean default (no explicit fill factor)

                    entry = (indexType, filterExpr, fillFactor, new List<string>(), new List<string>());
                    indexDict[key] = entry;
                }

                var columnName = reader.GetString(reader.GetOrdinal("ColumnName"));
                var isIncluded = reader.GetBoolean(reader.GetOrdinal("IsIncluded"));

                if (isIncluded)
                    entry.IncludedColumns.Add(columnName);
                else
                    entry.KeyColumns.Add(columnName);
            }

            return indexDict
                .Select(kvp => new IndexMetadata
                {
                    SchemaName = kvp.Key.Schema,
                    TableName = kvp.Key.Table,
                    IndexName = kvp.Key.Index,
                    IndexType = kvp.Value.IndexType,
                    KeyColumns = kvp.Value.KeyColumns,
                    IncludedColumns = kvp.Value.IncludedColumns,
                    FilterExpression = kvp.Value.Filter,
                    FillFactor = kvp.Value.FillFactor
                })
                .OrderBy(i => i.SchemaName)
                .ThenBy(i => i.TableName)
                .ThenBy(i => i.IndexName)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect index metadata");
            return new List<IndexMetadata>();
        }
    }

    private async Task<List<ConstraintMetadata>> CollectConstraintsAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            -- Primary Key and Unique constraints
            SELECT
                s.name AS SchemaName,
                OBJECT_NAME(kc.parent_object_id) AS TableName,
                kc.name AS ConstraintName,
                kc.type_desc AS ConstraintType,
                NULL AS Expression,
                ic.key_ordinal AS KeyOrdinal,
                c.name AS ColumnName
            FROM sys.key_constraints kc
            INNER JOIN sys.schemas s ON kc.schema_id = s.schema_id
            INNER JOIN sys.index_columns ic ON ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND kc.is_ms_shipped = 0
              AND ic.is_included_column = 0

            UNION ALL

            -- Check constraints
            SELECT
                s.name AS SchemaName,
                OBJECT_NAME(cc.parent_object_id) AS TableName,
                cc.name AS ConstraintName,
                'CHECK' AS ConstraintType,
                cc.definition AS Expression,
                0 AS KeyOrdinal,
                NULL AS ColumnName
            FROM sys.check_constraints cc
            INNER JOIN sys.schemas s ON cc.schema_id = s.schema_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND cc.is_ms_shipped = 0

            UNION ALL

            -- Default constraints
            SELECT
                s.name AS SchemaName,
                OBJECT_NAME(dc.parent_object_id) AS TableName,
                dc.name AS ConstraintName,
                'DEFAULT' AS ConstraintType,
                dc.definition AS Expression,
                0 AS KeyOrdinal,
                c.name AS ColumnName
            FROM sys.default_constraints dc
            INNER JOIN sys.schemas s ON dc.schema_id = s.schema_id
            INNER JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND dc.is_ms_shipped = 0

            ORDER BY SchemaName, TableName, ConstraintName, KeyOrdinal";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var constraintDict = new Dictionary<(string Schema, string Table, string Constraint), (string Type, string? Expression, List<string> Columns)>();

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(reader.GetOrdinal("SchemaName"));
                var tableName = reader.GetString(reader.GetOrdinal("TableName"));
                var constraintName = reader.GetString(reader.GetOrdinal("ConstraintName"));
                var key = (schemaName, tableName, constraintName);

                if (!constraintDict.TryGetValue(key, out var entry))
                {
                    var constraintType = reader.GetString(reader.GetOrdinal("ConstraintType"));
                    var expression = reader.IsDBNull(reader.GetOrdinal("Expression")) ? null : reader.GetString(reader.GetOrdinal("Expression"));
                    entry = (constraintType, expression, new List<string>());
                    constraintDict[key] = entry;
                }

                var columnNameOrd = reader.GetOrdinal("ColumnName");
                if (!reader.IsDBNull(columnNameOrd))
                {
                    var columnName = reader.GetString(columnNameOrd);
                    if (!entry.Columns.Contains(columnName))
                        entry.Columns.Add(columnName);
                }
            }

            return constraintDict
                .Select(kvp => new ConstraintMetadata
                {
                    SchemaName = kvp.Key.Schema,
                    TableName = kvp.Key.Table,
                    ConstraintName = kvp.Key.Constraint,
                    ConstraintType = kvp.Value.Type,
                    Expression = kvp.Value.Expression,
                    Columns = kvp.Value.Columns
                })
                .OrderBy(c => c.SchemaName)
                .ThenBy(c => c.TableName)
                .ThenBy(c => c.ConstraintName)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect constraint metadata");
            return new List<ConstraintMetadata>();
        }
    }

    private async Task<List<ForeignKeyMetadata>> CollectForeignKeysAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                s.name AS SchemaName,
                fk.name AS ConstraintName,
                OBJECT_NAME(fk.parent_object_id) AS ParentTable,
                pc.name AS ParentColumn,
                OBJECT_NAME(fk.referenced_object_id) AS ReferencedTable,
                rc.name AS ReferencedColumn,
                fk.update_referential_action_desc AS UpdateRule,
                fk.delete_referential_action_desc AS DeleteRule,
                fkc.constraint_column_id AS ColumnOrdinal
            FROM sys.foreign_keys fk
            INNER JOIN sys.schemas s ON fk.schema_id = s.schema_id
            INNER JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            INNER JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            INNER JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND fk.is_ms_shipped = 0
            ORDER BY s.name, fk.name, fkc.constraint_column_id";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var fkDict = new Dictionary<(string Schema, string Constraint), (string ParentTable, string ReferencedTable, string UpdateRule, string DeleteRule, List<string> ParentColumns, List<string> ReferencedColumns)>();

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(reader.GetOrdinal("SchemaName"));
                var constraintName = reader.GetString(reader.GetOrdinal("ConstraintName"));
                var key = (schemaName, constraintName);

                if (!fkDict.TryGetValue(key, out var entry))
                {
                    var parentTable = reader.GetString(reader.GetOrdinal("ParentTable"));
                    var referencedTable = reader.GetString(reader.GetOrdinal("ReferencedTable"));
                    var updateRule = reader.GetString(reader.GetOrdinal("UpdateRule"));
                    var deleteRule = reader.GetString(reader.GetOrdinal("DeleteRule"));
                    entry = (parentTable, referencedTable, updateRule, deleteRule, new List<string>(), new List<string>());
                    fkDict[key] = entry;
                }

                entry.ParentColumns.Add(reader.GetString(reader.GetOrdinal("ParentColumn")));
                entry.ReferencedColumns.Add(reader.GetString(reader.GetOrdinal("ReferencedColumn")));
            }

            return fkDict
                .Select(kvp => new ForeignKeyMetadata
                {
                    SchemaName = kvp.Key.Schema,
                    ConstraintName = kvp.Key.Constraint,
                    ParentTable = kvp.Value.ParentTable,
                    ParentColumns = kvp.Value.ParentColumns,
                    ReferencedTable = kvp.Value.ReferencedTable,
                    ReferencedColumns = kvp.Value.ReferencedColumns,
                    UpdateRule = kvp.Value.UpdateRule,
                    DeleteRule = kvp.Value.DeleteRule
                })
                .OrderBy(fk => fk.SchemaName)
                .ThenBy(fk => fk.ConstraintName)
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect foreign key metadata");
            return new List<ForeignKeyMetadata>();
        }
    }

    private async Task<List<ProgrammableObjectMetadata>> CollectProgrammableObjectsAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                s.name AS SchemaName,
                o.name AS ObjectName,
                o.type_desc AS ObjectType,
                sm.definition AS SourceText,
                CASE WHEN sm.definition IS NULL AND sm.object_id IS NOT NULL THEN 1 ELSE 0 END AS IsEncrypted
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            LEFT JOIN sys.sql_modules sm ON sm.object_id = o.object_id
            WHERE o.type IN ('V', 'TR', 'FN', 'IF', 'TF', 'P', 'AF')
              AND s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND o.is_ms_shipped = 0
            ORDER BY s.name, o.type_desc, o.name";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var results = new List<ProgrammableObjectMetadata>();

            while (await reader.ReadAsync(ct))
            {
                var schemaName = reader.GetString(reader.GetOrdinal("SchemaName"));
                var objectName = reader.GetString(reader.GetOrdinal("ObjectName"));
                var objectType = reader.GetString(reader.GetOrdinal("ObjectType"));
                var isEncrypted = reader.GetInt32(reader.GetOrdinal("IsEncrypted")) == 1;
                var sourceTextOrd = reader.GetOrdinal("SourceText");
                var sourceText = reader.IsDBNull(sourceTextOrd) ? null : reader.GetString(sourceTextOrd);

                string? inaccessibilityReason = null;
                if (sourceText == null && isEncrypted)
                {
                    inaccessibilityReason = "Object definition is encrypted";
                }
                else if (sourceText == null && !isEncrypted)
                {
                    // CLR or other non-SQL module — source is unavailable
                    inaccessibilityReason = "Object source text is not available (possibly CLR or external)";
                }

                results.Add(new ProgrammableObjectMetadata
                {
                    SchemaName = schemaName,
                    ObjectName = objectName,
                    ObjectType = objectType,
                    SourceText = sourceText,
                    IsEncrypted = isEncrypted,
                    InaccessibilityReason = inaccessibilityReason
                });
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect programmable object metadata");
            return new List<ProgrammableObjectMetadata>();
        }
    }

    private async Task<List<SynonymMetadata>> CollectSynonymsAsync(
        DbConnection connection, int timeoutSeconds, CancellationToken ct)
    {
        const string sql = @"
            SELECT
                s.name AS SchemaName,
                syn.name AS SynonymName,
                syn.base_object_name AS BaseObjectName
            FROM sys.synonyms syn
            INNER JOIN sys.schemas s ON syn.schema_id = s.schema_id
            WHERE s.name NOT IN ('sys', 'INFORMATION_SCHEMA')
              AND syn.is_ms_shipped = 0
            ORDER BY s.name, syn.name";

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);

            var results = new List<SynonymMetadata>();

            while (await reader.ReadAsync(ct))
            {
                results.Add(new SynonymMetadata
                {
                    SchemaName = reader.GetString(reader.GetOrdinal("SchemaName")),
                    SynonymName = reader.GetString(reader.GetOrdinal("SynonymName")),
                    BaseObjectName = reader.GetString(reader.GetOrdinal("BaseObjectName"))
                });
            }

            return results;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to collect synonym metadata");
            return new List<SynonymMetadata>();
        }
    }
}

