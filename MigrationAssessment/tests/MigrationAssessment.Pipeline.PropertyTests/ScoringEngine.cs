namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Object validation result used as input to the scoring engine.
/// Mirrors the PowerShell ObjectResults structure from Invoke-Scoring.ps1.
/// </summary>
public record ObjectResult(
    string ObjectName,
    string ObjectType,
    string DatabaseName,
    ObjectStatus Status
);

/// <summary>
/// Classification statuses for validated objects.
/// </summary>
public enum ObjectStatus
{
    Pass,
    FailSyntax,
    FailConvert,
    Skip
}

/// <summary>
/// Per-database scoring result.
/// </summary>
public record DatabaseScore(
    double? CompatibilityScore, // null means N/A (all objects are skip)
    int Pass,
    int FailSyntax,
    int FailConvert,
    int Skip
);

/// <summary>
/// Per-type breakdown of pass/fail counts and score for a specific object type.
/// </summary>
public record TypeBreakdown(
    string ObjectType,
    int Pass,
    int Fail,
    double? Score // null means N/A (zero convertible objects of this type)
);

/// <summary>
/// Aggregate scoring result across all databases.
/// </summary>
public record AggregateScore(
    double? CompatibilityScore, // null means N/A
    int TotalPass,
    int TotalFailSyntax,
    int TotalFailConvert,
    int TotalSkip
);

/// <summary>
/// Entry in the top failing types list.
/// </summary>
public record FailingTypeEntry(
    string Type,
    int FailCount
);

/// <summary>
/// C# implementation of the scoring formula from Invoke-Scoring.ps1.
/// Used for property-based testing of the scoring logic.
/// </summary>
public static class ScoringEngine
{
    /// <summary>
    /// Computes per-database compatibility scores from object results.
    /// Formula: (pass count) / (pass + fail-syntax + fail-convert) * 100, rounded to 1 decimal.
    /// Databases where all objects are "skip" get a score of null (N/A).
    /// </summary>
    public static Dictionary<string, DatabaseScore> ComputePerDatabaseScores(IEnumerable<ObjectResult> objectResults)
    {
        var byDatabase = objectResults.GroupBy(o => o.DatabaseName);
        var results = new Dictionary<string, DatabaseScore>();

        foreach (var group in byDatabase)
        {
            int pass = 0, failSyntax = 0, failConvert = 0, skip = 0;

            foreach (var obj in group)
            {
                switch (obj.Status)
                {
                    case ObjectStatus.Pass: pass++; break;
                    case ObjectStatus.FailSyntax: failSyntax++; break;
                    case ObjectStatus.FailConvert: failConvert++; break;
                    case ObjectStatus.Skip: skip++; break;
                }
            }

            int convertibleCount = pass + failSyntax + failConvert;
            double? score = convertibleCount == 0
                ? null
                : Math.Round((double)pass / convertibleCount * 100, 1);

            results[group.Key] = new DatabaseScore(score, pass, failSyntax, failConvert, skip);
        }

        return results;
    }

    /// <summary>
    /// Valid object types recognized by the pipeline.
    /// </summary>
    public static readonly string[] ValidObjectTypes = { "Table", "View", "StoredProcedure", "Function", "Trigger" };

    /// <summary>
    /// Computes per-type breakdowns for a single database's objects.
    /// Each type's pass = objects of that type with status Pass.
    /// Each type's fail = objects of that type with status FailSyntax or FailConvert.
    /// Each type's score = pass / (pass + fail) * 100, rounded to 1 decimal.
    /// Only types that have at least one object present are included.
    /// </summary>
    public static List<TypeBreakdown> ComputePerTypeBreakdown(IEnumerable<ObjectResult> databaseObjects)
    {
        var breakdowns = new List<TypeBreakdown>();

        foreach (var typeName in ValidObjectTypes)
        {
            var typeObjects = databaseObjects.Where(o => o.ObjectType == typeName).ToList();
            if (typeObjects.Count == 0)
                continue;

            int typePass = typeObjects.Count(o => o.Status == ObjectStatus.Pass);
            int typeFail = typeObjects.Count(o => o.Status == ObjectStatus.FailSyntax || o.Status == ObjectStatus.FailConvert);

            int typeConvertible = typePass + typeFail;
            double? typeScore = typeConvertible == 0
                ? null
                : Math.Round((double)typePass / typeConvertible * 100, 1);

            breakdowns.Add(new TypeBreakdown(typeName, typePass, typeFail, typeScore));
        }

        return breakdowns;
    }

    /// <summary>
    /// Computes the aggregate compatibility score across all databases,
    /// excluding databases where all objects are "skip" (score is N/A).
    /// </summary>
    public static AggregateScore ComputeAggregateScore(IEnumerable<ObjectResult> objectResults)
    {
        var perDb = ComputePerDatabaseScores(objectResults);

        int totalPass = 0, totalFailSyntax = 0, totalFailConvert = 0, totalSkip = 0;

        foreach (var db in perDb.Values)
        {
            // Only include databases that have at least one convertible object
            if (db.CompatibilityScore is not null)
            {
                totalPass += db.Pass;
                totalFailSyntax += db.FailSyntax;
                totalFailConvert += db.FailConvert;
            }
            totalSkip += db.Skip;
        }

        int aggregateConvertible = totalPass + totalFailSyntax + totalFailConvert;
        double? aggregateScore = aggregateConvertible == 0
            ? null
            : Math.Round((double)totalPass / aggregateConvertible * 100, 1);

        return new AggregateScore(aggregateScore, totalPass, totalFailSyntax, totalFailConvert, totalSkip);
    }

    /// <summary>
    /// Computes the top failing types when aggregate score is below 70%.
    /// Returns up to 5 types ranked by failure count descending.
    /// Returns empty list when aggregate score >= 70% or is N/A.
    /// </summary>
    public static List<FailingTypeEntry> ComputeTopFailingTypes(IEnumerable<ObjectResult> objectResults)
    {
        var aggregate = ComputeAggregateScore(objectResults);

        // Only report top failing types when aggregate < 70%
        if (aggregate.CompatibilityScore is null || aggregate.CompatibilityScore >= 70.0)
        {
            return new List<FailingTypeEntry>();
        }

        // Count failures per object type across all objects
        var failureCounts = objectResults
            .Where(o => o.Status == ObjectStatus.FailSyntax || o.Status == ObjectStatus.FailConvert)
            .GroupBy(o => o.ObjectType)
            .Select(g => new FailingTypeEntry(g.Key, g.Count()))
            .OrderByDescending(e => e.FailCount)
            .Take(5)
            .ToList();

        return failureCounts;
    }
}
