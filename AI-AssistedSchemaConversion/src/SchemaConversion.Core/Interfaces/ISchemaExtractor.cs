using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Core.Interfaces;

public interface ISchemaExtractor
{
    Task<IReadOnlyList<SchemaObject>> ExtractAsync(
        SchemaExtractionOptions options, CancellationToken ct);
}
