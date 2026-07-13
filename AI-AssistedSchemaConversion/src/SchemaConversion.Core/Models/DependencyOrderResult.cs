namespace SchemaConversion.Core.Models;

public sealed record DependencyOrderResult
{
    public required IReadOnlyList<SchemaObject> Ordered { get; init; }
    public required IReadOnlyList<IReadOnlyList<SchemaObject>> Cycles { get; init; }
}
