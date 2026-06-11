using PgPassthrough.SqlParser.Lexer;

namespace PgPassthrough.SqlParser.Parser;

/// <summary>
/// Wraps the token list produced by <see cref="TSqlLexer"/> and provides
/// the lookahead / consume primitives used by <see cref="TSqlParser"/>.
/// 
/// Thread-safety: not thread-safe. One instance per parse call.
/// </summary>
internal sealed class ParseContext
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _pos;

    public ParseContext(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    // -------------------------------------------------------------------------
    // Lookahead
    // -------------------------------------------------------------------------

    /// <summary>Current token without consuming it.</summary>
    public Token Current => _pos < _tokens.Count ? _tokens[_pos] : Eof;

    /// <summary>Look ahead by <paramref name="offset"/> positions (0 = current).</summary>
    public Token Peek(int offset = 1)
    {
        int i = _pos + offset;
        return i < _tokens.Count ? _tokens[i] : Eof;
    }

    /// <summary>True when the current token has the given kind.</summary>
    public bool Is(TokenKind kind) => Current.Kind == kind;

    /// <summary>True when the current token is a keyword matching <paramref name="keyword"/> (case-insensitive).</summary>
    public bool IsKeyword(string keyword)
        => string.Equals(Current.Value, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the current token kind is in the given set.</summary>
    public bool IsAny(params TokenKind[] kinds)
    {
        foreach (var k in kinds)
            if (Current.Kind == k) return true;
        return false;
    }

    /// <summary>Returns true and advances if current kind matches.</summary>
    public bool TryConsume(TokenKind kind, out Token token)
    {
        if (Current.Kind == kind)
        {
            token = Consume();
            return true;
        }
        token = Eof;
        return false;
    }

    /// <summary>Advances if the current token matches, otherwise throws.</summary>
    public Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw ParseError($"Expected {kind} but found {Current.Kind} ('{Current.Value}')");
        return Consume();
    }

    /// <summary>Expects a keyword by value (case-insensitive).</summary>
    public Token ExpectKeyword(string keyword)
    {
        if (!string.Equals(Current.Value, keyword, StringComparison.OrdinalIgnoreCase))
            throw ParseError($"Expected keyword '{keyword}' but found '{Current.Value}'");
        return Consume();
    }

    /// <summary>
    /// Consumes and returns the current token, advancing position.
    /// </summary>
    public Token Consume()
    {
        var t = Current;
        if (_pos < _tokens.Count) _pos++;
        return t;
    }

    /// <summary>
    /// Optionally consumes the current token if it matches.
    /// Returns null if not matched.
    /// </summary>
    public Token? TryConsume(TokenKind kind)
    {
        if (Current.Kind == kind)
            return Consume();
        return null;
    }

    /// <summary>
    /// Optionally consumes a keyword token matching the given text.
    /// </summary>
    public Token? TryConsumeKeyword(string keyword)
    {
        if (string.Equals(Current.Value, keyword, StringComparison.OrdinalIgnoreCase)
            && Current.Kind != TokenKind.Identifier) // keyword, not bare identifier
            return Consume();
        // Also match if it happens to be an Identifier with that value (contextual keywords)
        if (string.Equals(Current.Value, keyword, StringComparison.OrdinalIgnoreCase))
            return Consume();
        return null;
    }

    /// <summary>Skips tokens until EOF or one of the stop-kinds is the current token.</summary>
    public void SkipTo(params TokenKind[] stopKinds)
    {
        while (!Current.IsEof && !IsAny(stopKinds))
            Consume();
    }

    /// <summary>Records current position for backtracking.</summary>
    public int SavePosition() => _pos;

    /// <summary>Restores position to a previously saved value.</summary>
    public void RestorePosition(int saved) => _pos = saved;

    public bool IsEof => Current.IsEof;

    private static Token Eof => new(TokenKind.EndOfFile, string.Empty, 0, 0);

    public ParseException ParseError(string message)
    {
        var t = Current;
        return new ParseException($"{message} at line {t.Line}, column {t.Column}");
    }
}
