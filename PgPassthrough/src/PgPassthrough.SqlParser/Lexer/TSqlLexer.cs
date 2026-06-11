using System.Text;

namespace PgPassthrough.SqlParser.Lexer;

/// <summary>
/// Converts a T-SQL source string into a flat list of <see cref="Token"/>s.
///
/// Design decisions:
/// - Single-pass, index-driven (no regex, no string splits).
/// - Produces ALL tokens including whitespace is skipped inline.
/// - Keywords are case-insensitive via the <see cref="Keywords"/> table.
/// - Bracket identifiers [name] are unquoted; the brackets are consumed.
/// - Double-quoted identifiers "name" are also unquoted (QUOTED_IDENTIFIER behaviour).
/// - String literals resolve doubled-quote escapes: '' → '
/// - Unicode string literals N'...' are treated identically to '...'
/// - Block comments /* ... */ are skipped; nested block comments are supported.
/// - Line comments -- ... \n are skipped.
/// - Produces a single EndOfFile token at the end.
/// </summary>
public sealed class TSqlLexer
{
    private readonly string _source;
    private int _pos;
    private int _line;
    private int _col;

    private TSqlLexer(string source)
    {
        _source = source;
        _pos    = 0;
        _line   = 1;
        _col    = 1;
    }

    /// <summary>
    /// Tokenises <paramref name="source"/> and returns the complete token list,
    /// terminated by an <see cref="TokenKind.EndOfFile"/> token.
    /// </summary>
    public static List<Token> Tokenize(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var lexer = new TSqlLexer(source);
        return lexer.Tokenize();
    }

    private List<Token> Tokenize()
    {
        var tokens = new List<Token>(EstimateTokenCount(_source.Length));

        while (true)
        {
            SkipWhitespaceAndComments();
            if (_pos >= _source.Length)
                break;

            var token = NextToken();
            if (token.Kind != TokenKind.Unknown)
                tokens.Add(token);
            // Unknown tokens are silently skipped — they should be very rare
        }

        tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, _line, _col));
        return tokens;
    }

    // -------------------------------------------------------------------------
    // Main dispatch
    // -------------------------------------------------------------------------

    private Token NextToken()
    {
        int startLine = _line;
        int startCol  = _col;
        char c = Current();

        // String literals
        if (c == '\'' || (c == 'N' && Peek(1) == '\''))
            return ScanStringLiteral(startLine, startCol);

        // Hex literals 0x...
        if (c == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
            return ScanHexLiteral(startLine, startCol);

        // Numeric literals
        if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(Peek(1))))
            return ScanNumericLiteral(startLine, startCol);

        // Parameters @name or @@name
        if (c == '@')
            return ScanParameter(startLine, startCol);

        // Bracket-quoted identifiers [name]
        if (c == '[')
            return ScanBracketIdentifier(startLine, startCol);

        // Double-quoted identifiers "name"
        if (c == '"')
            return ScanDoubleQuotedIdentifier(startLine, startCol);

        // Money literals $1.00
        if (c == '$')
            return ScanMoneyLiteral(startLine, startCol);

        // Identifiers and keywords
        if (char.IsLetter(c) || c == '_' || c == '#')
            return ScanIdentifierOrKeyword(startLine, startCol);

        // Operators and punctuation
        return ScanOperatorOrPunctuation(startLine, startCol);
    }

    // -------------------------------------------------------------------------
    // String literals
    // -------------------------------------------------------------------------

    private Token ScanStringLiteral(int line, int col)
    {
        // Skip optional N prefix
        if (Current() == 'N') Advance();

        Advance(); // consume opening '
        var sb = new StringBuilder();

        while (_pos < _source.Length)
        {
            char c = Current();
            if (c == '\'')
            {
                Advance();
                if (_pos < _source.Length && Current() == '\'')
                {
                    // Doubled quote — escape sequence
                    sb.Append('\'');
                    Advance();
                }
                else
                {
                    break; // end of literal
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }

        return new Token(TokenKind.StringLiteral, sb.ToString(), line, col);
    }

    // -------------------------------------------------------------------------
    // Hex literals
    // -------------------------------------------------------------------------

    private Token ScanHexLiteral(int line, int col)
    {
        int start = _pos;
        Advance(); // '0'
        Advance(); // 'x' or 'X'
        while (_pos < _source.Length && IsHexDigit(Current()))
            Advance();
        return new Token(TokenKind.HexLiteral, _source[start.._pos], line, col);
    }

    // -------------------------------------------------------------------------
    // Numeric literals: integer, decimal, float
    // -------------------------------------------------------------------------

    private Token ScanNumericLiteral(int line, int col)
    {
        int start = _pos;
        bool hasDot = false;
        bool hasExp = false;

        while (_pos < _source.Length && char.IsAsciiDigit(Current()))
            Advance();

        if (_pos < _source.Length && Current() == '.')
        {
            hasDot = true;
            Advance();
            while (_pos < _source.Length && char.IsAsciiDigit(Current()))
                Advance();
        }

        if (_pos < _source.Length && (Current() == 'e' || Current() == 'E'))
        {
            hasExp = true;
            Advance();
            if (_pos < _source.Length && (Current() == '+' || Current() == '-'))
                Advance();
            while (_pos < _source.Length && char.IsAsciiDigit(Current()))
                Advance();
        }

        string text = _source[start.._pos];
        TokenKind kind = hasExp ? TokenKind.FloatLiteral
                       : hasDot ? TokenKind.DecimalLiteral
                       : TokenKind.IntegerLiteral;

        return new Token(kind, text, line, col);
    }

    // -------------------------------------------------------------------------
    // Parameters @name and @@name
    // -------------------------------------------------------------------------

    private Token ScanParameter(int line, int col)
    {
        int start = _pos;
        Advance(); // first @

        if (_pos < _source.Length && Current() == '@')
        {
            Advance(); // second @ for @@global
            int nameStart = _pos;
            while (_pos < _source.Length && IsIdentifierChar(Current()))
                Advance();
            string name = _source[start.._pos]; // includes @@
            return new Token(TokenKind.GlobalVariable, name, line, col);
        }

        // Single @name
        while (_pos < _source.Length && IsIdentifierChar(Current()))
            Advance();

        string paramName = _source[start.._pos]; // includes @
        if (paramName.Length == 1)
        {
            // Bare @ with no name — treat as punctuation
            return new Token(TokenKind.At, "@", line, col);
        }
        return new Token(TokenKind.Parameter, paramName, line, col);
    }

    // -------------------------------------------------------------------------
    // Bracket identifiers [schema.name] and [name with spaces]
    // -------------------------------------------------------------------------

    private Token ScanBracketIdentifier(int line, int col)
    {
        Advance(); // consume [
        var sb = new StringBuilder();

        while (_pos < _source.Length)
        {
            char c = Current();
            if (c == ']')
            {
                Advance();
                // Doubled ]] is an escape for ] inside a bracket identifier
                if (_pos < _source.Length && Current() == ']')
                {
                    sb.Append(']');
                    Advance();
                }
                else
                {
                    break;
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }

        return new Token(TokenKind.Identifier, sb.ToString(), line, col);
    }

    // -------------------------------------------------------------------------
    // Double-quoted identifiers "name"
    // -------------------------------------------------------------------------

    private Token ScanDoubleQuotedIdentifier(int line, int col)
    {
        Advance(); // consume opening "
        var sb = new StringBuilder();

        while (_pos < _source.Length)
        {
            char c = Current();
            if (c == '"')
            {
                Advance();
                if (_pos < _source.Length && Current() == '"')
                {
                    sb.Append('"');
                    Advance();
                }
                else
                {
                    break;
                }
            }
            else
            {
                sb.Append(c);
                Advance();
            }
        }

        return new Token(TokenKind.Identifier, sb.ToString(), line, col);
    }

    // -------------------------------------------------------------------------
    // Money literals $1.00
    // -------------------------------------------------------------------------

    private Token ScanMoneyLiteral(int line, int col)
    {
        int start = _pos;
        Advance(); // $

        // Optional sign
        if (_pos < _source.Length && (Current() == '-' || Current() == '+'))
            Advance();

        while (_pos < _source.Length && char.IsAsciiDigit(Current()))
            Advance();

        if (_pos < _source.Length && Current() == '.')
        {
            Advance();
            while (_pos < _source.Length && char.IsAsciiDigit(Current()))
                Advance();
        }

        return new Token(TokenKind.MoneyLiteral, _source[start.._pos], line, col);
    }

    // -------------------------------------------------------------------------
    // Identifiers and keywords
    // -------------------------------------------------------------------------

    private Token ScanIdentifierOrKeyword(int line, int col)
    {
        int start = _pos;

        // T-SQL temp tables: #name and ##name
        while (_pos < _source.Length && (IsIdentifierChar(Current()) || Current() == '#'))
            Advance();

        string text = _source[start.._pos];

        // Check keyword table (case-insensitive)
        if (Keywords.Map.TryGetValue(text, out TokenKind kwKind))
            return new Token(kwKind, text, line, col);

        return new Token(TokenKind.Identifier, text, line, col);
    }

    // -------------------------------------------------------------------------
    // Operators and punctuation
    // -------------------------------------------------------------------------

    private Token ScanOperatorOrPunctuation(int line, int col)
    {
        char c = Current();
        Advance();

        switch (c)
        {
            case '.': return new Token(TokenKind.Dot, ".", line, col);
            case ',': return new Token(TokenKind.Comma, ",", line, col);
            case ';': return new Token(TokenKind.Semicolon, ";", line, col);
            case '(': return new Token(TokenKind.OpenParen, "(", line, col);
            case ')': return new Token(TokenKind.CloseParen, ")", line, col);
            case '~': return new Token(TokenKind.Tilde, "~", line, col);
            case '^':
                if (MatchAndAdvance('=')) return new Token(TokenKind.CaretEqual, "^=", line, col);
                return new Token(TokenKind.Caret, "^", line, col);
            case '&':
                if (MatchAndAdvance('=')) return new Token(TokenKind.AmpersandEqual, "&=", line, col);
                return new Token(TokenKind.Ampersand, "&", line, col);
            case '|':
                if (MatchAndAdvance('=')) return new Token(TokenKind.PipeEqual, "|=", line, col);
                return new Token(TokenKind.Pipe, "|", line, col);
            case '+':
                if (MatchAndAdvance('=')) return new Token(TokenKind.PlusEqual, "+=", line, col);
                return new Token(TokenKind.Plus, "+", line, col);
            case '-':
                if (MatchAndAdvance('=')) return new Token(TokenKind.MinusEqual, "-=", line, col);
                return new Token(TokenKind.Minus, "-", line, col);
            case '*':
                if (MatchAndAdvance('=')) return new Token(TokenKind.StarEqual, "*=", line, col);
                return new Token(TokenKind.Star, "*", line, col);
            case '/':
                if (MatchAndAdvance('=')) return new Token(TokenKind.SlashEqual, "/=", line, col);
                return new Token(TokenKind.Slash, "/", line, col);
            case '%':
                if (MatchAndAdvance('=')) return new Token(TokenKind.PercentEqual, "%=", line, col);
                return new Token(TokenKind.Percent, "%", line, col);
            case '=':
                return new Token(TokenKind.Equal, "=", line, col);
            case '<':
                if (MatchAndAdvance('>')) return new Token(TokenKind.NotEqual, "<>", line, col);
                if (MatchAndAdvance('=')) return new Token(TokenKind.LessThanOrEqual, "<=", line, col);
                return new Token(TokenKind.LessThan, "<", line, col);
            case '>':
                if (MatchAndAdvance('=')) return new Token(TokenKind.GreaterThanOrEqual, ">=", line, col);
                return new Token(TokenKind.GreaterThan, ">", line, col);
            case '!':
                if (MatchAndAdvance('=')) return new Token(TokenKind.NotEqual, "!=", line, col);
                if (MatchAndAdvance('<')) return new Token(TokenKind.NotLessThan, "!<", line, col);
                if (MatchAndAdvance('>')) return new Token(TokenKind.NotGreaterThan, "!>", line, col);
                return new Token(TokenKind.Unknown, "!", line, col);
            case ':':
                if (MatchAndAdvance(':')) return new Token(TokenKind.DoubleColon, "::", line, col);
                return new Token(TokenKind.Colon, ":", line, col);
            default:
                return new Token(TokenKind.Unknown, c.ToString(), line, col);
        }
    }

    // -------------------------------------------------------------------------
    // Whitespace and comment skipping
    // -------------------------------------------------------------------------

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            char c = Current();

            if (c == '\r' || c == '\n')
            {
                AdvanceLine();
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                Advance();
                continue;
            }

            // Line comment: -- ...
            if (c == '-' && Peek(1) == '-')
            {
                while (_pos < _source.Length && Current() != '\n' && Current() != '\r')
                    Advance();
                continue;
            }

            // Block comment: /* ... */ with nesting support
            if (c == '/' && Peek(1) == '*')
            {
                SkipBlockComment();
                continue;
            }

            break;
        }
    }

    private void SkipBlockComment()
    {
        Advance(); // /
        Advance(); // *
        int depth = 1;

        while (_pos < _source.Length && depth > 0)
        {
            char c = Current();
            if (c == '/' && Peek(1) == '*')
            {
                Advance(); Advance();
                depth++;
            }
            else if (c == '*' && Peek(1) == '/')
            {
                Advance(); Advance();
                depth--;
            }
            else if (c == '\n' || c == '\r')
            {
                AdvanceLine();
            }
            else
            {
                Advance();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Character navigation helpers
    // -------------------------------------------------------------------------

    private char Current() => _pos < _source.Length ? _source[_pos] : '\0';
    private char Peek(int offset) => (_pos + offset) < _source.Length ? _source[_pos + offset] : '\0';

    private void Advance()
    {
        _pos++;
        _col++;
    }

    private void AdvanceLine()
    {
        char c = Current();
        if (c == '\r' && Peek(1) == '\n')
            _pos++; // skip \r in \r\n sequence
        _pos++;
        _line++;
        _col = 1;
    }

    private bool MatchAndAdvance(char expected)
    {
        if (_pos < _source.Length && Current() == expected)
        {
            Advance();
            return true;
        }
        return false;
    }

    // -------------------------------------------------------------------------
    // Character classification
    // -------------------------------------------------------------------------

    private static bool IsIdentifierChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$';

    private static bool IsHexDigit(char c)
        => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int EstimateTokenCount(int sourceLength)
        => Math.Max(16, sourceLength / 5);
}
