namespace SchemaConversion.Core.Options;

public sealed record ScriptGenerationOptions
{
    public required string OutputDirectory { get; init; }
    public ScriptOutputMode Mode { get; init; } = ScriptOutputMode.Consolidated;
    public bool IncludeComments { get; init; } = true;
}

public enum ScriptOutputMode
{
    Consolidated,
    PerSchema,
    PerType,
    PerObject
}
