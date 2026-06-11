namespace PgPassthrough.SqlParser.Parser;

/// <summary>
/// Thrown when the parser encounters a T-SQL construct it cannot parse.
/// Callers should catch this and produce an <see cref="PgPassthrough.SqlParser.Ast.UnparsedStatement"/>
/// rather than failing the entire request.
/// </summary>
public sealed class ParseException : Exception
{
    public ParseException(string message) : base(message) { }
    public ParseException(string message, Exception inner) : base(message, inner) { }
}
