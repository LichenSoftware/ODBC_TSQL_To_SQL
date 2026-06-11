namespace PgPassthrough.Core.Models;

/// <summary>
/// Describes a single column in a result set, in SQL Server client terms.
/// Used to build the COLMETADATA token in the TDS response.
/// </summary>
public sealed class ColumnMetadata
{
    public required string ColumnName { get; init; }

    /// <summary>SQL Server type code (e.g. SqlDbType value).</summary>
    public SqlServerTypeCode TypeCode { get; init; }

    /// <summary>Max length in bytes. -1 for MAX types (nvarchar(max), etc.).</summary>
    public int MaxLength { get; init; }

    /// <summary>Numeric precision (for decimal/numeric types).</summary>
    public byte Precision { get; init; }

    /// <summary>Numeric scale (for decimal/numeric types).</summary>
    public byte Scale { get; init; }

    public bool IsNullable { get; init; } = true;

    /// <summary>Whether this column has an IDENTITY property.</summary>
    public bool IsIdentity { get; init; }

    /// <summary>Zero-based ordinal position.</summary>
    public int Ordinal { get; init; }
}

/// <summary>
/// SQL Server type codes that appear in TDS COLMETADATA tokens.
/// This is a subset covering the most common types. Extend as needed.
/// </summary>
public enum SqlServerTypeCode : byte
{
    Null = 0x1F,
    TinyInt = 0x30,
    Bit = 0x32,
    SmallInt = 0x34,
    Int = 0x38,
    SmallDateTime = 0x3A,
    Real = 0x3B,
    Money = 0x3C,
    DateTime = 0x3D,
    Float = 0x3E,
    SmallMoney = 0x7A,
    BigInt = 0x7F,
    UniqueIdentifier = 0x24,
    VarBinary = 0xA5,
    VarChar = 0xA7,
    Binary = 0xAD,
    Char = 0xAF,
    NVarChar = 0xE7,
    NChar = 0xEF,
    Xml = 0xF1,
    DateTime2 = 0x2A,
    DateTimeOffset = 0x2B,
    Date = 0x28,
    Time = 0x29,
    Decimal = 0x6A,
    Numeric = 0x6C,
    Image = 0x22,
    Text = 0x23,
    NText = 0x63
}
