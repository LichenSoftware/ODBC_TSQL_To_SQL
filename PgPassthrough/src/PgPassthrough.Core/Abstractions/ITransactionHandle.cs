namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Represents an active database transaction. Scoped to a single client session.
/// </summary>
public interface ITransactionHandle : IAsyncDisposable
{
    /// <summary>Opaque transaction identifier for logging.</summary>
    Guid TransactionId { get; }

    /// <summary>Whether this transaction is still active (not committed or rolled back).</summary>
    bool IsActive { get; }

    Task CommitAsync(CancellationToken cancellationToken = default);
    Task RollbackAsync(CancellationToken cancellationToken = default);
    Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default);
    Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default);
}
