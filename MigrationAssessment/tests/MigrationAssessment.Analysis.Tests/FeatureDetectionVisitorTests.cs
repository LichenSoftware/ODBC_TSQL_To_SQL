using FluentAssertions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using MigrationAssessment.Analysis;

namespace MigrationAssessment.Analysis.Tests;

/// <summary>
/// Unit tests validating detection of new SQL Server features by the FeatureDetectionVisitor.
/// Each test parses a sample SQL statement and verifies the expected feature is detected.
/// </summary>
public class FeatureDetectionVisitorTests
{
    private IReadOnlyList<Core.Models.DetectedFeature> DetectFeatures(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        var visitor = new FeatureDetectionVisitor("test-stmt-1");
        fragment.Accept(visitor);
        visitor.FinalizeDetection();
        return visitor.DetectedFeatures;
    }

    // ═══════════════════════════════════════════════════════════════
    // STRING_CONCAT_PLUS
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_StringConcatPlus_InSelect()
    {
        var sql = "SELECT FirstName + ' ' + LastName AS FullName FROM Employees";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "STRING_CONCAT_PLUS");
    }

    [Fact]
    public void Detects_StringConcatPlus_InWhere()
    {
        var sql = "SELECT * FROM Logs WHERE Category + ':' + Message LIKE '%error%'";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "STRING_CONCAT_PLUS");
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP_WITHOUT_ORDER
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_TopWithoutOrder_WhenNoOrderBy()
    {
        var sql = "SELECT TOP 10 * FROM Orders";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "TOP_WITHOUT_ORDER");
        features.Should().Contain(f => f.FeatureName == "TOP");
    }

    [Fact]
    public void DoesNotDetect_TopWithoutOrder_WhenOrderByPresent()
    {
        var sql = "SELECT TOP 10 * FROM Orders ORDER BY OrderDate DESC";
        var features = DetectFeatures(sql);
        features.Should().NotContain(f => f.FeatureName == "TOP_WITHOUT_ORDER");
        features.Should().Contain(f => f.FeatureName == "TOP");
    }

    // ═══════════════════════════════════════════════════════════════
    // TRY_CATCH (already existed, validate detection)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_TryCatch_InProcedureBody()
    {
        var sql = @"
            BEGIN TRY
                SELECT 1/0
            END TRY
            BEGIN CATCH
                SELECT ERROR_MESSAGE()
            END CATCH";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "TRY_CATCH");
    }

    // ═══════════════════════════════════════════════════════════════
    // PRINT_STATEMENT
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_PrintStatement()
    {
        var sql = "PRINT 'Processing started'";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "PRINT_STATEMENT");
    }

    [Fact]
    public void Detects_PrintStatement_WithVariable()
    {
        var sql = "DECLARE @msg NVARCHAR(100) = 'Hello'; PRINT @msg";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "PRINT_STATEMENT");
    }

    // ═══════════════════════════════════════════════════════════════
    // RAISERROR
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_Raiserror()
    {
        var sql = "RAISERROR('An error occurred: %s', 16, 1, 'details')";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "RAISERROR");
    }

    // ═══════════════════════════════════════════════════════════════
    // THROW
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_Throw()
    {
        var sql = "THROW 50001, 'Custom error message', 1;";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "THROW");
    }

    // ═══════════════════════════════════════════════════════════════
    // IMPLICIT_CONVERSION
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_ImplicitConversion_IntColumnComparedToString()
    {
        var sql = "SELECT * FROM Orders WHERE OrderId = '123'";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "IMPLICIT_CONVERSION");
    }

    [Fact]
    public void DoesNotDetect_ImplicitConversion_StringToString()
    {
        var sql = "SELECT * FROM Orders WHERE Status = 'Active'";
        var features = DetectFeatures(sql);
        // 'Active' is not a numeric string, so no implicit conversion
        features.Should().NotContain(f => f.FeatureName == "IMPLICIT_CONVERSION");
    }

    // ═══════════════════════════════════════════════════════════════
    // CROSS_APPLY / OUTER_APPLY (already existed, validate)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_CrossApply()
    {
        var sql = "SELECT * FROM Orders o CROSS APPLY dbo.GetItems(o.OrderId) AS i";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "CROSS_APPLY");
    }

    [Fact]
    public void Detects_OuterApply()
    {
        var sql = "SELECT * FROM Customers c OUTER APPLY dbo.GetOrders(c.CustomerId) AS o";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "OUTER_APPLY");
    }

    // ═══════════════════════════════════════════════════════════════
    // PIVOT / UNPIVOT (already existed, validate)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_Pivot()
    {
        var sql = @"SELECT * FROM Sales
            PIVOT (SUM(Amount) FOR Quarter IN ([Q1],[Q2],[Q3],[Q4])) AS pvt";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "PIVOT");
    }

    [Fact]
    public void Detects_Unpivot()
    {
        var sql = @"SELECT * FROM Quarterly
            UNPIVOT (Amount FOR Quarter IN ([Q1],[Q2],[Q3],[Q4])) AS unpvt";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "UNPIVOT");
    }

    // ═══════════════════════════════════════════════════════════════
    // STRING_SPLIT
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_StringSplit()
    {
        // STRING_SPLIT as a function call is detected via FunctionCall visitor
        var sql = "SELECT * FROM dbo.STRING_SPLIT('a,b,c', ',')";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "STRING_SPLIT");
    }

    // ═══════════════════════════════════════════════════════════════
    // OPENJSON
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_OpenJson()
    {
        var sql = "SELECT * FROM OPENJSON(@json)";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "OPENJSON");
    }

    [Fact]
    public void Detects_OpenJson_WithSchema()
    {
        var sql = @"SELECT id, name FROM OPENJSON(@json)
            WITH (id INT '$.id', name NVARCHAR(100) '$.name')";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "OPENJSON");
    }

    // ═══════════════════════════════════════════════════════════════
    // FOR XML
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Detects_ForXmlPath()
    {
        var sql = "SELECT Name FROM Employees FOR XML PATH('Employee')";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "FOR_XML");
    }

    [Fact]
    public void Detects_ForXmlAuto()
    {
        var sql = "SELECT * FROM Orders FOR XML AUTO";
        var features = DetectFeatures(sql);
        features.Should().Contain(f => f.FeatureName == "FOR_XML");
    }
}
