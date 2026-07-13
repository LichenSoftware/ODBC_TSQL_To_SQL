using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Core.Interfaces;

public interface IScriptGenerator
{
    Task GenerateAsync(
        IReadOnlyList<ConversionSessionEntry> entries,
        ScriptGenerationOptions options,
        CancellationToken ct);
}
