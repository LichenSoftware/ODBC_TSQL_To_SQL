using FluentAssertions;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser.Tests.Parser;

/// <summary>
/// Tests for transaction statements, SET options, USE, EXEC, IF, WHILE,
/// DECLARE, CREATE TABLE, DROP TABLE, and batch recovery.
/// </summary>
public sealed class TSqlParserControlFlowTests
{
    private static T ParseSingle<T>(string sql) where T : SqlStatement
    {
        var batch = TSqlParser.Parse(sql);
        batch.Statements.Should().HaveCount(1, because: $"expected exactly one statement in: {sql}");
        return batch.Statements[0].Should().BeOfType<T>().Subject;
    }

    private static SqlBatch Parse(string sql) => TSqlParser.Parse(sql);

    // -------------------------------------------------------------------------
    // Transactions
    // -------------------------------------------------------------------------

    [Fact]
    public void BeginTransaction_NoName()
    {
        var s = ParseSingle<BeginTransactionStatement>("BEGIN TRANSACTION");
        s.TransactionName.Should().BeNull();
    }

    [Fact]
    public void BeginTransaction_WithName()
    {
        var s = ParseSingle<BeginTransactionStatement>("BEGIN TRAN MyTran");
        s.TransactionName.Should().Be("MyTran");
    }

    [Fact]
    public void CommitTransaction()
    {
        ParseSingle<CommitTransactionStatement>("COMMIT TRANSACTION");
    }

    [Fact]
    public void CommitTran_ShortForm()
    {
        ParseSingle<CommitTransactionStatement>("COMMIT TRAN");
    }

    [Fact]
    public void RollbackTransaction()
    {
        ParseSingle<RollbackTransactionStatement>("ROLLBACK");
    }

    [Fact]
    public void SaveTransaction()
    {
        var s = ParseSingle<SaveTransactionStatement>("SAVE TRANSACTION sp1");
        s.SavepointName.Should().Be("sp1");
    }

    // -------------------------------------------------------------------------
    // SET options
    // -------------------------------------------------------------------------

    [Fact]
    public void SetNocount_On()
    {
        var s = ParseSingle<SetOptionStatement>("SET NOCOUNT ON");
        s.OptionName.Should().Be("NOCOUNT");
        s.IsOn.Should().BeTrue();
    }

    [Fact]
    public void SetAnsiNulls_Off()
    {
        var s = ParseSingle<SetOptionStatement>("SET ANSI_NULLS OFF");
        s.OptionName.Should().Be("ANSI_NULLS");
        s.IsOn.Should().BeFalse();
    }

    [Fact]
    public void SetRowcount()
    {
        var s = ParseSingle<SetOptionStatement>("SET ROWCOUNT 100");
        s.OptionName.Should().Be("ROWCOUNT");
        s.Value.Should().BeOfType<IntegerLiteralExpression>()
               .Which.Value.Should().Be(100);
    }

    // -------------------------------------------------------------------------
    // USE
    // -------------------------------------------------------------------------

    [Fact]
    public void UseDatabase()
    {
        var s = ParseSingle<UseDatabaseStatement>("USE MyDatabase");
        s.DatabaseName.Should().Be("MyDatabase");
    }

    // -------------------------------------------------------------------------
    // EXEC
    // -------------------------------------------------------------------------

    [Fact]
    public void Exec_SimpleProc()
    {
        var s = ParseSingle<ExecuteStatement>("EXEC sp_who");
        s.ProcedureName.Name.Should().Be("sp_who");
        s.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Exec_WithPositionalArgs()
    {
        var s = ParseSingle<ExecuteStatement>("EXEC usp_GetOrders 1, 'Active'");
        s.Arguments.Should().HaveCount(2);
        s.Arguments[0].Value.Should().BeOfType<IntegerLiteralExpression>();
        s.Arguments[1].Value.Should().BeOfType<StringLiteralExpression>();
    }

    [Fact]
    public void Exec_WithNamedArgs()
    {
        var s = ParseSingle<ExecuteStatement>("EXEC usp_Proc @id = 1, @name = 'foo'");
        s.Arguments.Should().HaveCount(2);
        s.Arguments[0].ParameterName.Should().Be("@id");
        s.Arguments[1].ParameterName.Should().Be("@name");
    }

    [Fact]
    public void Execute_Keyword()
    {
        var s = ParseSingle<ExecuteStatement>("EXECUTE sp_help 'Orders'");
        s.ProcedureName.Name.Should().Be("sp_help");
    }

    // -------------------------------------------------------------------------
    // IF / WHILE
    // -------------------------------------------------------------------------

    [Fact]
    public void If_SimpleThen()
    {
        var s = ParseSingle<IfStatement>("IF 1 = 1 SELECT 1");
        s.Condition.Should().BeOfType<BinaryExpression>();
        s.ThenBranch.Should().BeOfType<SelectStatement>();
        s.ElseBranch.Should().BeNull();
    }

    [Fact]
    public void If_WithElse()
    {
        var s = ParseSingle<IfStatement>("IF @x > 0 SELECT 1 ELSE SELECT 0");
        s.ElseBranch.Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void If_WithBeginEndBlock()
    {
        var s = ParseSingle<IfStatement>(
            "IF @x = 1 BEGIN SELECT 1 END ELSE BEGIN SELECT 0 END");
        s.ThenBranch.Should().BeOfType<BeginEndBlock>();
        s.ElseBranch.Should().BeOfType<BeginEndBlock>();
    }

    [Fact]
    public void While_Loop()
    {
        var s = ParseSingle<WhileStatement>("WHILE @i < 10 BEGIN SET @i = @i + 1 END");
        s.Condition.Should().BeOfType<BinaryExpression>();
        s.Body.Should().BeOfType<BeginEndBlock>();
    }

    // -------------------------------------------------------------------------
    // DECLARE
    // -------------------------------------------------------------------------

    [Fact]
    public void Declare_SingleVariable()
    {
        var s = ParseSingle<DeclareStatement>("DECLARE @id INT");
        s.Declarations.Should().HaveCount(1);
        s.Declarations[0].Name.Should().Be("@id");
        s.Declarations[0].DataType.TypeName.Should().Be("INT");
    }

    [Fact]
    public void Declare_MultipleVariables()
    {
        var s = ParseSingle<DeclareStatement>("DECLARE @a INT, @b VARCHAR(50)");
        s.Declarations.Should().HaveCount(2);
    }

    [Fact]
    public void Declare_WithInitialValue()
    {
        var s = ParseSingle<DeclareStatement>("DECLARE @count INT = 0");
        s.Declarations[0].InitialValue.Should().BeOfType<IntegerLiteralExpression>()
                          .Which.Value.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // CREATE TABLE
    // -------------------------------------------------------------------------

    [Fact]
    public void CreateTable_BasicColumns()
    {
        var s = ParseSingle<CreateTableStatement>(
            "CREATE TABLE Orders (Id INT NOT NULL, Name NVARCHAR(100) NULL)");
        s.Table.Name.Should().Be("Orders");
        s.Columns.Should().HaveCount(2);
        s.Columns[0].Name.Should().Be("Id");
        s.Columns[0].IsNullable.Should().BeFalse();
        s.Columns[1].Name.Should().Be("Name");
        s.Columns[1].IsNullable.Should().BeTrue();
    }

    [Fact]
    public void CreateTable_TempTable()
    {
        var s = ParseSingle<CreateTableStatement>("CREATE TABLE #Temp (Id INT)");
        s.IsTemporary.Should().BeTrue();
        s.Table.IsTemporaryTable.Should().BeTrue();
    }

    [Fact]
    public void CreateTable_IdentityColumn()
    {
        var s = ParseSingle<CreateTableStatement>(
            "CREATE TABLE t (Id INT IDENTITY(1,1) NOT NULL)");
        s.Columns[0].IsIdentity.Should().BeTrue();
        s.Columns[0].IdentitySeed.Should().Be(1);
        s.Columns[0].IdentityIncrement.Should().Be(1);
    }

    [Fact]
    public void CreateTable_PrimaryKeyColumn()
    {
        var s = ParseSingle<CreateTableStatement>(
            "CREATE TABLE t (Id INT NOT NULL PRIMARY KEY)");
        s.Columns[0].IsPrimaryKey.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // DROP TABLE
    // -------------------------------------------------------------------------

    [Fact]
    public void DropTable_Single()
    {
        var s = ParseSingle<DropTableStatement>("DROP TABLE Orders");
        s.Tables.Should().HaveCount(1);
        s.Tables[0].Name.Should().Be("Orders");
        s.IfExists.Should().BeFalse();
    }

    [Fact]
    public void DropTable_IfExists()
    {
        var s = ParseSingle<DropTableStatement>("DROP TABLE IF EXISTS #Temp");
        s.IfExists.Should().BeTrue();
    }

    [Fact]
    public void DropTable_Multiple()
    {
        var s = ParseSingle<DropTableStatement>("DROP TABLE t1, t2, t3");
        s.Tables.Should().HaveCount(3);
    }

    // -------------------------------------------------------------------------
    // Batch recovery
    // -------------------------------------------------------------------------

    [Fact]
    public void Batch_MultipleSemicolonSeparated()
    {
        var batch = Parse("SELECT 1; SELECT 2; SELECT 3");
        batch.Statements.Should().HaveCount(3);
        batch.Statements.Should().AllBeOfType<SelectStatement>();
    }

    [Fact]
    public void Batch_UnsupportedStatement_ProducesUnparsed()
    {
        var batch = Parse("CREATE VIEW v AS SELECT 1; SELECT 2");
        // First statement is unsupported → UnparsedStatement
        batch.Statements[0].Should().BeOfType<UnparsedStatement>();
        // Second statement still parsed
        batch.Statements[1].Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void Batch_SetFollowedBySelect()
    {
        var batch = Parse("SET NOCOUNT ON; SELECT * FROM t");
        batch.Statements.Should().HaveCount(2);
        batch.Statements[0].Should().BeOfType<SetOptionStatement>();
        batch.Statements[1].Should().BeOfType<SelectStatement>();
    }

    [Fact]
    public void Batch_PrintStatement()
    {
        var s = ParseSingle<PrintStatement>("PRINT 'hello'");
        s.Expression.Should().BeOfType<StringLiteralExpression>()
                    .Which.Value.Should().Be("hello");
    }

    [Fact]
    public void Batch_ReturnWithValue()
    {
        var s = ParseSingle<ReturnStatement>("RETURN 0");
        s.Value.Should().BeOfType<IntegerLiteralExpression>().Which.Value.Should().Be(0);
    }
}
