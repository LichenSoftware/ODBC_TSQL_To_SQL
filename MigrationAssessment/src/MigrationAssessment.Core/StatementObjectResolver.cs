using System.Text.RegularExpressions;
using MigrationAssessment.Core.Interfaces;
using MigrationAssessment.Core.Models;

namespace MigrationAssessment.Core;

/// <summary>
/// Resolves statement-to-object attribution using normalized text containment matching.
/// This is the single shared implementation used by both the ObjectInventoryBuilder
/// (for building per-object feature/risk summaries) and the work item generator
/// (for labeling work items with the correct containing object).
/// </summary>
public sealed class StatementObjectResolver : IStatementObjectResolver
{
    /// <summary>
    /// Minimum normalized statement length to attempt matching.
    /// Very short statements (less than 20 chars normalized) could produce false positives.
    /// </summary>
    private const int MinMatchLength = 20;

    /// <inheritdoc />
    public IReadOnlyDictionary<AnalyzedStatement, ResolvedObject> ResolveStatementObjects(
        IReadOnlyList<AnalyzedStatement> statements,
        DatabaseObjectInventory objectInventory)
    {
        var result = new Dictionary<AnalyzedStatement, ResolvedObject>(
            ReferenceEqualityComparer.Instance);

        if (statements.Count == 0 || objectInventory.ProgrammableObjects.Count == 0)
        {
            return result;
        }

        // Pre-compute normalized source texts for each programmable object
        var objectSources = new List<(ProgrammableObjectMetadata Obj, string NormalizedSource, string MappedType)>();

        foreach (var obj in objectInventory.ProgrammableObjects)
        {
            if (obj.SourceText is null)
            {
                continue; // Encrypted or CLR — no source to match against
            }

            var normalizedSource = NormalizeForComparison(obj.SourceText);
            var mappedType = MapObjectType(obj.ObjectType);
            objectSources.Add((obj, normalizedSource, mappedType));
        }

        if (objectSources.Count == 0)
        {
            return result;
        }

        // Match each statement to its containing object
        foreach (var statement in statements)
        {
            var sqlText = statement.Source.SqlText;
            if (string.IsNullOrWhiteSpace(sqlText))
            {
                continue;
            }

            var normalizedStmt = NormalizeForComparison(StripParameterPrefix(sqlText));

            if (normalizedStmt.Length < MinMatchLength)
            {
                continue;
            }

            foreach (var (obj, normalizedSource, mappedType) in objectSources)
            {
                if (normalizedSource.Contains(normalizedStmt))
                {
                    result[statement] = new ResolvedObject
                    {
                        Name = obj.ObjectName,
                        Type = mappedType
                    };
                    break; // First match wins
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Strips the Query Store parameterized prefix: (@p1 type, @p2 type)SELECT ...
    /// </summary>
    internal static string StripParameterPrefix(string sqlText)
    {
        if (sqlText.Length > 0 && sqlText[0] == '(')
        {
            var depth = 0;
            for (int i = 0; i < sqlText.Length; i++)
            {
                if (sqlText[i] == '(') depth++;
                else if (sqlText[i] == ')') depth--;

                if (depth == 0)
                {
                    return sqlText[(i + 1)..].TrimStart();
                }
            }
        }
        return sqlText;
    }

    /// <summary>
    /// Normalizes SQL text for fuzzy comparison: lowercases, collapses all whitespace into single spaces.
    /// </summary>
    internal static string NormalizeForComparison(string text)
    {
        return Regex.Replace(text.ToLowerInvariant(), @"\s+", " ").Trim();
    }

    /// <summary>
    /// Maps SQL Server sys.objects.type_desc to our inventory type.
    /// </summary>
    public static string MapObjectType(string typeDesc)
    {
        return typeDesc.ToUpperInvariant() switch
        {
            "SQL_STORED_PROCEDURE" => "StoredProcedure",
            "VIEW" => "View",
            "SQL_SCALAR_FUNCTION" => "ScalarFunction",
            "SQL_INLINE_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "SQL_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "SQL_TRIGGER" => "Trigger",
            "CLR_STORED_PROCEDURE" => "StoredProcedure",
            "CLR_SCALAR_FUNCTION" => "ScalarFunction",
            "CLR_TABLE_VALUED_FUNCTION" => "TableValuedFunction",
            "AGGREGATE_FUNCTION" => "ScalarFunction",
            _ => "StoredProcedure" // Fallback
        };
    }

    /// <summary>
    /// Reference equality comparer for AnalyzedStatement (record type uses value equality by default).
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<AnalyzedStatement>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public bool Equals(AnalyzedStatement? x, AnalyzedStatement? y)
            => ReferenceEquals(x, y);

        public int GetHashCode(AnalyzedStatement obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
