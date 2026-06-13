using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Collectors;

/// <summary>
/// Detects SQL Server-specific features across 13 categories by querying system views.
/// Reports detailed inventory per category, inaccessible features when permissions are
/// insufficient, and zero counts for categories with no instances.
/// </summary>
public sealed class FeatureDetector : IFeatureDetector
{
    private readonly ILogger<FeatureDetector> _logger;

    // Feature category constants
    public const string SqlClr = "SQL CLR";
    public const string ServiceBroker = "Service Broker";
    public const string AgentJobs = "Agent Jobs";
    public const string Cdc = "CDC";
    public const string ChangeTracking = "Change Tracking";
    public const string Replication = "Replication";
    public const string LinkedServers = "Linked Servers";
    public const string FullTextSearch = "Full Text Search";
    public const string FileStream = "FileStream";
    public const string XmlIndexes = "XML Indexes";
    public const string TemporalTables = "Temporal Tables";
    public const string MemoryOptimized = "Memory Optimized";
    public const string Partitioning = "Partitioning";

    internal static readonly string[] AllCategories =
    [
        SqlClr, ServiceBroker, AgentJobs, Cdc, ChangeTracking,
        Replication, LinkedServers, FullTextSearch, FileStream,
        XmlIndexes, TemporalTables, MemoryOptimized, Partitioning
    ];

    public FeatureDetector(ILogger<FeatureDetector> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<FeatureDetectionResult> DetectAsync(
        DbConnection connection,
        CollectionOptions options,
        CancellationToken ct)
    {
        var featureCounts = new Dictionary<string, int>();
        foreach (var category in AllCategories)
        {
            featureCounts[category] = 0;
        }

        var detailedInventory = new List<DetectedServerFeature>();
        var inaccessibleFeatures = new List<InaccessibleFeature>();
        var timeoutSeconds = (int)options.QueryTimeout.TotalSeconds;

        await DetectSqlClrAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectServiceBrokerAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectAgentJobsAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectCdcAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectChangeTrackingAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectReplicationAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectLinkedServersAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectFullTextSearchAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectFileStreamAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectXmlIndexesAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectTemporalTablesAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectMemoryOptimizedAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);
        await DetectPartitioningAsync(connection, timeoutSeconds, featureCounts, detailedInventory, inaccessibleFeatures, ct);

        _logger.LogInformation(
            "Feature detection complete. Found features in {Count} categories",
            featureCounts.Count(kv => kv.Value > 0));

        return new FeatureDetectionResult
        {
            FeatureCounts = featureCounts,
            DetailedInventory = detailedInventory,
            InaccessibleFeatures = inaccessibleFeatures
        };
    }

    private async Task DetectSqlClrAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT name, permission_set_desc
            FROM sys.assemblies
            WHERE is_user_defined = 1
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var name = reader.GetString(reader.GetOrdinal("name"));
                var permissionSet = reader.GetString(reader.GetOrdinal("permission_set_desc"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = SqlClr,
                    ObjectName = name,
                    Properties = new Dictionary<string, string>
                    {
                        ["permission_set"] = permissionSet
                    }
                });
            }

            featureCounts[SqlClr] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} SQL CLR assemblies", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access SQL CLR information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = SqlClr,
                RequiredPermission = "VIEW DEFINITION on assemblies"
            });
        }
    }

    private async Task DetectServiceBrokerAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'Queue' AS object_type, name, NULL AS service_name
            FROM sys.service_queues
            WHERE is_ms_shipped = 0
            UNION ALL
            SELECT 'Service' AS object_type, name, NULL AS service_name
            FROM sys.services
            WHERE is_ms_shipped = 0
            UNION ALL
            SELECT 'Contract' AS object_type, name, NULL AS service_name
            FROM sys.service_contracts
            WHERE is_ms_shipped = 0
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var name = reader.GetString(reader.GetOrdinal("name"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = ServiceBroker,
                    ObjectName = name,
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType
                    }
                });
            }

            featureCounts[ServiceBroker] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} Service Broker objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Service Broker information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = ServiceBroker,
                RequiredPermission = "VIEW DEFINITION on service broker objects"
            });
        }
    }

    private async Task DetectAgentJobsAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT j.name AS job_name, js.step_name, js.subsystem, js.database_name
            FROM msdb.dbo.sysjobs j
            JOIN msdb.dbo.sysjobsteps js ON j.job_id = js.job_id
            WHERE js.database_name = DB_NAME()
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var jobName = reader.GetString(reader.GetOrdinal("job_name"));
                var stepName = reader.GetString(reader.GetOrdinal("step_name"));
                var subsystem = reader.GetString(reader.GetOrdinal("subsystem"));
                var databaseName = reader.GetString(reader.GetOrdinal("database_name"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = AgentJobs,
                    ObjectName = jobName,
                    Properties = new Dictionary<string, string>
                    {
                        ["step_name"] = stepName,
                        ["subsystem"] = subsystem,
                        ["database_name"] = databaseName
                    }
                });
            }

            featureCounts[AgentJobs] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} Agent Job steps targeting current database", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access SQL Agent Jobs information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = AgentJobs,
                RequiredPermission = "SELECT on msdb.dbo.sysjobs and msdb.dbo.sysjobsteps"
            });
        }
    }

    private async Task DetectCdcAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT SCHEMA_NAME(schema_id) AS schema_name, name AS table_name
            FROM sys.tables
            WHERE is_tracked_by_cdc = 1
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var schemaName = reader.GetString(reader.GetOrdinal("schema_name"));
                var tableName = reader.GetString(reader.GetOrdinal("table_name"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = Cdc,
                    ObjectName = $"{schemaName}.{tableName}",
                    Properties = new Dictionary<string, string>
                    {
                        ["schema"] = schemaName,
                        ["table"] = tableName
                    }
                });
            }

            featureCounts[Cdc] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} tables with CDC enabled", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access CDC information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = Cdc,
                RequiredPermission = "VIEW DEFINITION on sys.tables"
            });
        }
    }

    private async Task DetectChangeTrackingAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT OBJECT_SCHEMA_NAME(object_id) AS schema_name,
                   OBJECT_NAME(object_id) AS table_name,
                   is_track_columns_updated_on
            FROM sys.change_tracking_tables
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var schemaName = reader.GetString(reader.GetOrdinal("schema_name"));
                var tableName = reader.GetString(reader.GetOrdinal("table_name"));
                var trackColumns = reader.GetBoolean(reader.GetOrdinal("is_track_columns_updated_on"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = ChangeTracking,
                    ObjectName = $"{schemaName}.{tableName}",
                    Properties = new Dictionary<string, string>
                    {
                        ["schema"] = schemaName,
                        ["table"] = tableName,
                        ["track_columns_updated"] = trackColumns.ToString()
                    }
                });
            }

            featureCounts[ChangeTracking] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} tables with Change Tracking", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Change Tracking information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = ChangeTracking,
                RequiredPermission = "VIEW CHANGE TRACKING"
            });
        }
    }

    private async Task DetectReplicationAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'Article' AS object_type, a.name, p.name AS publication_name
            FROM sys.articles a
            JOIN sys.publications p ON a.pubid = p.pubid
            UNION ALL
            SELECT 'Publication' AS object_type, name, name AS publication_name
            FROM sys.publications
            UNION ALL
            SELECT 'Subscription' AS object_type, 
                   s.dest_db AS name, 
                   p.name AS publication_name
            FROM sys.subscriptions s
            JOIN sys.publications p ON s.pubid = p.pubid
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var name = reader.GetString(reader.GetOrdinal("name"));
                var publicationName = reader.GetString(reader.GetOrdinal("publication_name"));
                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = Replication,
                    ObjectName = name,
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType,
                        ["publication"] = publicationName
                    }
                });
            }

            featureCounts[Replication] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} replication objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Replication information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = Replication,
                RequiredPermission = "VIEW DEFINITION on replication objects"
            });
        }
    }

    private async Task DetectLinkedServersAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT s.name AS server_name, s.product, s.provider, s.data_source,
                   m.definition AS module_definition, 
                   OBJECT_SCHEMA_NAME(m.object_id) AS schema_name,
                   OBJECT_NAME(m.object_id) AS object_name
            FROM sys.servers s
            CROSS APPLY (
                SELECT object_id, definition 
                FROM sys.sql_modules 
                WHERE definition LIKE '%' + s.name + '.%'
            ) m
            WHERE s.server_id != 0
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var serverName = reader.GetString(reader.GetOrdinal("server_name"));
                var product = reader.IsDBNull(reader.GetOrdinal("product")) ? "" : reader.GetString(reader.GetOrdinal("product"));
                var provider = reader.IsDBNull(reader.GetOrdinal("provider")) ? "" : reader.GetString(reader.GetOrdinal("provider"));
                var dataSource = reader.IsDBNull(reader.GetOrdinal("data_source")) ? "" : reader.GetString(reader.GetOrdinal("data_source"));
                var referencingObject = reader.IsDBNull(reader.GetOrdinal("object_name")) ? "" : reader.GetString(reader.GetOrdinal("object_name"));
                var referencingSchema = reader.IsDBNull(reader.GetOrdinal("schema_name")) ? "" : reader.GetString(reader.GetOrdinal("schema_name"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = LinkedServers,
                    ObjectName = serverName,
                    Properties = new Dictionary<string, string>
                    {
                        ["product"] = product,
                        ["provider"] = provider,
                        ["data_source"] = dataSource,
                        ["referencing_object"] = $"{referencingSchema}.{referencingObject}"
                    }
                });
            }

            featureCounts[LinkedServers] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} linked server references in modules", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Linked Servers information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = LinkedServers,
                RequiredPermission = "VIEW DEFINITION on sys.servers and sys.sql_modules"
            });
        }
    }

    private async Task DetectFullTextSearchAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'Catalog' AS object_type, fc.name, NULL AS table_name, NULL AS column_name
            FROM sys.fulltext_catalogs fc
            UNION ALL
            SELECT 'Index' AS object_type, 
                   OBJECT_NAME(fi.object_id) AS name,
                   OBJECT_SCHEMA_NAME(fi.object_id) + '.' + OBJECT_NAME(fi.object_id) AS table_name,
                   NULL AS column_name
            FROM sys.fulltext_indexes fi
            UNION ALL
            SELECT 'IndexColumn' AS object_type,
                   COL_NAME(fic.object_id, fic.column_id) AS name,
                   OBJECT_SCHEMA_NAME(fic.object_id) + '.' + OBJECT_NAME(fic.object_id) AS table_name,
                   COL_NAME(fic.object_id, fic.column_id) AS column_name
            FROM sys.fulltext_index_columns fic
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var name = reader.IsDBNull(reader.GetOrdinal("name")) ? "" : reader.GetString(reader.GetOrdinal("name"));
                var tableName = reader.IsDBNull(reader.GetOrdinal("table_name")) ? "" : reader.GetString(reader.GetOrdinal("table_name"));
                var columnName = reader.IsDBNull(reader.GetOrdinal("column_name")) ? "" : reader.GetString(reader.GetOrdinal("column_name"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = FullTextSearch,
                    ObjectName = name,
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType,
                        ["table_name"] = tableName,
                        ["column_name"] = columnName
                    }
                });
            }

            featureCounts[FullTextSearch] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} Full Text Search objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Full Text Search information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = FullTextSearch,
                RequiredPermission = "VIEW DEFINITION on fulltext catalogs and indexes"
            });
        }
    }

    private async Task DetectFileStreamAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'FileGroup' AS object_type, ds.name, NULL AS schema_name, NULL AS table_name
            FROM sys.data_spaces ds
            WHERE ds.type = 'FD'
            UNION ALL
            SELECT 'Table' AS object_type, 
                   t.name,
                   SCHEMA_NAME(t.schema_id) AS schema_name,
                   t.name AS table_name
            FROM sys.tables t
            WHERE t.filestream_data_space_id IS NOT NULL
              AND t.filestream_data_space_id != 0
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var name = reader.GetString(reader.GetOrdinal("name"));
                var schemaName = reader.IsDBNull(reader.GetOrdinal("schema_name")) ? "" : reader.GetString(reader.GetOrdinal("schema_name"));
                var tableName = reader.IsDBNull(reader.GetOrdinal("table_name")) ? "" : reader.GetString(reader.GetOrdinal("table_name"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = FileStream,
                    ObjectName = string.IsNullOrEmpty(schemaName) ? name : $"{schemaName}.{name}",
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType,
                        ["table_name"] = tableName ?? ""
                    }
                });
            }

            featureCounts[FileStream] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} FileStream objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access FileStream information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = FileStream,
                RequiredPermission = "VIEW DEFINITION on sys.data_spaces and sys.tables"
            });
        }
    }

    private async Task DetectXmlIndexesAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT xi.name AS index_name,
                   OBJECT_SCHEMA_NAME(xi.object_id) AS schema_name,
                   OBJECT_NAME(xi.object_id) AS table_name,
                   xi.xml_index_type_description
            FROM sys.xml_indexes xi
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var indexName = reader.GetString(reader.GetOrdinal("index_name"));
                var schemaName = reader.GetString(reader.GetOrdinal("schema_name"));
                var tableName = reader.GetString(reader.GetOrdinal("table_name"));
                var indexType = reader.GetString(reader.GetOrdinal("xml_index_type_description"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = XmlIndexes,
                    ObjectName = indexName,
                    Properties = new Dictionary<string, string>
                    {
                        ["schema"] = schemaName,
                        ["table"] = tableName,
                        ["xml_index_type"] = indexType
                    }
                });
            }

            featureCounts[XmlIndexes] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} XML indexes", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access XML Indexes information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = XmlIndexes,
                RequiredPermission = "VIEW DEFINITION on sys.xml_indexes"
            });
        }
    }

    private async Task DetectTemporalTablesAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT SCHEMA_NAME(t.schema_id) AS schema_name,
                   t.name AS table_name,
                   t.temporal_type_desc,
                   OBJECT_NAME(t.history_table_id) AS history_table_name
            FROM sys.tables t
            WHERE t.temporal_type != 0
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var schemaName = reader.GetString(reader.GetOrdinal("schema_name"));
                var tableName = reader.GetString(reader.GetOrdinal("table_name"));
                var temporalType = reader.GetString(reader.GetOrdinal("temporal_type_desc"));
                var historyTable = reader.IsDBNull(reader.GetOrdinal("history_table_name")) ? "" : reader.GetString(reader.GetOrdinal("history_table_name"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = TemporalTables,
                    ObjectName = $"{schemaName}.{tableName}",
                    Properties = new Dictionary<string, string>
                    {
                        ["schema"] = schemaName,
                        ["table"] = tableName,
                        ["temporal_type"] = temporalType,
                        ["history_table"] = historyTable
                    }
                });
            }

            featureCounts[TemporalTables] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} temporal tables", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Temporal Tables information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = TemporalTables,
                RequiredPermission = "VIEW DEFINITION on sys.tables"
            });
        }
    }

    private async Task DetectMemoryOptimizedAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'Table' AS object_type,
                   SCHEMA_NAME(t.schema_id) AS schema_name,
                   t.name AS object_name,
                   t.durability_desc
            FROM sys.tables t
            WHERE t.is_memory_optimized = 1
            UNION ALL
            SELECT 'NativelyCompiledProc' AS object_type,
                   SCHEMA_NAME(o.schema_id) AS schema_name,
                   o.name AS object_name,
                   NULL AS durability_desc
            FROM sys.sql_modules m
            JOIN sys.objects o ON m.object_id = o.object_id
            WHERE m.uses_native_compilation = 1
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var schemaName = reader.GetString(reader.GetOrdinal("schema_name"));
                var objectName = reader.GetString(reader.GetOrdinal("object_name"));
                var durability = reader.IsDBNull(reader.GetOrdinal("durability_desc")) ? "" : reader.GetString(reader.GetOrdinal("durability_desc"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = MemoryOptimized,
                    ObjectName = $"{schemaName}.{objectName}",
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType,
                        ["schema"] = schemaName,
                        ["durability"] = durability
                    }
                });
            }

            featureCounts[MemoryOptimized] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} memory-optimized objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Memory Optimized information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = MemoryOptimized,
                RequiredPermission = "VIEW DEFINITION on sys.tables and sys.sql_modules"
            });
        }
    }

    private async Task DetectPartitioningAsync(
        DbConnection connection, int timeoutSeconds,
        Dictionary<string, int> featureCounts,
        List<DetectedServerFeature> inventory,
        List<InaccessibleFeature> inaccessible,
        CancellationToken ct)
    {
        const string sql = """
            SELECT 'PartitionScheme' AS object_type, ps.name, pf.name AS function_name
            FROM sys.partition_schemes ps
            JOIN sys.partition_functions pf ON ps.function_id = pf.function_id
            UNION ALL
            SELECT 'PartitionFunction' AS object_type, pf.name, pf.name AS function_name
            FROM sys.partition_functions pf
            """;

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.CommandTimeout = timeoutSeconds;

            using var reader = await cmd.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                count++;
                var objectType = reader.GetString(reader.GetOrdinal("object_type"));
                var name = reader.GetString(reader.GetOrdinal("name"));
                var functionName = reader.GetString(reader.GetOrdinal("function_name"));

                inventory.Add(new DetectedServerFeature
                {
                    FeatureCategory = Partitioning,
                    ObjectName = name,
                    Properties = new Dictionary<string, string>
                    {
                        ["object_type"] = objectType,
                        ["partition_function"] = functionName
                    }
                });
            }

            featureCounts[Partitioning] = count;
            if (count > 0)
                _logger.LogInformation("Detected {Count} partitioning objects", count);
        }
        catch (SqlException ex)
        {
            _logger.LogWarning(ex, "Cannot access Partitioning information due to insufficient permissions");
            inaccessible.Add(new InaccessibleFeature
            {
                FeatureCategory = Partitioning,
                RequiredPermission = "VIEW DEFINITION on sys.partition_schemes and sys.partition_functions"
            });
        }
    }
}
