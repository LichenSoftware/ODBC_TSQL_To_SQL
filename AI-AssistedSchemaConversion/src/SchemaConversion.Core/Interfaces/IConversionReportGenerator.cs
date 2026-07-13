using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Interfaces;

public interface IConversionReportGenerator
{
    Task<ConversionReport> GenerateAsync(
        string sessionId, IReadOnlyList<ConversionSessionEntry> entries, CancellationToken ct);
}
