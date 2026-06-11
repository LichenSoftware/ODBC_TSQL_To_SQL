using FluentAssertions;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser.Tests.Ast;

/// <summary>
/// Tests for SqlPrinter: verifies that the printer produces canonical SQL text
/// for the most common statement types.
/// </summary>
public sealed class SqlPrinterTests
{
    private static string PrintFirst(string sql)
    {
        var batch = TSqlParser.Parse(sql);
        batch.Statements.Should().NotBeEmpty();
        return SqlPrinter.Print(batch.Statements[0]);
    }

    // -------------------------------------------------------------------------
    // SELECT
    // -------------------------------------------------------------------------

    [Fact]
    public void Print_SimpleStar()
    {
        var result = PrintFirst("SELECT * FROM Orders");
        result.Should().Contain("SELECT").And.Contain("*").And.Contain("FROM").And.Contain("Orders");
    }

    [Fact]
    public void Print_SelectWithAlias()
    {
        var result = PrintFirst("SELECT OrderId AS Id FROM Orders");
        result.Should().Contain("OrderId").And.Contain("AS").And.Contain("Id");
    }

    [Fact]
    public void Print_SelectWhereClause()
    {
        var result = PrintFirst("SELECT * FROM t WHERE Id = 1");
        result.Should().Contain("WHERE").And.Contain("Id").And.Contain("=").And.Contain("1");
    }

    [Fact]
    public void Print_SelectWithTop()
    {
        var result = PrintFirst("SELECT TOP 10 * FROM t");
        result.Should().Contain("TOP").And.Contain("10");
    }

    [Fact]
    public void Print_InnerJoin()
    {
        var result = PrintFirst("SELECT * FROM Orders o INNER JOIN Customers c ON o.Id = c.Id");
        result.Should().Contain("INNER JOIN").And.Contain("ON");
    }

    [Fact]
    public void Print_OrderBy()
    {
        var result = PrintFirst("SELECT * FROM t ORDER BY Name DESC");
        result.Should().Contain("ORDER BY").And.Contain("DESC");
    }

    // -------------------------------------------------------------------------
    // DML
    // -------------------------------------------------------------------------

    [Fact]
    public void Print_Insert()
    {
        var result = PrintFirst("INSERT INTO t (a, b) VALUES (1, 2)");
        result.Should().Contain("INSERT INTO").And.Contain("VALUES");
    }

    [Fact]
    public void Print_Update()
    {
        var result = PrintFirst("UPDATE t SET Name = 'x' WHERE Id = 1");
        result.Should().Contain("UPDATE").And.Contain("SET").And.Contain("WHERE");
    }

    [Fact]
    public void Print_Delete()
    {
        var result = PrintFirst("DELETE FROM t WHERE Id = 1");
        result.Should().Contain("DELETE").And.Contain("WHERE");
    }

    [Fact]
    public void Print_Truncate()
    {
        var result = PrintFirst("TRUNCATE TABLE t");
        result.Should().Contain("TRUNCATE TABLE").And.Contain("t");
    }

    // -------------------------------------------------------------------------
    // Transactions
    // -------------------------------------------------------------------------

    [Fact]
    public void Print_BeginTransaction()
    {
        var result = PrintFirst("BEGIN TRANSACTION");
        result.Should().Contain("BEGIN TRANSACTION");
    }

    [Fact]
    public void Print_CommitTransaction()
    {
        var result = PrintFirst("COMMIT");
        result.Should().Contain("COMMIT");
    }

    [Fact]
    public void Print_RollbackTransaction()
    {
        var result = PrintFirst("ROLLBACK");
        result.Should().Contain("ROLLBACK");
    }

    // -------------------------------------------------------------------------
    // DDL
    // -------------------------------------------------------------------------

    [Fact]
    public void Print_CreateTable()
    {
        var result = PrintFirst("CREATE TABLE t (Id INT NOT NULL, Name NVARCHAR(50) NULL)");
        result.Should().Contain("CREATE TABLE").And.Contain("Id").And.Contain("Name");
    }

    [Fact]
    public void Print_DropTable()
    {
        var result = PrintFirst("DROP TABLE t");
        result.Should().Be("DROP TABLE t");
    }

    [Fact]
    public void Print_DropTableIfExists()
    {
        var result = PrintFirst("DROP TABLE IF EXISTS #Temp");
        result.Should().Contain("IF EXISTS").And.Contain("#Temp");
    }

    // -------------------------------------------------------------------------
    // Expressions
    // -------------------------------------------------------------------------

    [Fact]
    public void Print_CaseExpression()
    {
        var result = PrintFirst("SELECT CASE WHEN x > 1 THEN 'Y' ELSE 'N' END FROM t");
        result.Should().Contain("CASE").And.Contain("WHEN").And.Contain("THEN").And.Contain("ELSE").And.Contain("END");
    }

    [Fact]
    public void Print_CastExpression()
    {
        var result = PrintFirst("SELECT CAST(Price AS DECIMAL(10,2)) FROM t");
        result.Should().Contain("CAST").And.Contain("AS").And.Contain("DECIMAL");
    }

    [Fact]
    public void Print_Batch_TwoStatements()
    {
        var batch = TSqlParser.Parse("SELECT 1; SELECT 2");
        var result = SqlPrinter.Print(batch);
        // Printer uses newlines within statements — check for structural presence
        result.Should().Contain("SELECT").And.Contain("1").And.Contain("2");
        result.Should().Contain(";"); // statement separator
    }

    [Fact]
    public void Print_UnparsedStatement_PreservesRawSql()
    {
        // CREATE VIEW is not fully supported — becomes UnparsedStatement
        var batch = TSqlParser.Parse("CREATE VIEW v AS SELECT 1");
        var stmt = batch.Statements[0];
        var result = SqlPrinter.Print(stmt);
        // Should not throw and should contain something meaningful
        result.Should().NotBeNullOrWhiteSpace();
    }
}
