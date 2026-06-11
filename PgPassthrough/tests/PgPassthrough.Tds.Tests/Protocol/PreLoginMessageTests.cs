using FluentAssertions;
using PgPassthrough.Tds.Messages;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds.Tests.Protocol;

/// <summary>
/// Tests for PRELOGIN message handling.
/// </summary>
public sealed class PreLoginMessageTests
{
    [Fact]
    public void BuildResponse_ProducesValidStructure()
    {
        byte[] response = PreLoginMessage.BuildResponse(TdsProtocol.TdsVersion74);

        // Must be non-empty
        response.Should().NotBeEmpty();

        // First byte should be OptionVersion (0x00)
        response[0].Should().Be(0x00);
    }

    [Fact]
    public void BuildResponse_ContainsEncryptionNotSupported()
    {
        byte[] response = PreLoginMessage.BuildResponse(TdsProtocol.TdsVersion74);

        // Parse our own response and verify encryption = NOT SUPPORTED
        var parsed = PreLoginMessage.Parse(response.AsSpan());
        parsed.Encryption.Should().Be(PreLoginMessage.EncryptionNotSupported);
    }

    [Fact]
    public void Parse_WellFormedPayload_ExtractsTdsVersion()
    {
        // Build a minimal PRELOGIN: VERSION(6) + ENCRYPTION(1) + TERMINATOR
        // Option table: 2 options × 5 bytes = 10 bytes + 1 terminator = 11 byte header
        // Data starts at offset 11
        int dataStart = 11;

        byte[] payload = new byte[dataStart + 7]; // 6 version bytes + 1 encryption byte
        int pos = 0;

        // VERSION option: token=0, offset=dataStart, length=6
        payload[pos++] = 0x00;
        payload[pos++] = 0x00; payload[pos++] = (byte)dataStart; // offset BE
        payload[pos++] = 0x00; payload[pos++] = 0x06; // length BE

        // ENCRYPTION option: token=1, offset=dataStart+6, length=1
        payload[pos++] = 0x01;
        payload[pos++] = 0x00; payload[pos++] = (byte)(dataStart + 6);
        payload[pos++] = 0x00; payload[pos++] = 0x01;

        // Terminator
        payload[pos++] = 0xFF;

        // VERSION data: 0x74000004 (TDS 7.4) big-endian + 2-byte sub-build
        payload[dataStart + 0] = 0x74;
        payload[dataStart + 1] = 0x00;
        payload[dataStart + 2] = 0x00;
        payload[dataStart + 3] = 0x04;
        payload[dataStart + 4] = 0x00;
        payload[dataStart + 5] = 0x00;

        // ENCRYPTION data
        payload[dataStart + 6] = PreLoginMessage.EncryptionOff;

        var msg = PreLoginMessage.Parse(payload.AsSpan());

        msg.TdsVersion.Should().Be(TdsProtocol.TdsVersion74);
        msg.Encryption.Should().Be(PreLoginMessage.EncryptionOff);
    }
}
