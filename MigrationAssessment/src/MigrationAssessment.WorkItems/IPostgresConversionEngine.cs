namespace MigrationAssessment.WorkItems;

/// <summary>
/// Converts SQL Server T-SQL patterns to syntactically correct PostgreSQL equivalents.
/// Each detected feature has a dedicated transformation function that rewrites the
/// original statement structure into valid PostgreSQL syntax.
/// </summary>
public interface IPostgresConversionEngine
{
    /// <summary>
    /// Converts a SQL Server statement to its PostgreSQL equivalent based on the
    /// detected features. Returns a multi-line SQL string with TODO comments where
    /// human review is required.
    /// </summary>
    /// <param name="sqlServerPattern">The original SQL Server T-SQL statement.</param>
    /// <param name="detectedFeatures">List of feature names detected in the statement.</param>
    /// <returns>A syntactically correct PostgreSQL SQL snippet.</returns>
    string Convert(string sqlServerPattern, IReadOnlyList<string> detectedFeatures);

    /// <summary>
    /// Converts a SQL Server statement for a single detected feature.
    /// </summary>
    /// <param name="sqlServerPattern">The original SQL Server T-SQL statement.</param>
    /// <param name="featureName">The detected feature name.</param>
    /// <returns>A syntactically correct PostgreSQL SQL snippet.</returns>
    string Convert(string sqlServerPattern, string featureName);
}
