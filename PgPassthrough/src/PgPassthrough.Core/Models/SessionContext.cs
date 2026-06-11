namespace PgPassthrough.Core.Models;

/// <summary>
/// Per-client-session state. Immutable snapshot per request; mutable fields
/// updated by the session manager after SET statements or USE commands.
/// </summary>
public sealed class SessionContext
{
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>Client application name, from Login7.</summary>
    public string ApplicationName { get; init; } = "unknown";

    /// <summary>Client host name, from Login7.</summary>
    public string ClientHostName { get; init; } = "unknown";

    /// <summary>Authenticated login name.</summary>
    public string LoginName { get; init; } = string.Empty;

    /// <summary>Current database context (from USE or Login7).</summary>
    public string DatabaseName { get; set; } = "master";

    /// <summary>Current SET ANSI_NULLS setting. Default: ON (T-SQL default).</summary>
    public bool AnsiNulls { get; set; } = true;

    /// <summary>Current SET QUOTED_IDENTIFIER setting. Default: ON.</summary>
    public bool QuotedIdentifier { get; set; } = true;

    /// <summary>Whether an explicit transaction is active.</summary>
    public bool InTransaction { get; set; } = false;

    /// <summary>Active transaction handle, if any.</summary>
    public object? ActiveTransactionHandle { get; set; }

    /// <summary>The @@ROWCOUNT value from the last statement execution.</summary>
    public long LastRowCount { get; set; } = 0;

    /// <summary>The last identity value inserted, for @@IDENTITY / SCOPE_IDENTITY().</summary>
    public long? LastIdentityValue { get; set; }
}
