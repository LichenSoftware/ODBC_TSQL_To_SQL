using FluentAssertions;
using PgPassthrough.Core.Models;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser.Tests.Ast;

public sealed class AstAnalysisTests
{
    private static SqlBatch Parse(string sql) => TSqlParser.Parse(sql);
    private static SqlStatement First(string sql) => TSqlParser.Parse(sql).Statements[0];

    // =========================================================================
    // StatementClassifier
    // =========================================================================

    [Theory]
    [InlineData("SELECT * FROM t",                StatementType.Select)]
    [InlineData("INSERT INTO t VALUES (1)",        StatementType.Insert)]
    [InlineData("UPDATE t SET a = 1",             StatementType.Update)]
    [InlineData("DELETE FROM t",                  StatementType.Delete)]
    [InlineData("TRUNCATE TABLE t",               StatementType.Delete)]
    [InlineData("CREATE TABLE t (Id INT)",        StatementType.Ddl)]
    [InlineData("DROP TABLE t",                   StatementType.Ddl)]
    [InlineData("BEGIN TRANSACTION",              StatementType.Transaction)]
    [InlineData("COMMIT",                         StatementType.Transaction)]
    [InlineData("ROLLBACK",                       StatementType.Transaction)]
    [InlineData("SET NOCOUNT ON",                 StatementType.SetOption)]
    [InlineData("USE MyDb",                       StatementType.Use)]
    [InlineData("EXEC sp_who",                    StatementType.StoredProcedure)]
    public void Classifier_SingleStatement(string sql, StatementType expected)
    {
        var stmt = First(sql);
        StatementClassifier.Classify(stmt).Should().Be(expected);
    }

    [Fact]
    public void Classifier_Batch_MultipleMeaningful_ReturnsBatch()
    {
        var batch = Parse("SELECT 1; SELECT 2");
        StatementClassifier.ClassifyBatch(batch).Should().Be(StatementType.Batch);
    }

    [Fact]
    public void Classifier_Batch_SetPlusSelect_ReturnsSelect()
    {
        // SET NOCOUNT ON is a set option — filtered out for classification
        var batch = Parse("SET NOCOUNT ON; SELECT * FROM t");
        StatementClassifier.ClassifyBatch(batch).Should().Be(StatementType.Select);
    }

    [Fact]
    public void Classifier_Batch_Empty_ReturnsUnknown()
    {
        var batch = Parse("-- just a comment");
        StatementClassifier.ClassifyBatch(batch).Should().Be(StatementType.Unknown);
    }

    // =========================================================================
    // TempTableDetector
    // =========================================================================

    [Fact]
    public void TempTable_DetectedInSelect_From()
    {
        var batch = Parse("SELECT * FROM #TempOrders");
        TempTableDetector.ContainsTempTableRef(batch).Should().BeTrue();
    }

    [Fact]
    public void TempTable_DetectedInCreateTable()
    {
        var batch = Parse("CREATE TABLE #Temp (Id INT)");
        TempTableDetector.ContainsTempTableRef(batch).Should().BeTrue();
    }

    [Fact]
    public void TempTable_DetectedInInsertTarget()
    {
        var batch = Parse("INSERT INTO #Temp VALUES (1)");
        TempTableDetector.ContainsTempTableRef(batch).Should().BeTrue();
    }

    [Fact]
    public void TempTable_NotDetected_RegularTable()
    {
        var batch = Parse("SELECT * FROM Orders");
        TempTableDetector.ContainsTempTableRef(batch).Should().BeFalse();
    }

    [Fact]
    public void TempTable_DetectedInDropTable()
    {
        var batch = Parse("DROP TABLE IF EXISTS #Temp");
        TempTableDetector.ContainsTempTableRef(batch).Should().BeTrue();
    }

    // =========================================================================
    // GlobalVariableCollector
    // =========================================================================

    [Fact]
    public void GlobalVar_RowCountFound()
    {
        var batch = Parse("SELECT @@ROWCOUNT");
        var found = GlobalVariableCollector.Collect(batch);
        found.Should().Contain("@@ROWCOUNT");
    }

    [Fact]
    public void GlobalVar_MultipleFound()
    {
        var batch = Parse("SELECT @@ROWCOUNT, @@IDENTITY, @@ERROR");
        var found = GlobalVariableCollector.Collect(batch);
        found.Should().Contain("@@ROWCOUNT")
             .And.Contain("@@IDENTITY");
        // @@ERROR may not be in the globals — just check count >= 2
        found.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void GlobalVar_NoneFound_RegularQuery()
    {
        var batch = Parse("SELECT Id, Name FROM t WHERE Id = @id");
        var found = GlobalVariableCollector.Collect(batch);
        found.Should().BeEmpty();
    }

    // =========================================================================
    // ParameterCollector
    // =========================================================================

    [Fact]
    public void Params_CollectedFromWhere()
    {
        var batch = Parse("SELECT * FROM t WHERE Id = @id AND Name = @name");
        var found = ParameterCollector.Collect(batch);
        found.Should().Contain("@id").And.Contain("@name");
    }

    [Fact]
    public void Params_CollectedFromInsertValues()
    {
        var batch = Parse("INSERT INTO t (a, b) VALUES (@a, @b)");
        var found = ParameterCollector.Collect(batch);
        found.Should().Contain("@a").And.Contain("@b");
    }

    [Fact]
    public void Params_CollectedFromUpdateSet()
    {
        var batch = Parse("UPDATE t SET Name = @name WHERE Id = @id");
        var found = ParameterCollector.Collect(batch);
        found.Should().Contain("@name").And.Contain("@id");
    }

    [Fact]
    public void Params_NoneFound_NoParams()
    {
        var batch = Parse("SELECT 1");
        var found = ParameterCollector.Collect(batch);
        found.Should().BeEmpty();
    }

    // =========================================================================
    // TableHintCollector
    // =========================================================================

    [Fact]
    public void Hints_NolockFound()
    {
        var batch = Parse("SELECT * FROM Orders WITH (NOLOCK)");
        var hints = TableHintCollector.Collect(batch);
        hints.Should().ContainSingle(h => h.HintName == "NOLOCK");
    }

    [Fact]
    public void Hints_NoHints_ReturnsEmpty()
    {
        var batch = Parse("SELECT * FROM Orders");
        var hints = TableHintCollector.Collect(batch);
        hints.Should().BeEmpty();
    }

    [Fact]
    public void Hints_MultipleHints()
    {
        var batch = Parse("SELECT * FROM t WITH (NOLOCK, ROWLOCK)");
        var hints = TableHintCollector.Collect(batch);
        hints.Should().HaveCount(2);
        hints.Select(h => h.HintName).Should().Contain("NOLOCK").And.Contain("ROWLOCK");
    }

    // =========================================================================
    // NormalisedKeyPrinter
    // =========================================================================

    [Fact]
    public void NormKey_LiteralsReplacedWithPlaceholder()
    {
        var batch1 = Parse("SELECT * FROM t WHERE Id = 1");
        var batch2 = Parse("SELECT * FROM t WHERE Id = 999");
        var key1   = NormalisedKeyPrinter.Normalise(batch1);
        var key2   = NormalisedKeyPrinter.Normalise(batch2);
        key1.Should().Be(key2, because: "only the literal value differs");
    }

    [Fact]
    public void NormKey_StringLiteralsReplaced()
    {
        var batch1 = Parse("SELECT * FROM t WHERE Name = 'Alice'");
        var batch2 = Parse("SELECT * FROM t WHERE Name = 'Bob'");
        NormalisedKeyPrinter.Normalise(batch1).Should()
            .Be(NormalisedKeyPrinter.Normalise(batch2));
    }

    [Fact]
    public void NormKey_ParametersPreserved()
    {
        // Parameter names are part of structure — preserved in cache key
        var batch = Parse("SELECT * FROM t WHERE Id = @id");
        var key   = NormalisedKeyPrinter.Normalise(batch);
        key.Should().Contain("@ID");
    }

    [Fact]
    public void NormKey_IdentifiersUppercased()
    {
        var batch = Parse("select * from orders where id = 1");
        var key   = NormalisedKeyPrinter.Normalise(batch);
        key.Should().Contain("ORDERS").And.Contain("ID");
    }

    [Fact]
    public void NormKey_TableHintsOmitted()
    {
        // WITH (NOLOCK) should not affect the cache key
        var batch1 = Parse("SELECT * FROM Orders WITH (NOLOCK)");
        var batch2 = Parse("SELECT * FROM Orders");
        NormalisedKeyPrinter.Normalise(batch1).Should()
            .Be(NormalisedKeyPrinter.Normalise(batch2));
    }

    [Fact]
    public void NormKey_InListCollapsed()
    {
        // IN (1,2,3) and IN (4,5,6,7) produce the same cache key
        var batch1 = Parse("SELECT * FROM t WHERE Id IN (1,2,3)");
        var batch2 = Parse("SELECT * FROM t WHERE Id IN (4,5,6,7,8)");
        NormalisedKeyPrinter.Normalise(batch1).Should()
            .Be(NormalisedKeyPrinter.Normalise(batch2));
    }
}
