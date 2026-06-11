using Microsoft.Extensions.Options;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Validates credentials against the static list defined in <see cref="ServerConfiguration"/>.
/// 
/// This is the Phase 2 implementation. Production deployments should replace this
/// with a backend-delegated validator (e.g., attempt a PostgreSQL connection with
/// the client's credentials) so PgPassthrough does not hold separate credential state.
/// 
/// Tech debt: passwords are stored in plain text in appsettings. Use environment
/// variables or a secrets manager in production.
/// </summary>
public sealed class ConfiguredCredentialValidator : ICredentialValidator
{
    private readonly Dictionary<string, string> _credentials;

    public ConfiguredCredentialValidator(IOptions<TdsServerOptions> options)
    {
        _credentials = options.Value.AllowedLogins
            .ToDictionary(
                l => l.Username.ToLowerInvariant(),
                l => l.Password,
                StringComparer.OrdinalIgnoreCase);
    }

    public Task<bool> ValidateAsync(string username, string password, CancellationToken ct = default)
    {
        bool valid = _credentials.TryGetValue(username, out var expected)
                     && expected == password;
        return Task.FromResult(valid);
    }
}
