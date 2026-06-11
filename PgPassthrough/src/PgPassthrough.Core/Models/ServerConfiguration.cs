namespace PgPassthrough.Core.Models;

/// <summary>
/// Top-level server configuration, bound from appsettings.json.
/// </summary>
public sealed class ServerConfiguration
{
    public const string SectionName = "PgPassthrough";

    /// <summary>TCP port to listen on. Default: 1433.</summary>
    public int Port { get; set; } = 1433;

    /// <summary>Bind address. Default: 0.0.0.0 (all interfaces).</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>Maximum concurrent client sessions.</summary>
    public int MaxConcurrentSessions { get; set; } = 100;

    /// <summary>PostgreSQL backend configuration.</summary>
    public BackendConnectionOptions Backend { get; set; } = new();

    /// <summary>Translation cache settings.</summary>
    public CacheConfiguration Cache { get; set; } = new();

    /// <summary>
    /// Whether to enable detailed query logging.
    /// Warning: may log sensitive data. Disable in production by default.
    /// </summary>
    public bool EnableQueryLogging { get; set; } = false;
}

/// <summary>
/// Connection options for the backend PostgreSQL instance.
/// </summary>
public sealed class BackendConnectionOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5432;
    public string Database { get; set; } = "postgres";
    public string Username { get; set; } = "postgres";

    /// <summary>
    /// Password. In production, prefer environment variable or secrets manager.
    /// Do not commit real passwords in appsettings.json.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    public int MinPoolSize { get; set; } = 2;
    public int MaxPoolSize { get; set; } = 50;
    public int ConnectionTimeoutSeconds { get; set; } = 30;
    public int CommandTimeoutSeconds { get; set; } = 30;
    public bool SslMode { get; set; } = false;
}

/// <summary>
/// Configuration for the translation result cache.
/// </summary>
public sealed class CacheConfiguration
{
    /// <summary>Maximum number of distinct SQL strings to cache translations for.</summary>
    public int MaxEntries { get; set; } = 10_000;

    /// <summary>How long a cache entry remains valid. Null means indefinite.</summary>
    public TimeSpan? EntryTtl { get; set; } = null;
}
