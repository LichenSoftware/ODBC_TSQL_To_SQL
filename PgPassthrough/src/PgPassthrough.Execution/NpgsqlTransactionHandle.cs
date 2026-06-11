using Npgsql;
using PgPassthrough.Core.Abstractions;

namespace PgPassthrough.Execution;

/// <summary>
/// Wraps an Npgsql transaction as an <see cref="ITransactionHandle"/>.
/// Owns both the connection and transaction lifetime.
/// </summary>
internal sealed class NpgsqlTransactionHandle : ITransactionHandle
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private bool _completed;

    public Guid TransactionId { get; } = Guid.NewGuid();
    public bool IsActive => !_completed;

    public NpgsqlTransactionHandle(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_completed) return;
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        _completed = true;
    }

    public async Task CreateSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        await _transaction.SaveAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackToSavepointAsync(string name, CancellationToken cancellationToken = default)
    {
        await _transaction.RollbackAsync(name, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_completed)
        {
            try { await _transaction.RollbackAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
