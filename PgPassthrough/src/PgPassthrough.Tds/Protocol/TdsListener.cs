using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Accepts inbound TCP connections and spawns a <see cref="TdsSession"/> per client.
/// 
/// Lifecycle:
///   - StartAsync: opens the TCP listener and begins the accept loop
///   - StopAsync: signals shutdown; waits for all active sessions to close
/// 
/// Session management:
///   - Sessions are tracked in a concurrent set.
///   - MaxConcurrentSessions is enforced; excess connections receive an error and are closed.
///   - Each session runs on the thread pool via Task.Run / awaiting its ProcessAsync.
/// </summary>
public sealed class TdsListener : IAsyncDisposable
{
    private readonly IQueryHandler _queryHandler;
    private readonly ICredentialValidator _credentialValidator;
    private readonly ServerConfiguration _config;
    private readonly ILogger<TdsListener> _logger;
    private readonly ILoggerFactory _loggerFactory;

    private TcpListener? _tcpListener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<Guid, Task> _activeSessions = new();

    public TdsListener(
        IQueryHandler queryHandler,
        ICredentialValidator credentialValidator,
        IOptions<ServerConfiguration> config,
        ILogger<TdsListener> logger,
        ILoggerFactory loggerFactory)
    {
        _queryHandler        = queryHandler;
        _credentialValidator = credentialValidator;
        _config              = config.Value;
        _logger              = logger;
        _loggerFactory       = loggerFactory;
    }

    /// <summary>
    /// Starts the TCP listener. Returns immediately; the accept loop runs in the background.
    /// </summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        var endpoint = new IPEndPoint(
            IPAddress.Parse(_config.BindAddress),
            _config.Port);

        _tcpListener = new TcpListener(endpoint);
        _tcpListener.Start();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _logger.LogInformation("TDS listener started on {Endpoint}", endpoint);

        // Fire-and-forget the accept loop; caller waits via StopAsync
        _ = RunAcceptLoopAsync(_cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Signals shutdown and waits for all active sessions to finish.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("TDS listener stopping...");

        _cts?.Cancel();
        _tcpListener?.Stop();

        // Wait for all active sessions (with a timeout)
        var allSessions = _activeSessions.Values.ToArray();
        if (allSessions.Length > 0)
        {
            _logger.LogInformation("Waiting for {Count} active session(s) to close...", allSessions.Length);
            await Task.WhenAll(allSessions).WaitAsync(TimeSpan.FromSeconds(10), ct)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("TDS listener stopped.");
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _tcpListener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException ex) when (ct.IsCancellationRequested)
            {
                _ = ex; // expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting TCP connection");
                continue;
            }

            // Enforce session limit
            if (_activeSessions.Count >= _config.MaxConcurrentSessions)
            {
                _logger.LogWarning("Session limit ({Limit}) reached. Rejecting connection from {Ep}",
                    _config.MaxConcurrentSessions, client.Client.RemoteEndPoint);
                await RejectConnectionAsync(client, ct).ConfigureAwait(false);
                continue;
            }

            var sessionId = Guid.NewGuid();
            var sessionTask = RunSessionAsync(client, sessionId, ct);
            _activeSessions[sessionId] = sessionTask;

            // Clean up on completion — do not await here
            _ = sessionTask.ContinueWith(
                t => { _activeSessions.TryRemove(sessionId, out _); },
                TaskScheduler.Default);
        }
    }

    private async Task RunSessionAsync(TcpClient client, Guid sessionId, CancellationToken ct)
    {
        var sessionLogger = _loggerFactory.CreateLogger(typeof(TdsSession).FullName!);

        await using var session = new TdsSession(
            client,
            _queryHandler,
            _credentialValidator,
            sessionLogger,
            ct);

        _logger.LogDebug("Session {Id} starting from {Ep}", sessionId, client.Client.RemoteEndPoint?.ToString() ?? "unknown");

        try
        {
            await session.ProcessAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unhandled error in session {Id}", sessionId);
        }
        finally
        {
            _logger.LogDebug("Session {Id} ended.", sessionId);
        }
    }

    private static async Task RejectConnectionAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var writer = new TdsPacketWriter(client.GetStream());
            var responseWriter = new TdsResponseWriter(writer);

            await responseWriter.WriteLoginErrorAsync(new ServerError
            {
                Message  = "Server is at maximum capacity. Try again later.",
                Number   = 17830,
                Severity = 20,
                State    = 2
            }, ct).ConfigureAwait(false);
        }
        catch { /* cannot recover */ }
        finally
        {
            client.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _tcpListener?.Stop();

        var allSessions = _activeSessions.Values.ToArray();
        if (allSessions.Length > 0)
        {
            try { await Task.WhenAll(allSessions).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch { /* shutdown timeout */ }
        }
    }
}
