using FluentAssertions;
using PgPassthrough.Core.Models;
using PgPassthrough.SqlParser;
using PgPassthrough.SqlParser.Ast;

namespace PgPassthrough.SqlParser.Tests;

public sealed class SpExecuteSqlDecomposerTests
{
    private static RpcRequest MakeRpc(params (string name, object? value)[] args) =>
        new()
        {
            Session    = new SessionContext(),
            ProcedureName = "sp_executesql",
            Parameters = args.Select(a => new QueryParameter { Name = a.name, Value = a.value }).ToList()
        };

    // -------------------------------------------------------------------------
    // IsSpExecuteSql
    // -------------------------------------------------------------------------

    [Fact]
    public void IsSpExecuteSql_Matches_CaseInsensitive()
    {
        var rpc = MakeRpc(("@stmt", "SELECT 1"));
        SpExecuteSqlDecomposer.IsSpExecuteSql(rpc).Should().BeTrue();
    }

    [Fact]
    public void IsSpExecuteSql_DoesNotMatch_OtherProc()
    {
        var rpc = new RpcRequest
        {
            Session = new SessionContext(),
            ProcedureName = "sp_help",
            Parameters = []
        };
        SpExecuteSqlDecomposer.IsSpExecuteSql(rpc).Should().BeFalse();
    }

    // -------------------------------------------------------------------------
    // TryDecompose — basic cases
    // -------------------------------------------------------------------------

    [Fact]
    public void Decompose_SimpleSqlNoParams()
    {
        var rpc = MakeRpc(("@stmt", "SELECT * FROM Orders"));
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);

        result.Should().NotBeNull();
        result!.SqlText.Should().Be("SELECT * FROM Orders");
        result.Parameters.Should().BeEmpty();
        result.Ast.Statements.Should().HaveCount(1);
        result.Ast.Statements[0].Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void Decompose_WithParamDeclarationsAndValues()
    {
        var rpc = MakeRpc(
            ("@stmt",   "SELECT * FROM t WHERE Id = @id"),
            ("@params", "@id INT"),
            ("@id",     42)
        );
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);

        result.Should().NotBeNull();
        result!.Parameters.Should().HaveCount(1);
        result.Parameters[0].Name.Should().Be("@id");
        result.Parameters[0].Value.Should().Be(42);
        result.Parameters[0].TsqlType.Should().Be("INT");
    }

    [Fact]
    public void Decompose_MultipleParams()
    {
        var rpc = MakeRpc(
            ("@stmt",   "SELECT * FROM t WHERE Id = @id AND Name = @name"),
            ("@params", "@id INT, @name NVARCHAR(50)"),
            ("@id",     1),
            ("@name",   "Alice")
        );
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);

        result.Should().NotBeNull();
        result!.Parameters.Should().HaveCount(2);
        result.Parameters[0].TsqlType.Should().Be("INT");
        result.Parameters[1].TsqlType.Should().Be("NVARCHAR(50)");
        result.Parameters[1].Value.Should().Be("Alice");
    }

    [Fact]
    public void Decompose_ParamWithPrecision_DecimalType()
    {
        var rpc = MakeRpc(
            ("@stmt",   "INSERT INTO t VALUES (@price)"),
            ("@params", "@price DECIMAL(10,2)"),
            ("@price",  9.99m)
        );
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);

        result.Should().NotBeNull();
        // Decimal(10,2) has a comma inside parens — must not be split
        result!.Parameters[0].TsqlType.Should().Be("DECIMAL(10,2)");
    }

    [Fact]
    public void Decompose_NullStmt_ReturnsNull()
    {
        var rpc = MakeRpc(("@stmt", (object?)null));
        SpExecuteSqlDecomposer.TryDecompose(rpc).Should().BeNull();
    }

    [Fact]
    public void Decompose_EmptyParams_ReturnsEmptyList()
    {
        var rpc = MakeRpc(
            ("@stmt",   "SELECT 1"),
            ("@params", "")
        );
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);
        result.Should().NotBeNull();
        result!.Parameters.Should().BeEmpty();
    }

    [Fact]
    public void Decompose_ParsesAstCorrectly_Insert()
    {
        var rpc = MakeRpc(
            ("@stmt",   "INSERT INTO Orders (CustomerId, Amount) VALUES (@cid, @amt)"),
            ("@params", "@cid INT, @amt DECIMAL(10,2)"),
            ("@cid",    5),
            ("@amt",    199.99m)
        );
        var result = SpExecuteSqlDecomposer.TryDecompose(rpc);

        result.Should().NotBeNull();
        result!.Ast.Statements[0].Should().BeOfType<InsertStatement>();
        result.Parameters.Should().HaveCount(2);
    }
}
