using System.Text;

namespace PgPassthrough.Tds.Messages;

/// <summary>
/// Parses a TDS SQLBATCH message (packet type 0x01).
/// 
/// A SQLBatch is simply a UTF-16LE encoded SQL text, optionally preceded
/// by ALL_HEADERS (TDS 7.2+). We strip the ALL_HEADERS block and return
/// the raw SQL text.
/// 
/// Reference: MS-TDS §2.2.6.7
/// </summary>
internal static class SqlBatchMessage
{
    // ALL_HEADERS query notification header type IDs
    private const ushort HeaderQueryNotification  = 0x0001;
    private const ushort HeaderTransactionDescriptor = 0x0002;
    private const ushort HeaderTraceActivity       = 0x0003;

    /// <summary>
    /// Parses the SQLBatch payload and returns the SQL text string.
    /// </summary>
    public static string Parse(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 2) return string.Empty;

        int offset = SkipAllHeaders(payload);
        if (offset >= payload.Length) return string.Empty;

        // Remaining bytes are UTF-16LE SQL text
        return Encoding.Unicode.GetString(payload[offset..]);
    }

    /// <summary>
    /// Skips the optional ALL_HEADERS block and returns the offset of the SQL text.
    /// If no ALL_HEADERS block is present, returns 0.
    /// 
    /// ALL_HEADERS layout:
    ///   [TotalLength:4 LE]
    ///   [HeaderLength:4 LE][HeaderType:2 LE][HeaderData:variable]*
    /// </summary>
    private static int SkipAllHeaders(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return 0;

        uint totalLength = ReadUInt32LE(payload, 0);

        // Sanity check: totalLength must be > 4 (at least one header) and < payload size
        // Also, the SQL text must start at offset totalLength and be non-empty
        if (totalLength < 4 || totalLength >= (uint)payload.Length)
            return 0; // No ALL_HEADERS, payload is pure SQL

        // Verify we can read at least one header entry
        if (payload.Length < 10) return 0;

        // Quick validation: first header length must be reasonable
        uint firstHeaderLen = ReadUInt32LE(payload, 4);
        if (firstHeaderLen < 6 || firstHeaderLen > totalLength)
            return 0; // Not a valid ALL_HEADERS block

        return (int)totalLength;
    }

    private static uint ReadUInt32LE(ReadOnlySpan<byte> span, int offset)
    {
        if (offset + 4 > span.Length) return 0;
        return (uint)(span[offset] | (span[offset + 1] << 8) |
                      (span[offset + 2] << 16) | (span[offset + 3] << 24));
    }
}
