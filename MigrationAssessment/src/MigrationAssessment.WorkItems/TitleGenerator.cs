namespace MigrationAssessment.WorkItems;

/// <summary>
/// Generates work item titles in the format "[Risk N] Convert feature_name in object_name"
/// with a maximum length of 120 characters.
/// </summary>
public sealed class TitleGenerator
{
    private const int MaxTitleLength = 120;
    private const string TruncationSuffix = "...";
    private const string DefaultObjectName = "Ad Hoc Queries";

    /// <summary>
    /// Generates a title for a multi-feature work item.
    /// Single feature: "[Risk N] Convert {featureName} in {objectName}"
    /// Multiple features: "[Risk N] Convert {count} features in {objectName}"
    /// If the total length exceeds 120 characters, the object name is truncated with "...".
    /// </summary>
    /// <param name="detectedFeatures">The list of detected feature names (must contain at least one).</param>
    /// <param name="objectName">The database object name, or null for ad hoc queries.</param>
    /// <param name="riskLevel">The risk level (1-5).</param>
    /// <returns>A formatted title string of at most 120 characters.</returns>
    public string GenerateTitle(IReadOnlyList<string> detectedFeatures, string? objectName, int riskLevel)
    {
        ArgumentNullException.ThrowIfNull(detectedFeatures);
        if (detectedFeatures.Count == 0)
            throw new ArgumentException("Must contain at least one feature.", nameof(detectedFeatures));

        var resolvedObject = string.IsNullOrWhiteSpace(objectName)
            ? DefaultObjectName
            : objectName;

        string featurePart;
        if (detectedFeatures.Count == 1)
            featurePart = detectedFeatures[0];
        else
            featurePart = $"{detectedFeatures.Count} features";

        var prefix = $"[Risk {riskLevel}] Convert {featurePart} in ";
        var fullTitle = prefix + resolvedObject;

        if (fullTitle.Length <= MaxTitleLength)
            return fullTitle;

        var available = MaxTitleLength - prefix.Length - TruncationSuffix.Length;
        if (available <= 0)
            return fullTitle[..MaxTitleLength];

        return prefix + resolvedObject[..available] + TruncationSuffix;
    }

    /// <summary>
    /// Generates a title for a single-feature work item.
    /// Delegates to the multi-feature overload for consistent behavior.
    /// Format: "[Risk N] Convert &lt;featureName&gt; in &lt;objectName&gt;"
    /// If the total length exceeds 120 characters, the object name is truncated with "...".
    /// </summary>
    /// <param name="featureName">The SQL Server feature/construct name.</param>
    /// <param name="objectName">The database object name, or null for ad hoc queries.</param>
    /// <param name="riskLevel">The risk level (1-5).</param>
    /// <returns>A formatted title string of at most 120 characters.</returns>
    public string GenerateTitle(string featureName, string? objectName, int riskLevel)
        => GenerateTitle(new[] { featureName }, objectName, riskLevel);
}
