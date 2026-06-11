using FluentAssertions;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser.Tests.Parser;

/// <summary>INSERT / UPDATE / DELETE / TRUNCATE parsing tests.</summary>
public sealed class TSqlParserDmlTests
{
    private static T ParseSingle<T>(string sql) where T : SqlStatement
    {
        var batch = TSqlParser.Parse(sql);
        batch.Statements.Should().HaveCount(1);
        return batch.Statements[0].Should().BeOfType<T>().Subject;
    }

    // -------------------------------------------------------------------------
    // INSERT
    // -------------------------------------------------------------------------

    [Fact]
    public void Insert_ValuesWithColumnList()
    {
        var s = ParseSingle<InsertStatement>(
            "INSERT INTO Orders (CustomerId, Amount) VALUES (1, 99.99)");
        s.Target.Name.Should().Be("Orders");
        s.Columns.Should().BeEquivalentTo(new[] { "CustomerId", "Amount" });
        s.ValuesSource.Should().NotBeNull();
        s.ValuesSource!.RowValues.Should().HaveCount(1);
        s.ValuesSource.RowValues[0].Should().HaveCount(2);
    }

    [Fact]
    public void Insert_MultipleValueRows()
    {
        var s = ParseSingle<InsertStatement>(
            "INSERT INTO t (a, b) VALUES (1, 2), (3, 4)");
        s.ValuesSource!.RowValues.Should().HaveCount(2);
    }

    [Fact]
    public void Insert_SelectSource()
    {
        var s = ParseSingle<InsertStatement>(
            "INSERT INTO Archive SELECT * FROM Orders WHERE Year = 2023");
        s.SelectSource.Should().NotBeNull();
        s.ValuesSource.Should().BeNull();
    }

    [Fact]
    public void Insert_WithIntoKeyword()
    {
        var s = ParseSingle<InsertStatement>("INSERT INTO t VALUES (1)");
        s.Target.Name.Should().Be("t");
    }

    // -------------------------------------------------------------------------
    // UPDATE
    // -------------------------------------------------------------------------

    [Fact]
    public void Update_SingleSet()
    {
        var s = ParseSingle<UpdateStatement>(
            "UPDATE Orders SET Status = 'Shipped' WHERE OrderId = 1");
        s.Target.Name.Should().Be("Orders");
        s.Sets.Should().HaveCount(1);
        s.Sets[0].ColumnName.Should().Be("Status");
        s.Where.Should().NotBeNull();
    }

    [Fact]
    public void Update_MultipleSetClauses()
    {
        var s = ParseSingle<UpdateStatement>(
            "UPDATE t SET a = 1, b = 2, c = 3");
        s.Sets.Should().HaveCount(3);
    }

    [Fact]
    public void Update_WithFromJoin()
    {
        var s = ParseSingle<UpdateStatement>(
            "UPDATE o SET o.Status = 'Done' FROM Orders o INNER JOIN Shipments s ON o.Id = s.OrderId");
        s.From.Should().HaveCount(1);
        s.From[0].Should().BeOfType<JoinedSource>();
    }

    [Fact]
    public void Update_WithParameter()
    {
        var s = ParseSingle<UpdateStatement>(
            "UPDATE t SET Name = @name WHERE Id = @id");
        var firstSet = s.Sets[0].Value.Should().BeOfType<ParameterExpression>().Subject;
        firstSet.Name.Should().Be("@name");
    }

    // -------------------------------------------------------------------------
    // DELETE
    // -------------------------------------------------------------------------

    [Fact]
    public void Delete_WhereClause()
    {
        var s = ParseSingle<DeleteStatement>(
            "DELETE FROM Orders WHERE OrderId = 42");
        s.Target.Name.Should().Be("Orders");
        s.Where.Should().NotBeNull();
    }

    [Fact]
    public void Delete_WithoutFrom()
    {
        var s = ParseSingle<DeleteStatement>("DELETE Orders WHERE Id = 1");
        s.Target.Name.Should().Be("Orders");
    }

    [Fact]
    public void Delete_WithJoinedFrom()
    {
        var s = ParseSingle<DeleteStatement>(
            "DELETE o FROM Orders o INNER JOIN ToDelete d ON o.Id = d.Id");
        s.From.Should().HaveCount(1);
    }

    // -------------------------------------------------------------------------
    // TRUNCATE
    // -------------------------------------------------------------------------

    [Fact]
    public void Truncate_Table()
    {
        var s = ParseSingle<TruncateTableStatement>("TRUNCATE TABLE TempData");
        s.Table.Name.Should().Be("TempData");
    }
}
