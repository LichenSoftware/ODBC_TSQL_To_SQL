using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PgPassthrough.Core.Models;
using PgPassthrough.Translation;

namespace PgPassthrough.Translation.Tests;

/// <summary>
/// Tests for the T-SQL → PostgreSQL translation engine.
/// Each test feeds T-SQL in and asserts on the PostgreSQL output.
/// </summary>
public sealed class TranslatorTests
{
    private readonly TSqlToPgTranslator _translator;
    private readonly TranslationContext _ctx = new();

    public TranslatorTests()
    {
        var config = Options.Create(new ServerConfiguration());
        _translator = new TSqlToPgTranslator(config, NullLogger<TSqlToPgTranslator>.Instance);
    }

    private string Translate(string tsql)
    {
        var result = _translator.Translate(tsql, _ctx);
        return result.TranslatedSql;
    }

    private TranslationResult TranslateFull(string tsql) => _translator.Translate(tsql, _ctx);

    // =========================================================================
    // SELECT — TOP → LIMIT
    // =========================================================================

    [Fact]
    public void Select_TopN_TranslatesToLimit()
    {
        var pg = Translate("SELECT TOP 10 * FROM Orders");
        pg.Should().Contain("LIMIT 10");
        pg.Should().NotContain("TOP");
    }

    [Fact]
    public void Select_TopWithParens_TranslatesToLimit()
    {
        var pg = Translate("SELECT TOP (5) Name FROM Customers");
        pg.Should().Contain("LIMIT 5");
    }

    // =========================================================================
    // SELECT — basic structure preserved
    // =========================================================================

    [Fact]
    public void Select_Star_PassedThrough()
    {
        var pg = Translate("SELECT * FROM Orders");
        pg.Should().Contain("SELECT *").And.Contain("FROM").And.Contain("Orders");
    }

    [Fact]
    public void Select_WhereEquality()
    {
        var pg = Translate("SELECT * FROM t WHERE Id = 1");
        pg.Should().Contain("WHERE").And.Contain("Id").And.Contain("= 1");
    }

    [Fact]
    public void Select_OrderBy_Preserved()
    {
        var pg = Translate("SELECT * FROM t ORDER BY Name DESC");
        pg.Should().Contain("ORDER BY").And.Contain("DESC");
    }

    [Fact]
    public void Select_GroupBy_Preserved()
    {
        var pg = Translate("SELECT City, COUNT(*) FROM t GROUP BY City HAVING COUNT(*) > 5");
        pg.Should().Contain("GROUP BY").And.Contain("HAVING");
    }

    // =========================================================================
    // JOINs
    // =========================================================================

    [Fact]
    public void Join_InnerJoin_Preserved()
    {
        var pg = Translate("SELECT * FROM Orders o INNER JOIN Customers c ON o.CustId = c.Id");
        pg.Should().Contain("INNER JOIN");
    }

    [Fact]
    public void Join_CrossApply_TranslatedToLateral()
    {
        var pg = Translate("SELECT * FROM Orders o CROSS APPLY (SELECT TOP 1 * FROM Items i WHERE i.OrderId = o.Id) x");
        pg.Should().Contain("CROSS JOIN LATERAL");
    }

    // =========================================================================
    // Table hints — stripped
    // =========================================================================

    [Fact]
    public void TableHints_Nolock_Stripped()
    {
        var result = TranslateFull("SELECT * FROM Orders WITH (NOLOCK)");
        result.TranslatedSql.Should().NotContain("NOLOCK").And.NotContain("WITH");
        result.Warnings.Should().Contain(w => w.Code == "PG007");
    }

    // =========================================================================
    // Functions
    // =========================================================================

    [Fact]
    public void Function_Isnull_TranslatedToCoalesce()
    {
        var pg = Translate("SELECT ISNULL(Name, 'Unknown') FROM t");
        pg.Should().Contain("COALESCE(").And.NotContain("ISNULL");
    }

    [Fact]
    public void Function_Getdate_TranslatedToNow()
    {
        var pg = Translate("SELECT GETDATE()");
        pg.Should().Contain("NOW()");
    }

    [Fact]
    public void Function_Len_TranslatedToLength()
    {
        var pg = Translate("SELECT LEN(Name) FROM t");
        pg.Should().Contain("LENGTH(");
    }

    [Fact]
    public void Function_Charindex_TranslatedToPosition()
    {
        var pg = Translate("SELECT CHARINDEX('x', Name) FROM t");
        pg.Should().Contain("POSITION(").And.Contain(" IN ");
    }

    [Fact]
    public void Function_Substring_TranslatedToFromFor()
    {
        var pg = Translate("SELECT SUBSTRING(Name, 1, 3) FROM t");
        pg.Should().Contain("SUBSTRING(").And.Contain("FROM").And.Contain("FOR");
    }

    [Fact]
    public void Function_Dateadd_TranslatedToInterval()
    {
        var pg = Translate("SELECT DATEADD(day, 7, OrderDate) FROM Orders");
        pg.Should().Contain("INTERVAL").And.Contain("day");
    }

    [Fact]
    public void Function_Datediff_TranslatedToExtract()
    {
        var pg = Translate("SELECT DATEDIFF(day, StartDate, EndDate) FROM t");
        pg.Should().Contain("EXTRACT").And.Contain("EPOCH");
    }

    [Fact]
    public void Function_Newid_TranslatedToGenRandomUuid()
    {
        var pg = Translate("SELECT NEWID()");
        pg.Should().Contain("GEN_RANDOM_UUID()");
    }

    [Fact]
    public void Function_ScopeIdentity_TranslatedToLastval()
    {
        var pg = Translate("SELECT SCOPE_IDENTITY()");
        pg.Should().Contain("lastval()");
    }

    // =========================================================================
    // CAST / CONVERT
    // =========================================================================

    [Fact]
    public void Cast_TranslatedWithTypeMapping()
    {
        var pg = Translate("SELECT CAST(Price AS DECIMAL(10,2)) FROM t");
        pg.Should().Contain("CAST(").And.Contain("NUMERIC(10,2)");
    }

    [Fact]
    public void Convert_WithStyle_TranslatedToToChar()
    {
        var pg = Translate("SELECT CONVERT(VARCHAR(20), OrderDate, 103) FROM t");
        pg.Should().Contain("TO_CHAR(").And.Contain("DD/MM/YYYY");
    }

    [Fact]
    public void Convert_WithoutStyle_TranslatedToCast()
    {
        var pg = Translate("SELECT CONVERT(INT, '42')");
        pg.Should().Contain("CAST(").And.Contain("INTEGER");
    }

    // =========================================================================
    // Data types
    // =========================================================================

    [Fact]
    public void Type_Nvarchar_TranslatedToVarchar()
    {
        var pg = Translate("CREATE TABLE t (Name NVARCHAR(50) NOT NULL)");
        pg.Should().Contain("VARCHAR(50)").And.NotContain("NVARCHAR");
    }

    [Fact]
    public void Type_NvarcharMax_TranslatedToText()
    {
        var pg = Translate("CREATE TABLE t (Data NVARCHAR(MAX))");
        pg.Should().Contain("TEXT");
    }

    [Fact]
    public void Type_Bit_TranslatedToBoolean()
    {
        var pg = Translate("CREATE TABLE t (Active BIT NOT NULL)");
        pg.Should().Contain("BOOLEAN");
    }

    [Fact]
    public void Type_UniqueIdentifier_TranslatedToUuid()
    {
        var pg = Translate("CREATE TABLE t (Id UNIQUEIDENTIFIER NOT NULL)");
        pg.Should().Contain("UUID");
    }

    [Fact]
    public void Type_Datetime_TranslatedToTimestamp()
    {
        var pg = Translate("CREATE TABLE t (Created DATETIME NOT NULL)");
        pg.Should().Contain("TIMESTAMP");
    }

    // =========================================================================
    // Identity columns
    // =========================================================================

    [Fact]
    public void Identity_TranslatedToGeneratedAlways()
    {
        var pg = Translate("CREATE TABLE t (Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY)");
        pg.Should().Contain("GENERATED ALWAYS AS IDENTITY");
    }

    // =========================================================================
    // Temp tables
    // =========================================================================

    [Fact]
    public void TempTable_Select_TranslatedToPgTemp()
    {
        var pg = Translate("SELECT * FROM #TempOrders");
        pg.Should().Contain("pg_temp.");
    }

    [Fact]
    public void TempTable_Create_UsesTemporaryKeyword()
    {
        var pg = Translate("CREATE TABLE #Temp (Id INT NOT NULL)");
        pg.Should().Contain("CREATE TEMPORARY TABLE");
    }

    // =========================================================================
    // Global variables
    // =========================================================================

    [Fact]
    public void GlobalVar_RowCount_Translated()
    {
        var pg = Translate("SELECT @@ROWCOUNT");
        pg.Should().NotContain("@@ROWCOUNT");
    }

    [Fact]
    public void GlobalVar_Identity_TranslatedToLastval()
    {
        var pg = Translate("SELECT @@IDENTITY");
        pg.Should().Contain("lastval()");
    }

    // =========================================================================
    // Transactions
    // =========================================================================

    [Fact]
    public void BeginTransaction_TranslatedToBegin()
    {
        var pg = Translate("BEGIN TRANSACTION");
        pg.Should().Be("BEGIN");
    }

    [Fact]
    public void CommitTransaction_TranslatedToCommit()
    {
        var pg = Translate("COMMIT TRANSACTION");
        pg.Should().Be("COMMIT");
    }

    [Fact]
    public void RollbackTransaction_TranslatedToRollback()
    {
        var pg = Translate("ROLLBACK");
        pg.Should().Be("ROLLBACK");
    }

    [Fact]
    public void SaveTransaction_TranslatedToSavepoint()
    {
        var pg = Translate("SAVE TRANSACTION sp1");
        pg.Should().Contain("SAVEPOINT").And.Contain("sp1");
    }

    // =========================================================================
    // SET options — no-ops
    // =========================================================================

    [Fact]
    public void SetNocount_TranslatedToComment()
    {
        var pg = Translate("SET NOCOUNT ON");
        pg.Should().Contain("--").And.Contain("no-op");
    }

    [Fact]
    public void SetAnsiNulls_TranslatedToComment()
    {
        var pg = Translate("SET ANSI_NULLS ON");
        pg.Should().Contain("--");
    }

    // =========================================================================
    // INSERT / UPDATE / DELETE
    // =========================================================================

    [Fact]
    public void Insert_Values_StructurePreserved()
    {
        var pg = Translate("INSERT INTO Orders (Name, Amt) VALUES ('Test', 99.99)");
        pg.Should().Contain("INSERT INTO").And.Contain("VALUES");
    }

    [Fact]
    public void Update_Set_StructurePreserved()
    {
        var pg = Translate("UPDATE Orders SET Status = 'Shipped' WHERE Id = 1");
        pg.Should().Contain("UPDATE").And.Contain("SET").And.Contain("WHERE");
    }

    [Fact]
    public void Delete_TranslatedToDeleteFrom()
    {
        var pg = Translate("DELETE FROM Orders WHERE Id = 1");
        pg.Should().Contain("DELETE FROM").And.Contain("WHERE");
    }

    [Fact]
    public void Delete_WithJoin_UsesUsingSyntax()
    {
        var pg = Translate("DELETE o FROM Orders o INNER JOIN ToDelete d ON o.Id = d.Id");
        pg.Should().Contain("USING");
    }

    // =========================================================================
    // String concatenation
    // =========================================================================

    [Fact]
    public void StringConcat_PlusTranslatedToDoublePipe()
    {
        var pg = Translate("SELECT 'Hello' + ' ' + 'World'");
        pg.Should().Contain("||");
    }

    // =========================================================================
    // Cache behavior
    // =========================================================================

    [Fact]
    public void Cache_SecondCallReturnsCachedResult()
    {
        var result1 = TranslateFull("SELECT * FROM Orders WHERE Id = 1");
        var result2 = TranslateFull("SELECT * FROM Orders WHERE Id = 2");

        result1.FromCache.Should().BeFalse();
        result2.FromCache.Should().BeTrue("same normalised query structure should hit cache");
    }

    [Fact]
    public void Cache_DifferentStructure_NoCacheHit()
    {
        var result1 = TranslateFull("SELECT * FROM Orders");
        var result2 = TranslateFull("SELECT * FROM Customers");

        result1.FromCache.Should().BeFalse();
        result2.FromCache.Should().BeFalse();
    }

    // =========================================================================
    // Statement type classification
    // =========================================================================

    [Fact]
    public void StatementType_Select_Classified()
    {
        var result = TranslateFull("SELECT * FROM t");
        result.StatementType.Should().Be(StatementType.Select);
    }

    [Fact]
    public void StatementType_Insert_Classified()
    {
        var result = TranslateFull("INSERT INTO t VALUES (1)");
        result.StatementType.Should().Be(StatementType.Insert);
    }

    [Fact]
    public void StatementType_Batch_Classified()
    {
        var result = TranslateFull("SELECT 1; SELECT 2");
        result.StatementType.Should().Be(StatementType.Batch);
    }

    // =========================================================================
    // DROP TABLE IF EXISTS
    // =========================================================================

    [Fact]
    public void DropTableIfExists_TranslatedCorrectly()
    {
        var pg = Translate("DROP TABLE IF EXISTS #Temp");
        pg.Should().Contain("DROP TABLE IF EXISTS");
    }

    [Fact]
    public void DropTable_TranslatedCorrectly()
    {
        var pg = Translate("DROP TABLE Orders");
        pg.Should().Contain("DROP TABLE").And.Contain("Orders");
    }

    // =========================================================================
    // CASE expression
    // =========================================================================

    [Fact]
    public void CaseExpression_TranslatedCorrectly()
    {
        var pg = Translate("SELECT CASE WHEN Status = 1 THEN 'Active' ELSE 'Inactive' END FROM t");
        pg.Should().Contain("CASE WHEN").And.Contain("THEN").And.Contain("ELSE").And.Contain("END");
    }

    // =========================================================================
    // Parameters preserved
    // =========================================================================

    [Fact]
    public void Parameters_PreservedInOutput()
    {
        var pg = Translate("SELECT * FROM t WHERE Id = @id AND Name = @name");
        pg.Should().Contain("@id").And.Contain("@name");
    }
}
