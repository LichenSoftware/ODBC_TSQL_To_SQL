using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Extensibility seam: represents a pluggable backend database engine.
/// Implement this interface to support backends beyond PostgreSQL.
/// </summary>
public interface IBackendProvider
{
    /// <summary>Unique identifier for this provider, e.g. "postgresql", "mysql".</summary>
    string ProviderId { get; }

    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }

    /// <summary>Returns the execution engine for this provider.</summary>
    IExecutionEngine CreateExecutionEngine(BackendConnectionOptions options);

    /// <summary>Returns the SQL translator for this provider.</summary>
    ISqlTranslator CreateSqlTranslator();
}
