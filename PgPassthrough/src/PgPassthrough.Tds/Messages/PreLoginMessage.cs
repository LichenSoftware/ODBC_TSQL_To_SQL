using System.Buffers.Binary;

namespace PgPassthrough.Tds.Messages;

/// <summary>
/// Represents a TDS PRELOGIN message (packet type 0x12).
/// 
/// The client sends PRELOGIN before Login7 to negotiate capabilities.
/// Key fields we care about: TDS version, encryption request, instance name.
/// We respond indicating we do not support encryption (for now — see tech debt).
/// 
/// Reference: MS-TDS §2.2.6.4, §2.2.6.5
/// </summary>
internal sealed class PreLoginMessage
{
    // Option token IDs
    private const byte OptionVersion    = 0x00;
    private const byte OptionEncryption = 0x01;
    private const byte OptionInstance   = 0x02;
    private const byte OptionThreadId   = 0x03;
    private const byte OptionMars       = 0x04;
    private const byte OptionTraceId    = 0x05;
    private const byte OptionFedAuthRequired = 0x06;
    private const byte OptionNonce      = 0x07;
    private const byte OptionTerminator = 0xFF;

    // Encryption option values
    public const byte EncryptionOff     = 0x00; // No encryption
    public const byte EncryptionOn      = 0x01;
    public const byte EncryptionNotSupported = 0x02;
    public const byte EncryptionRequired = 0x03;

    public uint TdsVersion { get; private set; }
    public byte Encryption { get; private set; } = EncryptionOff;
    public bool MarsEnabled { get; private set; }

    /// <summary>
    /// Parses a PRELOGIN payload and returns a <see cref="PreLoginMessage"/>.
    /// </summary>
    public static PreLoginMessage Parse(ReadOnlySpan<byte> payload)
    {
        var msg = new PreLoginMessage();

        // The PRELOGIN payload is a set of option-offset-length tuples
        // followed by the actual option data. Each tuple is:
        //   [TokenType:1][Offset:2 BE][Length:2 BE]
        // Terminated by OptionTerminator (0xFF).

        int cursor = 0;
        var options = new List<(byte token, int offset, int length)>();

        while (cursor < payload.Length)
        {
            byte token = payload[cursor++];
            if (token == OptionTerminator) break;

            if (cursor + 4 > payload.Length)
                throw new InvalidDataException("Truncated PRELOGIN option header.");

            int offset = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(cursor, 2));
            int length = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(cursor + 2, 2));
            cursor += 4;
            options.Add((token, offset, length));
        }

        foreach (var (token, offset, length) in options)
        {
            if (offset + length > payload.Length) continue; // skip malformed
            var data = payload.Slice(offset, length);

            switch (token)
            {
                case OptionVersion when length >= 6:
                    msg.TdsVersion = BinaryPrimitives.ReadUInt32BigEndian(data);
                    break;
                case OptionEncryption when length >= 1:
                    msg.Encryption = data[0];
                    break;
                case OptionMars when length >= 1:
                    msg.MarsEnabled = data[0] == 0x01;
                    break;
            }
        }

        return msg;
    }

    /// <summary>
    /// Builds the server's PRELOGIN response payload.
    /// We respond: same TDS version, encryption NOT SUPPORTED, MARS off.
    /// 
    /// Tech debt: support TLS encryption (EncryptionOn) in a future phase.
    /// </summary>
    public static byte[] BuildResponse(uint serverTdsVersion, byte encryptionResponse = EncryptionNotSupported)
    {
        // We write: VERSION(6) + ENCRYPTION(1) + INSTANCE(1) + THREADID(0) + MARS(1) + TERMINATOR
        // Matching what SQL Server sends:
        //   Option table: VERSION, ENCRYPTION, INSTOPT, THREADID, MARS, TERMINATOR
        // Minimum viable: VERSION + ENCRYPTION + TERMINATOR

        // Option table: 2 entries × 5 bytes each = 10 bytes + 1 terminator = 11 bytes header
        // Data starts at offset 11
        const int dataStart = 11;

        var result = new byte[dataStart + 7]; // 6 version bytes + 1 encryption byte
        int pos = 0;

        // ---- Option table (big-endian offsets/lengths) ----
        // VERSION: token=0x00, offset=dataStart(BE), length=6(BE)
        result[pos++] = OptionVersion;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)dataStart); pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), 6); pos += 2;

        // ENCRYPTION: token=0x01, offset=dataStart+6(BE), length=1(BE)
        result[pos++] = OptionEncryption;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), (ushort)(dataStart + 6)); pos += 2;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(pos), 1); pos += 2;

        // Terminator
        result[pos++] = OptionTerminator;

        // ---- Data section ----
        // VERSION: 4-byte UL_VERSION (big-endian) + 2-byte US_SUBBUILD (little-endian)
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(pos), serverTdsVersion); pos += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(pos), 0); pos += 2; // sub-build = 0

        // ENCRYPTION response byte
        result[pos] = encryptionResponse;

        return result;
    }

}
