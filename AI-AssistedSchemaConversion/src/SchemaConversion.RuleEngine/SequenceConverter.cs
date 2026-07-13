using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server CREATE SEQUENCE statements to PostgreSQL equivalents.
/// Preserves data type, START WITH, INCREMENT BY, MINVALUE, MAXVALUE, CYCLE/NO CYCLE, and CACHE.
/// </summary>
public sealed class SequenceConverter : IRuleBasedConverter
{
    private readonly TypeMapper _typeMapper;
    private readonly ILogger<SequenceConverter> _logger;

    public SequenceConverter(
        TypeMapper typeMapper,
        ILogger<SequenceConverter> logger)
    {
        _typeMapper = typeMapper;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting sequence {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for sequence {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Sequence,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new CreateSequenceVisitor();
        fragment.Accept(visitor);

        if (visitor.Sequence is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Sequence,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "No CREATE SEQUENCE statement found in source definition."
            };
        }

        return ConvertSequence(visitor.Sequence, obj, context);
    }

    private ConversionResult ConvertSequence(CreateSequenceStatement createSeq, SchemaObject obj, ConversionContext context)
    {
        var compatibilityNotes = new List<CompatibilityNote>();
        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var sequenceName = obj.Name;

        // Default PostgreSQL sequence type
        var pgDataType = "BIGINT";

        var clauses = new List<string>();

        // Process sequence options from the AST
        if (createSeq.SequenceOptions is not null)
        {
            foreach (var option in createSeq.SequenceOptions)
            {
                switch (option.OptionKind)
                {
                    case SequenceOptionKind.As:
                        // The AS option contains the data type — extract from token stream
                        var dataTypeText = GetFragmentText(option).Trim();
                        // Extract just the type name after "AS "
                        if (dataTypeText.StartsWith("AS ", StringComparison.OrdinalIgnoreCase))
                        {
                            dataTypeText = dataTypeText[3..].Trim();
                        }
                        var mappingResult = _typeMapper.MapType(dataTypeText);
                        if (mappingResult.MappedType is not null)
                        {
                            pgDataType = mappingResult.MappedType;
                        }
                        break;

                    case SequenceOptionKind.Start:
                        var startValue = ExtractNumericValue(option);
                        if (startValue is not null)
                        {
                            clauses.Add($"    START WITH {startValue}");
                        }
                        break;

                    case SequenceOptionKind.Increment:
                        var incValue = ExtractNumericValue(option);
                        if (incValue is not null)
                        {
                            clauses.Add($"    INCREMENT BY {incValue}");
                        }
                        break;

                    case SequenceOptionKind.MinValue:
                        if (option.NoValue)
                        {
                            clauses.Add("    NO MINVALUE");
                        }
                        else
                        {
                            var minValue = ExtractNumericValue(option);
                            if (minValue is not null)
                            {
                                clauses.Add($"    MINVALUE {minValue}");
                            }
                        }
                        break;

                    case SequenceOptionKind.MaxValue:
                        if (option.NoValue)
                        {
                            clauses.Add("    NO MAXVALUE");
                        }
                        else
                        {
                            var maxValue = ExtractNumericValue(option);
                            if (maxValue is not null)
                            {
                                clauses.Add($"    MAXVALUE {maxValue}");
                            }
                        }
                        break;

                    case SequenceOptionKind.Cycle:
                        clauses.Add(option.NoValue ? "    NO CYCLE" : "    CYCLE");
                        break;

                    case SequenceOptionKind.Cache:
                        if (option.NoValue)
                        {
                            // PostgreSQL does not have NO CACHE — use CACHE 1 as equivalent
                            clauses.Add("    CACHE 1");
                            compatibilityNotes.Add(new CompatibilityNote
                            {
                                Category = "SequenceCache",
                                Description = $"Sequence '{sequenceName}': SQL Server NO CACHE converted to CACHE 1 in PostgreSQL."
                            });
                        }
                        else
                        {
                            var cacheValue = ExtractNumericValue(option);
                            if (cacheValue is not null)
                            {
                                clauses.Add($"    CACHE {cacheValue}");
                            }
                        }
                        break;
                }
            }
        }

        // Insert data type at the beginning
        clauses.Insert(0, $"    AS {pgDataType}");

        var ddl = $"CREATE SEQUENCE {targetSchema}.{QuoteIdentifier(sequenceName)}\n" +
                  string.Join("\n", clauses) + ";";

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Sequence,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0,
            CompatibilityNotes = compatibilityNotes
        };
    }

    /// <summary>
    /// Extracts a numeric value from a SequenceOption by reading its token stream.
    /// The SequenceOption AST node does not expose the value directly.
    /// </summary>
    private static string? ExtractNumericValue(SequenceOption option)
    {
        var text = GetFragmentText(option);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // The text will be like "START WITH 1", "INCREMENT BY 1", "MINVALUE -100", "MAXVALUE 9999", "CACHE 20"
        // Extract the last numeric token (possibly with leading minus)
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            var candidate = parts[i].Trim();
            if (IsNumericValue(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsNumericValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var start = 0;
        if (value[0] == '-' || value[0] == '+')
        {
            start = 1;
        }

        if (start >= value.Length)
        {
            return false;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (!char.IsDigit(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string GetFragmentText(TSqlFragment? fragment)
    {
        if (fragment is null) return string.Empty;

        var tokens = new List<string>();
        for (var i = fragment.FirstTokenIndex; i <= fragment.LastTokenIndex; i++)
        {
            if (i >= 0 && fragment.ScriptTokenStream is not null && i < fragment.ScriptTokenStream.Count)
            {
                tokens.Add(fragment.ScriptTokenStream[i].Text);
            }
        }
        return string.Join("", tokens).Trim();
    }

    private static string MapSchema(string sourceSchema, IReadOnlyDictionary<string, string> schemaMappings)
    {
        if (schemaMappings.TryGetValue(sourceSchema, out var mapped))
        {
            return mapped;
        }

        return sourceSchema.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            ? "public"
            : sourceSchema;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return identifier.ToLowerInvariant();
        }
        return $"\"{identifier}\"";
    }

    private sealed class CreateSequenceVisitor : TSqlFragmentVisitor
    {
        public CreateSequenceStatement? Sequence { get; private set; }

        public override void Visit(CreateSequenceStatement node)
        {
            Sequence ??= node;
        }
    }
}
