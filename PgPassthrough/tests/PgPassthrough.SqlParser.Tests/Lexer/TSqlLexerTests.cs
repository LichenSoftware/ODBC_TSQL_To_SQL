using FluentAssertions;
using PgPassthrough.SqlParser.Lexer;

namespace PgPassthrough.SqlParser.Tests.Lexer;

public sealed class TSqlLexerTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static List<Token> Lex(string sql) => TSqlLexer.Tokenize(sql);

    private static IEnumerable<(TokenKind kind, string value)> Pairs(string sql)
        => Lex(sql)
           .Where(t => t.Kind != TokenKind.EndOfFile)
           .Select(t => (t.Kind, t.Value));

    // -------------------------------------------------------------------------
    // Keywords
    // -------------------------------------------------------------------------

    [Fact]
    public void Keywords_AreRecognised_CaseInsensitive()
    {
        var tokens = Lex("select SELECT Select");
        tokens.Where(t => !t.IsEof)
              .Should().AllSatisfy(t => t.Kind.Should().Be(TokenKind.KwSelect));
    }

    [Fact]
    public void Keywords_MultiWord_OrderBy_GroupBy()
    {
        var kinds = Pairs("ORDER BY GROUP BY")
            .Select(p => p.kind).ToList();
        kinds.Should().ContainInOrder(
            TokenKind.KwOrderBy, TokenKind.KwBy,
            TokenKind.KwGroupBy, TokenKind.KwBy);
    }

    [Fact]
    public void Identifiers_NotKeywords_EmittedAsIdentifier()
    {
        var t = Lex("MyTable").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("MyTable");
    }

    // -------------------------------------------------------------------------
    // Bracket identifiers
    // -------------------------------------------------------------------------

    [Fact]
    public void BracketIdentifier_Unquoted()
    {
        var t = Lex("[Order Details]").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("Order Details");
    }

    [Fact]
    public void BracketIdentifier_EscapedBracket()
    {
        var t = Lex("[a]]b]").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("a]b");
    }

    [Fact]
    public void DoubleQuotedIdentifier_Unquoted()
    {
        var t = Lex("\"my col\"").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("my col");
    }

    // -------------------------------------------------------------------------
    // String literals
    // -------------------------------------------------------------------------

    [Fact]
    public void StringLiteral_Simple()
    {
        var t = Lex("'hello'").First();
        t.Kind.Should().Be(TokenKind.StringLiteral);
        t.Value.Should().Be("hello");
    }

    [Fact]
    public void StringLiteral_DoubledQuoteEscape()
    {
        var t = Lex("'it''s'").First();
        t.Kind.Should().Be(TokenKind.StringLiteral);
        t.Value.Should().Be("it's");
    }

    [Fact]
    public void StringLiteral_UnicodePrefix()
    {
        var t = Lex("N'hello'").First();
        t.Kind.Should().Be(TokenKind.StringLiteral);
        t.Value.Should().Be("hello");
    }

    [Fact]
    public void StringLiteral_Empty()
    {
        var t = Lex("''").First();
        t.Kind.Should().Be(TokenKind.StringLiteral);
        t.Value.Should().Be(string.Empty);
    }

    // -------------------------------------------------------------------------
    // Numeric literals
    // -------------------------------------------------------------------------

    [Fact]
    public void IntegerLiteral()
    {
        var t = Lex("42").First();
        t.Kind.Should().Be(TokenKind.IntegerLiteral);
        t.Value.Should().Be("42");
    }

    [Fact]
    public void DecimalLiteral()
    {
        var t = Lex("3.14").First();
        t.Kind.Should().Be(TokenKind.DecimalLiteral);
        t.Value.Should().Be("3.14");
    }

    [Fact]
    public void FloatLiteral_Scientific()
    {
        var t = Lex("1.5E10").First();
        t.Kind.Should().Be(TokenKind.FloatLiteral);
        t.Value.Should().Be("1.5E10");
    }

    [Fact]
    public void HexLiteral()
    {
        var t = Lex("0x1A2B").First();
        t.Kind.Should().Be(TokenKind.HexLiteral);
        t.Value.Should().Be("0x1A2B");
    }

    // -------------------------------------------------------------------------
    // Parameters and global variables
    // -------------------------------------------------------------------------

    [Fact]
    public void Parameter_AtName()
    {
        var t = Lex("@CustomerId").First();
        t.Kind.Should().Be(TokenKind.Parameter);
        t.Value.Should().Be("@CustomerId");
    }

    [Fact]
    public void GlobalVariable_DoubleAt()
    {
        var t = Lex("@@ROWCOUNT").First();
        t.Kind.Should().Be(TokenKind.GlobalVariable);
        t.Value.Should().Be("@@ROWCOUNT");
    }

    // -------------------------------------------------------------------------
    // Operators
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("<>", TokenKind.NotEqual)]
    [InlineData("!=", TokenKind.NotEqual)]
    [InlineData("<=", TokenKind.LessThanOrEqual)]
    [InlineData(">=", TokenKind.GreaterThanOrEqual)]
    [InlineData("::", TokenKind.DoubleColon)]
    [InlineData("+=", TokenKind.PlusEqual)]
    [InlineData("-=", TokenKind.MinusEqual)]
    public void CompoundOperators_RecognisedCorrectly(string src, TokenKind expected)
    {
        var t = Lex(src).First();
        t.Kind.Should().Be(expected);
        t.Value.Should().Be(src);
    }

    // -------------------------------------------------------------------------
    // Comments skipped
    // -------------------------------------------------------------------------

    [Fact]
    public void LineComment_Skipped()
    {
        var tokens = Lex("SELECT -- this is a comment\n1");
        var kinds  = tokens.Where(t => !t.IsEof).Select(t => t.Kind).ToList();
        kinds.Should().BeEquivalentTo(new[] { TokenKind.KwSelect, TokenKind.IntegerLiteral });
    }

    [Fact]
    public void BlockComment_Skipped()
    {
        var tokens = Lex("SELECT /* comment */ 1");
        var kinds  = tokens.Where(t => !t.IsEof).Select(t => t.Kind).ToList();
        kinds.Should().BeEquivalentTo(new[] { TokenKind.KwSelect, TokenKind.IntegerLiteral });
    }

    [Fact]
    public void NestedBlockComment_Skipped()
    {
        var tokens = Lex("1 /* outer /* inner */ still outer */ 2");
        var kinds  = tokens.Where(t => !t.IsEof).Select(t => t.Kind).ToList();
        kinds.Should().BeEquivalentTo(new[] { TokenKind.IntegerLiteral, TokenKind.IntegerLiteral });
    }

    // -------------------------------------------------------------------------
    // Temp table names
    // -------------------------------------------------------------------------

    [Fact]
    public void TempTable_SingleHash()
    {
        var t = Lex("#TempOrders").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("#TempOrders");
    }

    [Fact]
    public void TempTable_DoubleHash()
    {
        var t = Lex("##GlobalTemp").First();
        t.Kind.Should().Be(TokenKind.Identifier);
        t.Value.Should().Be("##GlobalTemp");
    }

    // -------------------------------------------------------------------------
    // Line/column tracking
    // -------------------------------------------------------------------------

    [Fact]
    public void LineTracking_MultiLine()
    {
        var tokens = Lex("SELECT\n1");
        tokens[0].Line.Should().Be(1);
        tokens[1].Line.Should().Be(2);
    }

    [Fact]
    public void ColumnTracking_SameLine()
    {
        var tokens = Lex("SELECT 1");
        tokens[0].Column.Should().Be(1);
        tokens[1].Column.Should().Be(8);
    }

    // -------------------------------------------------------------------------
    // EOF
    // -------------------------------------------------------------------------

    [Fact]
    public void EmptyInput_ProducesOnlyEof()
    {
        var tokens = Lex(string.Empty);
        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(TokenKind.EndOfFile);
    }

    [Fact]
    public void WhitespaceOnly_ProducesOnlyEof()
    {
        var tokens = Lex("   \t\n  ");
        tokens.Should().HaveCount(1);
        tokens[0].Kind.Should().Be(TokenKind.EndOfFile);
    }
}
