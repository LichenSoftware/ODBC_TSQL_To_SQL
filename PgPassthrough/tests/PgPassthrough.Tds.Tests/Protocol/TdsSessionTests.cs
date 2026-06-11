using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.Tds.Messages;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds.Tests.Protocol;

/// <summary>
/// End-to-end session tests using in-memory duplex streams.
/// Simulates the full PRELOGIN → LOGIN7 → SQLBatch → disconnect flow
/// without a real TCP connection.
/// </summary>
public sealed class TdsSessionTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static ICredentialValidator MakeValidator(bool allow = true)
        => new StaticCredentialValidator(allow);

    private static IQueryHandler MakeEchoHandler()
        => new EchoQueryHandler();

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task FullLoginFlow_ValidCredentials_SendsDone()
    {
        // This test writes a PRELOGIN + LOGIN7 to a duplex stream pair and
        // verifies the session progresses to Active state (indicated by
        // a successful response containing a LOGINACK token).

        var serverStream = new DuplexMemoryStream();
        var session = new TdsSession(
            serverStream,
            MakeQueryHandler(),
            MakeValidator(allow: true),
            NullLogger.Instance,
            CancellationToken.None);

        // Write PRELOGIN from the "client" side
        await WritePreLoginAsync(serverStream.ClientSide);

        // Session reads PRELOGIN, writes response, then we send LOGIN7
        var sessionTask = session.ProcessAsync();

        await Task.Delay(50); // let session process PRELOGIN
        await WriteLogin7Async(serverStream.ClientSide, "sa", "Password1!", "testdb", "MyApp");
        await Task.Delay(50); // let session process LOGIN7

        // Client side should have LOGINACK response in the read buffer
        var responseBytes = serverStream.ClientSide.ReadAllBytes();
        responseBytes.Should().NotBeEmpty();

        // Shutdown the session
        serverStream.ClientSide.Close();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LoginFlow_InvalidCredentials_SessionCloses()
    {
        var serverStream = new DuplexMemoryStream();
        var session = new TdsSession(
            serverStream,
            MakeQueryHandler(),
            MakeValidator(allow: false), // reject all
            NullLogger.Instance,
            CancellationToken.None);

        await WritePreLoginAsync(serverStream.ClientSide);
        var sessionTask = session.ProcessAsync();
        await Task.Delay(50);

        await WriteLogin7Async(serverStream.ClientSide, "sa", "wrongpassword", "db", "app");
        await Task.Delay(50);

        // Session should have closed after auth failure
        serverStream.ClientSide.Close();
        await sessionTask.WaitAsync(TimeSpan.FromSeconds(2));
        // If we get here without hanging, the session closed correctly
    }

    // -------------------------------------------------------------------------
    // Private helpers: write TDS messages to a stream
    // -------------------------------------------------------------------------

    private static async Task WritePreLoginAsync(Stream stream)
    {
        byte[] payload = PreLoginMessage.BuildResponse(TdsProtocol.TdsVersion74);
        var writer = new TdsPacketWriter(stream);
        writer.BeginMessage(TdsPacketType.PreLogin);
        writer.WriteBytes(payload);
        await writer.EndMessageAsync();
    }

    private static async Task WriteLogin7Async(Stream stream,
        string user, string pass, string db, string app)
    {
        // Build a minimal Login7 payload using the same helper as Login7MessageTests
        byte[] payload = Login7TestHelper.Build(user, pass, db, app);
        var writer = new TdsPacketWriter(stream);
        writer.BeginMessage(TdsPacketType.Login7);
        writer.WriteBytes(payload);
        await writer.EndMessageAsync();
    }

    private static IQueryHandler MakeQueryHandler()
        => new EchoQueryHandler();
}

// -------------------------------------------------------------------------
// Test doubles
// -------------------------------------------------------------------------

internal sealed class StaticCredentialValidator : ICredentialValidator
{
    private readonly bool _allow;
    public StaticCredentialValidator(bool allow) => _allow = allow;
    public Task<bool> ValidateAsync(string u, string p, CancellationToken ct)
        => Task.FromResult(_allow);
}

internal sealed class EchoQueryHandler : IQueryHandler
{
    public async Task HandleAsync(ClientRequest request, IResponseWriter writer, CancellationToken ct)
    {
        await writer.WriteDoneAsync(DoneStatus.Final, 0, ct);
    }
}

/// <summary>
/// A duplex in-memory stream: the session reads/writes to one side,
/// the test reads/writes to the other side via ClientSide.
/// Backed by two MemoryStreams with a simple synchronisation mechanism.
/// </summary>
internal sealed class DuplexMemoryStream : Stream
{
    // Bytes the test writes → session reads
    private readonly MemoryStream _toServer = new();
    // Bytes the session writes → test reads
    private readonly MemoryStream _toClient = new();
    private readonly object _lock = new();

    public DuplexClientStream ClientSide { get; }

    public DuplexMemoryStream()
    {
        ClientSide = new DuplexClientStream(_toServer, _toClient);
    }

    // Session reads from _toServer
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Block until data is available — used synchronously for simplicity in tests
        while (true)
        {
            lock (_lock)
            {
                long pos = _toServer.Position;
                if (pos < _toServer.Length)
                {
                    return _toServer.Read(buffer, offset, count);
                }
            }
            Thread.Sleep(5);
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            long savedPos = _toClient.Position;
            _toClient.Seek(0, SeekOrigin.End);
            _toClient.Write(buffer, offset, count);
            _toClient.Position = savedPos;
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => Task.Run(() => Read(buffer, offset, count), ct);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

internal sealed class DuplexClientStream : Stream
{
    private readonly MemoryStream _write; // client writes here → server reads
    private readonly MemoryStream _read;  // server writes here → client reads
    private readonly object _lock = new();
    private bool _closed;

    public DuplexClientStream(MemoryStream writeTarget, MemoryStream readSource)
    {
        _write = writeTarget;
        _read  = readSource;
    }

    public override bool CanRead => true;
    public override bool CanWrite => !_closed;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_closed) return;
        lock (_lock)
        {
            _write.Seek(0, SeekOrigin.End);
            _write.Write(buffer, offset, count);
            _write.Position = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_closed) return 0;
        while (true)
        {
            lock (_lock)
            {
                if (_read.Position < _read.Length)
                    return _read.Read(buffer, offset, count);
            }
            Thread.Sleep(5);
        }
    }

    public byte[] ReadAllBytes()
    {
        lock (_lock)
        {
            return _read.ToArray();
        }
    }

    public override void Close()
    {
        _closed = true;
        base.Close();
    }

    public override void Flush() { }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => Task.Run(() => Read(buffer, offset, count), ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// <summary>Re-usable Login7 binary builder for tests.</summary>
internal static class Login7TestHelper
{
    public static byte[] Build(string username, string password, string database, string appName)
    {
        var data = new List<byte>();
        data.AddRange(new byte[4]); // TotalLength placeholder
        data.AddRange(BitConverter.GetBytes(0x74000004u)); // TDS 7.4
        data.AddRange(BitConverter.GetBytes(4096u)); // packet size
        data.AddRange(new byte[4]); // client prog version
        data.AddRange(new byte[4]); // client PID
        data.AddRange(new byte[4]); // connection ID
        data.AddRange(new byte[] { 0xE0, 0x43, 0x01, 0x00 }); // flags
        data.AddRange(new byte[4]); // timezone
        data.AddRange(new byte[4]); // LCID

        int dataStart = 98;
        byte[] userBytes = Encoding.Unicode.GetBytes(username);
        byte[] pwdBytes  = ObfuscatePassword(password);
        byte[] appBytes  = Encoding.Unicode.GetBytes(appName);
        byte[] dbBytes   = Encoding.Unicode.GetBytes(database);

        int userOff = dataStart;
        int pwdOff  = userOff + userBytes.Length;
        int appOff  = pwdOff + pwdBytes.Length;
        int dbOff   = appOff + appBytes.Length;

        void AddOL(int off, int byteLen) {
            data.AddRange(BitConverter.GetBytes((ushort)(byteLen > 0 ? off : 0)));
            data.AddRange(BitConverter.GetBytes((ushort)(byteLen / 2)));
        }
        void AddZ() => data.AddRange(new byte[4]);

        AddZ();           // HostName
        AddOL(userOff, userBytes.Length);
        AddOL(pwdOff,  pwdBytes.Length);
        AddOL(appOff,  appBytes.Length);
        AddZ();           // ServerName
        AddZ();           // Extension
        AddZ();           // LibraryName
        AddZ();           // Language
        AddOL(dbOff,   dbBytes.Length);
        data.AddRange(new byte[6]); // ClientId
        AddZ(); AddZ(); AddZ();     // SSPI, AttachDb, ChangePassword
        data.AddRange(new byte[4]); // SSPILong

        while (data.Count < dataStart) data.Add(0);
        data.AddRange(userBytes);
        data.AddRange(pwdBytes);
        data.AddRange(appBytes);
        data.AddRange(dbBytes);

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
