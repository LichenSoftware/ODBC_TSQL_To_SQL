namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// TDS packet type codes (first byte of the 8-byte packet header).
/// Reference: MS-TDS §2.2.3.1
/// </summary>
internal static class TdsPacketType
{
    public const byte SqlBatch      = 0x01;
    public const byte PreTds7Login  = 0x02;
    public const byte Rpc           = 0x03;
    public const byte TabularResult = 0x04;
    public const byte AttentionSignal = 0x06;
    public const byte BulkLoadData  = 0x07;
    public const byte FederatedAuthToken = 0x08;
    public const byte TransactionManagerRequest = 0x0E;
    public const byte Login7        = 0x10;
    public const byte Sspi          = 0x11;
    public const byte PreLogin      = 0x12;
}

/// <summary>
/// TDS packet status flags (second byte of the 8-byte packet header).
/// Reference: MS-TDS §2.2.3.1.2
/// </summary>
[Flags]
internal enum TdsPacketStatus : byte
{
    Normal          = 0x00,
    EndOfMessage    = 0x01,
    IgnoreEvent     = 0x02,
    ResetConnection = 0x08,
    ResetConnectionKeepState = 0x10,
}

/// <summary>
/// TDS token type codes embedded within the tabular data stream payload.
/// Reference: MS-TDS §2.2.7
/// </summary>
internal static class TdsTokenType
{
    // Result set tokens
    public const byte ColMetadata   = 0x81;
    public const byte Row           = 0xD1;
    public const byte NbcRow        = 0xD2;  // Null-Bitmap-Compressed row

    // Done tokens
    public const byte Done          = 0xFD;
    public const byte DoneInProc    = 0xFE;
    public const byte DoneProc      = 0xFF;

    // Error / message tokens
    public const byte Error         = 0xAA;
    public const byte Info          = 0xAB;

    // Login / session tokens
    public const byte LoginAck      = 0xAD;
    public const byte EnvChange     = 0xE3;
    public const byte ReturnStatus  = 0x79;
    public const byte ReturnValue   = 0xAC;
    public const byte SessionState  = 0xE4;
    public const byte FeatureExtAck = 0xAE;
    public const byte Order         = 0xA9;
    public const byte Offset        = 0x78;
    public const byte TableName     = 0xA4;
    public const byte ColInfo       = 0xA5;
    public const byte AltMetadata   = 0x88;
    public const byte AltRow        = 0xD3;
}

/// <summary>
/// ENVCHANGE token sub-types.
/// Reference: MS-TDS §2.2.7.13
/// </summary>
internal static class EnvChangeType
{
    public const byte Database      = 0x01;
    public const byte Language      = 0x02;
    public const byte CharSet       = 0x03;
    public const byte PacketSize    = 0x04;
    public const byte SortLocaleId  = 0x05;
    public const byte SortFlags     = 0x06;
    public const byte SqlCollation  = 0x07;
    public const byte BeginTransaction = 0x08;
    public const byte CommitTransaction = 0x09;
    public const byte RollbackTransaction = 0x0A;
    public const byte EnlistDtcTransaction = 0x0B;
    public const byte DefectTransaction = 0x0C;
    public const byte DatabaseMirroring = 0x0D;
    public const byte ResetConnection = 0x12;
    public const byte RoutingInfo   = 0x14;
}

/// <summary>
/// TDS on-the-wire type codes for nullable (N-suffixed) variant types.
/// When a column is nullable, the server sends these instead of the
/// fixed-length type codes. Each is followed by a 1-byte maxLength in COLMETADATA
/// and a 1-byte actual-length prefix per ROW value.
/// Reference: MS-TDS §2.2.5.4.2
/// </summary>
internal static class TdsTypeCode
{
    /// <summary>Nullable integer (1, 2, 4, or 8 bytes).</summary>
    public const byte IntN = 0x26;

    /// <summary>Nullable bit (1 byte).</summary>
    public const byte BitN = 0x68;

    /// <summary>Nullable float/real (4 or 8 bytes).</summary>
    public const byte FltN = 0x6D;

    /// <summary>Nullable money/smallmoney (4 or 8 bytes).</summary>
    public const byte MoneyN = 0x6E;

    /// <summary>Nullable datetime/smalldatetime (4 or 8 bytes).</summary>
    public const byte DateTimeN = 0x6F;
}

/// <summary>
/// Fixed sizes and protocol constants.
/// </summary>
internal static class TdsProtocol
{
    /// <summary>Size of the TDS packet header in bytes.</summary>
    public const int PacketHeaderSize = 8;

    /// <summary>Default negotiated packet size (bytes). Matches SQL Server default.</summary>
    public const int DefaultPacketSize = 4096;

    /// <summary>Maximum packet size we accept from clients.</summary>
    public const int MaxPacketSize = 32768;

    /// <summary>Minimum packet size per spec.</summary>
    public const int MinPacketSize = 512;

    /// <summary>TDS version 7.4 (SQL Server 2012+).</summary>
    public const uint TdsVersion74 = 0x74000004;

    /// <summary>TDS version 7.3B (SQL Server 2008 R2).</summary>
    public const uint TdsVersion73B = 0x730B0003;

    /// <summary>Server name reported to clients.</summary>
    public const string ServerName = "PgPassthrough";

    /// <summary>Reported server version (impersonates SQL Server 2019).</summary>
    public const uint ServerVersion = 0x0F000000; // 15.0.0.0

    /// <summary>Collation LCID: 1033 = en-US.</summary>
    public const int CollationLcid = 1033;
}
