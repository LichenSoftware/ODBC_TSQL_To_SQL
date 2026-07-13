namespace SchemaConversion.RuleEngine.Models;

/// <summary>
/// Result of translating a T-SQL expression to PostgreSQL.
/// Either contains a successful translation or a reason why translation failed.
/// </summary>
public sealed record TranslationResult
{
    /// <summary>
    /// The translated PostgreSQL expression. Null if translation failed.
    /// </summary>
    public string? TranslatedExpression { get; init; }

    /// <summary>
    /// The reason translation could not be completed. Null if translation succeeded.
    /// </summary>
    public string? CannotTranslateReason { get; init; }

    /// <summary>
    /// Whether the translation was successful.
    /// </summary>
    public bool IsSuccess => TranslatedExpression is not null;

    /// <summary>
    /// Creates a successful translation result.
    /// </summary>
    public static TranslationResult Success(string translatedExpression) =>
        new() { TranslatedExpression = translatedExpression };

    /// <summary>
    /// Creates a failed translation result indicating AI fallback is needed.
    /// </summary>
    public static TranslationResult CannotTranslate(string reason) =>
        new() { CannotTranslateReason = reason };
}
