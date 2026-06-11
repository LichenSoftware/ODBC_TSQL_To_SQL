using System.Buffers.Binary;
using System.Text;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Writes TDS response data to the underlying network stream.
/// Buffers data and flushes in correctly-framed TDS packets.
/// 
/// Usage pattern:
///   writer.BeginMessage(packetType)
///   writer.Write*(...)   // write payload bytes
///   await writer.EndMessageAsync(ct)  // flushes all buffered bytes as packets
/// 
/// This class is NOT thread-safe. One instance per session.
/// </summary>
internal sealed class TdsPacketWriter : IAsyncDisposable
{
    private readonly Stream _stream;
    private int _negotiatedPacketSize;
    private byte _currentPacketType;
    private byte _packetId;

    // Buffer holding the current message payload (grows as needed)
    private readonly MemoryStream _payload = new();
    private bool _disposed;

    public TdsPacketWriter(Stream stream, int packetSize = TdsProtocol.DefaultPacketSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _negotiatedPacketSize = Math.Clamp(packetSize, TdsProtocol.MinPacketSize, TdsProtocol.MaxPacketSize);
    }

    // -------------------------------------------------------------------------
    // Message framing
    // -------------------------------------------------------------------------

    /// <summary>Begins a new TDS message. Must be paired with EndMessageAsync.</summary>
    public void BeginMessage(byte packetType)
    {
        _currentPacketType = packetType;
        _payload.SetLength(0);
        _payload.Position = 0;
    }

    /// <summary>
    /// Flushes the buffered payload to the stream as one or more TDS packets,
    /// with the final packet marked EOM.
    /// </summary>
    public async ValueTask EndMessageAsync(CancellationToken ct = default)
    {
        var data = _payload.GetBuffer().AsMemory(0, (int)_payload.Length);
        int maxPayloadPerPacket = _negotiatedPacketSize - TdsProtocol.PacketHeaderSize;

        int offset = 0;
        do
        {
            int remaining = data.Length - offset;
            int chunkSize = Math.Min(remaining, maxPayloadPerPacket);
            bool isLast = (offset + chunkSize) >= data.Length;

            byte statusByte = isLast
                ? (byte)TdsPacketStatus.EndOfMessage
                : (byte)TdsPacketStatus.Normal;

            int totalPacketSize = TdsProtocol.PacketHeaderSize + chunkSize;

            // Build the 8-byte header directly
            byte[] header = new byte[TdsProtocol.PacketHeaderSize];
            header[0] = _currentPacketType;
            header[1] = statusByte;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)totalPacketSize);
            header[4] = 0; // SPID high
            header[5] = 0; // SPID low
            header[6] = _packetId;
            header[7] = 0; // Window

            unchecked { _packetId++; }

            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(data.Slice(offset, chunkSize), ct).ConfigureAwait(false);

            offset += chunkSize;
        } while (offset < data.Length);

        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Primitive writers — all write into _payload
    // -------------------------------------------------------------------------

    public void WriteByte(byte value)           => _payload.WriteByte(value);
    public void WriteUInt8(byte value)          => _payload.WriteByte(value);

    public void WriteUInt16LE(ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteUInt32LE(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteUInt64LE(ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteInt16LE(short value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteInt32LE(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteInt64LE(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(buf, value);
        _payload.Write(buf);
    }

    public void WriteBytes(ReadOnlySpan<byte> bytes) => _payload.Write(bytes);

    /// <summary>
    /// Writes a B_VARCHAR: 1-byte length prefix + UTF-16LE encoded string.
    /// Used for server name, proc name, etc. in error/info tokens.
    /// </summary>
    public void WriteBVarchar(string value)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(value);
        WriteUInt8((byte)value.Length); // length in characters, not bytes
        _payload.Write(encoded);
    }

    /// <summary>
    /// Writes a US_VARCHAR: 2-byte length prefix + UTF-16LE encoded string.
    /// </summary>
    public void WriteUsVarchar(string value)
    {
        byte[] encoded = Encoding.Unicode.GetBytes(value);
        WriteUInt16LE((ushort)value.Length); // length in characters
        _payload.Write(encoded);
    }

    /// <summary>
    /// Writes a B_VARBYTE: 1-byte length prefix + raw bytes.
    /// Used for collation, environment change values, etc.
    /// </summary>
    public void WriteBVarbyte(ReadOnlySpan<byte> data)
    {
        WriteUInt8((byte)data.Length);
        _payload.Write(data);
    }

    /// <summary>Current length of the buffered payload in bytes.</summary>
    public int PayloadLength => (int)_payload.Length;

    /// <summary>
    /// Records the current payload position and returns it.
    /// Use with <see cref="PatchUInt16LE"/> to backfill length fields.
    /// </summary>
    public int GetPosition() => (int)_payload.Position;

    /// <summary>
    /// Patches a previously-written 2-byte LE length field at <paramref name="position"/>.
    /// </summary>
    public void PatchUInt16LE(int position, ushort value)
    {
        long saved = _payload.Position;
        _payload.Position = position;
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buf, value);
        _payload.Write(buf);
        _payload.Position = saved;
    }

    /// <summary>Update the negotiated packet size (called after Login7 handshake).</summary>
    public void SetPacketSize(int size)
    {
        _negotiatedPacketSize = Math.Clamp(size, TdsProtocol.MinPacketSize, TdsProtocol.MaxPacketSize);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _payload.DisposeAsync().ConfigureAwait(false);
    }
}
