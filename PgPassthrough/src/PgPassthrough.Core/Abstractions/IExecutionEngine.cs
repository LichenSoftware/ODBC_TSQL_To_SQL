using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Executes translated SQL against the backend database engine.
/// </summary>
public interface IExecutionEngine : IAsyncDisposable
{
    /// <summary>
    /// Executes a query and returns a result set.
    /// </summary>
    Task<ExecutionResult> ExecuteQueryAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a non-query statement (INSERT, UPDATE, DELETE, DDL).
    /// Returns rows affected.
    /// </summary>
    Task<ExecutionResult> ExecuteNonQueryAsync(
        ExecutionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Begins a new transaction and returns its handle.</summary>
    Task<ITransactionHandle> BeginTransactionAsync(
        TransactionOptions options,
        CancellationToken cancellationToken = default);
}
