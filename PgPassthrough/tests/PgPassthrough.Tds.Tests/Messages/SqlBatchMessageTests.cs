using System.Text;
using FluentAssertions;
using PgPassthrough.Tds.Messages;

namespace PgPassthrough.Tds.Tests.Messages;

/// <summary>
/// Tests for SQLBatch payload parsing.
/// </summary>
public sealed class SqlBatchMessageTests
{
    [Fact]
    public void Parse_PureSqlWithoutHeaders_ReturnsCorrectText()
    {
        string sql = "SELECT 1";
        byte[] payload = Encoding.Unicode.GetBytes(sql);

        string result = SqlBatchMessage.Parse(payload.AsSpan());

        result.Should().Be(sql);
    }

    [Fact]
    public void Parse_EmptyPayload_ReturnsEmpty()
    {
        string result = SqlBatchMessage.Parse(ReadOnlySpan<byte>.Empty);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_SqlWithAllHeadersBlock_StripsHeaderReturnsOnlySql()
    {
        // Build a payload with a valid ALL_HEADERS block followed by SQL text
        string sql = "SELECT TOP 10 * FROM Orders";
        byte[] sqlBytes = Encoding.Unicode.GetBytes(sql);

        // Transaction descriptor header (type 0x0002, length 18 bytes total)
        // HeaderLength(4) + HeaderType(2) + TransactionDescriptor(8) + OutstandingRequests(4) = 18
        byte[] header = BuildTransactionDescriptorHeader();

        // ALL_HEADERS total length = 4 (length field itself) + header length
        int totalLength = 4 + header.Length;
        byte[] allHeaders = new byte[totalLength];
        BitConverter.GetBytes((uint)totalLength).CopyTo(allHeaders, 0);
        header.CopyTo(allHeaders, 4);

        byte[] payload = allHeaders.Concat(sqlBytes).ToArray();

        string result = SqlBatchMessage.Parse(payload.AsSpan());
        result.Should().Be(sql);
    }

    [Fact]
    public void Parse_MultipleStatements_ReturnsEntireBatch()
    {
        string sql = "SELECT 1; SELECT 2; SELECT 3";
        byte[] payload = Encoding.Unicode.GetBytes(sql);

        string result = SqlBatchMessage.Parse(payload.AsSpan());
        result.Should().Be(sql);
    }

    private static byte[] BuildTransactionDescriptorHeader()
    {
        // Header structure: [Length:4 LE][Type:2 LE][Data:12]
        // Type 0x0002 = Transaction descriptor
        // Data: 8-byte transaction descriptor + 4-byte outstanding requests
        int headerLen = 4 + 2 + 8 + 4; // = 18
        var header = new byte[headerLen];
        BitConverter.GetBytes((uint)headerLen).CopyTo(header, 0);
        BitConverter.GetBytes((ushort)0x0002).CopyTo(header, 4);
        // Rest: zeros (descriptor = 0, outstanding = 0)
        return header;
    }
}
