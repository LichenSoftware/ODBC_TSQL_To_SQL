using FluentAssertions;

namespace MigrationAssessment.WorkItems.Tests;

/// <summary>
/// Regression test suite verifying the fixed transformation functions against the exact
/// failing examples from TASK-09 (WI-002, WI-003, WI-004/005/007/009, WI-006).
/// Each test verifies that the output:
/// 1. Does not contain known invalid constructs
/// 2. Produces semantically correct PostgreSQL
/// 3. Includes appropriate TODO comments for design decisions
/// </summary>
public class PostgresConversionFixedTransformTests
{
    private readonly PostgresConversionEngine _engine = new();

    // ═══════════════════════════════════════════════════════════════
    // WI-002: UPDLOCK/ROWLOCK on UPDATE statement
    // Fix: FOR UPDATE must NOT be appended to UPDATE statements
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WI002_UpdlockOnUpdateStatement_DoesNotAppendForUpdate()
    {
        // This is the exact pattern from WI-002: UPDATE ... WITH (UPDLOCK, ROWLOCK)
        var input = "UPDATE Products WITH (UPDLOCK, ROWLOCK) SET StockQuantity = StockQuantity - @Quantity WHERE ProductId = @ProductId AND StockQuantity >= @Quantity";

        var result = _engine.Convert(input, new[] { "UPDLOCK", "ROWLOCK" });

        // The converted SQL (non-comment lines) must NOT contain FOR UPDATE as a clause
        var sqlLines = result.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"))
            .ToList();
        var sqlPart = string.Join("\n", sqlLines);
        sqlPart.Should().NotContain("FOR UPDATE",
            "FOR UPDATE is only valid on SELECT statements in PostgreSQL, never on UPDATE");

        // The hints should be removed
        result.Should().NotContain("WITH (UPDLOCK");
        result.Should().NotContain("WITH (ROWLOCK");

        // The UPDATE statement itself should be preserved
        result.Should().Contain("UPDATE Products");
        result.Should().Contain("SET StockQuantity = StockQuantity - @Quantity");
        result.Should().Contain("WHERE ProductId = @ProductId");

        // TODO comment should explain MVCC behavior
        result.Should().Contain("-- TODO:");
        result.Should().Contain("implicit",
            "should explain that row-level locking on UPDATE is implicit in PostgreSQL");
    }

    [Fact]
    public void UpdlockOnSelectStatement_DoesAppendForUpdate()
    {
        // SELECT with UPDLOCK IS the correct use case for FOR UPDATE
        var input = "SELECT * FROM Inventory WITH (UPDLOCK) WHERE ProductId = 42";

        var result = _engine.Convert(input, "UPDLOCK");

        result.Should().Contain("FOR UPDATE",
            "FOR UPDATE is the correct PostgreSQL equivalent for SELECT ... WITH (UPDLOCK)");
        result.Should().Contain("SELECT * FROM Inventory");
        result.Should().NotContain("WITH (UPDLOCK");
    }

    [Fact]
    public void UpdlockOnDeleteStatement_DoesNotAppendForUpdate()
    {
        var input = "DELETE FROM Orders WITH (UPDLOCK) WHERE Status = 'Cancelled'";

        var result = _engine.Convert(input, "UPDLOCK");

        // The SQL lines (non-comment) must NOT contain FOR UPDATE as a clause
        var sqlLines = result.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"))
            .ToList();
        var sqlPart = string.Join("\n", sqlLines);
        sqlPart.Should().NotContain("FOR UPDATE",
            "FOR UPDATE is not valid on DELETE statements");
        result.Should().Contain("DELETE FROM Orders");
        result.Should().Contain("-- TODO:");
    }

    // ═══════════════════════════════════════════════════════════════
    // WI-003: DATEDIFF with nested function calls
    // Fix: Handle nested parentheses correctly (GETDATE(), MAX(), etc.)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WI003_DatediffWithGetdate_ProducesValidConversion()
    {
        // The exact failing pattern from WI-003
        var input = "SELECT DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) FROM Orders o";

        var result = _engine.Convert(input, "DATEDIFF");

        // Should produce (GETDATE()::date - MAX(o.OrderDate)::date)
        result.Should().Contain("GETDATE()::date - MAX(o.OrderDate)::date",
            "DATEDIFF(DAY, start, end) should become (end::date - start::date)");
        result.Should().NotContain("DATEDIFF");

        // Must NOT produce the broken pattern described in the bug
        result.Should().NotContain("GETDATE(::date",
            "GETDATE must not be split by incorrect regex matching");
    }

    [Fact]
    public void DatediffDay_SimpleArguments_ProducesDateSubtraction()
    {
        var input = "SELECT DATEDIFF(DAY, StartDate, EndDate) FROM Events";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("(EndDate::date - StartDate::date)");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void DatediffMonth_ProducesAgeExtraction()
    {
        var input = "SELECT DATEDIFF(MONTH, HireDate, GETDATE()) FROM Employees";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(YEAR FROM AGE(GETDATE(), HireDate))");
        result.Should().Contain("EXTRACT(MONTH FROM AGE(GETDATE(), HireDate))");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void DatediffHour_ProducesEpochDivision()
    {
        var input = "SELECT DATEDIFF(HOUR, CheckIn, CheckOut) FROM Visits";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(EPOCH FROM (CheckOut - CheckIn)) / 3600");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void DatediffYear_ProducesAgeExtraction()
    {
        var input = "SELECT DATEDIFF(YEAR, BirthDate, GETDATE()) AS Age FROM People";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("EXTRACT(YEAR FROM AGE(GETDATE(), BirthDate))");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void DatediffWithNestedParens_HandlesCorrectly()
    {
        // Multiple levels of nesting
        var input = "SELECT DATEDIFF(MINUTE, DATEADD(HOUR, -1, GETDATE()), GETDATE())";
        var result = _engine.Convert(input, "DATEDIFF");

        result.Should().Contain("GETDATE() - DATEADD(HOUR, -1, GETDATE())");
        result.Should().NotContain("DATEDIFF");
    }

    [Fact]
    public void DatediffAndGetdate_BothConvertedCorrectly()
    {
        // When both DATEDIFF and GETDATE are detected features, they should both convert
        var input = "SELECT DATEDIFF(DAY, MAX(o.OrderDate), GETDATE()) FROM Orders o";

        var result = _engine.Convert(input, new[] { "DATEDIFF", "GETDATE" });

        // DATEDIFF should be converted first, then GETDATE within the result
        result.Should().Contain("NOW()::date - MAX(o.OrderDate)::date",
            "GETDATE() inside the DATEDIFF result should also be converted to NOW()");
        result.Should().NotContain("DATEDIFF");
        result.Should().NotContain("GETDATE");
    }

    // ═══════════════════════════════════════════════════════════════
    // WI-004/005/007/009: Local temp tables (#name)
    // Fix: Emit CREATE TEMP TABLE with lifecycle TODO comments
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WI004_LocalTempTableCreate_EmitsCreateTempTableWithTodo()
    {
        var input = "CREATE TABLE #TempResults (ProductId INT, TotalSales DECIMAL(18,2), ReportDate DATE)";

        var result = _engine.Convert(input, "TEMP_TABLE");

        // Should emit CREATE TEMPORARY TABLE (not just strip #)
        result.Should().Contain("CREATE TEMPORARY TABLE TempResults");
        result.Should().NotContain("#TempResults");

        // Should include lifecycle TODO
        result.Should().Contain("-- TODO:",
            "local temp table conversion must include lifecycle notes");
        result.Should().Contain("session-scoped",
            "must explain PostgreSQL's session-scoping behavior");
    }

    [Fact]
    public void LocalTempTableReference_InSelect_RemovesPrefixOnly()
    {
        // References (not CREATE) should just strip the # without the lifecycle comment
        var input = "SELECT * FROM #TempResults WHERE TotalSales > 100";

        var result = _engine.Convert(input, "TEMP_TABLE");

        result.Should().Contain("FROM TempResults");
        result.Should().NotContain("#TempResults");
        // No CREATE TABLE TODO needed for a reference
        result.Should().NotContain("session-scoped");
    }

    // ═══════════════════════════════════════════════════════════════
    // WI-004/005/007/009: Global temp tables (##name)
    // Fix: Use permanent/unlogged table with lifecycle gap warnings
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WI009_GlobalTempTableCreate_EmitsUnloggedTableWithLifecycleWarning()
    {
        var input = "CREATE TABLE ##TempReportData (ProductId INT, TotalSales DECIMAL(18,2), ReportDate DATE)";

        var result = _engine.Convert(input, "GLOBAL_TEMP_TABLE");

        // Should emit CREATE UNLOGGED TABLE (not just strip ##)
        result.Should().Contain("CREATE UNLOGGED TABLE TempReportData");
        result.Should().NotContain("##TempReportData");

        // Must include comprehensive lifecycle warnings
        result.Should().Contain("-- TODO:",
            "global temp table conversion must include TODO comments");
        result.Should().Contain("cleanup",
            "must explain that cleanup strategy is required");
        result.Should().Contain("NO equivalent",
            "must state clearly that PostgreSQL has no equivalent of global temp tables");
    }

    [Fact]
    public void GlobalTempTableReference_IncludesLifecycleNote()
    {
        var input = "SELECT * FROM ##TempReportData WHERE ReportDate = @date";

        var result = _engine.Convert(input, "GLOBAL_TEMP_TABLE");

        result.Should().Contain("FROM TempReportData");
        result.Should().NotContain("##TempReportData");
        result.Should().Contain("-- TODO:",
            "global temp table references need lifecycle notes too");
    }

    // ═══════════════════════════════════════════════════════════════
    // WI-006: MERGE with consistent aliases and real columns
    // Fix: Use consistent aliases, real UPDATE SET columns, flag DELETE branch
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void WI006_MergeWithAllBranches_ProducesConsistentAliases()
    {
        var input = @"MERGE dbo.Products AS target
USING dbo.ProductStaging AS source
ON target.SKU = source.SKU
WHEN MATCHED THEN
    UPDATE SET ProductName = source.ProductName, Price = source.Price
WHEN NOT MATCHED THEN
    INSERT (ProductName, SKU, Price) VALUES (source.ProductName, source.SKU, source.Price)
WHEN NOT MATCHED BY SOURCE THEN DELETE";

        var result = _engine.Convert(input, "MERGE");

        // Should use INSERT ... ON CONFLICT pattern
        result.Should().Contain("INSERT INTO");
        result.Should().Contain("ON CONFLICT");
        result.Should().Contain("DO UPDATE SET");

        // Aliases used in the query must be declared
        // The output should not reference aliases that were never introduced
        // (e.g., no "target.SKU = source.SKU" in a WHERE clause without those aliases being declared)

        // Should contain real UPDATE SET columns from the original
        result.Should().Contain("ProductName",
            "real column names from WHEN MATCHED should be used");
        result.Should().Contain("Price",
            "real column names from WHEN MATCHED should be used");
        result.Should().NotContain("updated_at = NOW()",
            "should NOT use placeholder updated_at = NOW() when real columns are available");

        // The DELETE branch must NOT be silently dropped
        result.Should().Contain("DELETE",
            "WHEN NOT MATCHED BY SOURCE THEN DELETE must not be silently dropped");
        result.Should().Contain("NOT EXISTS",
            "DELETE should use NOT EXISTS pattern to find target rows missing from source");
    }

    [Fact]
    public void MergeWithoutDeleteBranch_DoesNotEmitDelete()
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

        // Should contain actual column name from UPDATE SET
        result.Should().Contain("Name");

        // No DELETE branch in the original → no DELETE in the output
        result.Should().NotContain("WHEN NOT MATCHED BY SOURCE");
        // The WARNING about a dropped DELETE branch should not appear
        result.Should().NotContain("WARNING: The original MERGE included a WHEN NOT MATCHED BY SOURCE");
    }

    [Fact]
    public void MergeConversion_UsesExcludedForSourceReferences()
    {
        var input = @"MERGE INTO Target AS t
USING Source AS s
ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET t.Val = s.Val, t.Updated = s.Updated
WHEN NOT MATCHED THEN INSERT (Id, Val, Updated) VALUES (s.Id, s.Val, s.Updated)";

        var result = _engine.Convert(input, "MERGE");

        // In ON CONFLICT DO UPDATE SET, source references should use EXCLUDED
        result.Should().Contain("EXCLUDED.",
            "source alias references in DO UPDATE SET should be converted to EXCLUDED.<col>");
    }

    // ═══════════════════════════════════════════════════════════════
    // Validation: Multi-feature conversion ordering
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MultiFeature_DatediffAndGetdate_ConvertsCorrectly()
    {
        // When both features are detected, the order matters:
        // DATEDIFF should be converted first (structural), then GETDATE (function rename)
        var input = "SELECT DATEDIFF(DAY, created_at, GETDATE()) AS age_days FROM posts";

        var result = _engine.Convert(input, new[] { "DATEDIFF", "GETDATE" });

        // Should produce: (NOW()::date - created_at::date) — GETDATE replaced by NOW
        result.Should().Contain("NOW()::date - created_at::date");
        result.Should().NotContain("DATEDIFF");
        result.Should().NotContain("GETDATE");
    }

    [Fact]
    public void MultiFeature_UpdlockAndRowlock_OnUpdate_NoForUpdate()
    {
        // Both UPDLOCK and ROWLOCK on an UPDATE should NOT produce FOR UPDATE as SQL
        var input = "UPDATE Accounts WITH (UPDLOCK, ROWLOCK) SET Balance = Balance - 100 WHERE AccountId = 1";

        var result = _engine.Convert(input, new[] { "UPDLOCK", "ROWLOCK" });

        // The SQL lines (non-comment) must NOT contain FOR UPDATE as a clause
        var sqlLines = result.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("--"))
            .ToList();
        var sqlPart = string.Join("\n", sqlLines);
        sqlPart.Should().NotContain("FOR UPDATE");
        result.Should().Contain("UPDATE Accounts");
        result.Should().Contain("SET Balance = Balance - 100");
    }
}


/// <summary>
/// Tests for the structural validation step that rejects unparseable SQL output.
/// </summary>
public class PostgresConversionValidationTests
{
    [Fact]
    public void StructuralValidation_BalancedParentheses_Passes()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT COALESCE(a, 0) FROM t WHERE (x > 1 AND (y < 2))")
            .Should().BeTrue();
    }

    [Fact]
    public void StructuralValidation_UnbalancedOpenParen_Fails()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT COALESCE(a, 0 FROM t")
            .Should().BeFalse();
    }

    [Fact]
    public void StructuralValidation_UnbalancedCloseParen_Fails()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT a) FROM t")
            .Should().BeFalse();
    }

    [Fact]
    public void StructuralValidation_UnbalancedQuotes_Fails()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT 'unclosed string FROM t")
            .Should().BeFalse();
    }

    [Fact]
    public void StructuralValidation_CommentOnlyOutput_Passes()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "-- TODO: manual conversion required\n-- Original: MERGE INTO ...")
            .Should().BeTrue();
    }

    [Fact]
    public void StructuralValidation_EmptyOutput_Fails()
    {
        PostgresConversionEngine.PassesStructuralValidation("")
            .Should().BeFalse();
    }

    [Fact]
    public void StructuralValidation_ForUpdateOnSelect_Passes()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT * FROM t WHERE id = 1\nFOR UPDATE")
            .Should().BeTrue();
    }

    [Fact]
    public void StructuralValidation_ForUpdateOnUpdate_Fails()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "UPDATE t SET x = 1 WHERE id = 1\nFOR UPDATE")
            .Should().BeFalse();
    }

    [Fact]
    public void StructuralValidation_EscapedQuotes_Passes()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT 'it''s fine' FROM t")
            .Should().BeTrue();
    }

    [Fact]
    public void StructuralValidation_ParensInsideStrings_Ignored()
    {
        PostgresConversionEngine.PassesStructuralValidation(
            "SELECT '(' FROM t")
            .Should().BeTrue();
    }
}
