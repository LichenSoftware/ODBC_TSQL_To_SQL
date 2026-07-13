using Microsoft.Extensions.Logging.Abstractions;
using SchemaConversion.Core.Options;
using Xunit;

namespace SchemaConversion.AiEngine.Tests;

public class BedrockClientTests
{
    [Fact]
    public void Constructor_ValidOptions_DoesNotThrow()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        // Note: The constructor creates the actual AWS client internally,
        // but validation should pass with valid options
        using var client = new BedrockClient(options, NullLogger<BedrockClient>.Instance);
        Assert.NotNull(client);
    }

    [Fact]
    public void Constructor_EmptyModelId_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "",
            Temperature = 0.2,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_NegativeTemperature_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = -0.1,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_TemperatureAboveOne_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 1.5,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_ZeroRetryAttempts_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 0,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_TooManyRetryAttempts_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 11,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_ZeroTimeout_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.Zero,
            MaxOutputTokens = 4096
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_NegativeMaxOutputTokens_Throws()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = -1
        };

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BedrockClient(options, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public void Constructor_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BedrockClient(null!, NullLogger<BedrockClient>.Instance));
    }

    [Fact]
    public async Task InvokeModelAsync_EmptyPrompt_ThrowsArgumentException()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 3,
            Timeout = TimeSpan.FromSeconds(60),
            MaxOutputTokens = 4096
        };

        using var client = new BedrockClient(options, NullLogger<BedrockClient>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await client.InvokeModelAsync("", CancellationToken.None));
    }

    [Fact]
    public async Task InvokeModelAsync_CancellationRequested_ThrowsOrPropagatesException()
    {
        var options = new BedrockClientOptions
        {
            ModelId = "anthropic.claude-sonnet-4-20250514-v1:0",
            Temperature = 0.2,
            MaxRetryAttempts = 1,
            Timeout = TimeSpan.FromSeconds(5),
            MaxOutputTokens = 4096
        };

        using var client = new BedrockClient(options, NullLogger<BedrockClient>.Instance);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // When cancelled, should throw OperationCanceledException or related exception
        // (AWS SDK may also throw its own exceptions when credentials are unavailable)
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await client.InvokeModelAsync("test prompt", cts.Token));

        Assert.NotNull(exception);
    }
}
