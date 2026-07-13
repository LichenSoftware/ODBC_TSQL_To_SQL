using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;
using SchemaConversion.RuleEngine.Models;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server user-defined types to PostgreSQL equivalents.
/// - Alias types (CREATE TYPE ... FROM base_type): produces PostgreSQL DOMAIN
/// - Table types (CREATE TYPE ... AS TABLE): produces PostgreSQL composite type
/// - CLR types: flagged with ManualReviewFlag
/// </summary>
public sealed class UserDefinedTypeConverter : IRuleBasedConverter
{
    private readonly TypeMapper _typeMapper;
    private readonly ILogger<UserDefinedTypeConverter> _logger;

    public UserDefinedTypeConverter(
        TypeMapper typeMapper,
        ILogger<UserDefinedTypeConverter> logger)
    {
        _typeMapper = typeMapper;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting user-defined type {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for UDT {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.UserDefinedType,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        // Check for table type
        var tableTypeVisitor = new CreateTypeTableVisitor();
        fragment.Accept(tableTypeVisitor);
        if (tableTypeVisitor.TableType is not null)
        {
            return ConvertTableType(tableTypeVisitor.TableType, obj, context);
        }

        // Check for alias type (CREATE TYPE ... FROM)
        var aliasVisitor = new CreateTypeAliasVisitor();
        fragment.Accept(aliasVisitor);
        if (aliasVisitor.AliasType is not null)
        {
            return ConvertAliasType(aliasVisitor.AliasType, obj, context);
        }

        // Check for CLR type (assembly-based)
        var sourceUpper = obj.SourceDefinition.ToUpperInvariant();
        if (sourceUpper.Contains("EXTERNAL NAME") || sourceUpper.Contains("ASSEMBLY"))
        {
            return ConvertClrType(obj, context);
        }

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.UserDefinedType,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.RuleBased,
            ErrorMessage = "Unrecognized user-defined type definition."
        };
    }

    private ConversionResult ConvertAliasType(CreateTypeUddtStatement aliasType, SchemaObject obj, ConversionContext context)
    {
        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var typeName = obj.Name;
        var compatibilityNotes = new List<CompatibilityNote>();

        // Map the base type
        var baseTypeName = GetDataTypeName(aliasType.DataType);
        int? precision = null;
        int? scale = null;
        int? length = null;

        if (aliasType.DataType is SqlDataTypeReference sqlType)
        {
            var parameters = sqlType.Parameters;
            if (parameters.Count >= 1)
            {
                var firstParam = parameters[0].Value;
                if (firstParam.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                {
                    length = -1;
                }
                else if (int.TryParse(firstParam, out var p))
                {
                    var upperType = baseTypeName.ToUpperInvariant();
                    if (upperType.Contains("CHAR") || upperType.Contains("BINARY"))
                    {
                        length = p;
                    }
                    else
                    {
                        precision = p;
                    }
                }
            }
            if (parameters.Count >= 2 && int.TryParse(parameters[1].Value, out var s))
            {
                scale = s;
            }
        }

        var mappingResult = _typeMapper.MapType(baseTypeName, precision, scale, length);
        if (mappingResult.MappedType is null)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.UserDefinedType,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"Cannot map base type '{baseTypeName}' for alias type."
            };
        }

        // Build DOMAIN definition
        var constraints = new List<string>();

        // NOT NULL constraint from the type definition
        if (aliasType.NullableConstraint is not null && !aliasType.NullableConstraint.Nullable)
        {
            constraints.Add("NOT NULL");
        }

        // Additional constraint from type mapping (e.g., TINYINT range check)
        if (mappingResult.AdditionalConstraint is not null)
        {
            var checkConstraint = mappingResult.AdditionalConstraint
                .Replace("{column}", "VALUE");
            constraints.Add(checkConstraint);
        }

        var constraintClause = constraints.Count > 0
            ? "\n    " + string.Join("\n    ", constraints)
            : string.Empty;

        var ddl = $"CREATE DOMAIN {targetSchema}.{QuoteIdentifier(typeName)} AS {mappingResult.MappedType}{constraintClause};";

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.UserDefinedType,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private ConversionResult ConvertTableType(CreateTypeTableStatement tableType, SchemaObject obj, ConversionContext context)
    {
        var targetSchema = MapSchema(obj.SchemaName, context.SchemaMappings);
        var typeName = obj.Name;
        var compatibilityNotes = new List<CompatibilityNote>();
        var reviewFlags = new List<ManualReviewFlag>();

        var columnDefs = new List<string>();

        if (tableType.Definition?.ColumnDefinitions is not null)
        {
            foreach (var column in tableType.Definition.ColumnDefinitions)
            {
                var columnName = column.ColumnIdentifier?.Value ?? "unknown";
                var dataTypeName = column.DataType is not null
                    ? GetDataTypeName(column.DataType)
                    : "TEXT";

                int? precision = null;
                int? scale = null;
                int? length = null;

                if (column.DataType is SqlDataTypeReference sqlType)
                {
                    var parameters = sqlType.Parameters;
                    if (parameters.Count >= 1)
                    {
                        var firstParam = parameters[0].Value;
                        if (firstParam.Equals("MAX", StringComparison.OrdinalIgnoreCase))
                        {
                            length = -1;
                        }
                        else if (int.TryParse(firstParam, out var p))
                        {
                            var upperType = dataTypeName.ToUpperInvariant();
                            if (upperType.Contains("CHAR") || upperType.Contains("BINARY"))
                            {
                                length = p;
                            }
                            else
                            {
                                precision = p;
                            }
                        }
                    }
                    if (parameters.Count >= 2 && int.TryParse(parameters[1].Value, out var s))
                    {
                        scale = s;
                    }
                }

                var mappingResult = _typeMapper.MapType(dataTypeName, precision, scale, length);
                var pgType = mappingResult.MappedType ?? "TEXT";

                if (mappingResult.RequiresManualReview)
                {
                    reviewFlags.Add(new ManualReviewFlag
                    {
                        Reason = $"Column '{columnName}' type '{dataTypeName}' requires manual review.",
                        CodeSection = columnName,
                        SuggestedAlternative = mappingResult.CompatibilityNote
                    });
                }

                columnDefs.Add($"    {QuoteIdentifier(columnName)} {pgType}");
            }
        }

        if (columnDefs.Count == 0)
        {
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.UserDefinedType,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = "Table type has no column definitions."
            };
        }

        compatibilityNotes.Add(new CompatibilityNote
        {
            Category = "TableType",
            Description = $"Table type '{typeName}' converted to PostgreSQL composite type. " +
                          "Note: composite types cannot have constraints or indexes. " +
                          "If used as a table variable, consider using a temporary table instead."
        });

        var ddl = $"CREATE TYPE {targetSchema}.{QuoteIdentifier(typeName)} AS (\n" +
                  string.Join(",\n", columnDefs) + "\n);";

        var status = reviewFlags.Count > 0 ? ConversionStatus.Flagged : ConversionStatus.Converted;

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.UserDefinedType,
            Status = status,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = reviewFlags.Count > 0 ? 0.7 : 1.0,
            ReviewFlags = reviewFlags,
            CompatibilityNotes = compatibilityNotes
        };
    }

    private static ConversionResult ConvertClrType(SchemaObject obj, ConversionContext context)
    {
        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.UserDefinedType,
            Status = ConversionStatus.Flagged,
            Method = ConversionMethod.RuleBased,
            ConfidenceScore = 0.0,
            ReviewFlags =
            [
                new ManualReviewFlag
                {
                    Reason = $"CLR user-defined type '{obj.Name}' cannot be automatically converted. " +
                             "PostgreSQL does not support CLR types.",
                    CodeSection = obj.SourceDefinition,
                    SuggestedAlternative = "Consider implementing the type logic as a PostgreSQL extension, " +
                                           "a composite type with functions, or a domain with constraints."
                }
            ]
        };
    }

    private static string GetDataTypeName(DataTypeReference dataType)
    {
        if (dataType is SqlDataTypeReference sqlType)
        {
            return sqlType.SqlDataTypeOption.ToString().ToUpperInvariant() switch
            {
                "NONE" => sqlType.Name?.BaseIdentifier?.Value ?? "UNKNOWN",
                var opt => opt
            };
        }

        return dataType.Name?.BaseIdentifier?.Value ?? "UNKNOWN";
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

    private sealed class CreateTypeAliasVisitor : TSqlFragmentVisitor
    {
        public CreateTypeUddtStatement? AliasType { get; private set; }

        public override void Visit(CreateTypeUddtStatement node)
        {
            AliasType ??= node;
        }
    }

    private sealed class CreateTypeTableVisitor : TSqlFragmentVisitor
    {
        public CreateTypeTableStatement? TableType { get; private set; }

        public override void Visit(CreateTypeTableStatement node)
        {
            TableType ??= node;
        }
    }
}
