using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Tds.Tokens;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Implements <see cref="IResponseWriter"/> over a TDS packet stream.
/// 
/// Wraps a <see cref="TdsPacketWriter"/> and delegates to <see cref="TdsTokenWriter"/>
/// to serialise individual tokens. The response is accumulated in the packet writer's
/// buffer and flushed to the network on <see cref="FlushAsync"/>.
/// 
/// This class is NOT thread-safe. One instance per session per response.
/// </summary>
internal sealed class TdsResponseWriter : IResponseWriter
{
    private readonly TdsPacketWriter _packetWriter;
    private readonly TdsTokenWriter _tokenWriter;
    private IReadOnlyList<ColumnMetadata>? _currentColumns;

    public TdsResponseWriter(TdsPacketWriter packetWriter)
    {
        _packetWriter = packetWriter;
        _tokenWriter = new TdsTokenWriter(packetWriter);
    }

    public void BeginTabularResult()
    {
        _packetWriter.BeginMessage(TdsPacketType.TabularResult);
    }

    /// <inheritdoc/>
    public ValueTask WriteColumnsAsync(IReadOnlyList<ColumnMetadata> columns, CancellationToken ct = default)
    {
        _currentColumns = columns;
        _tokenWriter.WriteColMetadata(columns);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WriteRowAsync(object?[] values, CancellationToken ct = default)
    {
        if (_currentColumns == null)
            throw new InvalidOperationException("WriteColumnsAsync must be called before WriteRowAsync.");
        _tokenWriter.WriteRow(_currentColumns, values);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WriteDoneAsync(DoneStatus status, long rowCount, CancellationToken ct = default)
    {
        _tokenWriter.WriteDone(status, 0xC1, rowCount); // 0xC1 = SELECT
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WriteErrorAsync(ServerError error, CancellationToken ct = default)
    {
        _tokenWriter.WriteError(error);
        // An error is always followed by a DONE(Error) token
        _tokenWriter.WriteDone(DoneStatus.Final | DoneStatus.Error, 0, 0);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public ValueTask WriteInfoAsync(ServerMessage message, CancellationToken ct = default)
    {
        _tokenWriter.WriteInfo(message);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask FlushAsync(CancellationToken ct = default)
    {
        await _packetWriter.EndMessageAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the LOGINACK + ENVCHANGE tokens for a successful login response.
    /// </summary>
    public async ValueTask WriteLoginResponseAsync(string databaseName, uint tdsVersion, int packetSize, CancellationToken ct = default)
    {
        _packetWriter.BeginMessage(TdsPacketType.TabularResult);
        _tokenWriter.WriteLoginAck(tdsVersion);
        _tokenWriter.WriteEnvChangeDatabase(databaseName, "master");
        _tokenWriter.WriteEnvChangePacketSize(packetSize, TdsProtocol.DefaultPacketSize);
        _tokenWriter.WriteDone(DoneStatus.Final, 0, 0);
        await _packetWriter.EndMessageAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a standalone error response (not preceded by column metadata).
    /// Begins and ends its own message.
    /// </summary>
    public async ValueTask WriteLoginErrorAsync(ServerError error, CancellationToken ct = default)
    {
        _packetWriter.BeginMessage(TdsPacketType.TabularResult);
        _tokenWriter.WriteError(error);
        _tokenWriter.WriteDone(DoneStatus.Final | DoneStatus.Error, 0, 0);
        await _packetWriter.EndMessageAsync(ct).ConfigureAwait(false);
    }
}
