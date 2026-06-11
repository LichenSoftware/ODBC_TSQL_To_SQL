namespace PgPassthrough.Core.Models;

/// <summary>
/// Context provided to the translator alongside the SQL text.
/// Allows session-specific translation decisions (e.g., database name substitution).
/// </summary>
public sealed class TranslationContext
{
    public string DatabaseName { get; init; } = "public";
    public bool AnsiNulls { get; init; } = true;
    public bool QuotedIdentifier { get; init; } = true;

    /// <summary>
    /// Named parameter definitions passed with the query (e.g. from sp_executesql).
    /// The translator uses these to ensure parameter token substitution is correct.
    /// </summary>
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];
}

/// <summary>
/// Output of the translation pipeline.
/// </summary>
public sealed class TranslationResult
{
    /// <summary>The translated SQL ready for the backend.</summary>
    public required string TranslatedSql { get; init; }

    /// <summary>Whether this result came from the translation cache.</summary>
    public bool FromCache { get; init; }

    /// <summary>Any warnings generated during translation (non-fatal).</summary>
    public IReadOnlyList<TranslationWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Indicates the statement type to allow the caller to decide
    /// whether to use ExecuteQuery vs ExecuteNonQuery.
    /// </summary>
    public StatementType StatementType { get; init; } = StatementType.Unknown;
}

/// <summary>
/// A non-fatal translation warning that should be forwarded to the client
/// as an informational message.
/// </summary>
public sealed class TranslationWarning
{
    public required string Code { get; init; }
    public required string Message { get; init; }
}

public enum StatementType
{
    Unknown,
    Select,
    Insert,
    Update,
    Delete,
    Ddl,
    Transaction,
    SetOption,
    Use,
    StoredProcedure,
    Batch   // Multiple statements
}
