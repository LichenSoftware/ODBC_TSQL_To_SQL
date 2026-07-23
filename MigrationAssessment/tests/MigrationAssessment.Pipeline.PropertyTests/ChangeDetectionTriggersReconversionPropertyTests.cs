using FluentAssertions;
using FsCheck;
using FsCheck.Xunit;
using System.Text.RegularExpressions;

namespace MigrationAssessment.Pipeline.PropertyTests;

/// <summary>
/// Property-based tests for Change Detection Triggers Re-conversion.
///
/// Property 10: Change Detection Triggers Re-conversion — For any modification to a
/// conversion rule file or prompt template (detected by SHA-256 hash difference from the
/// previous Scoring Report), the Pipeline Runner SHALL re-convert objects of the types
/// associated with the changed file, where prompt templates map to their corresponding
/// object type and mapping files apply to all object types.
///
/// **Feature: migration-validation-pipeline, Property 10: Change Detection Triggers Re-conversion**
/// **Validates: Requirements 4.4**
/// </summary>
[Trait("Feature", "migration-validation-pipeline")]
[Trait("Property", "10: Change Detection Triggers Re-conversion")]
public class ChangeDetectionTriggersReconversionPropertyTests
{
    #region Change Detection Engine (C# model of Get-TypesRequiringReconversion)

    /// <summary>
    /// All valid object types recognized by the pipeline.
    /// </summary>
    public static readonly string[] AllObjectTypes =
        { "StoredProcedure", "Function", "View", "Trigger", "Table" };

    /// <summary>
    /// Static mapping from well-known config/mapping file names to the object types
    /// they affect. All three mapping files affect every object type.
    /// </summary>
    private static readonly Dictionary<string, string[]> MappingFileTypes = new()
    {
        ["type-mappings.json"]     = AllObjectTypes,
        ["function-mappings.json"] = AllObjectTypes,
        ["schema-mappings.json"]   = AllObjectTypes,
    };

    /// <summary>
    /// Ordered prompt-template patterns (evaluated top-to-bottom, first match wins).
    /// Mirrors the $promptPatterns array in Get-TypesRequiringReconversion.
    /// </summary>
    private static readonly (string Pattern, string[] Types)[] PromptPatterns =
    {
        (@"^stored-procedure\..*\.md$", new[] { "StoredProcedure" }),
        (@"^function\..*\.md$",         new[] { "Function" }),
        (@"^view\..*\.md$",             new[] { "View" }),
        (@"^trigger\..*\.md$",          new[] { "Trigger" }),
        (@"^complex-object\..*\.md$",   AllObjectTypes),
    };

    /// <summary>
    /// C# implementation of Get-TypesRequiringReconversion from Run-MigrationPipeline.ps1.
    ///
    /// For every file whose hash differs (added, removed, or modified) between the
    /// previous and current hash sets, maps the file name to the object types it affects
    /// and returns the distinct sorted set of those types.
    /// </summary>
    public static string[] GetTypesRequiringReconversion(
        Dictionary<string, string> previousHashes,
        Dictionary<string, string> currentHashes)
    {
        // Union of all file names present in either set
        var allFileNames = previousHashes.Keys.Union(currentHashes.Keys).ToHashSet();

        var affectedTypes = new HashSet<string>();

        foreach (var fileName in allFileNames)
        {
            previousHashes.TryGetValue(fileName, out var previousHash);
            currentHashes.TryGetValue(fileName, out var currentHash);

            // Detect any change: added (prev null), removed (curr null), or modified
            bool hashChanged =
                (previousHash is null && currentHash is not null) ||
                (previousHash is not null && currentHash is null) ||
                (previousHash is not null && currentHash is not null && previousHash != currentHash);

            if (!hashChanged)
                continue;

            // Resolve affected types for this file
            string[]? typesForFile = null;

            if (MappingFileTypes.TryGetValue(fileName, out var mappingTypes))
            {
                typesForFile = mappingTypes;
            }
            else
            {
                foreach (var (pattern, types) in PromptPatterns)
                {
                    if (Regex.IsMatch(fileName, pattern))
                    {
                        typesForFile = types;
                        break; // First match wins
                    }
                }
            }

            if (typesForFile is not null)
            {
                foreach (var t in typesForFile)
                    affectedTypes.Add(t);
            }
        }

        // Return distinct, sorted list (mirrors PowerShell's Select-Object -Unique | Sort-Object)
        return affectedTypes.OrderBy(t => t).ToArray();
    }

    /// <summary>
    /// Resolves the set of object types affected by a single file name change,
    /// without requiring hash comparison context. Used for per-file assertions.
    /// Returns null when the file name is not recognized (not a config or prompt file).
    /// </summary>
    public static string[]? GetAffectedTypesForFile(string fileName)
    {
        if (MappingFileTypes.TryGetValue(fileName, out var mappingTypes))
            return mappingTypes.OrderBy(t => t).ToArray();

        foreach (var (pattern, types) in PromptPatterns)
        {
            if (Regex.IsMatch(fileName, pattern))
                return types.OrderBy(t => t).ToArray();
        }

        return null;
    }

    #endregion

    #region Generators

    /// <summary>
    /// Generates a random SHA-256-style hex hash string (64 characters).
    /// </summary>
    private static Gen<string> GenHash()
    {
        var hexChars = "0123456789abcdef".ToCharArray();
        return from chars in Gen.ArrayOf(64, Gen.Elements(hexChars))
               select new string(chars);
    }

    /// <summary>
    /// Generates a different hash than the given one (guaranteed to differ).
    /// </summary>
    private static Gen<string> GenDifferentHash(string original)
    {
        // Flip the first character
        char flipped = original[0] == 'a' ? 'b' : 'a';
        return Gen.Constant(flipped + original[1..]);
    }

    /// <summary>
    /// Generates the name of one of the three well-known mapping files.
    /// </summary>
    private static Gen<string> GenMappingFileName()
    {
        return Gen.Elements(
            "type-mappings.json",
            "function-mappings.json",
            "schema-mappings.json"
        );
    }

    /// <summary>
    /// Generates a versioned prompt template file name (e.g., stored-procedure.v1.0.0.md).
    /// </summary>
    private static Gen<string> GenPromptFileName()
    {
        return from prefix in Gen.Elements(
                   "stored-procedure",
                   "function",
                   "view",
                   "trigger",
                   "complex-object")
               from major in Gen.Choose(1, 5)
               from minor in Gen.Choose(0, 9)
               from patch in Gen.Choose(0, 9)
               select $"{prefix}.v{major}.{minor}.{patch}.md";
    }

    /// <summary>
    /// Generates any tracked config/prompt file name.
    /// </summary>
    private static Gen<string> GenTrackedFileName()
    {
        return Gen.OneOf(GenMappingFileName(), GenPromptFileName());
    }

    /// <summary>
    /// Generates a hash comparison scenario: previous hashes, current hashes,
    /// and a label indicating which files changed.
    /// </summary>
    private static Gen<(Dictionary<string, string> Previous, Dictionary<string, string> Current, List<string> ChangedFiles)>
        GenHashComparisonScenario()
    {
        return from fileCount in Gen.Choose(1, 8)
               from fileNames in Gen.ListOf(fileCount, GenTrackedFileName())
               let distinctFiles = fileNames.Distinct().ToList()
               from hashPairs in GenHashPairsForFiles(distinctFiles)
               select hashPairs;
    }

    private static Gen<(Dictionary<string, string>, Dictionary<string, string>, List<string>)>
        GenHashPairsForFiles(List<string> fileNames)
    {
        // For each file, randomly decide: unchanged, modified, added (not in previous), or removed (not in current)
        var genChangeType = Gen.Elements("unchanged", "modified", "added", "removed");

        return from changeTypes in Gen.ListOf(fileNames.Count, genChangeType)
               from hashes in Gen.ListOf(fileNames.Count, GenHash())
               from altHashes in Gen.ListOf(fileNames.Count, GenHash())
               select BuildHashDicts(fileNames, changeTypes.ToList(), hashes.ToList(), altHashes.ToList());
    }

    private static (Dictionary<string, string>, Dictionary<string, string>, List<string>)
        BuildHashDicts(
            List<string> fileNames,
            List<string> changeTypes,
            List<string> hashes,
            List<string> altHashes)
    {
        var previous = new Dictionary<string, string>();
        var current  = new Dictionary<string, string>();
        var changedFiles = new List<string>();

        for (int i = 0; i < fileNames.Count; i++)
        {
            var file       = fileNames[i];
            var changeType = changeTypes[i];
            var hash       = hashes[i];
            var altHash    = altHashes[i];

            // Ensure the two hashes differ for "modified" by patching the end if equal
            string differentHash = (hash == altHash) ? hash[..62] + "ff" : altHash;

            switch (changeType)
            {
                case "unchanged":
                    previous[file] = hash;
                    current[file]  = hash;
                    break;
                case "modified":
                    previous[file] = hash;
                    current[file]  = differentHash;
                    changedFiles.Add(file);
                    break;
                case "added":
                    // Present in current only
                    current[file] = hash;
                    changedFiles.Add(file);
                    break;
                case "removed":
                    // Present in previous only
                    previous[file] = hash;
                    changedFiles.Add(file);
                    break;
            }
        }

        return (previous, current, changedFiles);
    }

    #endregion

    #region Property 10: Change Detection Tests

    /// <summary>
    /// Property 10.1: When a mapping file (type-mappings.json, function-mappings.json,
    /// schema-mappings.json) hash changes, ALL object types are flagged for re-conversion.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_mapping_file_flags_all_object_types()
    {
        var gen = from mappingFile in GenMappingFileName()
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  // Guarantee the hashes differ
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (mappingFile, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (mappingFile, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [mappingFile] = oldHash };
            var current  = new Dictionary<string, string> { [mappingFile] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            // All five object types must be flagged
            foreach (var type in AllObjectTypes)
            {
                result.Should().Contain(type,
                    $"mapping file '{mappingFile}' change must flag object type '{type}'");
            }

            result.Length.Should().Be(AllObjectTypes.Length,
                "mapping file change must flag exactly all 5 object types");
        });
    }

    /// <summary>
    /// Property 10.2: When a stored-procedure prompt template hash changes, ONLY
    /// StoredProcedure objects are flagged.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_stored_procedure_prompt_flags_only_stored_procedure()
    {
        var gen = from minor in Gen.Choose(0, 9)
                  from patch in Gen.Choose(0, 9)
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  let fileName = $"stored-procedure.v1.{minor}.{patch}.md"
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (fileName, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (fileName, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [fileName] = oldHash };
            var current  = new Dictionary<string, string> { [fileName] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            result.Should().BeEquivalentTo(new[] { "StoredProcedure" },
                $"only StoredProcedure should be flagged when '{fileName}' changes");
        });
    }

    /// <summary>
    /// Property 10.3: When a function prompt template hash changes, ONLY Function
    /// objects are flagged.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_function_prompt_flags_only_function()
    {
        var gen = from minor in Gen.Choose(0, 9)
                  from patch in Gen.Choose(0, 9)
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  let fileName = $"function.v1.{minor}.{patch}.md"
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (fileName, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (fileName, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [fileName] = oldHash };
            var current  = new Dictionary<string, string> { [fileName] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            result.Should().BeEquivalentTo(new[] { "Function" },
                $"only Function should be flagged when '{fileName}' changes");
        });
    }

    /// <summary>
    /// Property 10.4: When a view prompt template hash changes, ONLY View objects are flagged.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_view_prompt_flags_only_view()
    {
        var gen = from minor in Gen.Choose(0, 9)
                  from patch in Gen.Choose(0, 9)
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  let fileName = $"view.v1.{minor}.{patch}.md"
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (fileName, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (fileName, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [fileName] = oldHash };
            var current  = new Dictionary<string, string> { [fileName] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            result.Should().BeEquivalentTo(new[] { "View" },
                $"only View should be flagged when '{fileName}' changes");
        });
    }

    /// <summary>
    /// Property 10.5: When a trigger prompt template hash changes, ONLY Trigger objects
    /// are flagged.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_trigger_prompt_flags_only_trigger()
    {
        var gen = from minor in Gen.Choose(0, 9)
                  from patch in Gen.Choose(0, 9)
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  let fileName = $"trigger.v1.{minor}.{patch}.md"
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (fileName, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (fileName, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [fileName] = oldHash };
            var current  = new Dictionary<string, string> { [fileName] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            result.Should().BeEquivalentTo(new[] { "Trigger" },
                $"only Trigger should be flagged when '{fileName}' changes");
        });
    }

    /// <summary>
    /// Property 10.6: When a complex-object prompt template hash changes, ALL object
    /// types are flagged (same as a mapping file change).
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Modified_complex_object_prompt_flags_all_types()
    {
        var gen = from minor in Gen.Choose(0, 9)
                  from patch in Gen.Choose(0, 9)
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  let fileName = $"complex-object.v1.{minor}.{patch}.md"
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (fileName, oldHash, safeNew);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (fileName, oldHash, newHash) = tuple;

            var previous = new Dictionary<string, string> { [fileName] = oldHash };
            var current  = new Dictionary<string, string> { [fileName] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            foreach (var type in AllObjectTypes)
            {
                result.Should().Contain(type,
                    $"complex-object prompt change must flag '{type}'");
            }

            result.Length.Should().Be(AllObjectTypes.Length,
                "complex-object prompt change must flag exactly all 5 object types");
        });
    }

    /// <summary>
    /// Property 10.7: When no hashes change, no types are flagged for re-conversion.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property No_hash_change_means_no_types_flagged()
    {
        var gen = from fileCount in Gen.Choose(1, 6)
                  from fileNames in Gen.ListOf(fileCount, GenTrackedFileName())
                  let distinctFiles = fileNames.Distinct().ToList()
                  from hashes in Gen.ListOf(distinctFiles.Count, GenHash())
                  let hashDict = distinctFiles.Zip(hashes).ToDictionary(p => p.First, p => p.Second)
                  select hashDict;

        return Prop.ForAll(gen.ToArbitrary(), hashDict =>
        {
            // Identical previous and current hashes
            var previous = new Dictionary<string, string>(hashDict);
            var current  = new Dictionary<string, string>(hashDict);

            var result = GetTypesRequiringReconversion(previous, current);

            result.Should().BeEmpty(
                "when no hashes change, no types should be flagged for re-conversion");
        });
    }

    /// <summary>
    /// Property 10.8: When a file is added (present in current but not in previous),
    /// it triggers the appropriate types.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Added_file_triggers_appropriate_types()
    {
        var gen = from trackedFile in GenTrackedFileName()
                  from newHash in GenHash()
                  select (trackedFile, newHash);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (trackedFile, newHash) = tuple;

            // File is only present in current (newly added)
            var previous = new Dictionary<string, string>();
            var current  = new Dictionary<string, string> { [trackedFile] = newHash };

            var result = GetTypesRequiringReconversion(previous, current);

            var expectedTypes = GetAffectedTypesForFile(trackedFile);

            result.Should().BeEquivalentTo(expectedTypes!,
                $"adding '{trackedFile}' should trigger its associated types");
        });
    }

    /// <summary>
    /// Property 10.9: When a file is removed (present in previous but not in current),
    /// it triggers the appropriate types.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Removed_file_triggers_appropriate_types()
    {
        var gen = from trackedFile in GenTrackedFileName()
                  from oldHash in GenHash()
                  select (trackedFile, oldHash);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (trackedFile, oldHash) = tuple;

            // File was in previous but no longer in current
            var previous = new Dictionary<string, string> { [trackedFile] = oldHash };
            var current  = new Dictionary<string, string>();

            var result = GetTypesRequiringReconversion(previous, current);

            var expectedTypes = GetAffectedTypesForFile(trackedFile);

            result.Should().BeEquivalentTo(expectedTypes!,
                $"removing '{trackedFile}' should trigger its associated types");
        });
    }

    /// <summary>
    /// Property 10.10: The result of change detection is always a subset of the five
    /// recognized object types — no unknown types are ever returned.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Result_is_always_subset_of_valid_object_types()
    {
        return Prop.ForAll(GenHashComparisonScenario().ToArbitrary(), scenario =>
        {
            var (previous, current, _) = scenario;

            var result = GetTypesRequiringReconversion(previous, current);

            foreach (var type in result)
            {
                AllObjectTypes.Should().Contain(type,
                    $"returned type '{type}' must be one of the 5 recognized object types");
            }
        });
    }

    /// <summary>
    /// Property 10.11: When multiple files change simultaneously, the flagged types are
    /// the union of the types for each individual changed file.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Union_of_types_when_multiple_files_change()
    {
        var gen = from file1 in GenTrackedFileName()
                  from file2 in GenTrackedFileName()
                  where file1 != file2
                  from hash1Old in GenHash()
                  from hash1New in GenHash()
                  from hash2Old in GenHash()
                  from hash2New in GenHash()
                  let safe1New = (hash1Old == hash1New) ? hash1New[..62] + "ff" : hash1New
                  let safe2New = (hash2Old == hash2New) ? hash2New[..62] + "ff" : hash2New
                  select (file1, hash1Old, safe1New, file2, hash2Old, safe2New);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (file1, hash1Old, hash1New, file2, hash2Old, hash2New) = tuple;

            var previous = new Dictionary<string, string>
            {
                [file1] = hash1Old,
                [file2] = hash2Old,
            };
            var current = new Dictionary<string, string>
            {
                [file1] = hash1New,
                [file2] = hash2New,
            };

            var result = GetTypesRequiringReconversion(previous, current);

            // Expected = union of types for file1 and file2
            var typesForFile1 = GetAffectedTypesForFile(file1) ?? Array.Empty<string>();
            var typesForFile2 = GetAffectedTypesForFile(file2) ?? Array.Empty<string>();
            var expectedTypes = typesForFile1.Union(typesForFile2).OrderBy(t => t).ToArray();

            result.Should().BeEquivalentTo(expectedTypes,
                $"changing both '{file1}' and '{file2}' should flag the union of their types");
        });
    }

    /// <summary>
    /// Property 10.12: Empty hash comparison (no files on either side) produces no
    /// types for re-conversion.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Fact]
    public void Empty_hash_sets_produce_no_types()
    {
        var result = GetTypesRequiringReconversion(
            new Dictionary<string, string>(),
            new Dictionary<string, string>());

        result.Should().BeEmpty("no files means no types to flag");
    }

    /// <summary>
    /// Property 10.13: Unchanged files mixed with changed files — only the changed
    /// files contribute types; unchanged files do not inflate the result.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Unchanged_files_do_not_contribute_types()
    {
        var gen = from changedFile in GenTrackedFileName()
                  from unchangedFile in GenTrackedFileName()
                  where changedFile != unchangedFile
                  from oldHash in GenHash()
                  from newHash in GenHash()
                  from sharedHash in GenHash()
                  let safeNew = (oldHash == newHash) ? newHash[..62] + "ff" : newHash
                  select (changedFile, unchangedFile, oldHash, safeNew, sharedHash);

        return Prop.ForAll(gen.ToArbitrary(), tuple =>
        {
            var (changedFile, unchangedFile, oldHash, newHash, sharedHash) = tuple;

            var previous = new Dictionary<string, string>
            {
                [changedFile]   = oldHash,
                [unchangedFile] = sharedHash,
            };
            var current = new Dictionary<string, string>
            {
                [changedFile]   = newHash,
                [unchangedFile] = sharedHash,
            };

            var resultWithBothFiles = GetTypesRequiringReconversion(previous, current);

            // Result from only the changed file (no unchanged file present)
            var resultChangedOnly = GetTypesRequiringReconversion(
                new Dictionary<string, string> { [changedFile] = oldHash },
                new Dictionary<string, string> { [changedFile] = newHash });

            resultWithBothFiles.Should().BeEquivalentTo(resultChangedOnly,
                "presence of an unchanged file should not alter the set of flagged types");
        });
    }

    /// <summary>
    /// Property 10.14: The returned type list contains no duplicates — each type appears
    /// at most once regardless of how many changed files map to that type.
    ///
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Result_contains_no_duplicate_types()
    {
        return Prop.ForAll(GenHashComparisonScenario().ToArbitrary(), scenario =>
        {
            var (previous, current, _) = scenario;

            var result = GetTypesRequiringReconversion(previous, current);

            result.Length.Should().Be(result.Distinct().Count(),
                "result should not contain duplicate object types");
        });
    }

    #endregion
}
