using FluentAssertions;
using PgPassthrough.Core.Models;
using PgPassthrough.Tds.Protocol;
using PgPassthrough.Tds.Tokens;

namespace PgPassthrough.Tds.Tests.Protocol;

/// <summary>
/// Smoke-tests that TdsTokenWriter produces a valid byte stream.
/// These tests verify the structural correctness of token output
/// (correct token type byte, non-empty payload) rather than
/// byte-for-byte TDS conformance — full conformance is covered by
/// the integration tests in Phase 8.
/// </summary>
public sealed class TdsTokenWriterTests
{
    private MemoryStream _stream = new();
    private TdsPacketWriter _packetWriter = null!;
    private TdsTokenWriter _tokenWriter = null!;

    private void Setup()
    {
        _stream = new MemoryStream();
        _packetWriter = new TdsPacketWriter(_stream);
        _tokenWriter = new TdsTokenWriter(_packetWriter);
        _packetWriter.BeginMessage(TdsPacketType.TabularResult);
    }

    private async Task<byte[]> Flush()
    {
        await _packetWriter.EndMessageAsync();
        // Strip the 8-byte TDS packet header to get the payload
        var all = _stream.ToArray();
        return all[TdsProtocol.PacketHeaderSize..];
    }

    [Fact]
    public async Task WriteLoginAck_ProducesLoginAckToken()
    {
        Setup();
        _tokenWriter.WriteLoginAck(TdsProtocol.TdsVersion74);
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.LoginAck);
    }

    [Fact]
    public async Task WriteError_ProducesErrorToken()
    {
        Setup();
        _tokenWriter.WriteError(new ServerError { Message = "test error" });
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Error);
    }

    [Fact]
    public async Task WriteColMetadata_ZeroColumns_WritesNoMetadataMarker()
    {
        Setup();
        _tokenWriter.WriteColMetadata(Array.Empty<ColumnMetadata>());
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.ColMetadata);
        // 0xFFFF count for no-metadata
        ushort count = (ushort)(payload[1] | (payload[2] << 8));
        count.Should().Be(0xFFFF);
    }

    [Fact]
    public async Task WriteColMetadata_OneNVarCharColumn_WritesCorrectCount()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Name", TypeCode = SqlServerTypeCode.NVarChar, MaxLength = 100, Ordinal = 0 }
        };
        _tokenWriter.WriteColMetadata(columns);
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.ColMetadata);
        ushort count = (ushort)(payload[1] | (payload[2] << 8));
        count.Should().Be(1);
    }

    [Fact]
    public async Task WriteDone_ProducesDoneToken()
    {
        Setup();
        _tokenWriter.WriteDone(DoneStatus.Final, 0xC1, 42);
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Done);
    }

    [Fact]
    public async Task WriteEnvChangeDatabase_ProducesEnvChangeToken()
    {
        Setup();
        _tokenWriter.WriteEnvChangeDatabase("mydb", "master");
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.EnvChange);
    }

    [Fact]
    public async Task WriteRow_StringValue_NonEmptyPayload()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Val", TypeCode = SqlServerTypeCode.NVarChar, MaxLength = 50, Ordinal = 0 }
        };
        _tokenWriter.WriteRow(columns, new object?[] { "hello" });
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Row);
        payload.Length.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task WriteRow_NullValue_WritesNullSentinel()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Val", TypeCode = SqlServerTypeCode.NVarChar, MaxLength = 50, Ordinal = 0 }
        };
        _tokenWriter.WriteRow(columns, new object?[] { null });
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Row);
        // Null sentinel for NVarChar: two bytes 0xFF 0xFF
        payload[1].Should().Be(0xFF);
        payload[2].Should().Be(0xFF);
    }

    [Fact]
    public async Task WriteRow_IntValue_FourByteLittleEndian()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Id", TypeCode = SqlServerTypeCode.Int, Ordinal = 0 }
        };
        _tokenWriter.WriteRow(columns, new object?[] { 1234 });
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Row);
        // Now uses INTN format: 1-byte length prefix (4) + 4 LE bytes
        payload[1].Should().Be(4); // length prefix
        int value = payload[2] | (payload[3] << 8) | (payload[4] << 16) | (payload[5] << 24);
        value.Should().Be(1234);
    }

    [Fact]
    public async Task WriteRow_NullInt_WritesZeroLengthByte()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Id", TypeCode = SqlServerTypeCode.Int, Ordinal = 0, IsNullable = true }
        };
        _tokenWriter.WriteRow(columns, new object?[] { null });
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.Row);
        payload[1].Should().Be(0); // length = 0 means NULL for INTN
    }

    [Fact]
    public async Task WriteColMetadata_IntColumn_UsesIntNTypeCode()
    {
        Setup();
        var columns = new List<ColumnMetadata>
        {
            new() { ColumnName = "Id", TypeCode = SqlServerTypeCode.Int, Ordinal = 0 }
        };
        _tokenWriter.WriteColMetadata(columns);
        var payload = await Flush();

        payload[0].Should().Be(TdsTokenType.ColMetadata);
        // Count = 1
        ushort count = (ushort)(payload[1] | (payload[2] << 8));
        count.Should().Be(1);
        // After count: UserType (4 bytes) + Flags (2 bytes) = 6 bytes at offset 3
        // Then type info starts at offset 9
        byte typeCode = payload[9]; // position: 3 + 4 (UserType) + 2 (Flags) = 9
        typeCode.Should().Be(0x26); // INTN
        payload[10].Should().Be(4); // maxLength = 4 for INT
    }
}
