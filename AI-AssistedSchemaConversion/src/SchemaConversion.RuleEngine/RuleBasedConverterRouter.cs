using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Routes schema objects to the appropriate rule-based converter based on their ObjectType.
/// Implements IRuleBasedConverter and dispatches to individual converters via DI.
/// Objects that require AI conversion (StoredProcedure, Function, Trigger) return Failed status.
/// </summary>
public sealed class RuleBasedConverterRouter : IRuleBasedConverter
{
    private readonly TableConverter _tableConverter;
    private readonly ConstraintConverter _constraintConverter;
    private readonly IndexConverter _indexConverter;
    private readonly SequenceConverter _sequenceConverter;
    private readonly ViewConverter _viewConverter;
    private readonly SchemaConverter _schemaConverter;
    private readonly UserDefinedTypeConverter _userDefinedTypeConverter;
    private readonly SynonymConverter _synonymConverter;
    private readonly PermissionConverter _permissionConverter;
    private readonly ILogger<RuleBasedConverterRouter> _logger;

    public RuleBasedConverterRouter(
        TableConverter tableConverter,
        ConstraintConverter constraintConverter,
        IndexConverter indexConverter,
        SequenceConverter sequenceConverter,
        ViewConverter viewConverter,
        SchemaConverter schemaConverter,
        UserDefinedTypeConverter userDefinedTypeConverter,
        SynonymConverter synonymConverter,
        PermissionConverter permissionConverter,
        ILogger<RuleBasedConverterRouter> logger)
    {
        _tableConverter = tableConverter;
        _constraintConverter = constraintConverter;
        _indexConverter = indexConverter;
        _sequenceConverter = sequenceConverter;
        _viewConverter = viewConverter;
        _schemaConverter = schemaConverter;
        _userDefinedTypeConverter = userDefinedTypeConverter;
        _synonymConverter = synonymConverter;
        _permissionConverter = permissionConverter;
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Routing {ObjectType} {Schema}.{Name} to converter",
            obj.ObjectType, obj.SchemaName, obj.Name);

        return obj.ObjectType switch
        {
            SchemaObjectType.Table => _tableConverter.Convert(obj, context),
            SchemaObjectType.Constraint => _constraintConverter.Convert(obj, context),
            SchemaObjectType.Index => _indexConverter.Convert(obj, context),
            SchemaObjectType.Sequence => _sequenceConverter.Convert(obj, context),
            SchemaObjectType.View => _viewConverter.Convert(obj, context),
            SchemaObjectType.Schema => _schemaConverter.Convert(obj, context),
            SchemaObjectType.UserDefinedType => _userDefinedTypeConverter.Convert(obj, context),
            SchemaObjectType.Synonym => _synonymConverter.Convert(obj, context),
            SchemaObjectType.Permission => _permissionConverter.Convert(obj, context),

            // These object types require AI-assisted conversion
            SchemaObjectType.StoredProcedure => CreateAiFallbackResult(obj, "Stored procedures require AI-assisted conversion."),
            SchemaObjectType.Function => CreateAiFallbackResult(obj, "Functions require AI-assisted conversion."),
            SchemaObjectType.Trigger => CreateAiFallbackResult(obj, "Triggers require AI-assisted conversion."),

            _ => new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = obj.ObjectType,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"Unknown object type: {obj.ObjectType}"
            }
        };
    }

    private static ConversionResult CreateAiFallbackResult(SchemaObject obj, string reason)
    {
        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = obj.ObjectType,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.RuleBased,
            ErrorMessage = reason
        };
    }
}
