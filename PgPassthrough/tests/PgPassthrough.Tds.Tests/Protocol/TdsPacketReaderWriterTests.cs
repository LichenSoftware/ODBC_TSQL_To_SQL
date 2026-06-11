using FluentAssertions;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds.Tests.Protocol;

/// <summary>
/// Tests for TDS packet framing: write → read roundtrip.
/// </summary>
public sealed class TdsPacketReaderWriterTests
{
    [Fact]
    public async Task SinglePacketMessage_WrittenAndRead_PayloadMatches()
    {
        // Arrange
        byte[] payload = "Hello TDS"u8.ToArray();
        var stream = new MemoryStream();
        var writer = new TdsPacketWriter(stream);

        writer.BeginMessage(TdsPacketType.SqlBatch);
        writer.WriteBytes(payload);
        await writer.EndMessageAsync();

        // Rewind and read
        stream.Position = 0;
        var reader = new TdsPacketReader(stream);
        var message = await reader.ReadMessageAsync(CancellationToken.None);

        message.Type.Should().Be(TdsPacketType.SqlBatch);
        message.Payload.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task MultiPacketMessage_ReassembledCorrectly()
    {
        // Arrange: force small packet size so a large payload splits across packets
        const int packetSize = 32; // very small — forces split
        byte[] payload = new byte[100];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 256);

        var stream = new MemoryStream();
        var writer = new TdsPacketWriter(stream, packetSize);
        writer.BeginMessage(TdsPacketType.SqlBatch);
        writer.WriteBytes(payload);
        await writer.EndMessageAsync();

        // Rewind
        stream.Position = 0;
        var reader = new TdsPacketReader(stream);
        reader.SetPacketSize(packetSize);
        var message = await reader.ReadMessageAsync(CancellationToken.None);

        message.Payload.Should().BeEquivalentTo(payload);
    }

    [Fact]
    public async Task EmptyPayload_WrittenAndRead_NoPayloadBytes()
    {
        var stream = new MemoryStream();
        var writer = new TdsPacketWriter(stream);
        writer.BeginMessage(TdsPacketType.PreLogin);
        await writer.EndMessageAsync();

        stream.Position = 0;
        var reader = new TdsPacketReader(stream);
        var message = await reader.ReadMessageAsync(CancellationToken.None);

        message.Type.Should().Be(TdsPacketType.PreLogin);
        message.Payload.Should().BeEmpty();
    }

    [Fact]
    public async Task ClosedStream_ThrowsEndOfStreamException()
    {
        var stream = new MemoryStream(Array.Empty<byte>());
        var reader = new TdsPacketReader(stream);

        Func<Task> act = async () => await reader.ReadMessageAsync(CancellationToken.None);
        await act.Should().ThrowAsync<EndOfStreamException>();
    }

    [Fact]
    public async Task PacketWithExactlyOnePayloadByte_RoundTrips()
    {
        var stream = new MemoryStream();
        var writer = new TdsPacketWriter(stream);
        writer.BeginMessage(TdsPacketType.Rpc);
        writer.WriteByte(0xAB);
        await writer.EndMessageAsync();

        stream.Position = 0;
        var reader = new TdsPacketReader(stream);
        var message = await reader.ReadMessageAsync(CancellationToken.None);

        message.Payload.Should().HaveCount(1);
        message.Payload[0].Should().Be(0xAB);
    }
}
