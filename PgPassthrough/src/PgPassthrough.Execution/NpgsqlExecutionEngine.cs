using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Execution;

/// <summary>
/// Executes translated SQL against a PostgreSQL backend via Npgsql.
/// Uses NpgsqlDataSource for built-in connection pooling.
/// 
/// Parameter mapping: T-SQL @name parameters are rewritten to positional $1, $2, ...
/// by the execution layer before sending to PostgreSQL.
/// </summary>
public sealed class NpgsqlExecutionEngine : IExecutionEngine
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;
    private readonly ILogger<NpgsqlExecutionEngine> _logger;

    public NpgsqlExecutionEngine(NpgsqlDataSource dataSource, BackendConnectionOptions options, ILogger<NpgsqlExecutionEngine> logger)
    {
        _dataSource = dataSource;
        _commandTimeoutSeconds = options.CommandTimeoutSeconds;
        _logger = logger;
    }

    public async Task<ExecutionResult> ExecuteQueryAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        NpgsqlConnection? conn = null;
        try
        {
            var (sql, paramNames) = RewriteParameters(request.Sql);
            conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = (int)(request.CommandTimeout?.TotalSeconds ?? _commandTimeoutSeconds);

            BindParameters(cmd, paramNames, request.Parameters);

            var reader = await cmd.ExecuteReaderAsync(
                System.Data.CommandBehavior.CloseConnection, cancellationToken).ConfigureAwait(false);
            var resultSet = new NpgsqlResultSet(reader);
            await resultSet.InitializeAsync(cancellationToken).ConfigureAwait(false);

            return new ExecutionResult
            {
                IsSuccess = true,
                ResultSet = resultSet
            };
        }
        catch (PostgresException ex)
        {
            conn?.Dispose();
            _logger.LogWarning("PostgreSQL error executing query: {SqlState} {Message}", ex.SqlState, ex.Message);
            return new ExecutionResult
            {
                IsSuccess = false,
                Error = new BackendError
                {
                    Message = ex.MessageText,
                    SqlState = ex.SqlState,
                    Detail = ex.Detail,
                    Hint = ex.Hint,
                    Position = ex.Position > 0 ? ex.Position.ToString() : null
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            conn?.Dispose();
            _logger.LogError(ex, "Unexpected error executing query");
            return new ExecutionResult
            {
                IsSuccess = false,
                Error = new BackendError { Message = ex.Message }
            };
        }
    }

    public async Task<ExecutionResult> ExecuteNonQueryAsync(ExecutionRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var (sql, paramNames) = RewriteParameters(request.Sql);
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.CommandTimeout = (int)(request.CommandTimeout?.TotalSeconds ?? _commandTimeoutSeconds);

            BindParameters(cmd, paramNames, request.Parameters);

            int rowsAffected = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return new ExecutionResult
            {
                IsSuccess = true,
                RowsAffected = rowsAffected < 0 ? 0 : rowsAffected
            };
        }
        catch (PostgresException ex)
        {
            _logger.LogWarning("PostgreSQL error executing non-query: {SqlState} {Message}", ex.SqlState, ex.Message);
            return new ExecutionResult
            {
                IsSuccess = false,
                Error = new BackendError
                {
                    Message = ex.MessageText,
                    SqlState = ex.SqlState,
                    Detail = ex.Detail,
                    Hint = ex.Hint,
                    Position = ex.Position > 0 ? ex.Position.ToString() : null
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error executing non-query");
            return new ExecutionResult
            {
                IsSuccess = false,
                Error = new BackendError { Message = ex.Message }
            };
        }
    }

    public async Task<ITransactionHandle> BeginTransactionAsync(TransactionOptions options, CancellationToken cancellationToken = default)
    {
        var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var isolationLevel = MapIsolationLevel(options.IsolationLevel);
        var tx = await conn.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
        return new NpgsqlTransactionHandle(conn, tx);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask; // DataSource is owned by DI

    // -------------------------------------------------------------------------
    // Parameter rewriting: @name → $1, $2, ...
    // -------------------------------------------------------------------------

    private static readonly Regex ParamRegex = new(@"@(\w+)", RegexOptions.Compiled);

    /// <summary>
    /// Rewrites @name parameters to positional $N parameters.
    /// Returns the rewritten SQL and the ordered list of parameter names (without @).
    /// </summary>
    internal static (string Sql, List<string> ParamNames) RewriteParameters(string sql)
    {
        var paramNames = new List<string>();
        var paramIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string rewritten = ParamRegex.Replace(sql, match =>
        {
            string name = match.Groups[1].Value;
            if (!paramIndex.TryGetValue(name, out int idx))
            {
                idx = paramNames.Count + 1;
                paramIndex[name] = idx;
                paramNames.Add(name);
            }
            return $"${idx}";
        });

        return (rewritten, paramNames);
    }

    private static void BindParameters(NpgsqlCommand cmd, List<string> paramNames, IReadOnlyList<QueryParameter> parameters)
    {
        foreach (var name in paramNames)
        {
            var param = parameters.FirstOrDefault(p =>
                p.Name.TrimStart('@').Equals(name, StringComparison.OrdinalIgnoreCase));

            var npgParam = new NpgsqlParameter();
            npgParam.Value = param?.Value ?? DBNull.Value;
            cmd.Parameters.Add(npgParam);
        }
    }

    private static System.Data.IsolationLevel MapIsolationLevel(TransactionIsolationLevel level) => level switch
    {
        TransactionIsolationLevel.ReadUncommitted => System.Data.IsolationLevel.ReadUncommitted,
        TransactionIsolationLevel.ReadCommitted => System.Data.IsolationLevel.ReadCommitted,
        TransactionIsolationLevel.RepeatableRead => System.Data.IsolationLevel.RepeatableRead,
        TransactionIsolationLevel.Serializable => System.Data.IsolationLevel.Serializable,
        TransactionIsolationLevel.Snapshot => System.Data.IsolationLevel.RepeatableRead, // PG doesn't have snapshot
        _ => System.Data.IsolationLevel.ReadCommitted
    };
}
