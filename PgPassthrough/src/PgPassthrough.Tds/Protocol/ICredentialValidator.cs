namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Validates client login credentials during the Login7 handshake.
/// 
/// The default implementation checks against a configured set of
/// username/password pairs. Future implementations can delegate to
/// LDAP, Active Directory, or the backend database.
/// </summary>
public interface ICredentialValidator
{
    /// <summary>
    /// Returns true if the provided credentials are accepted.
    /// </summary>
    Task<bool> ValidateAsync(string username, string password, CancellationToken ct = default);
}
