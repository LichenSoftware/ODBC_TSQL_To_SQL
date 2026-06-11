namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Options specific to the TDS server, bound from the "TdsServer" config section.
/// </summary>
public sealed class TdsServerOptions
{
    public const string SectionName = "TdsServer";

    /// <summary>
    /// Allowed SQL login credentials.
    /// In production, prefer delegating to the backend PostgreSQL instance.
    /// </summary>
    public List<AllowedLogin> AllowedLogins { get; set; } = new();
}

/// <summary>A single allowed login entry.</summary>
public sealed class AllowedLogin
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
