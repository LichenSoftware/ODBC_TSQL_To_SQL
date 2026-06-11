using System.Buffers.Binary;
using System.Text;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Tds.Messages;

/// <summary>
/// Parses a TDS RPC request message (packet type 0x03).
/// 
/// RPC messages are used for:
///   - Stored procedure calls: EXEC sp_name @p1, @p2
///   - sp_executesql: parameterised query execution
///   - sp_prepare / sp_execute: prepared statement handles
///   - Metadata discovery: sp_tables, sp_columns, sp_pkeys, etc.
/// 
/// Reference: MS-TDS §2.2.6.5 RPC Request
/// </summary>
internal sealed class RpcRequestMessage
{
    // Well-known procedure IDs (used instead of a name when ProcIDSwitch = 0xFFFF)
    // Reference: MS-TDS §2.2.6.5 ProcID
    private static readonly Dictionary<ushort, string> KnownProcIds = new()
    {
        [1]  = "sp_cursor",
        [2]  = "sp_cursoropen",
        [3]  = "sp_cursorprepare",
        [4]  = "sp_cursorexecute",
        [5]  = "sp_cursorprepexec",
        [6]  = "sp_cursorunprepare",
        [7]  = "sp_cursorfetch",
        [8]  = "sp_cursoroption",
        [9]  = "sp_cursorclose",
        [10] = "sp_executesql",
        [11] = "sp_prepare",
        [12] = "sp_execute",
        [13] = "sp_prepexec",
        [14] = "sp_prepexecrpc",
        [15] = "sp_unprepare",
    };

    public string ProcedureName { get; private set; } = string.Empty;
    public RpcOptionFlags OptionFlags { get; private set; }
    public List<RpcParameter> Parameters { get; } = new();

    private RpcRequestMessage() { }

    /// <summary>
    /// Parses the RPC request payload. May contain multiple procedure calls
    /// separated by BatchFlag (0xFF 0xFF).
    /// Returns the first procedure call; batched RPCs are uncommon in practice.
    /// </summary>
    public static RpcRequestMessage Parse(ReadOnlySpan<byte> payload)
    {
        var msg = new RpcRequestMessage();
        int offset = SkipAllHeaders(payload);

        // Read procedure name or proc ID
        if (offset + 2 > payload.Length)
            return msg;

        ushort nameSwitch = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += 2;

        if (nameSwitch == 0xFFFF)
        {
            // Proc ID follows
            if (offset + 2 > payload.Length) return msg;
            ushort procId = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
            offset += 2;
            msg.ProcedureName = KnownProcIds.TryGetValue(procId, out var name) ? name : $"proc_{procId}";
        }
        else
        {
            // US_VARCHAR: nameSwitch is the character count
            int byteLen = nameSwitch * 2;
            if (offset + byteLen > payload.Length) return msg;
            msg.ProcedureName = Encoding.Unicode.GetString(payload.Slice(offset, byteLen));
            offset += byteLen;
        }

        if (offset + 2 > payload.Length) return msg;
        msg.OptionFlags = (RpcOptionFlags)BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
        offset += 2;

        // Parse parameters
        while (offset < payload.Length)
        {
            // BatchFlag: 0xFF 0xFF separates multiple procedure calls
            if (payload[offset] == 0xFF && offset + 1 < payload.Length && payload[offset + 1] == 0xFF)
                break; // Only parse first procedure

            var (param, newOffset) = ParseParameter(payload, offset);
            if (param == null) break;
            msg.Parameters.Add(param);
            offset = newOffset;
        }

        return msg;
    }

    private static (RpcParameter? param, int newOffset) ParseParameter(ReadOnlySpan<byte> payload, int offset)
    {
        if (offset >= payload.Length) return (null, offset);

        // B_VARCHAR: 1-byte length + UTF-16LE name
        byte nameLen = payload[offset++];
        string paramName = string.Empty;
        if (nameLen > 0)
        {
            int byteLen = nameLen * 2;
            if (offset + byteLen > payload.Length) return (null, offset);
            paramName = Encoding.Unicode.GetString(payload.Slice(offset, byteLen));
            offset += byteLen;
        }

        if (offset + 2 > payload.Length) return (null, offset);
        byte statusFlags = payload[offset++];
        bool isOutput = (statusFlags & 0x01) != 0;
        bool isDefaultValue = (statusFlags & 0x02) != 0;

        // Type info: type token + type-specific info
        if (offset >= payload.Length) return (null, offset);
        byte typeToken = payload[offset++];

        var (value, newOffset) = ReadTypedValue(payload, offset, typeToken);
        return (new RpcParameter(paramName, value, typeToken, isOutput), newOffset);
    }

    /// <summary>
    /// Reads a typed value from the parameter stream.
    /// Only the most common type tokens are decoded here; 
    /// unknowns are returned as raw bytes.
    /// </summary>
    private static (object? value, int newOffset) ReadTypedValue(ReadOnlySpan<byte> payload, int offset, byte typeToken)
    {
        // Fixed-length types
        switch (typeToken)
        {
            case 0x30: // TINYINT (1 byte)
                if (offset >= payload.Length) return (null, offset);
                return (payload[offset], offset + 1);

            case 0x32: // BIT (1 byte, 0/1)
                if (offset >= payload.Length) return (null, offset);
                return (payload[offset] != 0, offset + 1);

            case 0x34: // SMALLINT (2 bytes LE)
                if (offset + 2 > payload.Length) return (null, offset);
                return (BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]), offset + 2);

            case 0x38: // INT (4 bytes LE)
                if (offset + 4 > payload.Length) return (null, offset);
                return (BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]), offset + 4);

            case 0x7F: // BIGINT (8 bytes LE)
                if (offset + 8 > payload.Length) return (null, offset);
                return (BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]), offset + 8);

            case 0x3B: // REAL (4 bytes)
                if (offset + 4 > payload.Length) return (null, offset);
                return (BitConverter.ToSingle(payload.Slice(offset, 4)), offset + 4);

            case 0x3E: // FLOAT (8 bytes)
                if (offset + 8 > payload.Length) return (null, offset);
                return (BitConverter.ToDouble(payload.Slice(offset, 8)), offset + 8);
        }

        // Variable-length types — length-prefixed value or NULL sentinel
        switch (typeToken)
        {
            // INT family with length byte (nullable variant)
            case 0x26 when offset < payload.Length: // INTN
            {
                byte len = payload[offset++];
                if (len == 0) return (null, offset); // SQL NULL
                if (offset + len > payload.Length) return (null, offset);
                long v = len switch
                {
                    1 => payload[offset],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(payload[offset..]),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(payload[offset..]),
                    _ => 0
                };
                return (v, offset + len);
            }

            case 0xA7: // VARCHAR (1-byte length-prefix per char, 2-byte length in bytes)
            case 0xAF: // CHAR
            {
                // Max length info (2 bytes) precedes the value
                if (offset + 2 > payload.Length) return (null, offset);
                offset += 2; // skip max length
                if (offset + 5 > payload.Length) return (null, offset);
                offset += 5; // skip collation (5 bytes)
                if (offset + 2 > payload.Length) return (null, offset);
                ushort byteLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                offset += 2;
                if (byteLen == 0xFFFF) return (null, offset); // NULL
                if (offset + byteLen > payload.Length) return (null, offset);
                // VARCHAR uses the session collation — assume UTF-8 for now
                string s = Encoding.UTF8.GetString(payload.Slice(offset, byteLen));
                return (s, offset + byteLen);
            }

            case 0xE7: // NVARCHAR
            case 0xEF: // NCHAR
            {
                if (offset + 2 > payload.Length) return (null, offset);
                offset += 2; // skip max length
                if (offset + 5 > payload.Length) return (null, offset);
                offset += 5; // skip collation
                if (offset + 2 > payload.Length) return (null, offset);
                ushort byteLen = BinaryPrimitives.ReadUInt16LittleEndian(payload[offset..]);
                offset += 2;
                if (byteLen == 0xFFFF) return (null, offset); // NULL
                if (offset + byteLen > payload.Length) return (null, offset);
                string s = Encoding.Unicode.GetString(payload.Slice(offset, byteLen));
                return (s, offset + byteLen);
            }

            case 0x6A: // DECIMAL
            case 0x6C: // NUMERIC
            {
                if (offset + 3 > payload.Length) return (null, offset);
                byte maxLen = payload[offset++];
                byte precision = payload[offset++];
                byte scale = payload[offset++];
                if (offset >= payload.Length) return (null, offset);
                byte dataLen = payload[offset++];
                if (dataLen == 0) return (null, offset); // NULL
                if (offset + dataLen > payload.Length) return (null, offset);
                // Simplified: return as decimal string
                offset += dataLen;
                return (0m, offset); // TODO: proper decimal parsing in Phase 5
            }
        }

        // Unknown type: skip by returning null
        // A real implementation would throw or log here
        return (null, offset);
    }

    private static int SkipAllHeaders(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4) return 0;
        uint totalLength = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
        if (totalLength < 4 || totalLength >= (uint)payload.Length) return 0;
        uint firstHeaderLen = (uint)(payload[4] | (payload[5] << 8) | (payload[6] << 16) | (payload[7] << 24));
        if (firstHeaderLen < 6 || firstHeaderLen > totalLength) return 0;
        return (int)totalLength;
    }
}

/// <summary>A single parameter from an RPC request.</summary>
internal sealed class RpcParameter
{
    public RpcParameter(string name, object? value, byte typeToken, bool isOutput)
    {
        Name = name;
        Value = value;
        TypeToken = typeToken;
        IsOutput = isOutput;
    }

    public string Name { get; }
    public object? Value { get; }
    public byte TypeToken { get; }
    public bool IsOutput { get; }

    /// <summary>Converts to the core model parameter.</summary>
    public QueryParameter ToQueryParameter() => new()
    {
        Name = Name.StartsWith('@') ? Name : $"@{Name}",
        Value = Value,
        IsOutput = IsOutput
    };
}

[Flags]
internal enum RpcOptionFlags : ushort
{
    None = 0x0000,
    WithRecomp = 0x0001,
    NoMetadata = 0x0002,
    ReuseMetadata = 0x0004,
}
