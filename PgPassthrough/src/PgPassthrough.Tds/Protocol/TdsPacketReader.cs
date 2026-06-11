using System.Buffers;
using System.Buffers.Binary;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Reads TDS packets from the underlying network stream.
/// Handles packet header parsing and multi-packet message reassembly.
/// Exposes the complete message payload as a byte array.
/// 
/// This class is NOT thread-safe. One instance per session.
/// </summary>
internal sealed class TdsPacketReader : IDisposable
{
    private readonly Stream _stream;
    private readonly byte[] _headerBuffer = new byte[TdsProtocol.PacketHeaderSize];
    private int _negotiatedPacketSize = TdsProtocol.DefaultPacketSize;
    private bool _disposed;

    public TdsPacketReader(Stream stream)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    }

    /// <summary>
    /// Reads a complete TDS message from the stream. Reassembles multi-packet
    /// messages transparently. Returns the full payload (excluding headers) and
    /// the packet type of the first packet in the message.
    /// </summary>
    /// <exception cref="TdsProtocolException">Thrown on protocol violations.</exception>
    /// <exception cref="EndOfStreamException">Thrown when the client disconnects.</exception>
    public async ValueTask<TdsMessage> ReadMessageAsync(CancellationToken ct)
    {
        // Collect payload bytes across packets. Pre-size to one packet worth.
        var payloadBuffer = new List<byte>(_negotiatedPacketSize);
        var singlePacketBuf = new byte[_negotiatedPacketSize];

        byte messageType = 0;
        bool isFirstPacket = true;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            // Read the 8-byte packet header
            await ReadExactAsync(_stream, _headerBuffer, TdsProtocol.PacketHeaderSize, ct)
                .ConfigureAwait(false);

            byte packetType  = _headerBuffer[0];
            byte statusByte  = _headerBuffer[1];
            ushort packetLen = BinaryPrimitives.ReadUInt16BigEndian(_headerBuffer.AsSpan(2, 2));

            if (packetLen < TdsProtocol.PacketHeaderSize)
                throw new TdsProtocolException(
                    $"Invalid packet length {packetLen}; must be >= {TdsProtocol.PacketHeaderSize}");

            if (isFirstPacket)
            {
                messageType = packetType;
                isFirstPacket = false;
            }
            else if (packetType != messageType)
            {
                throw new TdsProtocolException(
                    $"Packet type mismatch in multi-packet message: expected {messageType}, got {packetType}");
            }

            int payloadLength = packetLen - TdsProtocol.PacketHeaderSize;
            if (payloadLength > 0)
            {
                // Ensure our temp buffer is large enough
                if (singlePacketBuf.Length < payloadLength)
                    singlePacketBuf = new byte[payloadLength];

                await ReadExactAsync(_stream, singlePacketBuf, payloadLength, ct)
                    .ConfigureAwait(false);
                payloadBuffer.AddRange(singlePacketBuf.AsSpan(0, payloadLength).ToArray());
            }

            var status = (TdsPacketStatus)statusByte;
            if (status.HasFlag(TdsPacketStatus.EndOfMessage))
                break;
        }

        return new TdsMessage(messageType, payloadBuffer.ToArray());
    }

    /// <summary>
    /// Updates the negotiated packet size after PRELOGIN/Login7 negotiation.
    /// </summary>
    public void SetPacketSize(int size)
    {
        _negotiatedPacketSize = Math.Clamp(size, TdsProtocol.MinPacketSize, TdsProtocol.MaxPacketSize);
    }

    private static async ValueTask ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = await stream.ReadAsync(buffer, totalRead, count - totalRead, ct)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(
                    "Connection closed by client while reading TDS packet.");
            totalRead += read;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}

/// <summary>
/// A fully-reassembled TDS message: type byte + complete payload bytes.
/// </summary>
internal sealed class TdsMessage
{
    public TdsMessage(byte type, byte[] payload)
    {
        Type    = type;
        Payload = payload;
    }

    public byte Type { get; }
    public byte[] Payload { get; }
    public ReadOnlySpan<byte> PayloadSpan => Payload.AsSpan();
    public ReadOnlyMemory<byte> PayloadMemory => Payload.AsMemory();
}
