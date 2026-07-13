using SchemaConversion.Core.Models;

namespace SchemaConversion.Core.Interfaces;

public interface IAuditLogWriter
{
    Task WriteAsync(AuditLogEntry entry, CancellationToken ct);
}
