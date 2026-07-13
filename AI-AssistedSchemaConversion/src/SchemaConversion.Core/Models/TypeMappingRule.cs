namespace SchemaConversion.Core.Models;

public sealed record TypeMappingRule
{
    public required string SqlServerType { get; init; }
    public required string PostgresType { get; init; }
    public bool PreservePrecision { get; init; }
    public int? MaxPrecision { get; init; }
    public string? AdditionalConstraint { get; init; }
}
