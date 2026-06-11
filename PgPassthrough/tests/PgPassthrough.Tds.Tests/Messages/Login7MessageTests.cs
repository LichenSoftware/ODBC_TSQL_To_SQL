using System.Text;
using FluentAssertions;
using PgPassthrough.Tds.Messages;

namespace PgPassthrough.Tds.Tests.Messages;

/// <summary>
/// Tests for LOGIN7 message parsing, including password de-obfuscation.
/// </summary>
public sealed class Login7MessageTests
{
    [Fact]
    public void Parse_WellFormedPayload_ExtractsFields()
    {
        // Build a minimal LOGIN7 payload manually so we know exactly what to expect.
        byte[] payload = BuildMinimalLogin7("sa", "Password1!", "mydb", "MyApp");

        var msg = Login7Message.Parse(payload.AsSpan());

        msg.UserName.Should().Be("sa");
        msg.Password.Should().Be("Password1!");
        msg.Database.Should().Be("mydb");
        msg.AppName.Should().Be("MyApp");
    }

    [Fact]
    public void Parse_EmptyPayload_ThrowsTdsProtocolException()
    {
        byte[] payload = new byte[10]; // too short
        Action act = () => Login7Message.Parse(payload.AsSpan());
        act.Should().Throw<PgPassthrough.Tds.Protocol.TdsProtocolException>();
    }

    [Fact]
    public void Parse_NoDatabase_ReturnsEmptyDatabase()
    {
        byte[] payload = BuildMinimalLogin7("user", "pass", "", "app");
        var msg = Login7Message.Parse(payload.AsSpan());
        msg.Database.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Helper: builds a minimal valid LOGIN7 binary payload
    // -------------------------------------------------------------------------

    private static byte[] BuildMinimalLogin7(
        string username, string password, string database, string appName)
    {
        // We build the fixed header + offset table + data section by hand.
        // This validates our parser against a known-good binary layout.

        var data = new List<byte>();

        // Fixed header offsets (we'll patch TotalLength at the end)
        // Bytes 0-3: TotalLength (placeholder)
        data.AddRange(new byte[4]);
        // Bytes 4-7: TDS version 7.4
        data.AddRange(BitConverter.GetBytes(0x74000004u));
        // Bytes 8-11: packet size 4096
        data.AddRange(BitConverter.GetBytes(4096u));
        // Bytes 12-15: client prog version
        data.AddRange(new byte[4]);
        // Bytes 16-19: client PID
        data.AddRange(new byte[4]);
        // Bytes 20-23: connection ID
        data.AddRange(new byte[4]);
        // Bytes 24-27: flags
        data.AddRange(new byte[] { 0xE0, 0x43, 0x01, 0x00 });
        // Bytes 28-31: timezone
        data.AddRange(new byte[4]);
        // Bytes 32-35: LCID
        data.AddRange(new byte[4]);

        // Offset table starts at 36. We have these fields in order:
        // HostName, UserName, Password, AppName, ServerName, Ext, LibraryName,
        // Language, Database, ClientId(6), SSPI, AttachDb, ChangePassword

        // We'll only populate: UserName, Password, AppName, Database
        // All others: offset=0, length=0

        // First, plan the data section layout
        // Data section starts at: 36 + 13 offset-pairs×4 + 6(ClientId) + sizeof(SSPI long) = 36 + 52 + 6 + 4 = 98
        int dataStart = 98;

        byte[] userBytes = Encoding.Unicode.GetBytes(username);
        byte[] pwdBytes  = ObfuscatePassword(password);
        byte[] appBytes  = Encoding.Unicode.GetBytes(appName);
        byte[] dbBytes   = Encoding.Unicode.GetBytes(database);

        int userOff  = dataStart;
        int pwdOff   = userOff + userBytes.Length;
        int appOff   = pwdOff + pwdBytes.Length;
        int dbOff    = appOff + appBytes.Length;

        void AddOffLen(int off, int byteLen, int charDiv = 2)
        {
            int charLen = byteLen / charDiv;
            data.AddRange(BitConverter.GetBytes((ushort)(byteLen > 0 ? off : 0)));
            data.AddRange(BitConverter.GetBytes((ushort)(byteLen / 2)));
        }

        void AddZeroOffLen()
        {
            data.AddRange(new byte[4]);
        }

        // HostName
        AddZeroOffLen();
        // UserName
        AddOffLen(userOff, userBytes.Length);
        // Password
        AddOffLen(pwdOff, pwdBytes.Length);
        // AppName
        AddOffLen(appOff, appBytes.Length);
        // ServerName
        AddZeroOffLen();
        // Extension
        AddZeroOffLen();
        // LibraryName
        AddZeroOffLen();
        // Language
        AddZeroOffLen();
        // Database
        AddOffLen(dbOff, dbBytes.Length);
        // ClientId (6 bytes, not an offset pair)
        data.AddRange(new byte[6]);
        // SSPI
        AddZeroOffLen();
        // AttachDb
        AddZeroOffLen();
        // ChangePassword
        AddZeroOffLen();
        // SSPILong (4 bytes, not an offset pair)
        data.AddRange(new byte[4]);

        // Sanity: we should be at dataStart now
        // (may differ slightly due to the non-offset-pair fields — just fill)
        while (data.Count < dataStart) data.Add(0);

        // Data section
        data.AddRange(userBytes);
        data.AddRange(pwdBytes);
        data.AddRange(appBytes);
        data.AddRange(dbBytes);

        // Patch TotalLength
        var result = data.ToArray();
        BitConverter.GetBytes((uint)result.Length).CopyTo(result, 0);
        return result;
    }

    private static byte[] ObfuscatePassword(string password)
    {
        byte[] utf16 = Encoding.Unicode.GetBytes(password);
        for (int i = 0; i < utf16.Length; i++)
        {
            byte b = utf16[i];
            // Encode per MS-TDS: swap nibbles, then XOR 0xA5
            b = (byte)(((b & 0x0F) << 4) | ((b & 0xF0) >> 4));
            b ^= 0xA5;
            utf16[i] = b;
        }
        return utf16;
    }
}
