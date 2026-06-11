using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Protocol-agnostic interface for writing response data back to the client.
/// The TDS layer provides a concrete implementation that serialises tokens
/// into the TDS packet stream.
/// </summary>
public interface IResponseWriter
{
    /// <summary>Writes column metadata (schema row descriptor).</summary>
    ValueTask WriteColumnsAsync(IReadOnlyList<ColumnMetadata> columns, CancellationToken ct = default);

    /// <summary>Writes a single data row.</summary>
    ValueTask WriteRowAsync(object?[] values, CancellationToken ct = default);

    /// <summary>Writes a DONE token indicating statement completion.</summary>
    ValueTask WriteDoneAsync(DoneStatus status, long rowCount, CancellationToken ct = default);

    /// <summary>Writes an error back to the client.</summary>
    ValueTask WriteErrorAsync(ServerError error, CancellationToken ct = default);

    /// <summary>Writes an informational message.</summary>
    ValueTask WriteInfoAsync(ServerMessage message, CancellationToken ct = default);

    /// <summary>Flushes any buffered data to the underlying transport.</summary>
    ValueTask FlushAsync(CancellationToken ct = default);
}
