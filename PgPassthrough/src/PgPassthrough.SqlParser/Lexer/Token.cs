namespace PgPassthrough.SqlParser.Lexer;

/// <summary>
/// A single lexical token produced by <see cref="TSqlLexer"/>.
/// Immutable value type; stored by value in the token list.
/// </summary>
public readonly struct Token
{
    public Token(TokenKind kind, string value, int line, int column)
    {
        Kind   = kind;
        Value  = value;
        Line   = line;
        Column = column;
    }

    /// <summary>What kind of token this is.</summary>
    public TokenKind Kind { get; }

    /// <summary>
    /// The raw source text of this token.
    /// For keywords: the original casing (e.g. "Select", "SELECT", "select").
    /// For identifiers: the unquoted name (brackets/quotes stripped).
    /// For string literals: the content without the surrounding quotes,
    ///   with escape sequences resolved ('' → ').
    /// For numeric literals: the original text.
    /// For parameters: the name including the @ prefix.
    /// </summary>
    public string Value { get; }

    /// <summary>1-based line number in the source text.</summary>
    public int Line { get; }

    /// <summary>1-based column number in the source text.</summary>
    public int Column { get; }

    /// <summary>Convenience: returns true for End-of-file.</summary>
    public bool IsEof => Kind == TokenKind.EndOfFile;

    public override string ToString() =>
        $"[{Kind} '{Value}' @{Line}:{Column}]";
}
