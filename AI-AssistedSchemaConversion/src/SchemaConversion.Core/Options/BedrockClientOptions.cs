namespace SchemaConversion.Core.Options;

public sealed class BedrockClientOptions
{
    public required string ModelId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);
    public int MaxRetryAttempts { get; init; } = 3;
    public double Temperature { get; init; } = 0.2;
    public int MaxOutputTokens { get; init; } = 8192;
}
