namespace PgPassthrough.Core.Models;

/// <summary>
/// A named or positional query parameter with its SQL Server type information.
/// </summary>
public sealed class QueryParameter
{
    /// <summary>
    /// Parameter name including the @ prefix, e.g. "@CustomerId".
    /// For positional parameters, use "@p1", "@p2", etc.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>The parameter value. Null represents SQL NULL.</summary>
    public object? Value { get; init; }

    /// <summary>The declared T-SQL data type, e.g. "nvarchar(50)", "int".</summary>
    public string? TsqlType { get; init; }

    /// <summary>Whether this is an OUTPUT parameter.</summary>
    public bool IsOutput { get; init; }
}
