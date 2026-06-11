using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PgPassthrough.Tds.Protocol;

namespace PgPassthrough.Tds;

/// <summary>
/// <see cref="IHostedService"/> that owns the <see cref="TdsListener"/> lifecycle.
/// Registered in DI as a hosted service; the .NET host calls StartAsync/StopAsync.
/// </summary>
public sealed class TdsServerService : IHostedService
{
    private readonly TdsListener _listener;
    private readonly ILogger<TdsServerService> _logger;

    public TdsServerService(TdsListener listener, ILogger<TdsServerService> logger)
    {
        _listener = listener;
        _logger   = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting TDS server service...");
        await _listener.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Stopping TDS server service...");
        await _listener.StopAsync(cancellationToken).ConfigureAwait(false);
        await _listener.DisposeAsync().ConfigureAwait(false);
    }
}
