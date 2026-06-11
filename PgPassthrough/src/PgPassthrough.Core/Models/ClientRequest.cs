namespace PgPassthrough.Core.Models;

/// <summary>
/// Discriminated union of all request types a client session can issue.
/// </summary>
public abstract class ClientRequest
{
    /// <summary>Unique identifier for tracing this request through logs.</summary>
    public Guid RequestId { get; } = Guid.NewGuid();

    /// <summary>The session that originated this request.</summary>
    public required SessionContext Session { get; init; }
}

/// <summary>A plain SQL batch request (TDS SQLBatch message).</summary>
public sealed class SqlBatchRequest : ClientRequest
{
    /// <summary>Raw T-SQL text as received from the client.</summary>
    public required string SqlText { get; init; }
}

/// <summary>
/// A stored procedure or system procedure RPC request (TDS RPC message).
/// Covers both user sprocs and special system calls like sp_executesql.
/// </summary>
public sealed class RpcRequest : ClientRequest
{
    public required string ProcedureName { get; init; }
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];
}

/// <summary>
/// Transaction control request: BEGIN, COMMIT, ROLLBACK, SAVEPOINT.
/// </summary>
public sealed class TransactionRequest : ClientRequest
{
    public required TransactionAction Action { get; init; }
    public string? SavepointName { get; init; }
    public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;
    public string? TransactionName { get; init; }
}

public enum TransactionAction
{
    Begin,
    Commit,
    Rollback,
    Savepoint,
    RollbackToSavepoint
}

public enum TransactionIsolationLevel
{
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
    Snapshot        // SQL Server-specific; maps to REPEATABLE READ in PG
}
