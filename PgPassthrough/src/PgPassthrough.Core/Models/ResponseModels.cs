namespace PgPassthrough.Core.Models;

/// <summary>
/// Status flags for the TDS DONE token.
/// </summary>
[Flags]
public enum DoneStatus : ushort
{
    Final = 0x00,
    More = 0x01,
    Error = 0x02,
    InTransaction = 0x04,
    Count = 0x10,
    Attention = 0x20,
    RpcInBatch = 0x80,
    ServerError = 0x100
}

/// <summary>An error to be sent to the client as a TDS ERROR token.</summary>
public sealed class ServerError
{
    public required string Message { get; init; }
    public int Number { get; init; } = 50000;
    public byte Severity { get; init; } = 16;
    public byte State { get; init; } = 1;
    public string ServerName { get; init; } = "PgPassthrough";
    public string ProcedureName { get; init; } = string.Empty;
    public int LineNumber { get; init; }
}

/// <summary>An informational message (TDS INFO token, severity 0-10).</summary>
public sealed class ServerMessage
{
    public required string Message { get; init; }
    public int Number { get; init; } = 0;
    public byte Severity { get; init; } = 0;
    public string ServerName { get; init; } = "PgPassthrough";
}
