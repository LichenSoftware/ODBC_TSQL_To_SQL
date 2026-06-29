using FluentAssertions;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Analysis.Tests;

/// <summary>
/// Unit tests for SchemaAnalyzer validating detection of SQL Server schema patterns
/// that require conversion for PostgreSQL migration.
/// </summary>
public class SchemaAnalyzerTests
{
    private readonly SchemaAnalyzer _analyzer = new();

    private static DatabaseObjectInventory CreateInventory(
        IReadOnlyList<TableMetadata>? tables = null,
        IReadOnlyList<IndexMetadata>? indexes = null)
    {
        return new DatabaseObjectInventory
        {
            Tables = tables ?? [],
            Indexes = indexes ?? [],
            Constraints = [],
            ForeignKeys = [],
            ProgrammableObjects = [],
            Synonyms = []
        };
    }

    private static TableMetadata CreateTable(string schema, string name, params ColumnMetadata[] columns)
    {
        return new TableMetadata
        {
            SchemaName = schema,
            TableName = name,
            Columns = columns
        };
    }

    private static ColumnMetadata Col(string name, string dataType,
        int? maxLength = null, bool isIdentity = false, string? computedDef = null)
    {
        return new ColumnMetadata
        {
            ColumnName = name,
            OrdinalPosition = 1,
            DataType = dataType,
            IsNullable = true,
            MaxLength = maxLength,
            IsIdentity = isIdentity,
            ComputedDefinition = computedDef
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Data Type Mappings
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("UNIQUEIDENTIFIER", "UUID")]
    [InlineData("DATETIME", "TIMESTAMPTZ")]
    [InlineData("SMALLDATETIME", "TIMESTAMPTZ")]
    [InlineData("BIT", "BOOLEAN")]
    [InlineData("IMAGE", "BYTEA")]
    [InlineData("MONEY", "NUMERIC(19,4)")]
    [InlineData("SMALLMONEY", "NUMERIC(10,4)")]
    [InlineData("TINYINT", "SMALLINT")]
    public void Analyze_DetectsDataTypeMapping(string sqlServerType, string expectedPgType)
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "TestTable", Col("TestCol", sqlServerType))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "DataType" &&
            f.ColumnName == "TestCol" &&
            f.PostgresType == expectedPgType);
    }

    [Fact]
    public void Analyze_DetectsVarcharMax_AsTextType()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Docs", Col("Content", "NVARCHAR", maxLength: -1))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "DataType" &&
            f.ColumnName == "Content" &&
            f.PostgresType == "TEXT");
    }

    [Fact]
    public void Analyze_DetectsVarbinaryMax_AsBytea()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Files", Col("Data", "VARBINARY", maxLength: -1))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "DataType" &&
            f.ColumnName == "Data" &&
            f.PostgresType == "BYTEA");
    }

    // ═══════════════════════════════════════════════════════════════
    // Identity Columns
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_DetectsIdentityColumn()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Orders", Col("OrderId", "INT", isIdentity: true))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "Identity" &&
            f.ColumnName == "OrderId" &&
            f.PostgresType == "GENERATED ALWAYS AS IDENTITY" &&
            f.RiskScore == 2);
    }

    // ═══════════════════════════════════════════════════════════════
    // Clustered Indexes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_DetectsClusteredIndex()
    {
        var inventory = CreateInventory(indexes: [
            new IndexMetadata
            {
                SchemaName = "dbo",
                TableName = "Orders",
                IndexName = "PK_Orders",
                IndexType = "CLUSTERED",
                KeyColumns = ["OrderId"]
            }
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "ClusteredIndex" &&
            f.ColumnName == "PK_Orders" &&
            f.RiskScore == 3);
    }

    [Fact]
    public void Analyze_DoesNotFlag_NonClusteredIndex()
    {
        var inventory = CreateInventory(indexes: [
            new IndexMetadata
            {
                SchemaName = "dbo",
                TableName = "Orders",
                IndexName = "IX_Orders_Date",
                IndexType = "NONCLUSTERED",
                KeyColumns = ["OrderDate"]
            }
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().NotContain(f => f.IssueType == "ClusteredIndex");
    }

    // ═══════════════════════════════════════════════════════════════
    // Collation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_FlagsStringColumns_ForCollationReview()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Users",
                Col("Name", "NVARCHAR", maxLength: 100),
                Col("Age", "INT"))
        ]);

        var result = _analyzer.Analyze(inventory);

        // String column should have collation finding
        result.Findings.Should().Contain(f =>
            f.IssueType == "Collation" &&
            f.ColumnName == "Name");

        // Non-string column should not
        result.Findings.Should().NotContain(f =>
            f.IssueType == "Collation" &&
            f.ColumnName == "Age");
    }

    // ═══════════════════════════════════════════════════════════════
    // Computed Columns
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_DetectsComputedColumn()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Products",
                Col("FullName", "NVARCHAR", computedDef: "FirstName + ' ' + LastName"))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f =>
            f.IssueType == "ComputedColumn" &&
            f.ColumnName == "FullName" &&
            f.RiskScore == 3);
    }

    // ═══════════════════════════════════════════════════════════════
    // Effort Estimation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_EmptyInventory_ReturnsZeroEffort()
    {
        var inventory = CreateInventory();

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().BeEmpty();
        result.EstimatedEffort.MinHours.Should().Be(0);
        result.EstimatedEffort.MaxHours.Should().Be(0);
    }

    [Fact]
    public void Analyze_WithFindings_ReturnsNonZeroEffort()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Orders",
                Col("Id", "INT", isIdentity: true),
                Col("Amount", "MONEY"),
                Col("Created", "DATETIME"))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.EstimatedEffort.MinHours.Should().BeGreaterThan(0);
        result.EstimatedEffort.MaxHours.Should().BeGreaterThan(0);
        result.EstimatedEffort.MaxHours.Should().BeGreaterThanOrEqualTo(result.EstimatedEffort.MinHours);
    }

    // ═══════════════════════════════════════════════════════════════
    // Output Structure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void Analyze_FindingsInclude_AllRequiredFields()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Accounts", Col("AccountId", "UNIQUEIDENTIFIER"))
        ]);

        var result = _analyzer.Analyze(inventory);

        var finding = result.Findings.First(f => f.IssueType == "DataType");
        finding.TableName.Should().Be("dbo.Accounts");
        finding.ColumnName.Should().Be("AccountId");
        finding.IssueType.Should().Be("DataType");
        finding.SqlServerType.Should().NotBeNullOrWhiteSpace();
        finding.PostgresType.Should().Be("UUID");
        finding.RiskScore.Should().BeInRange(1, 5);
    }

    [Fact]
    public void Analyze_FindingCountsByType_SummarizesCorrectly()
    {
        var inventory = CreateInventory(
            tables: [
                CreateTable("dbo", "T1",
                    Col("Id", "INT", isIdentity: true),
                    Col("Guid", "UNIQUEIDENTIFIER"),
                    Col("Name", "NVARCHAR", maxLength: 50))
            ],
            indexes: [
                new IndexMetadata
                {
                    SchemaName = "dbo",
                    TableName = "T1",
                    IndexName = "PK_T1",
                    IndexType = "CLUSTERED",
                    KeyColumns = ["Id"]
                }
            ]);

        var result = _analyzer.Analyze(inventory);

        result.FindingCountsByType.Should().ContainKey("DataType");
        result.FindingCountsByType.Should().ContainKey("Identity");
        result.FindingCountsByType.Should().ContainKey("ClusteredIndex");
        result.FindingCountsByType.Should().ContainKey("Collation");
    }

    [Fact]
    public void Analyze_MultipleTables_IncludesQualifiedTableName()
    {
        var inventory = CreateInventory(tables: [
            CreateTable("dbo", "Orders", Col("Id", "UNIQUEIDENTIFIER")),
            CreateTable("sales", "Invoices", Col("InvoiceId", "UNIQUEIDENTIFIER"))
        ]);

        var result = _analyzer.Analyze(inventory);

        result.Findings.Should().Contain(f => f.TableName == "dbo.Orders");
        result.Findings.Should().Contain(f => f.TableName == "sales.Invoices");
    }
}
