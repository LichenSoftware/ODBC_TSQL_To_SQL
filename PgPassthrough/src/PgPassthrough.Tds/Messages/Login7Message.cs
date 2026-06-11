using System.Buffers.Binary;
using System.Text;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds.Messages;

/// <summary>
/// Parses a TDS LOGIN7 message (packet type 0x10).
/// 
/// LOGIN7 carries authentication credentials and client capability flags.
/// We extract: login name, password (obfuscated), database name, app name,
/// host name, and the requested packet size.
/// 
/// Reference: MS-TDS §2.2.6.4 LOGIN7
/// </summary>
internal sealed class Login7Message
{
    public uint TdsVersion { get; private set; }
    public uint RequestedPacketSize { get; private set; } = TdsProtocol.DefaultPacketSize;
    public uint ClientProgVersion { get; private set; }
    public uint ClientPid { get; private set; }
    public uint ConnectionId { get; private set; }
    public Login7OptionFlags1 OptionFlags1 { get; private set; }
    public Login7OptionFlags2 OptionFlags2 { get; private set; }
    public Login7TypeFlags TypeFlags { get; private set; }
    public Login7OptionFlags3 OptionFlags3 { get; private set; }
    public int TimeZone { get; private set; }
    public uint Lcid { get; private set; }

    public string HostName { get; private set; } = string.Empty;
    public string UserName { get; private set; } = string.Empty;
    public string Password { get; private set; } = string.Empty;
    public string AppName { get; private set; } = string.Empty;
    public string ServerName { get; private set; } = string.Empty;
    public string LibraryName { get; private set; } = string.Empty;
    public string Language { get; private set; } = string.Empty;
    public string Database { get; private set; } = string.Empty;
    public string AttachDbFile { get; private set; } = string.Empty;
    public string ChangePassword { get; private set; } = string.Empty;

    private Login7Message() { }

    /// <summary>
    /// Parses a LOGIN7 payload.
    /// </summary>
    /// <exception cref="TdsProtocolException">Thrown for malformed payloads.</exception>
    public static Login7Message Parse(ReadOnlySpan<byte> payload)
    {
        // Copy to byte[] so we can use it in helper methods without ref-struct
        // capture restrictions. LOGIN7 payloads are typically 200-500 bytes.
        return Parse(payload.ToArray());
    }

    private static Login7Message Parse(byte[] payload)
    {
        if (payload.Length < 36)
            throw new TdsProtocolException($"LOGIN7 payload too short: {payload.Length} bytes");

        var msg = new Login7Message();

        msg.TdsVersion             = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
        msg.RequestedPacketSize    = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8));
        msg.ClientProgVersion      = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(12));
        msg.ClientPid              = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(16));
        msg.ConnectionId           = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(20));
        msg.OptionFlags1           = (Login7OptionFlags1)payload[24];
        msg.OptionFlags2           = (Login7OptionFlags2)payload[25];
        msg.TypeFlags              = (Login7TypeFlags)payload[26];
        msg.OptionFlags3           = (Login7OptionFlags3)payload[27];
        msg.TimeZone               = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(28));
        msg.Lcid                   = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(32));

        // Offset/length pairs starting at byte 36
        int pos = 36;

        (int off, int len) ReadOffLen()
        {
            if (pos + 4 > payload.Length) return (0, 0);
            int o = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos));
            int l = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(pos + 2));
            pos += 4;
            return (o, l);
        }

        var (hosOff, hosLen)   = ReadOffLen();
        var (usrOff, usrLen)   = ReadOffLen();
        var (pwdOff, pwdLen)   = ReadOffLen();
        var (appOff, appLen)   = ReadOffLen();
        var (srvOff, srvLen)   = ReadOffLen();
        ReadOffLen(); // unused / extension offset
        var (libOff, libLen)   = ReadOffLen();
        var (langOff, langLen) = ReadOffLen();
        var (dbOff, dbLen)     = ReadOffLen();
        pos += 6; // ClientId (MAC address) — skip
        ReadOffLen(); // SSPI
        var (adbOff, adbLen)   = ReadOffLen();
        var (chpOff, chpLen)   = ReadOffLen();

        string ReadStr(int offset, int charCount)
        {
            if (offset == 0 || charCount == 0) return string.Empty;
            int byteLen = charCount * 2;
            if (offset + byteLen > payload.Length) return string.Empty;
            return Encoding.Unicode.GetString(payload, offset, byteLen);
        }

        msg.HostName       = ReadStr(hosOff, hosLen);
        msg.UserName       = ReadStr(usrOff, usrLen);
        msg.Password       = DecodePassword(payload, pwdOff, pwdLen);
        msg.AppName        = ReadStr(appOff, appLen);
        msg.ServerName     = ReadStr(srvOff, srvLen);
        msg.LibraryName    = ReadStr(libOff, libLen);
        msg.Language       = ReadStr(langOff, langLen);
        msg.Database       = ReadStr(dbOff, dbLen);
        msg.AttachDbFile   = ReadStr(adbOff, adbLen);
        msg.ChangePassword = DecodePassword(payload, chpOff, chpLen);

        return msg;
    }

    /// <summary>
    /// TDS password obfuscation: swap nibbles then XOR with 0xA5.
    /// Reference: MS-TDS §2.2.6.4 PasswordChange
    /// </summary>
    private static string DecodePassword(byte[] payload, int offset, int charCount)
    {
        if (offset == 0 || charCount == 0) return string.Empty;
        int byteLen = charCount * 2;
        if (offset + byteLen > payload.Length) return string.Empty;

        byte[] decoded = new byte[byteLen];
        for (int i = 0; i < byteLen; i++)
        {
            byte b = payload[offset + i];
            // Decoding: reverse the encoding (encode = swap nibbles then XOR 0xA5)
            // So decode = XOR 0xA5 first, then swap nibbles
            b ^= 0xA5;
            b = (byte)(((b & 0x0F) << 4) | ((b & 0xF0) >> 4));
            decoded[i] = b;
        }
        return Encoding.Unicode.GetString(decoded);
    }
}

[Flags]
internal enum Login7OptionFlags1 : byte
{
    None = 0x00,
    ByteOrderX86 = 0x01,
    CharAscii = 0x02,
    FloatIEEE754 = 0x04,
    DumpLoadOn = 0x08,
    UseDbOn = 0x10,
    DatabaseFatal = 0x20,
    SetLangOn = 0x40,
    LangFatal = 0x80,
}

[Flags]
internal enum Login7OptionFlags2 : byte
{
    None = 0x00,
    Language = 0x01,
    Odbc = 0x02,
    TransBoundary = 0x04,
    CacheConnect = 0x08,
    UserTypeServer = 0x10,
    UserTypeRemUser = 0x20,
    UserTypeSqlUser = 0x40,
    IntegratedSecurity = 0x80,
}

[Flags]
internal enum Login7TypeFlags : byte
{
    None = 0x00,
    SqlOlap = 0x01,
    SqlTSql = 0x02,
    ReadOnlyIntent = 0x20,
}

[Flags]
internal enum Login7OptionFlags3 : byte
{
    None = 0x00,
    ChangePassword = 0x01,
    SendYukonBinaryXml = 0x02,
    UserInstance = 0x04,
    UnknownCollationHandling = 0x08,
    Extension = 0x10,
}
