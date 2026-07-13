namespace SchemaConversion.Core.Models;

public sealed record AuditLogEntry
{
    public required string SessionId { get; init; }
    public required string ObjectName { get; init; }
    public required SchemaObjectType ObjectType { get; init; }
    public required string PromptTemplateVersion { get; init; }
    public required string FullPrompt { get; init; }
    public required string ModelId { get; init; }
    public required string FullResponse { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public int RetryAttempt { get; init; }
    public bool IsError { get; init; }
}
