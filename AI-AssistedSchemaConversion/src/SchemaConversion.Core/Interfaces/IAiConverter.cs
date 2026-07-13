using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Core.Interfaces;

public interface IAiConverter
{
    Task<ConversionResult> ConvertAsync(
        SchemaObject obj, ConversionContext context, CancellationToken ct);
}
