using System.Text.RegularExpressions;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Validates path segments to prevent directory traversal attacks.
/// </summary>
internal static partial class PathValidator
{
    // Allowed: alphanumeric, hyphen, underscore
    [GeneratedRegex(@"^[a-zA-Z0-9\-_]+$")]
    private static partial Regex SessionIdPattern();

    // Allowed: alphanumeric, hyphen, underscore, dot (for schema.object names used in filenames)
    [GeneratedRegex(@"^[a-zA-Z0-9\-_.]+$")]
    private static partial Regex FileNameSegmentPattern();

    /// <summary>
    /// Validates a session ID. Must be alphanumeric + hyphen + underscore only.
    /// </summary>
    public static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        if (!SessionIdPattern().IsMatch(sessionId))
        {
            throw new ArgumentException(
                "Session ID contains invalid characters. Only alphanumeric, hyphen, and underscore are allowed.",
                nameof(sessionId));
        }
    }

    /// <summary>
    /// Validates a schema name or object name for use in file paths.
    /// Must not contain path separators or traversal sequences.
    /// </summary>
    public static void ValidateNameSegment(string name, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Name contains path traversal sequence '..'.",
                paramName);
        }

        if (!FileNameSegmentPattern().IsMatch(name))
        {
            throw new ArgumentException(
                "Name contains invalid characters. Only alphanumeric, hyphen, underscore, and dot are allowed.",
                paramName);
        }
    }

    /// <summary>
    /// Validates that a resolved path is within the expected base directory.
    /// </summary>
    public static void ValidateResolvedPath(string resolvedPath, string baseDirectory)
    {
        var fullResolved = Path.GetFullPath(resolvedPath);
        var fullBase = Path.GetFullPath(baseDirectory);

        if (!fullResolved.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Resolved path escapes the base directory. Potential path traversal detected.");
        }
    }
}
