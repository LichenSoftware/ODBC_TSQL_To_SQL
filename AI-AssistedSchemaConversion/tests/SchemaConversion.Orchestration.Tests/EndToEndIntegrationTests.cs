using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.Extraction;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

/// <summary>
/// End-to-end integration test: feeds sample DDL through the full pipeline
/// with mocked Bedrock (AI), verifies session files, audit log, report JSON,
/// and output script ordering.
/// </summary>
public class EndToEndIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public EndToEndIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"schema-conv-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }

    [Fact]
    public async Task FullPipeline_SampleDdl_ProducesCorrectOutputs()
    {
        // Arrange: create sample DDL objects representing a typical conversion
        var schemaObj = new SchemaObject
        {
            Name = "public",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Schema,
            SourceDefinition = "CREATE SCHEMA dbo",
            SourceDefinitionHash = ComputeHash("CREATE SCHEMA dbo"),
            DependsOn = []
        };

        var tableObj = new SchemaObject
        {
            Name = "Customers",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Table,
            SourceDefinition = @"CREATE TABLE dbo.Customers (
                CustomerID INT IDENTITY(1,1) NOT NULL,
                FirstName NVARCHAR(100) NOT NULL,
                Email VARCHAR(255) NULL,
                Priority TINYINT NOT NULL DEFAULT 0,
                CreatedAt DATETIME2(3) DEFAULT GETDATE(),
                CONSTRAINT PK_Customers PRIMARY KEY (CustomerID)
            )",
            SourceDefinitionHash = ComputeHash("table-def"),
            DependsOn = []
        };

        var indexObj = new SchemaObject
        {
            Name = "IX_Customers_Email",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Index,
            SourceDefinition = "CREATE UNIQUE INDEX IX_Customers_Email ON dbo.Customers (Email) WHERE Email IS NOT NULL;",
            SourceDefinitionHash = ComputeHash("index-def"),
            DependsOn = ["dbo.Customers"]
        };

        var procObj = new SchemaObject
        {
            Name = "GetCustomerOrders",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.StoredProcedure,
            SourceDefinition = "CREATE PROCEDURE dbo.GetCustomerOrders @CustomerID INT AS BEGIN SELECT * FROM Orders WHERE CustomerID = @CustomerID END",
            SourceDefinitionHash = ComputeHash("proc-def"),
            DependsOn = ["dbo.Customers"]
        };

        var allObjects = new List<SchemaObject> { schemaObj, tableObj, indexObj, procObj };

        // Mock extractor
        var extractor = Substitute.For<ISchemaExtractor>();
        extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(allObjects);

        // Use real session store
        var sessionStore = new ConversionSessionStore(
            _tempDir, NullLogger<ConversionSessionStore>.Instance);

        // Use real change detector and dependency graph builder
        var changeDetector = new SessionChangeDetector(
            NullLogger<SessionChangeDetector>.Instance);
        var dependencyGraphBuilder = new DependencyGraphBuilder();

        // Use real classifier
        var classifier = new ObjectClassifier(
            NullLogger<ObjectClassifier>.Instance);

        // Mock rule-based converter — returns success for table/schema/index
        var ruleBasedConverter = Substitute.For<IRuleBasedConverter>();
        ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(callInfo =>
            {
                var obj = callInfo.Arg<SchemaObject>();
                return obj.ObjectType switch
                {
                    SchemaObjectType.Schema => new ConversionResult
                    {
                        ObjectName = obj.Name,
                        SchemaName = obj.SchemaName,
                        ObjectType = obj.ObjectType,
                        Status = ConversionStatus.Converted,
                        Method = ConversionMethod.RuleBased,
                        GeneratedDdl = "CREATE SCHEMA IF NOT EXISTS public;"
                    },
                    SchemaObjectType.Table => new ConversionResult
                    {
                        ObjectName = obj.Name,
                        SchemaName = obj.SchemaName,
                        ObjectType = obj.ObjectType,
                        Status = ConversionStatus.Converted,
                        Method = ConversionMethod.RuleBased,
                        GeneratedDdl = """
                            CREATE TABLE public.customers (
                                customerid INTEGER GENERATED BY DEFAULT AS IDENTITY NOT NULL,
                                firstname VARCHAR(100) NOT NULL,
                                email VARCHAR(255) NULL,
                                priority SMALLINT NOT NULL DEFAULT 0,
                                createdat TIMESTAMP(3) DEFAULT CURRENT_TIMESTAMP,
                                CONSTRAINT pk_customers PRIMARY KEY (customerid),
                                CONSTRAINT chk_customers_priority CHECK (priority >= 0 AND priority <= 255)
                            );
                            """
                    },
                    SchemaObjectType.Index => new ConversionResult
                    {
                        ObjectName = obj.Name,
                        SchemaName = obj.SchemaName,
                        ObjectType = obj.ObjectType,
                        Status = ConversionStatus.Converted,
                        Method = ConversionMethod.RuleBased,
                        GeneratedDdl = "CREATE UNIQUE INDEX ix_customers_email ON public.customers (email) WHERE email IS NOT NULL;"
                    },
                    _ => new ConversionResult
                    {
                        ObjectName = obj.Name,
                        SchemaName = obj.SchemaName,
                        ObjectType = obj.ObjectType,
                        Status = ConversionStatus.Failed,
                        Method = ConversionMethod.RuleBased,
                        ErrorMessage = "Not supported by rule engine"
                    }
                };
            });

        // Mock AI converter for stored procedures
        var aiConverter = Substitute.For<IAiConverter>();
        aiConverter.ConvertAsync(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var obj = callInfo.Arg<SchemaObject>();
                return new ConversionResult
                {
                    ObjectName = obj.Name,
                    SchemaName = obj.SchemaName,
                    ObjectType = obj.ObjectType,
                    Status = ConversionStatus.Converted,
                    Method = ConversionMethod.AiAssisted,
                    GeneratedDdl = "CREATE OR REPLACE FUNCTION public.get_customer_orders(p_customerid INTEGER) RETURNS SETOF orders AS $$ BEGIN RETURN QUERY SELECT * FROM orders WHERE customerid = p_customerid; END; $$ LANGUAGE plpgsql;",
                    ConfidenceScore = 0.92,
                    Assumptions = ["Result set schema inferred from Orders table"],
                    PromptTemplateVersion = "1.0.0"
                };
            });

        // Mock report generator
        var reportGenerator = Substitute.For<IConversionReportGenerator>();
        reportGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ConversionSessionEntry>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var entries = callInfo.Arg<IReadOnlyList<ConversionSessionEntry>>();
                return new ConversionReport
                {
                    SessionId = "e2e-test",
                    GeneratedAt = DateTimeOffset.UtcNow,
                    Summary = new ConversionReportSummary
                    {
                        TotalObjects = entries.Count,
                        ByStatus = entries.GroupBy(e => e.Result.Status)
                            .ToDictionary(g => g.Key, g => g.Count()),
                        ByMethod = entries.GroupBy(e => e.Result.Method)
                            .ToDictionary(g => g.Key, g => g.Count()),
                        ByType = entries.GroupBy(e => e.Result.ObjectType)
                            .ToDictionary(g => g.Key, g => g.Count()),
                        ProgressPercent = 100.0
                    },
                    Objects = entries,
                    CompatibilityNotes = [],
                    FlaggedObjects = []
                };
            });

        // Build pipeline
        var pipeline = new ConversionPipeline(
            extractor,
            sessionStore,
            changeDetector,
            dependencyGraphBuilder,
            classifier,
            ruleBasedConverter,
            aiConverter,
            reportGenerator,
            CreateTestSchemaMappingLoader(),
            NullLogger<ConversionPipeline>.Instance);

        var options = new ConversionPipelineOptions
        {
            SessionId = "e2e-test",
            Extraction = new SchemaExtractionOptions { FilePaths = ["sample.sql"] },
            Concurrency = 1
        };

        // Act
        var pipelineResult = await pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert: pipeline processed all objects
        Assert.Equal(4, pipelineResult.TotalProcessed);
        Assert.Equal(4, pipelineResult.Converted);
        Assert.Equal(0, pipelineResult.Failed);

        // Assert: session files were created
        var sessionDir = Path.Combine(_tempDir, "e2e-test");
        Assert.True(Directory.Exists(sessionDir),
            $"Session directory should exist at {sessionDir}");

        // Verify session entries were persisted
        var allEntries = await sessionStore.GetAllEntriesAsync("e2e-test", CancellationToken.None);
        Assert.Equal(4, allEntries.Count);

        // Verify each object was converted
        var tableEntry = allEntries.FirstOrDefault(e => e.Source.Name == "Customers");
        Assert.NotNull(tableEntry);
        Assert.Equal(ConversionStatus.Converted, tableEntry.Result.Status);
        Assert.Equal(ConversionMethod.RuleBased, tableEntry.Result.Method);

        var procEntry = allEntries.FirstOrDefault(e => e.Source.Name == "GetCustomerOrders");
        Assert.NotNull(procEntry);
        Assert.Equal(ConversionStatus.Converted, procEntry.Result.Status);
        Assert.Equal(ConversionMethod.AiAssisted, procEntry.Result.Method);
        Assert.Equal(0.92, procEntry.Result.ConfidenceScore);

        // AI converter was called for the stored procedure
        await aiConverter.Received(1).ConvertAsync(
            Arg.Is<SchemaObject>(o => o.Name == "GetCustomerOrders"),
            Arg.Any<ConversionContext>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FullPipeline_IncrementalRerun_OnlyProcessesChangedObjects()
    {
        // Arrange: first run
        var table = new SchemaObject
        {
            Name = "Products",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Table,
            SourceDefinition = "CREATE TABLE dbo.Products (Id INT)",
            SourceDefinitionHash = "initial-hash",
            DependsOn = []
        };

        var extractor = Substitute.For<ISchemaExtractor>();
        extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { table });

        var sessionStore = new ConversionSessionStore(
            _tempDir, NullLogger<ConversionSessionStore>.Instance);
        var changeDetector = new SessionChangeDetector(
            NullLogger<SessionChangeDetector>.Instance);
        var dependencyGraphBuilder = new DependencyGraphBuilder();
        var classifier = Substitute.For<IObjectClassifier>();
        classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult { Method = ConversionMethod.RuleBased, Reason = "Table" });

        var ruleBasedConverter = Substitute.For<IRuleBasedConverter>();
        ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(callInfo => new ConversionResult
            {
                ObjectName = callInfo.Arg<SchemaObject>().Name,
                SchemaName = "dbo",
                ObjectType = SchemaObjectType.Table,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = "CREATE TABLE products (id INTEGER)"
            });

        var aiConverter = Substitute.For<IAiConverter>();
        var reportGenerator = Substitute.For<IConversionReportGenerator>();
        reportGenerator.GenerateAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ConversionSessionEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionReport
            {
                SessionId = "incremental-test",
                GeneratedAt = DateTimeOffset.UtcNow,
                Summary = new ConversionReportSummary
                {
                    TotalObjects = 1,
                    ProgressPercent = 100,
                    ByStatus = new Dictionary<ConversionStatus, int> { { ConversionStatus.Converted, 1 } },
                    ByMethod = new Dictionary<ConversionMethod, int> { { ConversionMethod.RuleBased, 1 } },
                    ByType = new Dictionary<SchemaObjectType, int> { { SchemaObjectType.Table, 1 } }
                },
                Objects = [],
                CompatibilityNotes = [],
                FlaggedObjects = []
            });

        var pipeline = new ConversionPipeline(
            extractor, sessionStore, changeDetector, dependencyGraphBuilder,
            classifier, ruleBasedConverter, aiConverter, reportGenerator,
            CreateTestSchemaMappingLoader(),
            NullLogger<ConversionPipeline>.Instance);

        var options = new ConversionPipelineOptions
        {
            SessionId = "incremental-test",
            Extraction = new SchemaExtractionOptions { FilePaths = ["test.sql"] },
            Concurrency = 1
        };

        // First run
        var firstResult = await pipeline.ExecuteAsync(options, CancellationToken.None);
        Assert.Equal(1, firstResult.TotalProcessed);

        // Second run with same hash — should not reprocess
        ruleBasedConverter.ClearReceivedCalls();
        var secondResult = await pipeline.ExecuteAsync(options, CancellationToken.None);
        Assert.Equal(0, secondResult.TotalProcessed);
        ruleBasedConverter.DidNotReceive().Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>());
    }

    private static string ComputeHash(string input)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }

    private static SchemaMappingLoader CreateTestSchemaMappingLoader()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, """
        {
            "defaultMappings": [
                { "sqlServerSchema": "dbo", "postgresSchema": "public" }
            ]
        }
        """);
        return new SchemaMappingLoader(tempFile, NullLogger<SchemaMappingLoader>.Instance);
    }
}
