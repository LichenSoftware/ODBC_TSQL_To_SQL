using Npgsql;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Execution;

/// <summary>
/// Wraps an <see cref="NpgsqlDataReader"/> as an <see cref="IResultSet"/>.
/// Maps PostgreSQL column types to SQL Server type codes for TDS encoding.
/// </summary>
public sealed class NpgsqlResultSet : IResultSet
{
    private readonly NpgsqlDataReader _reader;
    private IReadOnlyList<ColumnMetadata>? _columns;
    private long _rowsAffected;

    public IReadOnlyList<ColumnMetadata> Columns => _columns ?? Array.Empty<ColumnMetadata>();
    public long RowsAffected => _rowsAffected;

    public NpgsqlResultSet(NpgsqlDataReader reader)
    {
        _reader = reader;
    }

    /// <summary>
    /// Must be called after construction to build column metadata from the reader's schema.
    /// </summary>
    internal async Task InitializeAsync(CancellationToken ct = default)
    {
        _rowsAffected = _reader.RecordsAffected;
        BuildColumnMetadata();
        await Task.CompletedTask; // schema is available synchronously after ExecuteReader
    }

    public async ValueTask<bool> ReadAsync(CancellationToken cancellationToken = default)
    {
        bool hasRow = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return hasRow;
    }

    public object? GetValue(int columnIndex)
    {
        if (_reader.IsDBNull(columnIndex))
            return null;
        return _reader.GetValue(columnIndex);
    }

    public async ValueTask<bool> NextResultAsync(CancellationToken cancellationToken = default)
    {
        bool hasNext = await _reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        if (hasNext)
        {
            _rowsAffected = _reader.RecordsAffected;
            BuildColumnMetadata();
        }
        return hasNext;
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------
    // Column metadata construction
    // -------------------------------------------------------------------------

    private void BuildColumnMetadata()
    {
        if (_reader.FieldCount == 0)
        {
            _columns = Array.Empty<ColumnMetadata>();
            return;
        }

        var columns = new List<ColumnMetadata>(_reader.FieldCount);
        for (int i = 0; i < _reader.FieldCount; i++)
        {
            string pgTypeName = _reader.GetDataTypeName(i);
            string colName = _reader.GetName(i);
            Type clrType = _reader.GetFieldType(i);

            var (typeCode, maxLen, precision, scale) = MapPgTypeToSqlServer(pgTypeName, clrType);

            columns.Add(new ColumnMetadata
            {
                ColumnName = string.IsNullOrEmpty(colName) ? $"column{i}" : colName,
                TypeCode = typeCode,
                MaxLength = maxLen,
                Precision = precision,
                Scale = scale,
                IsNullable = true,
                Ordinal = i
            });
        }
        _columns = columns;
    }

    /// <summary>
    /// Maps a PostgreSQL type name to the closest SQL Server type code + metadata.
    /// </summary>
    private static (SqlServerTypeCode TypeCode, int MaxLength, byte Precision, byte Scale) MapPgTypeToSqlServer(
        string pgTypeName, Type clrType)
    {
        string typeName = pgTypeName.ToLowerInvariant();

        return typeName switch
        {
            "boolean" or "bool" => (SqlServerTypeCode.Bit, 0, 0, 0),
            "smallint" or "int2" => (SqlServerTypeCode.SmallInt, 0, 0, 0),
            "integer" or "int4" or "int" => (SqlServerTypeCode.Int, 0, 0, 0),
            "bigint" or "int8" => (SqlServerTypeCode.BigInt, 0, 0, 0),
            "real" or "float4" => (SqlServerTypeCode.Real, 0, 0, 0),
            "double precision" or "float8" => (SqlServerTypeCode.Float, 0, 0, 0),
            "numeric" or "decimal" => (SqlServerTypeCode.Decimal, 0, 38, 10),
            "money" => (SqlServerTypeCode.Money, 0, 0, 0),
            "text" => (SqlServerTypeCode.NVarChar, -1, 0, 0),
            "character varying" or "varchar" => (SqlServerTypeCode.NVarChar, 4000, 0, 0),
            "character" or "char" or "bpchar" => (SqlServerTypeCode.NChar, 4000, 0, 0),
            "bytea" => (SqlServerTypeCode.VarBinary, -1, 0, 0),
            "uuid" => (SqlServerTypeCode.UniqueIdentifier, 0, 0, 0),
            "date" => (SqlServerTypeCode.Date, 0, 0, 0),
            "time" or "time without time zone" => (SqlServerTypeCode.Time, 0, 0, 7),
            "time with time zone" or "timetz" => (SqlServerTypeCode.Time, 0, 0, 7),
            "timestamp" or "timestamp without time zone" => (SqlServerTypeCode.DateTime2, 0, 0, 7),
            "timestamp with time zone" or "timestamptz" => (SqlServerTypeCode.DateTimeOffset, 0, 0, 7),
            "interval" => (SqlServerTypeCode.NVarChar, 100, 0, 0),
            "json" or "jsonb" => (SqlServerTypeCode.NVarChar, -1, 0, 0),
            "xml" => (SqlServerTypeCode.Xml, 0, 0, 0),
            "inet" or "cidr" or "macaddr" => (SqlServerTypeCode.NVarChar, 50, 0, 0),
            "bit" or "bit varying" or "varbit" => (SqlServerTypeCode.VarBinary, 128, 0, 0),
            "oid" => (SqlServerTypeCode.Int, 0, 0, 0),
            "name" => (SqlServerTypeCode.NVarChar, 128, 0, 0),
            _ => MapByClrType(clrType)
        };
    }

    /// <summary>Fallback: map by the .NET CLR type that Npgsql returns.</summary>
    private static (SqlServerTypeCode TypeCode, int MaxLength, byte Precision, byte Scale) MapByClrType(Type clrType)
    {
        if (clrType == typeof(bool)) return (SqlServerTypeCode.Bit, 0, 0, 0);
        if (clrType == typeof(short)) return (SqlServerTypeCode.SmallInt, 0, 0, 0);
        if (clrType == typeof(int)) return (SqlServerTypeCode.Int, 0, 0, 0);
        if (clrType == typeof(long)) return (SqlServerTypeCode.BigInt, 0, 0, 0);
        if (clrType == typeof(float)) return (SqlServerTypeCode.Real, 0, 0, 0);
        if (clrType == typeof(double)) return (SqlServerTypeCode.Float, 0, 0, 0);
        if (clrType == typeof(decimal)) return (SqlServerTypeCode.Decimal, 0, 38, 10);
        if (clrType == typeof(Guid)) return (SqlServerTypeCode.UniqueIdentifier, 0, 0, 0);
        if (clrType == typeof(DateTime)) return (SqlServerTypeCode.DateTime2, 0, 0, 7);
        if (clrType == typeof(DateTimeOffset)) return (SqlServerTypeCode.DateTimeOffset, 0, 0, 7);
        if (clrType == typeof(TimeSpan)) return (SqlServerTypeCode.Time, 0, 0, 7);
        if (clrType == typeof(byte[])) return (SqlServerTypeCode.VarBinary, -1, 0, 0);
        if (clrType == typeof(string)) return (SqlServerTypeCode.NVarChar, 4000, 0, 0);

        // Ultimate fallback
        return (SqlServerTypeCode.NVarChar, 4000, 0, 0);
    }
}
