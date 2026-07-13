using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Interfaces;

public interface IConversionSessionStore
{
    Task<ConversionSession> LoadOrCreateAsync(string sessionId, CancellationToken ct);
    Task SaveEntryAsync(string sessionId, ConversionSessionEntry entry, CancellationToken ct);
    Task<ConversionSessionEntry?> GetEntryAsync(
        string sessionId, string schemaName, string objectName, CancellationToken ct);
    Task<IReadOnlyList<ConversionSessionEntry>> GetAllEntriesAsync(
        string sessionId, CancellationToken ct);
}
