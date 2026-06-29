using FluentAssertions;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Unit tests for the PostgresConversionEngine per-feature transformation functions.
/// Validates that each feature converter produces syntactically correct PostgreSQL SQL
/// that preserves the original statement structure.
/// </summary>
public class PostgresConversionEngineTests
{
    private readonly PostgresConversionEngine _engine = new();

    // ═══════════════════════════════════════════════════════════════
    // ISNULL → COALESCE (Risk 2)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertIsnull_SimpleSelect_ReplacesWithCoalesce()
    {
        var input = "SELECT ISNULL(FirstName, 'Unknown') FROM Customers";
        var result = _engine.Convert(input, "ISNULL");

        result.Should().Contain("COALESCE(FirstName, 'Unknown')");
        result.Should().NotContain("ISNULL");
    }

    [Fact]
    public void ConvertIsnull_MultipleOccurrences_ReplacesAll()
    {
        var input = "SELECT ISNULL(A, 0), ISNULL(B, '') FROM T";
        var result = _engine.Convert(input, "ISNULL");

        result.Should().Contain("COALESCE(A, 0)");
        result.Should().Contain("COALESCE(B, '')");
        result.Should().NotContain("ISNULL");
    }

    [Fact]
    public void ConvertIsnull_PreservesRestOfStatement()
    {
        var input = "SELECT ISNULL(col, 0) AS val FROM dbo.Orders WHERE Status = 1";
        var result = _engine.Convert(input, "ISNULL");

        result.Should().Contain("COALESCE(col, 0) AS val");
        result.Should().Contain("FROM dbo.Orders WHERE Status = 1");
    }

    // ═══════════════════════════════════════════════════════════════
    // GETDATE → NOW() (Risk 2)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertGetdate_SimpleSelect_ReplacesWithNow()
    {
        var input = "SELECT GETDATE() AS CurrentTime";
        var result = _engine.Convert(input, "GETDATE");

        result.Should().Contain("NOW()");
        result.Should().NotContain("GETDATE");
    }

    [Fact]
    public void ConvertGetdate_InWhereClause_ReplacesWithNow()
    {
        var input = "SELECT * FROM Orders WHERE OrderDate > GETDATE()";
        var result = _engine.Convert(input, "GETDATE");

        result.Should().Contain("OrderDate > NOW()");
        result.Should().NotContain("GETDATE");
    }

    [Fact]
    public void ConvertGetdate_WithSpacesInParens_StillMatches()
    {
        var input = "INSERT INTO Logs (Created) VALUES (GETDATE( ))";
        var result = _engine.Convert(input, "GETDATE");

        result.Should().Contain("NOW()");
        result.Should().NotContain("GETDATE");
    }

    // ═══════════════════════════════════════════════════════════════
    // TOP → LIMIT (Risk 2)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertTop_SelectTopN_ConvertsToLimit()
    {
        var input = "SELECT TOP 10 * FROM Orders";
        var result = _engine.Convert(input, "TOP");

        result.Should().Contain("SELECT * FROM Orders");
        result.Should().Contain("LIMIT 10");
        result.Should().NotContain("TOP");
    }

    [Fact]
    public void ConvertTop_SelectTopWithParens_ConvertsToLimit()
    {
        var input = "SELECT TOP (25) Name, Email FROM Customers WHERE Active = 1";
        var result = _engine.Convert(input, "TOP");

        result.Should().Contain("SELECT Name, Email FROM Customers WHERE Active = 1");
        result.Should().Contain("LIMIT 25");
        result.Should().NotContain("TOP");
    }

    [Fact]
    public void ConvertTop_PreservesWhereAndOrderBy()
    {
        var input = "SELECT TOP 5 Id, Name FROM Products WHERE Price > 100 ORDER BY Price DESC";
        var result = _engine.Convert(input, "TOP");

        result.Should().Contain("Id, Name FROM Products WHERE Price > 100 ORDER BY Price DESC");
        result.Should().Contain("LIMIT 5");
    }

    // ═══════════════════════════════════════════════════════════════
    // DATEDIFF → PostgreSQL date arithmetic (Risk 2)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertDatediff_DayPart_UsesDateSubtraction()
    {
        var input = "SELECT DATEDIFF(DAY, StartDate, EndDate) FROM Events";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("(EndDate::date - StartDate::date)");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void ConvertDatediff_SecondPart_UsesExtractEpoch()
    {
        var input = "SELECT DATEDIFF(SECOND, @start, @end)";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(EPOCH FROM (@end - @start))::int");
    }

    [Fact]
    public void ConvertDatediff_MonthPart_UsesAge()
    {
        var input = "SELECT DATEDIFF(MONTH, HireDate, GETDATE()) FROM Employees";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(YEAR FROM AGE(");
        result.Should().Contain("EXTRACT(MONTH FROM AGE(");
    }

    [Fact]
    public void ConvertDatediff_YearPart_UsesAgeExtract()
    {
        var input = "SELECT DATEDIFF(YEAR, BirthDate, GETDATE()) AS Age FROM People";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(YEAR FROM AGE(");
    }

    [Fact]
    public void ConvertDatediff_HourPart_UsesEpochDivision()
    {
        var input = "SELECT DATEDIFF(HOUR, CheckIn, CheckOut) FROM Visits";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(EPOCH FROM (CheckOut - CheckIn)) / 3600");
    }

    [Fact]
    public void ConvertDatediff_MinutePart_UsesEpochDivision()
    {
        var input = "SELECT DATEDIFF(MINUTE, StartTime, EndTime) FROM Tasks";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(EPOCH FROM (EndTime - StartTime)) / 60");
    }

    // ═══════════════════════════════════════════════════════════════
    // NOLOCK → removed with TODO (Risk 4)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertNolock_RemovesHintAndAddsTodo()
    {
        var input = "SELECT * FROM Orders WITH (NOLOCK) WHERE Status = 'Active'";
        var result = _engine.Convert(input, "NOLOCK");

        result.Should().NotContain("WITH (NOLOCK)");
        result.Should().Contain("-- TODO:");
        result.Should().Contain("SELECT * FROM Orders");
        result.Should().Contain("WHERE Status = 'Active'");
    }

    [Fact]
    public void ConvertNolock_NoHintPresent_ReturnsUnchanged()
    {
        var input = "SELECT * FROM Orders WHERE Status = 'Active'";
        var result = _engine.Convert(input, "NOLOCK");

        result.Should().Be(input);
    }

    // ═══════════════════════════════════════════════════════════════
    // UPDLOCK → FOR UPDATE with TODO (Risk 4)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertUpdlock_ReplacesWithForUpdate()
    {
        var input = "SELECT * FROM Inventory WITH (UPDLOCK, ROWLOCK) WHERE ProductId = 42";
        var result = _engine.Convert(input, "UPDLOCK");

        result.Should().NotContain("WITH (UPDLOCK");
        result.Should().Contain("FOR UPDATE");
        result.Should().Contain("-- TODO:");
        result.Should().Contain("SELECT * FROM Inventory");
    }

    [Fact]
    public void ConvertUpdlock_NoHintPresent_ReturnsUnchanged()
    {
        var input = "SELECT * FROM Products WHERE Id = 1";
        var result = _engine.Convert(input, "UPDLOCK");

        result.Should().Be(input);
    }

    // ═══════════════════════════════════════════════════════════════
    // ROWLOCK → removed with TODO (Risk 4)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertRowlock_RemovesHintAndAddsTodo()
    {
        var input = "UPDATE Orders WITH (ROWLOCK) SET Status = 'Shipped' WHERE Id = 1";
        var result = _engine.Convert(input, "ROWLOCK");

        result.Should().NotContain("WITH (ROWLOCK)");
        result.Should().Contain("-- TODO:");
        result.Should().Contain("UPDATE Orders");
    }

    // ═══════════════════════════════════════════════════════════════
    // MERGE → INSERT ON CONFLICT (Risk 4)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertMerge_ProducesUpsertWithTodo()
    {
        var input = @"MERGE INTO dbo.Customers AS t
USING dbo.StagingCustomers AS s
ON t.CustomerId = s.CustomerId
WHEN MATCHED THEN UPDATE SET t.Name = s.Name
WHEN NOT MATCHED THEN INSERT (CustomerId, Name) VALUES (s.CustomerId, s.Name)";

        var result = _engine.Convert(input, "MERGE");

        result.Should().Contain("INSERT INTO");
        result.Should().Contain("ON CONFLICT");
        result.Should().Contain("DO UPDATE SET");
        result.Should().Contain("-- TODO:");
        result.Should().Contain("dbo.Customers");
    }

    [Fact]
    public void ConvertMerge_OutputIsMultiLine()
    {
        var input = "MERGE INTO Target USING Source ON Target.Id = Source.Id WHEN MATCHED THEN UPDATE SET Val = Source.Val";
        var result = _engine.Convert(input, "MERGE");

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThan(1,
            "MERGE conversion should produce a multi-line SQL snippet");
    }

    // ═══════════════════════════════════════════════════════════════
    // TEMP_TABLE → CREATE TEMPORARY TABLE (Risk 3)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertTempTable_CreateTable_AddsTemporary()
    {
        var input = "CREATE TABLE #TempOrders (Id INT, Amount DECIMAL(10,2))";
        var result = _engine.Convert(input, "TEMP_TABLE");

        result.Should().Contain("CREATE TEMPORARY TABLE TempOrders");
        result.Should().NotContain("#TempOrders");
    }

    [Fact]
    public void ConvertTempTable_Reference_RemovesHashPrefix()
    {
        var input = "SELECT * FROM #TempOrders WHERE Amount > 100";
        var result = _engine.Convert(input, "TEMP_TABLE");

        result.Should().Contain("FROM TempOrders");
        result.Should().NotContain("#TempOrders");
    }

    [Fact]
    public void ConvertTempTable_InsertInto_RemovesHashPrefix()
    {
        var input = "INSERT INTO #Results SELECT Id, Name FROM Customers";
        var result = _engine.Convert(input, "TEMP_TABLE");

        result.Should().Contain("INSERT INTO Results");
        result.Should().NotContain("#Results");
    }

    // ═══════════════════════════════════════════════════════════════
    // GLOBAL_TEMP_TABLE → UNLOGGED TABLE with TODO (Risk 4)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertGlobalTempTable_CreateTable_UsesUnlogged()
    {
        var input = "CREATE TABLE ##SharedCache (Key NVARCHAR(100), Value NVARCHAR(MAX))";
        var result = _engine.Convert(input, "GLOBAL_TEMP_TABLE");

        result.Should().Contain("CREATE UNLOGGED TABLE SharedCache");
        result.Should().Contain("-- TODO:");
        result.Should().NotContain("##SharedCache");
    }

    [Fact]
    public void ConvertGlobalTempTable_Reference_RemovesDoubleHash()
    {
        var input = "SELECT * FROM ##SharedCache WHERE Key = 'config'";
        var result = _engine.Convert(input, "GLOBAL_TEMP_TABLE");

        result.Should().Contain("FROM SharedCache");
        result.Should().NotContain("##SharedCache");
    }

    // ═══════════════════════════════════════════════════════════════
    // XML_METHOD → xpath/xmltable (Risk 5)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void ConvertXmlMethod_DotValue_ConvertsToXpath()
    {
        var input = "SELECT col.value('(/root/name)[1]', 'NVARCHAR(100)') FROM XmlData";
        var result = _engine.Convert(input, "XML_METHOD");

        result.Should().Contain("xpath('(/root/name)[1]', col)");
        result.Should().Contain("::text");
        result.Should().Contain("-- TODO:");
    }

    [Fact]
    public void ConvertXmlMethod_DotQuery_ConvertsToXpath()
    {
        var input = "SELECT doc.query('/invoice/items') FROM Documents";
        var result = _engine.Convert(input, "XML_METHOD");

        result.Should().Contain("xpath('/invoice/items', doc)");
    }

    [Fact]
    public void ConvertXmlMethod_DotExist_ConvertsToXpathExists()
    {
        var input = "SELECT * FROM Data WHERE payload.exist('/root/flag') = 1";
        var result = _engine.Convert(input, "XML_METHOD");

        result.Should().Contain("xpath_exists('/root/flag', payload)");
    }

    [Fact]
    public void ConvertXmlMethod_DotNodes_ConvertsToXmltable()
    {
        var input = "SELECT T.c.value('.', 'INT') FROM Data CROSS APPLY col.nodes('/items/item') AS T(c)";
        var result = _engine.Convert(input, "XML_METHOD");

        result.Should().Contain("xmltable('/items/item' PASSING col COLUMNS");
        result.Should().Contain("-- TODO:");
    }

    // ═══════════════════════════════════════════════════════════════
    // Multi-feature conversion
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Convert_MultipleFeatures_AppliesAllTransformations()
    {
        var input = "SELECT TOP 5 ISNULL(Name, 'N/A'), GETDATE() FROM Orders WITH (NOLOCK)";
        var result = _engine.Convert(input, new[] { "TOP", "ISNULL", "GETDATE", "NOLOCK" });

        result.Should().Contain("COALESCE(Name, 'N/A')");
        result.Should().Contain("NOW()");
        result.Should().Contain("LIMIT 5");
        result.Should().Contain("-- TODO:");
        result.Should().NotContain("ISNULL(");
        result.Should().NotContain("GETDATE()");
        result.Should().NotContain("WITH (NOLOCK)");
        result.Should().NotContain("TOP 5");
    }

    // ═══════════════════════════════════════════════════════════════
    // Output format validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Convert_Risk3Plus_IncludesTodoComments()
    {
        // Risk 4 features should include TODO comments
        var nolockInput = "SELECT * FROM T WITH (NOLOCK)";
        var nolockResult = _engine.Convert(nolockInput, "NOLOCK");
        nolockResult.Should().Contain("-- TODO:");

        // Risk 4 features
        var updlockInput = "SELECT * FROM T WITH (UPDLOCK)";
        var updlockResult = _engine.Convert(updlockInput, "UPDLOCK");
        updlockResult.Should().Contain("-- TODO:");

        // Risk 4 MERGE
        var mergeInput = "MERGE INTO T USING S ON T.id = S.id WHEN MATCHED THEN UPDATE SET x = 1";
        var mergeResult = _engine.Convert(mergeInput, "MERGE");
        mergeResult.Should().Contain("-- TODO:");

        // Risk 4 global temp table
        var globalInput = "CREATE TABLE ##Cache (Id INT)";
        var globalResult = _engine.Convert(globalInput, "GLOBAL_TEMP_TABLE");
        globalResult.Should().Contain("-- TODO:");

        // Risk 5 XML
        var xmlInput = "SELECT col.value('xpath', 'INT') FROM T";
        var xmlResult = _engine.Convert(xmlInput, "XML_METHOD");
        xmlResult.Should().Contain("-- TODO:");
    }

    [Fact]
    public void Convert_OutputIsMultiLineForComplexFeatures()
    {
        var mergeInput = "MERGE INTO T USING S ON T.id = S.id WHEN MATCHED THEN UPDATE SET x = 1";
        var result = _engine.Convert(mergeInput, "MERGE");

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().BeGreaterThan(1,
            "complex conversions should produce multi-line SQL");
    }

    [Fact]
    public void Convert_Risk1And2_ProducesValidSyntax()
    {
        // Risk 2: ISNULL should produce valid COALESCE
        var isnullResult = _engine.Convert("SELECT ISNULL(x, 0) FROM T", "ISNULL");
        isnullResult.Should().Contain("COALESCE(x, 0)");

        // Risk 2: GETDATE should produce valid NOW()
        var getdateResult = _engine.Convert("SELECT GETDATE()", "GETDATE");
        getdateResult.Should().Contain("NOW()");

        // Risk 2: TOP should produce valid LIMIT
        var topResult = _engine.Convert("SELECT TOP 10 * FROM T", "TOP");
        topResult.Should().Contain("LIMIT 10");

        // Risk 2: DATEDIFF with DAY should produce valid date subtraction
        var datediffResult = _engine.Convert("SELECT DATEDIFF(DAY, a, b) FROM T", "DATEDIFF");
        datediffResult.Should().Contain("(b::date - a::date)");
    }

    [Fact]
    public void Convert_UnknownFeature_ReturnsOriginalSql()
    {
        var input = "SELECT * FROM Products";
        var result = _engine.Convert(input, "UNKNOWN_FEATURE_XYZ");

        result.Should().Be(input,
            "unknown features should pass through the SQL unchanged");
    }

    [Fact]
    public void Convert_EmptyOrWhitespaceSql_ReturnsPlaceholder()
    {
        var result = _engine.Convert("", "ISNULL");
        result.Should().Contain("No SQL Server pattern provided");

        var whitespaceResult = _engine.Convert("   ", "GETDATE");
        whitespaceResult.Should().Contain("No SQL Server pattern provided");
    }
}
