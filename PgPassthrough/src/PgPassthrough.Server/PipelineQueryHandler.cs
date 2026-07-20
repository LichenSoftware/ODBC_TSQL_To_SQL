using Microsoft.Extensions.Logging;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Translation;

namespace PgPassthrough.Server;

/// <summary>
/// Production query handler that implements the full pipeline:
///   1. Translate T-SQL → PostgreSQL SQL
///   2. Execute against PostgreSQL backend
///   3. Stream results back to the client via IResponseWriter
/// 
/// Handles SQL batches, RPC calls (sp_executesql, etc.), and transaction control.
/// </summary>
internal sealed class PipelineQueryHandler : IQueryHandler
{
    private readonly ISqlTranslator _translator;
    private readonly IExecutionEngine _executionEngine;
    private readonly ProcedureMappingStore _mappingStore;
    private readonly ILogger<PipelineQueryHandler> _logger;

    public PipelineQueryHandler(
        ISqlTranslator translator,
        IExecutionEngine executionEngine,
        ProcedureMappingStore mappingStore,
        ILogger<PipelineQueryHandler> logger)
    {
        _translator = translator;
        _executionEngine = executionEngine;
        _mappingStore = mappingStore;
        _logger = logger;
    }

    public async Task HandleAsync(
        ClientRequest request,
        IResponseWriter responseWriter,
        CancellationToken cancellationToken = default)
    {
        switch (request)
        {
            case SqlBatchRequest batch:
                await HandleSqlBatchAsync(batch, responseWriter, cancellationToken).ConfigureAwait(false);
                break;
            case RpcRequest rpc:
                await HandleRpcAsync(rpc, responseWriter, cancellationToken).ConfigureAwait(false);
                break;
            case TransactionRequest txn:
                await HandleTransactionAsync(txn, responseWriter, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    // -------------------------------------------------------------------------
    // SQL Batch
    // -------------------------------------------------------------------------

    private async Task HandleSqlBatchAsync(
        SqlBatchRequest batch,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        string sql = batch.SqlText.TrimStart();

        // SET statements and session-level commands don't go to the backend
        if (IsSessionCommand(sql))
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, ct).ConfigureAwait(false);
            return;
        }

        // Translate
        var context = new TranslationContext
        {
            DatabaseName = batch.Session.DatabaseName
        };
        var translation = _translator.Translate(batch.SqlText, context);

        // Forward translation warnings as INFO tokens
        foreach (var warning in translation.Warnings)
        {
            await responseWriter.WriteInfoAsync(new ServerMessage
            {
                Message = $"[Translation] {warning.Code}: {warning.Message}",
                Number = 0,
                Severity = 10
            }, ct).ConfigureAwait(false);
        }

        // If translation produced empty SQL (e.g., SET NOCOUNT was stripped to a comment)
        if (string.IsNullOrWhiteSpace(translation.TranslatedSql) ||
            translation.TranslatedSql.TrimStart().StartsWith("--"))
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, ct).ConfigureAwait(false);
            return;
        }

        // Execute
        var execRequest = new ExecutionRequest
        {
            Sql = translation.TranslatedSql,
            RequestId = batch.RequestId
        };

        _logger.LogDebug("Translated SQL [{Type}]: {Sql}", translation.StatementType, translation.TranslatedSql);

        // Use query execution (which streams result sets) for any statement that may
        // return rows. Only known DML/DDL statements use the non-query path.
        if (translation.StatementType is StatementType.Insert
            or StatementType.Update
            or StatementType.Delete
            or StatementType.Ddl
            or StatementType.Transaction
            or StatementType.SetOption
            or StatementType.Use)
        {
            await ExecuteNonQueryAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteQueryAndStreamAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // RPC (sp_executesql, user sprocs)
    // -------------------------------------------------------------------------

    private async Task HandleRpcAsync(
        RpcRequest rpc,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        // sp_executesql: extract the SQL and parameters
        if (rpc.ProcedureName.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
        {
            await HandleSpExecuteSqlAsync(rpc, responseWriter, ct).ConfigureAwait(false);
            return;
        }

        // sp_reset_connection: just acknowledge
        if (rpc.ProcedureName.Equals("sp_reset_connection", StringComparison.OrdinalIgnoreCase))
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, ct).ConfigureAwait(false);
            return;
        }

        // Other RPCs: look up in procedure mapping store first
        var mapping = _mappingStore.Lookup(null, rpc.ProcedureName)
                   ?? _mappingStore.Lookup("dbo", rpc.ProcedureName);

        string callSql;
        IReadOnlyList<QueryParameter> execParams;

        if (mapping != null)
        {
            // Build the correct PostgreSQL call based on the mapping
            // Use @p1, @p2, ... named params so the execution engine can rewrite them to $1, $2, ...
            var rpcParams = rpc.Parameters.Where(p => !p.IsOutput).ToList();

            // Build param placeholders with type casts from the mapping
            var placeholders = new List<string>();
            for (int i = 0; i < rpcParams.Count; i++)
            {
                var placeholder = $"@p{i + 1}";
                // Add type cast if we know the target type from the mapping
                if (i < mapping.Parameters.Count)
                {
                    var pgType = mapping.Parameters[i].PostgresType?.ToUpperInvariant() ?? "";
                    var cast = pgType switch
                    {
                        "INT" or "INTEGER" => "::int",
                        "BIGINT" => "::bigint",
                        "SMALLINT" => "::smallint",
                        "TEXT" => "::text",
                        "BOOLEAN" or "BOOL" => "::boolean",
                        "NUMERIC" or "DECIMAL" => "::numeric",
                        _ when pgType.StartsWith("VARCHAR") => "::text",
                        _ when pgType.StartsWith("NUMERIC") => "::numeric",
                        _ => ""
                    };
                    placeholder += cast;
                }
                placeholders.Add(placeholder);
            }
            var paramPlaceholders = string.Join(", ", placeholders);

            // Rename params to positional names for the execution engine
            execParams = rpcParams.Select((p, i) => new QueryParameter
            {
                Name = $"@p{i + 1}",
                Value = p.Value,
                TsqlType = p.TsqlType,
                IsOutput = false
            }).ToList<QueryParameter>();

            if (mapping.CallStyle == "SELECT")
            {
                callSql = $"SELECT * FROM {mapping.PostgresSchema}.{mapping.PostgresName}({paramPlaceholders})";
            }
            else
            {
                callSql = $"CALL {mapping.PostgresSchema}.{mapping.PostgresName}({paramPlaceholders})";
            }

            _logger.LogDebug("Mapped RPC {Proc} → {Sql}", rpc.ProcedureName, callSql);
        }
        else
        {
            // No mapping found — try as a function call with public schema
            var rpcParams = rpc.Parameters.Where(p => !p.IsOutput).ToList();
            var paramPlaceholders = rpcParams.Count > 0
                ? string.Join(", ", rpcParams.Select((_, i) => $"@p{i + 1}"))
                : "";

            execParams = rpcParams.Select((p, i) => new QueryParameter
            {
                Name = $"@p{i + 1}",
                Value = p.Value,
                TsqlType = p.TsqlType,
                IsOutput = false
            }).ToList<QueryParameter>();

            callSql = $"SELECT * FROM public.{rpc.ProcedureName}({paramPlaceholders})";
        }

        var execRequest = new ExecutionRequest
        {
            Sql = callSql,
            Parameters = execParams,
            RequestId = rpc.RequestId
        };

        await ExecuteQueryAndStreamAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
    }

    private async Task HandleSpExecuteSqlAsync(
        RpcRequest rpc,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        // First parameter is the SQL text
        string? sqlText = rpc.Parameters.FirstOrDefault()?.Value?.ToString();
        if (string.IsNullOrEmpty(sqlText))
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, ct).ConfigureAwait(false);
            return;
        }

        // Skip the first two params (SQL text + param definitions) and pass the rest
        var dataParams = rpc.Parameters.Skip(2).ToList();

        var context = new TranslationContext
        {
            DatabaseName = rpc.Session.DatabaseName,
            Parameters = dataParams
        };
        var translation = _translator.Translate(sqlText, context);

        var execRequest = new ExecutionRequest
        {
            Sql = translation.TranslatedSql,
            Parameters = dataParams,
            RequestId = rpc.RequestId
        };

        if (translation.StatementType is StatementType.Insert
            or StatementType.Update
            or StatementType.Delete
            or StatementType.Ddl
            or StatementType.Transaction
            or StatementType.SetOption
            or StatementType.Use)
        {
            await ExecuteNonQueryAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteQueryAndStreamAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
        }
    }

    // -------------------------------------------------------------------------
    // Transaction control
    // -------------------------------------------------------------------------

    private async Task HandleTransactionAsync(
        TransactionRequest txn,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        // For now, translate to SQL and execute directly
        string sql = txn.Action switch
        {
            TransactionAction.Begin => "BEGIN",
            TransactionAction.Commit => "COMMIT",
            TransactionAction.Rollback => "ROLLBACK",
            TransactionAction.Savepoint => $"SAVEPOINT {txn.SavepointName ?? "sp1"}",
            TransactionAction.RollbackToSavepoint => $"ROLLBACK TO SAVEPOINT {txn.SavepointName ?? "sp1"}",
            _ => "BEGIN"
        };

        var execRequest = new ExecutionRequest { Sql = sql, RequestId = txn.RequestId };
        await ExecuteNonQueryAsync(execRequest, responseWriter, ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Execution helpers
    // -------------------------------------------------------------------------

    private async Task ExecuteQueryAndStreamAsync(
        ExecutionRequest request,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        await using var result = await _executionEngine.ExecuteQueryAsync(request, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await WriteBackendErrorAsync(result.Error!, responseWriter, ct).ConfigureAwait(false);
            return;
        }

        if (result.ResultSet == null)
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, ct).ConfigureAwait(false);
            return;
        }

        await using var rs = result.ResultSet;

        // Write column metadata
        await responseWriter.WriteColumnsAsync(rs.Columns, ct).ConfigureAwait(false);

        // Stream rows
        long rowCount = 0;
        while (await rs.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new object?[rs.Columns.Count];
            for (int i = 0; i < rs.Columns.Count; i++)
            {
                values[i] = rs.GetValue(i);
            }
            await responseWriter.WriteRowAsync(values, ct).ConfigureAwait(false);
            rowCount++;
        }

        await responseWriter.WriteDoneAsync(DoneStatus.Final | DoneStatus.Count, rowCount, ct).ConfigureAwait(false);
    }

    private async Task ExecuteNonQueryAsync(
        ExecutionRequest request,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        await using var result = await _executionEngine.ExecuteNonQueryAsync(request, ct).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            await WriteBackendErrorAsync(result.Error!, responseWriter, ct).ConfigureAwait(false);
            return;
        }

        var status = result.RowsAffected > 0
            ? DoneStatus.Final | DoneStatus.Count
            : DoneStatus.Final;
        await responseWriter.WriteDoneAsync(status, result.RowsAffected, ct).ConfigureAwait(false);
    }

    private static async Task WriteBackendErrorAsync(
        BackendError error,
        IResponseWriter responseWriter,
        CancellationToken ct)
    {
        string message = error.Message;
        if (!string.IsNullOrEmpty(error.Detail))
            message += $"\nDetail: {error.Detail}";
        if (!string.IsNullOrEmpty(error.Hint))
            message += $"\nHint: {error.Hint}";

        await responseWriter.WriteErrorAsync(new ServerError
        {
            Message = message,
            Number = MapSqlStateToErrorNumber(error.SqlState),
            Severity = 16,
            State = 1
        }, ct).ConfigureAwait(false);
    }

    private static int MapSqlStateToErrorNumber(string? sqlState)
    {
        // Map common PostgreSQL SQLSTATE codes to SQL Server error numbers
        return sqlState switch
        {
            "42P01" => 208,    // undefined_table → Invalid object name
            "42703" => 207,    // undefined_column → Invalid column name
            "23505" => 2627,   // unique_violation → Violation of UNIQUE KEY constraint
            "23503" => 547,    // foreign_key_violation → FK constraint
            "23502" => 515,    // not_null_violation → Cannot insert NULL
            "42601" => 102,    // syntax_error → Incorrect syntax
            "42P07" => 2714,   // duplicate_table → Object already exists
            "57014" => 0,      // query_canceled
            _ => 50000         // Generic user error
        };
    }

    // -------------------------------------------------------------------------
    // Session command detection
    // -------------------------------------------------------------------------

    private static bool IsSessionCommand(string sql)
    {
        return sql.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("USE ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("PRINT ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("GO", StringComparison.OrdinalIgnoreCase);
    }
}
