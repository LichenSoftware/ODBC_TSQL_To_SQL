using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Options;

public sealed record ConversionContext
{
    public required string SessionId { get; init; }
    public IReadOnlyList<TypeMappingRule> TypeMappings { get; init; } = [];
    public IReadOnlyList<FunctionMappingRule> FunctionMappings { get; init; } = [];
    public IReadOnlyDictionary<string, string> SchemaMappings { get; init; } = new Dictionary<string, string>();
    public double ConfidenceThreshold { get; init; } = 0.7;
}
