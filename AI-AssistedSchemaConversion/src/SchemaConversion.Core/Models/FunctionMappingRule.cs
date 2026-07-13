namespace SchemaConversion.Core.Models;

public sealed record FunctionMappingRule
{
    public required string SqlServerFunction { get; init; }
    public required string PostgresExpression { get; init; }
    public int ArgCount { get; init; }
}
