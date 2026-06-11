using PgPassthrough.Core.Models;

namespace PgPassthrough.Core.Abstractions;

/// <summary>
/// Translates a SQL statement from the source dialect (T-SQL) into
/// the target backend dialect (e.g. PostgreSQL SQL).
/// </summary>
public interface ISqlTranslator
{
    /// <summary>
    /// Translates the given T-SQL text into a backend-compatible SQL statement.
    /// Implementations are expected to be thread-safe and cache results internally.
    /// </summary>
    /// <param name="tsql">Raw T-SQL text from the client.</param>
    /// <param name="context">Session context (database name, SET options, etc.).</param>
    /// <returns>A <see cref="TranslationResult"/> containing the translated SQL and diagnostics.</returns>
    TranslationResult Translate(string tsql, TranslationContext context);
}
