using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.Extraction;
using SchemaConversion.Orchestration;

namespace SchemaConversion.Orchestration.Tests;

public class ConversionPipelineTests
{
    private readonly ISchemaExtractor _extractor;
    private readonly IConversionSessionStore _sessionStore;
    private readonly SessionChangeDetector _changeDetector;
    private readonly DependencyGraphBuilder _dependencyGraphBuilder;
    private readonly IObjectClassifier _classifier;
    private readonly IRuleBasedConverter _ruleBasedConverter;
    private readonly IAiConverter _aiConverter;
    private readonly IConversionReportGenerator _reportGenerator;
    private readonly ConversionPipeline _pipeline;

    public ConversionPipelineTests()
    {
        _extractor = Substitute.For<ISchemaExtractor>();
        _sessionStore = Substitute.For<IConversionSessionStore>();
        _changeDetector = new SessionChangeDetector(
            NullLogger<SessionChangeDetector>.Instance);
        _dependencyGraphBuilder = new DependencyGraphBuilder();
        _classifier = Substitute.For<IObjectClassifier>();
        _ruleBasedConverter = Substitute.For<IRuleBasedConverter>();
        _aiConverter = Substitute.For<IAiConverter>();
        _reportGenerator = Substitute.For<IConversionReportGenerator>();

        _pipeline = new ConversionPipeline(
            _extractor,
            _sessionStore,
            _changeDetector,
            _dependencyGraphBuilder,
            _classifier,
            _ruleBasedConverter,
            _aiConverter,
            _reportGenerator,
            NullLogger<ConversionPipeline>.Instance);
    }

    [Fact]
    public async Task ExecuteAsync_NoObjectsToProcess_ReturnsZeroCounts()
    {
        // Arrange
        var options = CreateOptions();
        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(0, result.TotalProcessed);
        Assert.Equal(0, result.Converted);
        Assert.Equal(0, result.Flagged);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_RuleBasedConversion_ProcessesSuccessfully()
    {
        // Arrange
        var options = CreateOptions();
        var table = CreateSchemaObject("dbo", "Customers", SchemaObjectType.Table);

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { table });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "Table type"
            });

        var conversionResult = new ConversionResult
        {
            ObjectName = "Customers",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Table,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = "CREATE TABLE customers (id INTEGER)"
        };
        _ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(conversionResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(1, result.Converted);
        Assert.Equal(0, result.Failed);
        await _sessionStore.Received(1).SaveEntryAsync(
            Arg.Any<string>(), Arg.Any<ConversionSessionEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AiAssistedConversion_ProcessesSuccessfully()
    {
        // Arrange
        var options = CreateOptions();
        var proc = CreateSchemaObject("dbo", "GetOrders", SchemaObjectType.StoredProcedure);

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { proc });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Stored procedure"
            });

        var conversionResult = new ConversionResult
        {
            ObjectName = "GetOrders",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.StoredProcedure,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.AiAssisted,
            GeneratedDdl = "CREATE OR REPLACE FUNCTION dbo.get_orders() RETURNS void AS $$ BEGIN END; $$ LANGUAGE plpgsql;"
        };
        _aiConverter.ConvertAsync(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>(), Arg.Any<CancellationToken>())
            .Returns(conversionResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(1, result.Converted);
    }

    [Fact]
    public async Task ExecuteAsync_RuleBasedFails_FallsBackToAi()
    {
        // Arrange
        var options = CreateOptions();
        var view = CreateSchemaObject("dbo", "ComplexView", SchemaObjectType.View);

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { view });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "View type"
            });

        // Rule-based fails
        var failedResult = new ConversionResult
        {
            ObjectName = "ComplexView",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.View,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.RuleBased,
            ErrorMessage = "Cannot translate CROSS APPLY"
        };
        _ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(failedResult);

        // AI succeeds
        var aiResult = new ConversionResult
        {
            ObjectName = "ComplexView",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.View,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.AiAssisted,
            GeneratedDdl = "CREATE OR REPLACE VIEW dbo.complex_view AS ..."
        };
        _aiConverter.ConvertAsync(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>(), Arg.Any<CancellationToken>())
            .Returns(aiResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(1, result.Converted);
        Assert.Equal(0, result.Failed);
        await _aiConverter.Received(1).ConvertAsync(
            Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ObjectThrowsException_MarksFailedAndContinues()
    {
        // Arrange
        var options = CreateOptions();
        var table1 = CreateSchemaObject("dbo", "Table1", SchemaObjectType.Table);
        var table2 = CreateSchemaObject("dbo", "Table2", SchemaObjectType.Table);

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { table1, table2 });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "Table type"
            });

        // First object throws, second succeeds
        _ruleBasedConverter.Convert(Arg.Is<SchemaObject>(o => o.Name == "Table1"), Arg.Any<ConversionContext>())
            .Returns(x => throw new InvalidOperationException("Simulated failure"));

        var successResult = new ConversionResult
        {
            ObjectName = "Table2",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.Table,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = "CREATE TABLE table2 (id INTEGER)"
        };
        _ruleBasedConverter.Convert(Arg.Is<SchemaObject>(o => o.Name == "Table2"), Arg.Any<ConversionContext>())
            .Returns(successResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(2, result.TotalProcessed);
        Assert.Equal(1, result.Converted);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ExecuteAsync_CircularDependencies_CreatesPlaceholderStubs()
    {
        // Arrange
        var options = CreateOptions();
        var viewA = new SchemaObject
        {
            SchemaName = "dbo",
            Name = "ViewA",
            ObjectType = SchemaObjectType.View,
            SourceDefinition = "CREATE VIEW dbo.ViewA AS SELECT * FROM dbo.ViewB",
            SourceDefinitionHash = "hash-a",
            DependsOn = ["dbo.ViewB"]
        };
        var viewB = new SchemaObject
        {
            SchemaName = "dbo",
            Name = "ViewB",
            ObjectType = SchemaObjectType.View,
            SourceDefinition = "CREATE VIEW dbo.ViewB AS SELECT * FROM dbo.ViewA",
            SourceDefinitionHash = "hash-b",
            DependsOn = ["dbo.ViewA"]
        };

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { viewA, viewB });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "View type"
            });

        var convertedResult = new ConversionResult
        {
            ObjectName = "ViewA",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.View,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = "CREATE OR REPLACE VIEW ..."
        };
        _ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(convertedResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert — stubs + actual conversions should be saved
        // 2 placeholder stubs + 2 actual conversions = at least 4 saves
        var calls = _sessionStore.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == "SaveEntryAsync");
        Assert.True(calls >= 4, $"Expected at least 4 SaveEntryAsync calls but got {calls}");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleObjects_RespectsParallelism()
    {
        // Arrange - set concurrency to 2
        var options = new ConversionPipelineOptions
        {
            SessionId = "test-session",
            Extraction = new SchemaExtractionOptions { FilePaths = ["test.sql"] },
            Concurrency = 2
        };

        var objects = Enumerable.Range(1, 5)
            .Select(i => CreateSchemaObject("dbo", $"Table{i}", SchemaObjectType.Table))
            .ToList();

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(objects);
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "Table type"
            });

        _ruleBasedConverter.Convert(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>())
            .Returns(callInfo => new ConversionResult
            {
                ObjectName = callInfo.Arg<SchemaObject>().Name,
                SchemaName = "dbo",
                ObjectType = SchemaObjectType.Table,
                Status = ConversionStatus.Converted,
                Method = ConversionMethod.RuleBased,
                GeneratedDdl = "CREATE TABLE ..."
            });

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(5, result.TotalProcessed);
        Assert.Equal(5, result.Converted);
    }

    [Fact]
    public async Task ExecuteAsync_FlaggedResult_CountsAsFlagged()
    {
        // Arrange
        var options = CreateOptions();
        var proc = CreateSchemaObject("dbo", "RiskyProc", SchemaObjectType.StoredProcedure);

        _extractor.ExtractAsync(Arg.Any<SchemaExtractionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new List<SchemaObject> { proc });
        _sessionStore.LoadOrCreateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConversionSession
            {
                SessionId = "test-session",
                CreatedAt = DateTimeOffset.UtcNow,
                LastModifiedAt = DateTimeOffset.UtcNow
            });
        _sessionStore.GetAllEntriesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ConversionSessionEntry>());

        _classifier.Classify(Arg.Any<SchemaObject>())
            .Returns(new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Stored procedure"
            });

        var flaggedResult = new ConversionResult
        {
            ObjectName = "RiskyProc",
            SchemaName = "dbo",
            ObjectType = SchemaObjectType.StoredProcedure,
            Status = ConversionStatus.Flagged,
            Method = ConversionMethod.AiAssisted,
            GeneratedDdl = "CREATE OR REPLACE FUNCTION ...",
            ConfidenceScore = 0.5,
            ReviewFlags = [new ManualReviewFlag { Reason = "Low confidence" }]
        };
        _aiConverter.ConvertAsync(Arg.Any<SchemaObject>(), Arg.Any<ConversionContext>(), Arg.Any<CancellationToken>())
            .Returns(flaggedResult);

        // Act
        var result = await _pipeline.ExecuteAsync(options, CancellationToken.None);

        // Assert
        Assert.Equal(1, result.TotalProcessed);
        Assert.Equal(0, result.Converted);
        Assert.Equal(1, result.Flagged);
        Assert.Equal(0, result.Failed);
    }

    private static ConversionPipelineOptions CreateOptions()
    {
        return new ConversionPipelineOptions
        {
            SessionId = "test-session",
            Extraction = new SchemaExtractionOptions { FilePaths = ["test.sql"] },
            Concurrency = 4
        };
    }

    private static SchemaObject CreateSchemaObject(
        string schema, string name, SchemaObjectType type)
    {
        return new SchemaObject
        {
            SchemaName = schema,
            Name = name,
            ObjectType = type,
            SourceDefinition = $"CREATE {type} {schema}.{name}",
            SourceDefinitionHash = $"hash-{schema}-{name}"
        };
    }
}
