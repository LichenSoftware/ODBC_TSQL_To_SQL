namespace PgPassthrough.Tds.Protocol;

/// <summary>
/// Thrown when the incoming TDS byte stream violates the protocol specification.
/// The session should be closed on receipt of this exception.
/// </summary>
public sealed class TdsProtocolException : Exception
{
    public TdsProtocolException(string message) : base(message) { }
    public TdsProtocolException(string message, Exception inner) : base(message, inner) { }
}
