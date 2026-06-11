using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Top-level request handler for a client session. Receives a parsed client
/// request and orchestrates translation + execution + result encoding.
/// </summary>
public interface IQueryHandler
{
    Task HandleAsync(
        ClientRequest request,
        IResponseWriter responseWriter,
        CancellationToken cancellationToken = default);
}
