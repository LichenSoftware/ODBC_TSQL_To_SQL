namespace SchemaConversion.Core.Models;

public sealed record SchemaObject
{
    public required string Name { get; init; }
    public required string SchemaName { get; init; }
    public required SchemaObjectType ObjectType { get; init; }
    public required string SourceDefinition { get; init; }
    public required string SourceDefinitionHash { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = [];
}
