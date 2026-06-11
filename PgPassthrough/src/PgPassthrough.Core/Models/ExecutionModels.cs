using PgPassthrough.Core.Abstractions;

namespace PgPassthrough.Core.Models;

/// <summary>
/// A request sent to the execution engine after translation.
/// </summary>
public sealed class ExecutionRequest
{
    /// <summary>The translated SQL to execute against the backend.</summary>
    public required string Sql { get; init; }

    /// <summary>Bound parameter values.</summary>
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    /// <summary>Active transaction to enlist this command in, if any.</summary>
    public ITransactionHandle? Transaction { get; init; }

    /// <summary>Per-command timeout override. Null uses the engine default.</summary>
    public TimeSpan? CommandTimeout { get; init; }

    /// <summary>Tracing correlation id.</summary>
    public Guid RequestId { get; init; } = Guid.NewGuid();
}

/// <summary>
/// The result of executing a SQL command against the backend.
/// </summary>
public sealed class ExecutionResult : IAsyncDisposable
{
    public bool IsSuccess { get; init; }

    /// <summary>Set when IsSuccess is false.</summary>
    public BackendError? Error { get; init; }

    /// <summary>Result set reader. Non-null for SELECT statements.</summary>
    public IResultSet? ResultSet { get; init; }

    /// <summary>Rows affected for DML statements.</summary>
    public long RowsAffected { get; init; }

    /// <summary>
    /// RETURNING clause value or equivalent for identity retrieval.
    /// Populated when the translated query included identity capture.
    /// </summary>
    public long? GeneratedIdentity { get; init; }

    public ValueTask DisposeAsync()
    {
        return ResultSet?.DisposeAsync() ?? ValueTask.CompletedTask;
    }
}

/// <summary>
/// An error returned from the backend database.
/// </summary>
public sealed class BackendError
{
    public required string Message { get; init; }
    public string? SqlState { get; init; }
    public string? Detail { get; init; }
    public string? Hint { get; init; }
    public string? Position { get; init; }
}

/// <summary>
/// Options for transaction isolation.
/// </summary>
public sealed class TransactionOptions
{
    public TransactionIsolationLevel IsolationLevel { get; init; } = TransactionIsolationLevel.ReadCommitted;
}
