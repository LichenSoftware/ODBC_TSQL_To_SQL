using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;

namespace SchemaConversion.Orchestration;

/// <summary>
/// Classifies schema objects to determine whether they should be processed by
/// the rule-based converter or the AI-assisted converter.
/// </summary>
public sealed partial class ObjectClassifier : IObjectClassifier
{
    private readonly ILogger<ObjectClassifier> _logger;
    private readonly HashSet<string> _forceAiObjects;
    private readonly HashSet<string> _forceRulesObjects;

    /// <summary>
    /// SQL Server-specific keywords in view definitions that indicate the view
    /// cannot be converted by simple rule-based translation.
    /// </summary>
    private static readonly string[] SqlServerViewKeywords =
    [
        "CROSS APPLY",
        "OUTER APPLY",
        "FOR XML",
        "OPENROWSET",
        "OPENJSON",
        "PIVOT",
        "UNPIVOT"
    ];

    public ObjectClassifier(
        ILogger<ObjectClassifier> logger,
        IReadOnlyList<string>? forceAiObjects = null,
        IReadOnlyList<string>? forceRulesObjects = null)
    {
        _logger = logger;
        _forceAiObjects = new HashSet<string>(
            forceAiObjects ?? [],
            StringComparer.OrdinalIgnoreCase);
        _forceRulesObjects = new HashSet<string>(
            forceRulesObjects ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    public ClassificationResult Classify(SchemaObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);

        var qualifiedName = $"{obj.SchemaName}.{obj.Name}";

        // Manual override: force-rules takes priority over force-ai
        if (_forceRulesObjects.Contains(qualifiedName) || _forceRulesObjects.Contains(obj.Name))
        {
            _logger.LogDebug("Object {Name} forced to RuleBased by manual override", qualifiedName);
            return new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "Manual override: forced to rule-based conversion"
            };
        }

        if (_forceAiObjects.Contains(qualifiedName) || _forceAiObjects.Contains(obj.Name))
        {
            _logger.LogDebug("Object {Name} forced to AiAssisted by manual override", qualifiedName);
            return new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Manual override: forced to AI-assisted conversion"
            };
        }

        // Classification by object type
        return obj.ObjectType switch
        {
            SchemaObjectType.StoredProcedure => new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Stored procedures require AI-assisted conversion due to procedural logic complexity"
            },
            SchemaObjectType.Function => new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Functions require AI-assisted conversion due to procedural logic complexity"
            },
            SchemaObjectType.Trigger => new ClassificationResult
            {
                Method = ConversionMethod.AiAssisted,
                Reason = "Triggers require AI-assisted conversion due to procedural logic complexity"
            },
            SchemaObjectType.View => ClassifyView(obj),
            _ => new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = $"Object type {obj.ObjectType} is handled by deterministic rule-based conversion"
            }
        };
    }

    /// <summary>
    /// Views are rule-based by default, but get promoted to AI-assisted if their
    /// source definition contains SQL Server-specific keywords that cannot be
    /// translated by simple rules.
    /// </summary>
    private ClassificationResult ClassifyView(SchemaObject obj)
    {
        if (string.IsNullOrWhiteSpace(obj.SourceDefinition))
        {
            return new ClassificationResult
            {
                Method = ConversionMethod.RuleBased,
                Reason = "View with empty source definition defaults to rule-based conversion"
            };
        }

        foreach (var keyword in SqlServerViewKeywords)
        {
            if (obj.SourceDefinition.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "View {Schema}.{Name} classified as AI-assisted due to keyword: {Keyword}",
                    obj.SchemaName, obj.Name, keyword);

                return new ClassificationResult
                {
                    Method = ConversionMethod.AiAssisted,
                    Reason = $"View contains SQL Server-specific keyword '{keyword}' requiring AI-assisted conversion"
                };
            }
        }

        return new ClassificationResult
        {
            Method = ConversionMethod.RuleBased,
            Reason = "View can be converted by rule-based translation (no SQL Server-specific keywords detected)"
        };
    }
}
