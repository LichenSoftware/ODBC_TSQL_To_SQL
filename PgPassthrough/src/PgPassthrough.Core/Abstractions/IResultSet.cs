using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Represents a tabular result set returned from query execution.
/// Modelled as a forward-only, async reader to handle large result sets
/// without materialising them entirely in memory.
/// </summary>
public interface IResultSet : IAsyncDisposable
{
    /// <summary>Column schema for the current result.</summary>
    IReadOnlyList<ColumnMetadata> Columns { get; }

    /// <summary>Advances to the next row. Returns false when exhausted.</summary>
    ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets the raw value at the current row for the given column index.</summary>
    object? GetValue(int columnIndex);

    /// <summary>
    /// Advances to the next result set (for multi-statement batches).
    /// Returns false if no more result sets exist.
    /// </summary>
    ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default);

    /// <summary>Total rows affected (for DML statements).</summary>
    long RowsAffected { get; }
}
