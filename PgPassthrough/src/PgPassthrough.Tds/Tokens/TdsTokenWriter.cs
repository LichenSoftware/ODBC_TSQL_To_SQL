using System.Text;
using PgPassthrough.Core.Models;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds.Tokens;

/// <summary>
/// Writes TDS response tokens into a <see cref="TdsPacketWriter"/>.
/// Each public method appends one complete token to the writer's payload buffer.
/// 
/// Token layout references: MS-TDS §2.2.7.*
/// </summary>
internal sealed class TdsTokenWriter
{
    private readonly TdsPacketWriter _writer;

    public TdsTokenWriter(TdsPacketWriter writer)
    {
        _writer = writer;
    }

    // -------------------------------------------------------------------------
    // LOGINACK token (0xAD)
    // Sent in response to a successful Login7.
    // Reference: MS-TDS §2.2.7.14
    // -------------------------------------------------------------------------

    public void WriteLoginAck(uint tdsVersion)
    {
        // Build token body first so we know its length
        using var body = new BodyBuilder();
        body.WriteByte(0x01);            // Interface: SQL_DFLT (1 = TDS/SQL)
        body.WriteUInt32BE(tdsVersion);  // TDS version echoed back
        // Program name: B_VARCHAR "PgPassthrough"
        const string progName = TdsProtocol.ServerName;
        body.WriteByte((byte)progName.Length);
        body.WriteBytes(Encoding.Unicode.GetBytes(progName));
        // Server version: major.minor.build (4 bytes)
        body.WriteByte(15); // major (SQL Server 2019 = 15)
        body.WriteByte(0);  // minor
        body.WriteByte(0);  // build high
        body.WriteByte(0);  // build low

        _writer.WriteByte(TdsTokenType.LoginAck);
        _writer.WriteUInt16LE((ushort)body.Length);
        _writer.WriteBytes(body.ToSpan());
    }

    // -------------------------------------------------------------------------
    // ENVCHANGE token (0xE3)
    // Reference: MS-TDS §2.2.7.13
    // -------------------------------------------------------------------------

    public void WriteEnvChangeDatabase(string newDb, string oldDb)
        => WriteEnvChangeString(EnvChangeType.Database, newDb, oldDb);

    public void WriteEnvChangePacketSize(int newSize, int oldSize)
        => WriteEnvChangeString(EnvChangeType.PacketSize, newSize.ToString(), oldSize.ToString());

    public void WriteEnvChangeBeginTransaction(ulong transactionDescriptor)
    {
        // EnvChange for BEGIN TRANSACTION carries the 8-byte transaction descriptor
        using var body = new BodyBuilder();
        body.WriteByte(EnvChangeType.BeginTransaction);
        body.WriteByte(8); // new value length
        body.WriteUInt64LE(transactionDescriptor);
        body.WriteByte(0); // old value length (empty)

        _writer.WriteByte(TdsTokenType.EnvChange);
        _writer.WriteUInt16LE((ushort)(body.Length));
        _writer.WriteBytes(body.ToSpan());
    }

    public void WriteEnvChangeCommitTransaction()
        => WriteEnvChangeTransactionEnd(EnvChangeType.CommitTransaction);

    public void WriteEnvChangeRollbackTransaction()
        => WriteEnvChangeTransactionEnd(EnvChangeType.RollbackTransaction);

    private void WriteEnvChangeTransactionEnd(byte envType)
    {
        using var body = new BodyBuilder();
        body.WriteByte(envType);
        body.WriteByte(0); // new value: empty
        body.WriteByte(0); // old value: empty

        _writer.WriteByte(TdsTokenType.EnvChange);
        _writer.WriteUInt16LE((ushort)body.Length);
        _writer.WriteBytes(body.ToSpan());
    }

    private void WriteEnvChangeString(byte envType, string newVal, string oldVal)
    {
        using var body = new BodyBuilder();
        body.WriteByte(envType);
        // new value: B_VARCHAR
        body.WriteByte((byte)newVal.Length);
        body.WriteBytes(Encoding.Unicode.GetBytes(newVal));
        // old value: B_VARCHAR
        body.WriteByte((byte)oldVal.Length);
        body.WriteBytes(Encoding.Unicode.GetBytes(oldVal));

        _writer.WriteByte(TdsTokenType.EnvChange);
        _writer.WriteUInt16LE((ushort)body.Length);
        _writer.WriteBytes(body.ToSpan());
    }

    // -------------------------------------------------------------------------
    // ERROR token (0xAA) and INFO token (0xAB)
    // Reference: MS-TDS §2.2.7.10
    // -------------------------------------------------------------------------

    public void WriteError(ServerError error)
        => WriteErrorOrInfo(TdsTokenType.Error, error.Number, error.Severity, error.State,
            error.Message, error.ServerName, error.ProcedureName, error.LineNumber);

    public void WriteInfo(ServerMessage msg)
        => WriteErrorOrInfo(TdsTokenType.Info, msg.Number, msg.Severity, 0,
            msg.Message, msg.ServerName, string.Empty, 0);

    private void WriteErrorOrInfo(byte tokenType, int number, byte severity, byte state,
        string message, string serverName, string procName, int lineNumber)
    {
        using var body = new BodyBuilder();
        body.WriteInt32LE(number);
        body.WriteByte(severity);
        body.WriteByte(state);
        // Message: US_VARCHAR
        body.WriteUInt16LE((ushort)message.Length);
        body.WriteBytes(Encoding.Unicode.GetBytes(message));
        // Server name: B_VARCHAR
        body.WriteByte((byte)Math.Min(serverName.Length, 128));
        body.WriteBytes(Encoding.Unicode.GetBytes(serverName[..Math.Min(serverName.Length, 128)]));
        // Proc name: B_VARCHAR
        body.WriteByte((byte)Math.Min(procName.Length, 128));
        if (procName.Length > 0)
            body.WriteBytes(Encoding.Unicode.GetBytes(procName[..Math.Min(procName.Length, 128)]));
        // Line number: 4 bytes LE (TDS 7.2+)
        body.WriteInt32LE(lineNumber);

        _writer.WriteByte(tokenType);
        _writer.WriteUInt16LE((ushort)body.Length);
        _writer.WriteBytes(body.ToSpan());
    }

    // -------------------------------------------------------------------------
    // COLMETADATA token (0x81)
    // Reference: MS-TDS §2.2.7.4
    // -------------------------------------------------------------------------

    public void WriteColMetadata(IReadOnlyList<ColumnMetadata> columns)
    {
        if (columns.Count == 0)
        {
            // No-metadata indicator: write 0xFFFF count
            _writer.WriteByte(TdsTokenType.ColMetadata);
            _writer.WriteUInt16LE(0xFFFF);
            return;
        }

        _writer.WriteByte(TdsTokenType.ColMetadata);
        _writer.WriteUInt16LE((ushort)columns.Count);

        foreach (var col in columns)
        {
            WriteColumnData(col);
        }
    }

    private void WriteColumnData(ColumnMetadata col)
    {
        // UserType: 4 bytes (0 for most types)
        _writer.WriteUInt32LE(0);

        // Flags: 2 bytes
        ushort flags = 0;
        if (col.IsNullable) flags |= 0x0001;
        if (col.IsIdentity) flags |= 0x0010;
        _writer.WriteUInt16LE(flags);

        // Type info
        WriteTypeInfo(col);

        // Column name: B_VARCHAR
        _writer.WriteByte((byte)col.ColumnName.Length);
        _writer.WriteBytes(Encoding.Unicode.GetBytes(col.ColumnName));
    }

    private void WriteTypeInfo(ColumnMetadata col)
    {
        switch (col.TypeCode)
        {
            // ---------------------------------------------------------------
            // Integer types → always use INTN (0x26) so they are nullable.
            // INTN metadata: type byte + 1-byte maxLength.
            // ---------------------------------------------------------------
            case SqlServerTypeCode.TinyInt:
                _writer.WriteByte(TdsTypeCode.IntN);
                _writer.WriteByte(1);
                break;

            case SqlServerTypeCode.SmallInt:
                _writer.WriteByte(TdsTypeCode.IntN);
                _writer.WriteByte(2);
                break;

            case SqlServerTypeCode.Int:
                _writer.WriteByte(TdsTypeCode.IntN);
                _writer.WriteByte(4);
                break;

            case SqlServerTypeCode.BigInt:
                _writer.WriteByte(TdsTypeCode.IntN);
                _writer.WriteByte(8);
                break;

            // ---------------------------------------------------------------
            // Bit → BITN (0x68) + 1-byte maxLength (always 1)
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Bit:
                _writer.WriteByte(TdsTypeCode.BitN);
                _writer.WriteByte(1);
                break;

            // ---------------------------------------------------------------
            // Float / Real → FLTN (0x6D) + 1-byte maxLength (4 or 8)
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Real:
                _writer.WriteByte(TdsTypeCode.FltN);
                _writer.WriteByte(4);
                break;

            case SqlServerTypeCode.Float:
                _writer.WriteByte(TdsTypeCode.FltN);
                _writer.WriteByte(8);
                break;

            // ---------------------------------------------------------------
            // Money / SmallMoney → MONEYN (0x6E) + 1-byte maxLength (4 or 8)
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Money:
                _writer.WriteByte(TdsTypeCode.MoneyN);
                _writer.WriteByte(8);
                break;

            case SqlServerTypeCode.SmallMoney:
                _writer.WriteByte(TdsTypeCode.MoneyN);
                _writer.WriteByte(4);
                break;

            // ---------------------------------------------------------------
            // DateTime / SmallDateTime → DATETIMN (0x6F) + 1-byte maxLength
            // ---------------------------------------------------------------
            case SqlServerTypeCode.DateTime:
                _writer.WriteByte(TdsTypeCode.DateTimeN);
                _writer.WriteByte(8);
                break;

            case SqlServerTypeCode.SmallDateTime:
                _writer.WriteByte(TdsTypeCode.DateTimeN);
                _writer.WriteByte(4);
                break;

            // ---------------------------------------------------------------
            // Length-prefixed Unicode string types
            // ---------------------------------------------------------------
            case SqlServerTypeCode.NVarChar:
            case SqlServerTypeCode.NChar:
            {
                _writer.WriteByte((byte)col.TypeCode);
                ushort maxLen = col.MaxLength == -1 ? (ushort)0xFFFF : (ushort)(col.MaxLength * 2);
                _writer.WriteUInt16LE(maxLen);
                WriteCollation();
                break;
            }

            // ---------------------------------------------------------------
            // Length-prefixed ANSI string types
            // ---------------------------------------------------------------
            case SqlServerTypeCode.VarChar:
            case SqlServerTypeCode.Char:
            {
                _writer.WriteByte((byte)col.TypeCode);
                ushort maxLen = col.MaxLength == -1 ? (ushort)0xFFFF : (ushort)col.MaxLength;
                _writer.WriteUInt16LE(maxLen);
                WriteCollation();
                break;
            }

            // ---------------------------------------------------------------
            // Length-prefixed binary types
            // ---------------------------------------------------------------
            case SqlServerTypeCode.VarBinary:
            case SqlServerTypeCode.Binary:
            {
                _writer.WriteByte((byte)col.TypeCode);
                ushort maxLen = col.MaxLength == -1 ? (ushort)0xFFFF : (ushort)col.MaxLength;
                _writer.WriteUInt16LE(maxLen);
                break;
            }

            // ---------------------------------------------------------------
            // Decimal / Numeric — 1-byte maxLength + precision + scale
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Decimal:
            case SqlServerTypeCode.Numeric:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(17); // max byte length for decimal
                _writer.WriteByte(col.Precision == 0 ? (byte)18 : col.Precision);
                _writer.WriteByte(col.Scale);
                break;
            }

            // ---------------------------------------------------------------
            // UniqueIdentifier (GUID) — nullable variant uses length byte
            // ---------------------------------------------------------------
            case SqlServerTypeCode.UniqueIdentifier:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(16);
                break;
            }

            // ---------------------------------------------------------------
            // DateTime2 / DateTimeOffset / Time — scale byte
            // ---------------------------------------------------------------
            case SqlServerTypeCode.DateTime2:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(col.Scale == 0 ? (byte)7 : col.Scale);
                break;
            }

            case SqlServerTypeCode.DateTimeOffset:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(col.Scale == 0 ? (byte)7 : col.Scale);
                break;
            }

            case SqlServerTypeCode.Date:
                _writer.WriteByte((byte)col.TypeCode);
                break;

            case SqlServerTypeCode.Time:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(col.Scale == 0 ? (byte)7 : col.Scale);
                break;
            }

            // ---------------------------------------------------------------
            // XML
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Xml:
            {
                _writer.WriteByte((byte)col.TypeCode);
                _writer.WriteByte(0); // SchemaPresent = 0
                break;
            }

            default:
                // Fallback: write as NVarChar(4000)
                _writer.WriteByte((byte)SqlServerTypeCode.NVarChar);
                _writer.WriteUInt16LE(8000); // 4000 chars * 2
                WriteCollation();
                break;
        }
    }

    // Standard en-US collation (5 bytes)
    private void WriteCollation()
    {
        // LCID 1033 (en-US), SortId 52, Version 0
        // Bytes: 09 04 D0 00 34
        _writer.WriteBytes(new byte[] { 0x09, 0x04, 0xD0, 0x00, 0x34 });
    }

    // -------------------------------------------------------------------------
    // ROW token (0xD1)
    // Reference: MS-TDS §2.2.7.18
    // -------------------------------------------------------------------------

    public void WriteRow(IReadOnlyList<ColumnMetadata> columns, object?[] values)
    {
        _writer.WriteByte(TdsTokenType.Row);

        for (int i = 0; i < columns.Count; i++)
        {
            object? value = i < values.Length ? values[i] : null;
            WriteColumnValue(columns[i], value);
        }
    }

    private void WriteColumnValue(ColumnMetadata col, object? value)
    {
        if (value == null || value == DBNull.Value)
        {
            WriteNullValue(col);
            return;
        }

        switch (col.TypeCode)
        {
            // ---------------------------------------------------------------
            // Nullable fixed-length types: 1-byte length prefix + data
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Bit:
                _writer.WriteByte(1); // length
                _writer.WriteByte(Convert.ToByte(Convert.ToBoolean(value) ? 1 : 0));
                break;

            case SqlServerTypeCode.TinyInt:
                _writer.WriteByte(1); // length
                _writer.WriteByte(Convert.ToByte(value));
                break;

            case SqlServerTypeCode.SmallInt:
                _writer.WriteByte(2); // length
                _writer.WriteInt16LE(Convert.ToInt16(value));
                break;

            case SqlServerTypeCode.Int:
                _writer.WriteByte(4); // length
                _writer.WriteInt32LE(Convert.ToInt32(value));
                break;

            case SqlServerTypeCode.BigInt:
                _writer.WriteByte(8); // length
                _writer.WriteInt64LE(Convert.ToInt64(value));
                break;

            case SqlServerTypeCode.Real:
                _writer.WriteByte(4); // length
                _writer.WriteBytes(BitConverter.GetBytes(Convert.ToSingle(value)));
                break;

            case SqlServerTypeCode.Float:
                _writer.WriteByte(8); // length
                _writer.WriteBytes(BitConverter.GetBytes(Convert.ToDouble(value)));
                break;

            case SqlServerTypeCode.Money:
                _writer.WriteByte(8); // length
                WriteMoney(Convert.ToDecimal(value));
                break;

            case SqlServerTypeCode.SmallMoney:
                _writer.WriteByte(4); // length
                WriteSmallMoney(Convert.ToDecimal(value));
                break;

            case SqlServerTypeCode.DateTime:
                _writer.WriteByte(8); // length
                WriteDateTime(Convert.ToDateTime(value));
                break;

            case SqlServerTypeCode.SmallDateTime:
                _writer.WriteByte(4); // length
                WriteSmallDateTime(Convert.ToDateTime(value));
                break;

            // ---------------------------------------------------------------
            // Variable-length string types: 2-byte length prefix + data
            // ---------------------------------------------------------------
            case SqlServerTypeCode.NVarChar:
            case SqlServerTypeCode.NChar:
            {
                string s = Convert.ToString(value) ?? string.Empty;
                byte[] encoded = Encoding.Unicode.GetBytes(s);
                if (col.MaxLength == -1)
                {
                    // PLP (Partially Length-Prefixed) format for nvarchar(max)
                    _writer.WriteUInt64LE((ulong)encoded.Length); // total length
                    if (encoded.Length > 0)
                    {
                        _writer.WriteUInt32LE((uint)encoded.Length); // chunk size
                        _writer.WriteBytes(encoded);
                        _writer.WriteUInt32LE(0); // terminator chunk
                    }
                }
                else
                {
                    _writer.WriteUInt16LE((ushort)encoded.Length);
                    _writer.WriteBytes(encoded);
                }
                break;
            }

            case SqlServerTypeCode.VarChar:
            case SqlServerTypeCode.Char:
            {
                string s = Convert.ToString(value) ?? string.Empty;
                byte[] encoded = Encoding.UTF8.GetBytes(s);
                if (col.MaxLength == -1)
                {
                    _writer.WriteUInt64LE((ulong)encoded.Length);
                    if (encoded.Length > 0)
                    {
                        _writer.WriteUInt32LE((uint)encoded.Length);
                        _writer.WriteBytes(encoded);
                        _writer.WriteUInt32LE(0);
                    }
                }
                else
                {
                    _writer.WriteUInt16LE((ushort)encoded.Length);
                    _writer.WriteBytes(encoded);
                }
                break;
            }

            // ---------------------------------------------------------------
            // UniqueIdentifier (GUID) — 1-byte length prefix + 16 bytes
            // ---------------------------------------------------------------
            case SqlServerTypeCode.UniqueIdentifier:
            {
                Guid g = value is Guid gv ? gv : Guid.Parse(value.ToString()!);
                _writer.WriteByte(16); // length
                _writer.WriteBytes(g.ToByteArray());
                break;
            }

            // ---------------------------------------------------------------
            // Decimal / Numeric — 1-byte length + sign byte + int128 magnitude
            // ---------------------------------------------------------------
            case SqlServerTypeCode.Decimal:
            case SqlServerTypeCode.Numeric:
            {
                decimal d = Convert.ToDecimal(value);
                WriteDecimalValue(d, col.Scale);
                break;
            }

            // ---------------------------------------------------------------
            // Date/Time types (non-legacy)
            // ---------------------------------------------------------------
            case SqlServerTypeCode.DateTime2:
            {
                DateTime dt = Convert.ToDateTime(value);
                byte scale = col.Scale == 0 ? (byte)7 : col.Scale;
                byte timeLen = GetTimeLength(scale);
                _writer.WriteByte((byte)(timeLen + 3)); // time bytes + 3-byte date
                WriteTimeComponent(dt, scale, timeLen);
                WriteDateComponent(dt);
                break;
            }

            case SqlServerTypeCode.Date:
            {
                DateTime dt = Convert.ToDateTime(value);
                _writer.WriteByte(3); // length: always 3 bytes
                WriteDateComponent(dt);
                break;
            }

            case SqlServerTypeCode.Time:
            {
                DateTime dt = Convert.ToDateTime(value);
                byte scale = col.Scale == 0 ? (byte)7 : col.Scale;
                byte timeLen = GetTimeLength(scale);
                _writer.WriteByte(timeLen);
                WriteTimeComponent(dt, scale, timeLen);
                break;
            }

            case SqlServerTypeCode.DateTimeOffset:
            {
                DateTimeOffset dto = value is DateTimeOffset dtoVal
                    ? dtoVal
                    : new DateTimeOffset(Convert.ToDateTime(value));
                byte scale = col.Scale == 0 ? (byte)7 : col.Scale;
                byte timeLen = GetTimeLength(scale);
                _writer.WriteByte((byte)(timeLen + 3 + 2)); // time + date + offset
                WriteTimeComponent(dto.DateTime, scale, timeLen);
                WriteDateComponent(dto.DateTime);
                short offsetMinutes = (short)dto.Offset.TotalMinutes;
                _writer.WriteInt16LE(offsetMinutes);
                break;
            }

            // ---------------------------------------------------------------
            // Binary types: 2-byte length prefix + data
            // ---------------------------------------------------------------
            case SqlServerTypeCode.VarBinary:
            case SqlServerTypeCode.Binary:
            {
                byte[] bytes = value as byte[] ?? Array.Empty<byte>();
                if (col.MaxLength == -1)
                {
                    _writer.WriteUInt64LE((ulong)bytes.Length);
                    if (bytes.Length > 0)
                    {
                        _writer.WriteUInt32LE((uint)bytes.Length);
                        _writer.WriteBytes(bytes);
                        _writer.WriteUInt32LE(0);
                    }
                }
                else
                {
                    _writer.WriteUInt16LE((ushort)bytes.Length);
                    _writer.WriteBytes(bytes);
                }
                break;
            }

            default:
            {
                // Fallback: serialise to string as NVarChar
                string s = value.ToString() ?? string.Empty;
                byte[] encoded = Encoding.Unicode.GetBytes(s);
                _writer.WriteUInt16LE((ushort)encoded.Length);
                _writer.WriteBytes(encoded);
                break;
            }
        }
    }

    private void WriteNullValue(ColumnMetadata col)
    {
        switch (col.TypeCode)
        {
            // Nullable fixed-length types: length byte = 0 means NULL
            case SqlServerTypeCode.Bit:
            case SqlServerTypeCode.TinyInt:
            case SqlServerTypeCode.SmallInt:
            case SqlServerTypeCode.Int:
            case SqlServerTypeCode.BigInt:
            case SqlServerTypeCode.Real:
            case SqlServerTypeCode.Float:
            case SqlServerTypeCode.Money:
            case SqlServerTypeCode.SmallMoney:
            case SqlServerTypeCode.DateTime:
            case SqlServerTypeCode.SmallDateTime:
            case SqlServerTypeCode.UniqueIdentifier:
            case SqlServerTypeCode.Decimal:
            case SqlServerTypeCode.Numeric:
                _writer.WriteByte(0); // length = 0 → NULL
                break;

            // Variable-length string/binary: 0xFFFF = NULL
            case SqlServerTypeCode.NVarChar:
            case SqlServerTypeCode.NChar:
            case SqlServerTypeCode.VarChar:
            case SqlServerTypeCode.Char:
            case SqlServerTypeCode.VarBinary:
            case SqlServerTypeCode.Binary:
                if (col.MaxLength == -1)
                {
                    // PLP NULL: 0xFFFFFFFFFFFFFFFF
                    _writer.WriteUInt64LE(0xFFFFFFFFFFFFFFFF);
                }
                else
                {
                    _writer.WriteUInt16LE(0xFFFF);
                }
                break;

            // Date/Time types with length prefix
            case SqlServerTypeCode.DateTime2:
            case SqlServerTypeCode.Date:
            case SqlServerTypeCode.Time:
            case SqlServerTypeCode.DateTimeOffset:
                _writer.WriteByte(0); // length = 0 → NULL
                break;

            default:
                _writer.WriteUInt16LE(0xFFFF);
                break;
        }
    }

    private void WriteDecimalValue(decimal value, byte scale)
    {
        // TDS decimal: 1-byte length + 1-byte sign + 16-byte magnitude (int128)
        int[] bits = decimal.GetBits(value);
        bool positive = value >= 0;

        _writer.WriteByte(17); // total data length: 1 sign + 16 magnitude
        _writer.WriteByte(positive ? (byte)1 : (byte)0); // 1=positive, 0=negative

        // Write magnitude as 128-bit LE integer (4 x uint32)
        _writer.WriteUInt32LE((uint)bits[0]);
        _writer.WriteUInt32LE((uint)bits[1]);
        _writer.WriteUInt32LE((uint)bits[2]);
        _writer.WriteUInt32LE(0); // high 32 bits always 0 for .NET decimal
    }

    private void WriteDateTime(DateTime value)
    {
        // TDS DATETIME (DATETIMN len=8): days since 1900-01-01 (int32) + 1/300s ticks (int32)
        int days = (int)(value.Date - new DateTime(1900, 1, 1)).TotalDays;
        int ticks = (int)(value.TimeOfDay.TotalSeconds * 300);
        _writer.WriteInt32LE(days);
        _writer.WriteInt32LE(ticks);
    }

    private void WriteSmallDateTime(DateTime value)
    {
        // TDS SMALLDATETIME (DATETIMN len=4): days since 1900-01-01 (uint16) + minutes (uint16)
        ushort days = (ushort)(value.Date - new DateTime(1900, 1, 1)).TotalDays;
        ushort minutes = (ushort)value.TimeOfDay.TotalMinutes;
        _writer.WriteUInt16LE(days);
        _writer.WriteUInt16LE(minutes);
    }

    private void WriteMoney(decimal value)
    {
        // TDS MONEY: int64 representing value * 10000
        long moneyVal = (long)(value * 10000m);
        // Money is stored as high-int32 then low-int32 (not standard LE int64!)
        int high = (int)(moneyVal >> 32);
        int low = (int)(moneyVal & 0xFFFFFFFF);
        _writer.WriteInt32LE(high);
        _writer.WriteInt32LE(low);
    }

    private void WriteSmallMoney(decimal value)
    {
        // TDS SMALLMONEY: int32 representing value * 10000
        int moneyVal = (int)(value * 10000m);
        _writer.WriteInt32LE(moneyVal);
    }

    /// <summary>Gets the byte length of the time component for a given scale.</summary>
    private static byte GetTimeLength(byte scale) => scale switch
    {
        <= 2 => 3,
        <= 4 => 4,
        _    => 5  // scale 5-7
    };

    private void WriteTimeComponent(DateTime value, byte scale, byte timeLen)
    {
        // Time value in units of 10^(-scale) seconds
        long ticks = value.TimeOfDay.Ticks; // in 100ns units
        // Convert to scale units: ticks / (10^(7-scale))
        long divisor = (long)Math.Pow(10, 7 - scale);
        long timeVal = ticks / divisor;

        // Write as LE integer of timeLen bytes
        for (int i = 0; i < timeLen; i++)
        {
            _writer.WriteByte((byte)(timeVal & 0xFF));
            timeVal >>= 8;
        }
    }

    private void WriteDateComponent(DateTime value)
    {
        // TDS DATE: days since 0001-01-01 as 3-byte LE integer
        int days = (int)(value.Date - new DateTime(1, 1, 1)).TotalDays;
        _writer.WriteByte((byte)(days & 0xFF));
        _writer.WriteByte((byte)((days >> 8) & 0xFF));
        _writer.WriteByte((byte)((days >> 16) & 0xFF));
    }

    // -------------------------------------------------------------------------
    // DONE token (0xFD), DONEINPROC (0xFE), DONEPROC (0xFF)
    // Reference: MS-TDS §2.2.7.6
    // -------------------------------------------------------------------------

    public void WriteDone(DoneStatus status, ushort curCmd, long rowCount)
        => WriteDoneToken(TdsTokenType.Done, status, curCmd, rowCount);

    public void WriteDoneInProc(DoneStatus status, ushort curCmd, long rowCount)
        => WriteDoneToken(TdsTokenType.DoneInProc, status, curCmd, rowCount);

    public void WriteDoneProc(DoneStatus status, ushort curCmd, long rowCount)
        => WriteDoneToken(TdsTokenType.DoneProc, status, curCmd, rowCount);

    private void WriteDoneToken(byte tokenType, DoneStatus status, ushort curCmd, long rowCount)
    {
        _writer.WriteByte(tokenType);
        _writer.WriteUInt16LE((ushort)status);
        _writer.WriteUInt16LE(curCmd);
        _writer.WriteUInt64LE((ulong)rowCount);
    }

    // -------------------------------------------------------------------------
    // Helper: small inline buffer for token body construction
    // -------------------------------------------------------------------------

    private sealed class BodyBuilder : IDisposable
    {
        private readonly MemoryStream _ms = new();

        public int Length => (int)_ms.Length;

        public void WriteByte(byte b) => _ms.WriteByte(b);

        public void WriteBytes(ReadOnlySpan<byte> bytes) => _ms.Write(bytes);

        public void WriteUInt16LE(ushort v)
        {
            _ms.WriteByte((byte)(v & 0xFF));
            _ms.WriteByte((byte)(v >> 8));
        }

        public void WriteUInt32BE(uint v)
        {
            _ms.WriteByte((byte)(v >> 24));
            _ms.WriteByte((byte)((v >> 16) & 0xFF));
            _ms.WriteByte((byte)((v >> 8) & 0xFF));
            _ms.WriteByte((byte)(v & 0xFF));
        }

        public void WriteUInt64LE(ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                _ms.WriteByte((byte)(v & 0xFF));
                v >>= 8;
            }
        }

        public void WriteInt32LE(int v) => WriteUInt32LE_Internal((uint)v);

        private void WriteUInt32LE_Internal(uint v)
        {
            _ms.WriteByte((byte)(v & 0xFF));
            _ms.WriteByte((byte)((v >> 8) & 0xFF));
            _ms.WriteByte((byte)((v >> 16) & 0xFF));
            _ms.WriteByte((byte)((v >> 24) & 0xFF));
        }

        public ReadOnlySpan<byte> ToSpan() => _ms.GetBuffer().AsSpan(0, (int)_ms.Length);

        public void Dispose() => _ms.Dispose();
    }
}
