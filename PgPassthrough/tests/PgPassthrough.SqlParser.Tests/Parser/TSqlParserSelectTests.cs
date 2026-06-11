using FluentAssertions;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;

namespace PgPassthrough.SqlParser.Tests.Parser;

/// <summary>SELECT statement parsing tests.</summary>
public sealed class TSqlParserSelectTests
{
    private static SqlBatch Parse(string sql) => TSqlParser.Parse(sql);

    private static SelectStatement ParseSelect(string sql)
    {
        var batch = Parse(sql);
        batch.Statements.Should().HaveCount(1);
        return batch.Statements[0].Should().BeOfType<SelectStatement>().Subject;
    }

    // -------------------------------------------------------------------------
    // Basic SELECT
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_StarFromTable()
    {
        var s = ParseSelect("SELECT * FROM Orders");
        s.SelectList.Should().HaveCount(1);
        s.SelectList[0].IsStar.Should().BeTrue();
        s.From.Should().HaveCount(1);
        var tbl = s.From[0].Should().BeOfType<TableReferenceSource>().Subject;
        tbl.Name.Name.Should().Be("Orders");
    }

    [Fact]
    public void Select_SingleColumn()
    {
        var s = ParseSelect("SELECT OrderId FROM Orders");
        s.SelectList.Should().HaveCount(1);
        var item = s.SelectList[0];
        item.IsStar.Should().BeFalse();
        var col = item.Expression.Should().BeOfType<ColumnReferenceExpression>().Subject;
        col.ColumnName.Should().Be("OrderId");
    }

    [Fact]
    public void Select_MultipleColumns()
    {
        var s = ParseSelect("SELECT a, b, c FROM t");
        s.SelectList.Should().HaveCount(3);
    }

    [Fact]
    public void Select_ColumnWithAlias_As()
    {
        var s = ParseSelect("SELECT OrderId AS Id FROM Orders");
        s.SelectList[0].Alias.Should().Be("Id");
    }

    [Fact]
    public void Select_Distinct()
    {
        var s = ParseSelect("SELECT DISTINCT City FROM Customers");
        s.Distinct.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // TOP clause
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_TopN()
    {
        var s = ParseSelect("SELECT TOP 10 * FROM Orders");
        s.Top.Should().NotBeNull();
        var topVal = s.Top!.Count.Should().BeOfType<IntegerLiteralExpression>().Subject;
        topVal.Value.Should().Be(10);
        s.Top.Percent.Should().BeFalse();
        s.Top.WithTies.Should().BeFalse();
    }

    [Fact]
    public void Select_TopWithParens()
    {
        var s = ParseSelect("SELECT TOP (5) * FROM t");
        s.Top!.Count.Should().BeOfType<IntegerLiteralExpression>()
              .Which.Value.Should().Be(5);
    }

    // -------------------------------------------------------------------------
    // WHERE clause
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_WhereSimpleEquality()
    {
        var s = ParseSelect("SELECT * FROM Orders WHERE OrderId = 1");
        s.Where.Should().NotBeNull();
        var bin = s.Where.Should().BeOfType<BinaryExpression>().Subject;
        bin.Operator.Should().Be(BinaryOperator.Equal);
        bin.Left.Should().BeOfType<ColumnReferenceExpression>()
           .Which.ColumnName.Should().Be("OrderId");
        bin.Right.Should().BeOfType<IntegerLiteralExpression>()
           .Which.Value.Should().Be(1);
    }

    [Fact]
    public void Select_WhereAndOr()
    {
        var s = ParseSelect("SELECT * FROM t WHERE a = 1 AND b = 2 OR c = 3");
        s.Where.Should().BeOfType<BinaryExpression>()
               .Which.Operator.Should().Be(BinaryOperator.Or);
    }

    [Fact]
    public void Select_WhereIsNull()
    {
        var s = ParseSelect("SELECT * FROM t WHERE col IS NULL");
        s.Where.Should().BeOfType<IsNullExpression>()
               .Which.IsNot.Should().BeFalse();
    }

    [Fact]
    public void Select_WhereIsNotNull()
    {
        var s = ParseSelect("SELECT * FROM t WHERE col IS NOT NULL");
        s.Where.Should().BeOfType<IsNullExpression>()
               .Which.IsNot.Should().BeTrue();
    }

    [Fact]
    public void Select_WhereLike()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Name LIKE '%Smith%'");
        var like = s.Where.Should().BeOfType<LikeExpression>().Subject;
        like.IsNot.Should().BeFalse();
        like.Pattern.Should().BeOfType<StringLiteralExpression>()
                    .Which.Value.Should().Be("%Smith%");
    }

    [Fact]
    public void Select_WhereNotLike()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Name NOT LIKE 'A%'");
        s.Where.Should().BeOfType<LikeExpression>().Which.IsNot.Should().BeTrue();
    }

    [Fact]
    public void Select_WhereBetween()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Age BETWEEN 18 AND 65");
        var between = s.Where.Should().BeOfType<BetweenExpression>().Subject;
        between.IsNot.Should().BeFalse();
        between.Low.Should().BeOfType<IntegerLiteralExpression>().Which.Value.Should().Be(18);
        between.High.Should().BeOfType<IntegerLiteralExpression>().Which.Value.Should().Be(65);
    }

    [Fact]
    public void Select_WhereInList()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Id IN (1, 2, 3)");
        var inList = s.Where.Should().BeOfType<InListExpression>().Subject;
        inList.IsNot.Should().BeFalse();
        inList.Items.Should().HaveCount(3);
    }

    [Fact]
    public void Select_WhereNotIn()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Id NOT IN (1, 2)");
        s.Where.Should().BeOfType<InListExpression>().Which.IsNot.Should().BeTrue();
    }

    [Fact]
    public void Select_WhereParameter()
    {
        var s = ParseSelect("SELECT * FROM t WHERE Id = @id");
        var bin = s.Where.Should().BeOfType<BinaryExpression>().Subject;
        bin.Right.Should().BeOfType<ParameterExpression>()
                 .Which.Name.Should().Be("@id");
    }

    // -------------------------------------------------------------------------
    // ORDER BY
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_OrderByAscDesc()
    {
        var s = ParseSelect("SELECT * FROM t ORDER BY Name ASC, Age DESC");
        s.OrderBy.Should().HaveCount(2);
        s.OrderBy[0].Direction.Should().Be(SortDirection.Ascending);
        s.OrderBy[1].Direction.Should().Be(SortDirection.Descending);
    }

    // -------------------------------------------------------------------------
    // GROUP BY / HAVING
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_GroupByHaving()
    {
        var s = ParseSelect("SELECT City, COUNT(*) FROM t GROUP BY City HAVING COUNT(*) > 5");
        s.GroupBy.Should().HaveCount(1);
        s.Having.Should().NotBeNull();
    }

    // -------------------------------------------------------------------------
    // JOINs
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_InnerJoin()
    {
        var s = ParseSelect("SELECT * FROM Orders o INNER JOIN Customers c ON o.CustomerId = c.Id");
        s.From.Should().HaveCount(1);
        var join = s.From[0].Should().BeOfType<JoinedSource>().Subject;
        join.JoinType.Should().Be(JoinType.Inner);
        join.Condition.Should().NotBeNull();
    }

    [Fact]
    public void Select_LeftOuterJoin()
    {
        var s = ParseSelect("SELECT * FROM a LEFT OUTER JOIN b ON a.id = b.id");
        var join = s.From[0].Should().BeOfType<JoinedSource>().Subject;
        join.JoinType.Should().Be(JoinType.LeftOuter);
    }

    [Fact]
    public void Select_CrossJoin()
    {
        var s = ParseSelect("SELECT * FROM a CROSS JOIN b");
        var join = s.From[0].Should().BeOfType<JoinedSource>().Subject;
        join.JoinType.Should().Be(JoinType.Cross);
    }

    // -------------------------------------------------------------------------
    // Table hints
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_WithNolock()
    {
        var s = ParseSelect("SELECT * FROM Orders WITH (NOLOCK)");
        var tbl = s.From[0].Should().BeOfType<TableReferenceSource>().Subject;
        tbl.Hints.Should().ContainSingle(h => h.HintName == "NOLOCK");
    }

    // -------------------------------------------------------------------------
    // Subqueries
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_SubqueryInFrom()
    {
        var s = ParseSelect("SELECT * FROM (SELECT Id FROM t) AS sub");
        s.From[0].Should().BeOfType<SubquerySource>()
                 .Which.Alias.Should().Be("sub");
    }

    [Fact]
    public void Select_SubqueryInWhere_Exists()
    {
        var s = ParseSelect("SELECT * FROM t WHERE EXISTS (SELECT 1 FROM u WHERE u.id = t.id)");
        s.Where.Should().BeOfType<ExistsExpression>();
    }

    [Fact]
    public void Select_SubqueryInWhere_InSubquery()
    {
        var s = ParseSelect("SELECT * FROM t WHERE id IN (SELECT id FROM u)");
        s.Where.Should().BeOfType<InSubqueryExpression>();
    }

    // -------------------------------------------------------------------------
    // Expressions
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_CaseExpression_Searched()
    {
        var s = ParseSelect("SELECT CASE WHEN a > 1 THEN 'Y' ELSE 'N' END FROM t");
        var item = s.SelectList[0].Expression.Should().BeOfType<CaseExpression>().Subject;
        item.WhenClauses.Should().HaveCount(1);
        item.ElseExpression.Should().NotBeNull();
    }

    [Fact]
    public void Select_CastExpression()
    {
        var s = ParseSelect("SELECT CAST(Price AS DECIMAL(10,2)) FROM t");
        s.SelectList[0].Expression.Should().BeOfType<CastExpression>();
    }

    [Fact]
    public void Select_ConvertExpression()
    {
        var s = ParseSelect("SELECT CONVERT(VARCHAR(20), OrderDate, 103) FROM t");
        var conv = s.SelectList[0].Expression.Should().BeOfType<ConvertExpression>().Subject;
        conv.Style.Should().NotBeNull();
    }

    [Fact]
    public void Select_FunctionCall()
    {
        var s = ParseSelect("SELECT LEN(Name) FROM t");
        var fn = s.SelectList[0].Expression.Should().BeOfType<FunctionCallExpression>().Subject;
        fn.Name.Name.Should().Be("LEN");
        fn.Arguments.Should().HaveCount(1);
    }

    [Fact]
    public void Select_GlobalVariable_RowCount()
    {
        var s = ParseSelect("SELECT @@ROWCOUNT");
        s.SelectList[0].Expression.Should().BeOfType<GlobalVariableExpression>()
                       .Which.Name.Should().Be("@@ROWCOUNT");
    }

    [Fact]
    public void Select_QualifiedColumnName()
    {
        var s = ParseSelect("SELECT o.OrderId FROM Orders o");
        var col = s.SelectList[0].Expression.Should().BeOfType<ColumnReferenceExpression>().Subject;
        col.TableAlias.Should().Be("o");
        col.ColumnName.Should().Be("OrderId");
    }

    [Fact]
    public void Select_NullLiteral()
    {
        var s = ParseSelect("SELECT NULL");
        s.SelectList[0].Expression.Should().BeOfType<NullLiteralExpression>();
    }

    [Fact]
    public void Select_StringLiteral()
    {
        var s = ParseSelect("SELECT 'hello'");
        s.SelectList[0].Expression.Should().BeOfType<StringLiteralExpression>()
                       .Which.Value.Should().Be("hello");
    }

    // -------------------------------------------------------------------------
    // UNION
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_UnionAll()
    {
        var s = ParseSelect("SELECT 1 UNION ALL SELECT 2");
        s.SetOperator.Should().NotBeNull();
        s.SetOperator!.Kind.Should().Be(SetOperatorKind.Union);
        s.SetOperator.All.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Qualified names
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_SchemaQualifiedTable()
    {
        var s = ParseSelect("SELECT * FROM dbo.Orders");
        var tbl = s.From[0].Should().BeOfType<TableReferenceSource>().Subject;
        tbl.Name.Schema.Should().Be("dbo");
        tbl.Name.Name.Should().Be("Orders");
    }

    [Fact]
    public void Select_ThreePartName()
    {
        var s = ParseSelect("SELECT * FROM mydb.dbo.Orders");
        var tbl = s.From[0].Should().BeOfType<TableReferenceSource>().Subject;
        tbl.Name.Database.Should().Be("mydb");
        tbl.Name.Schema.Should().Be("dbo");
        tbl.Name.Name.Should().Be("Orders");
    }

    // -------------------------------------------------------------------------
    // Arithmetic expressions
    // -------------------------------------------------------------------------

    [Fact]
    public void Select_ArithmeticExpression()
    {
        var s = ParseSelect("SELECT Price * 1.1 FROM t");
        s.SelectList[0].Expression.Should().BeOfType<BinaryExpression>()
                       .Which.Operator.Should().Be(BinaryOperator.Multiply);
    }

    [Fact]
    public void Select_UnaryNegation()
    {
        var s = ParseSelect("SELECT -1");
        s.SelectList[0].Expression.Should().BeOfType<UnaryExpression>()
                       .Which.Operator.Should().Be(UnaryOperator.Negate);
    }
}
