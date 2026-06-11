using Microsoft.Extensions.Logging;
using PgPassthrough.Core.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PgPassthrough.Core.Diagnostics;

/// <summary>
/// Centralises logging and metrics for the middleware.
/// Uses Microsoft.Extensions.Logging for structured logging and
/// System.Diagnostics.Metrics for OpenTelemetry-compatible instrumentation.
/// </summary>
public sealed class DiagnosticsService
{
    private static readonly Meter _meter = new("PgPassthrough", "1.0.0");

    // Metrics instruments
    private static readonly Counter<long> _requestsTotal =
        _meter.CreateCounter<long>("pgpassthrough.requests.total", "requests", "Total client requests received");

    private static readonly Counter<long> _errorsTotal =
        _meter.CreateCounter<long>("pgpassthrough.errors.total", "errors", "Total errors returned to clients");

    private static readonly Histogram<double> _translationDuration =
        _meter.CreateHistogram<double>("pgpassthrough.translation.duration_ms", "ms", "SQL translation duration");

    private static readonly Histogram<double> _executionDuration =
        _meter.CreateHistogram<double>("pgpassthrough.execution.duration_ms", "ms", "Backend execution duration");

    private static readonly Counter<long> _cacheHits =
        _meter.CreateCounter<long>("pgpassthrough.cache.hits", "hits", "Translation cache hits");

    private static readonly Counter<long> _cacheMisses =
        _meter.CreateCounter<long>("pgpassthrough.cache.misses", "misses", "Translation cache misses");

    private static readonly UpDownCounter<int> _activeSessions =
        _meter.CreateUpDownCounter<int>("pgpassthrough.sessions.active", "sessions", "Currently active client sessions");

    private readonly ILogger<DiagnosticsService> _logger;
    private readonly bool _queryLoggingEnabled;

    public DiagnosticsService(ILogger<DiagnosticsService> logger, ServerConfiguration config)
    {
        _logger = logger;
        _queryLoggingEnabled = config.EnableQueryLogging;
    }

    public void SessionConnected(SessionContext session)
    {
        _activeSessions.Add(1);
        _logger.LogInformation("Session {SessionId} connected: client={Client} app={App} db={Db}",
            session.SessionId, session.ClientHostName, session.ApplicationName, session.DatabaseName);
    }

    public void SessionDisconnected(SessionContext session)
    {
        _activeSessions.Add(-1);
        _logger.LogInformation("Session {SessionId} disconnected", session.SessionId);
    }

    public void RequestReceived(ClientRequest request)
    {
        _requestsTotal.Add(1);

        if (_queryLoggingEnabled && request is SqlBatchRequest batch)
        {
            _logger.LogDebug("Request {RequestId} SQL: {Sql}", request.RequestId, batch.SqlText);
        }
    }

    public void TranslationCompleted(Guid requestId, TranslationResult result, TimeSpan elapsed)
    {
        _translationDuration.Record(elapsed.TotalMilliseconds);

        if (result.FromCache)
            _cacheHits.Add(1);
        else
            _cacheMisses.Add(1);

        foreach (var warning in result.Warnings)
        {
            _logger.LogWarning("Translation warning [{Code}] for request {RequestId}: {Message}",
                warning.Code, requestId, warning.Message);
        }
    }

    public void ExecutionCompleted(Guid requestId, bool success, long rowsAffected, TimeSpan elapsed)
    {
        _executionDuration.Record(elapsed.TotalMilliseconds);

        _logger.LogDebug("Request {RequestId} executed in {ElapsedMs}ms, rows={Rows}, success={Success}",
            requestId, elapsed.TotalMilliseconds, rowsAffected, success);
    }

    public void ErrorOccurred(Guid requestId, string message, Exception? exception = null)
    {
        _errorsTotal.Add(1);
        _logger.LogError(exception, "Error for request {RequestId}: {Message}", requestId, message);
    }

    public void LogUnsupportedFeature(string feature, string context)
    {
        _logger.LogWarning("Unsupported T-SQL feature '{Feature}' encountered in: {Context}", feature, context);
    }

    /// <summary>
    /// Creates a timing scope. Dispose to record elapsed time.
    /// </summary>
    public TimingScope BeginTiming(string operationName) => new(operationName, _logger);

    public sealed class TimingScope : IDisposable
    {
        private readonly string _operationName;
        private readonly ILogger _logger;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        internal TimingScope(string operationName, ILogger logger)
        {
            _operationName = operationName;
            _logger = logger;
        }

        public TimeSpan Elapsed => _sw.Elapsed;

        public void Dispose()
        {
            _sw.Stop();
            _logger.LogTrace("Operation '{Operation}' completed in {ElapsedMs}ms",
                _operationName, _sw.Elapsed.TotalMilliseconds);
        }
    }
}
