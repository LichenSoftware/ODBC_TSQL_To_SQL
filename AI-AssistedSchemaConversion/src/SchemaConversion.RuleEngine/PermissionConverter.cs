using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.RuleEngine;

/// <summary>
/// Converts SQL Server permission statements to PostgreSQL equivalents.
/// - GRANT: converts to PostgreSQL GRANT with mapped schema/object references
/// - REVOKE: converts to PostgreSQL REVOKE
/// - DENY: flags with ManualReviewFlag (PostgreSQL has no DENY equivalent)
/// </summary>
public sealed class PermissionConverter : IRuleBasedConverter
{
    private readonly ILogger<PermissionConverter> _logger;

    public PermissionConverter(ILogger<PermissionConverter> logger)
    {
        _logger = logger;
    }

    public ConversionResult Convert(SchemaObject obj, ConversionContext context)
    {
        _logger.LogDebug("Converting permission {Schema}.{Name}", obj.SchemaName, obj.Name);

        var parser = new TSql160Parser(initialQuotedIdentifiers: false);
        using var reader = new StringReader(obj.SourceDefinition);
        var fragment = parser.Parse(reader, out var parseErrors);

        if (parseErrors.Count > 0)
        {
            var errorMessages = string.Join("; ", parseErrors.Select(e => e.Message));
            _logger.LogWarning("Parse errors for permission {Name}: {Errors}", obj.Name, errorMessages);
            return new ConversionResult
            {
                ObjectName = obj.Name,
                SchemaName = obj.SchemaName,
                ObjectType = SchemaObjectType.Permission,
                Status = ConversionStatus.Failed,
                Method = ConversionMethod.RuleBased,
                ErrorMessage = $"T-SQL parse errors: {errorMessages}"
            };
        }

        var visitor = new PermissionStatementVisitor();
        fragment.Accept(visitor);

        if (visitor.Grant is not null)
        {
            return ConvertGrant(visitor.Grant, obj, context);
        }

        if (visitor.Revoke is not null)
        {
            return ConvertRevoke(visitor.Revoke, obj, context);
        }

        if (visitor.Deny is not null)
        {
            return ConvertDeny(obj);
        }

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Permission,
            Status = ConversionStatus.Failed,
            Method = ConversionMethod.RuleBased,
            ErrorMessage = "No GRANT, REVOKE, or DENY statement found in source definition."
        };
    }

    private ConversionResult ConvertGrant(GrantStatement grant, SchemaObject obj, ConversionContext context)
    {
        var permissions = ExtractPermissions(grant.Permissions);
        var securityTarget = ExtractSecurityTarget(grant.SecurityTargetObject, context);
        var principals = ExtractPrincipals(grant.Principals);

        string ddl;
        if (grant.WithGrantOption)
        {
            ddl = $"GRANT {permissions} ON {securityTarget} TO {principals} WITH GRANT OPTION;";
        }
        else
        {
            ddl = $"GRANT {permissions} ON {securityTarget} TO {principals};";
        }

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Permission,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0
        };
    }

    private ConversionResult ConvertRevoke(RevokeStatement revoke, SchemaObject obj, ConversionContext context)
    {
        var permissions = ExtractPermissions(revoke.Permissions);
        var securityTarget = ExtractSecurityTarget(revoke.SecurityTargetObject, context);
        var principals = ExtractPrincipals(revoke.Principals);

        var ddl = $"REVOKE {permissions} ON {securityTarget} FROM {principals};";

        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Permission,
            Status = ConversionStatus.Converted,
            Method = ConversionMethod.RuleBased,
            GeneratedDdl = ddl,
            ConfidenceScore = 1.0
        };
    }

    private static ConversionResult ConvertDeny(SchemaObject obj)
    {
        return new ConversionResult
        {
            ObjectName = obj.Name,
            SchemaName = obj.SchemaName,
            ObjectType = SchemaObjectType.Permission,
            Status = ConversionStatus.Flagged,
            Method = ConversionMethod.RuleBased,
            ConfidenceScore = 0.3,
            ReviewFlags =
            [
                new ManualReviewFlag
                {
                    Reason = "DENY statement cannot be directly converted. " +
                             "PostgreSQL does not have a DENY equivalent.",
                    CodeSection = obj.SourceDefinition,
                    SuggestedAlternative = "Consider using REVOKE to remove the privilege, " +
                                           "or implement a role-based approach where the denied " +
                                           "permission is simply not granted to the target role."
                }
            ]
        };
    }

    private static string ExtractPermissions(IList<Permission> permissions)
    {
        if (permissions is null || permissions.Count == 0)
        {
            return "ALL";
        }

        var permNames = permissions.Select(p =>
        {
            if (p.Identifiers is not null && p.Identifiers.Count > 0)
            {
                var permName = string.Join(" ", p.Identifiers.Select(id => id.Value.ToUpperInvariant()));
                return MapPermissionName(permName);
            }
            return "ALL";
        });

        return string.Join(", ", permNames);
    }

    private static string MapPermissionName(string sqlServerPermission)
    {
        return sqlServerPermission.ToUpperInvariant() switch
        {
            "SELECT" => "SELECT",
            "INSERT" => "INSERT",
            "UPDATE" => "UPDATE",
            "DELETE" => "DELETE",
            "EXECUTE" => "EXECUTE",
            "REFERENCES" => "REFERENCES",
            "ALTER" => "ALL",
            "VIEW DEFINITION" => "USAGE",
            "CONTROL" => "ALL",
            _ => sqlServerPermission
        };
    }

    private static string ExtractSecurityTarget(SecurityTargetObject? target, ConversionContext context)
    {
        if (target is null)
        {
            return "SCHEMA public";
        }

        if (target.ObjectName?.MultiPartIdentifier is not null)
        {
            var identifiers = target.ObjectName.MultiPartIdentifier.Identifiers;
            if (identifiers.Count >= 2)
            {
                var schema = identifiers[0].Value;
                var objectName = identifiers[1].Value;
                var mappedSchema = MapSchema(schema, context.SchemaMappings);
                return $"{mappedSchema}.{QuoteIdentifier(objectName)}";
            }
            else if (identifiers.Count == 1)
            {
                var objectName = identifiers[0].Value;
                return $"public.{QuoteIdentifier(objectName)}";
            }
        }

        return "SCHEMA public";
    }

    private static string ExtractPrincipals(IList<SecurityPrincipal> principals)
    {
        if (principals is null || principals.Count == 0)
        {
            return "PUBLIC";
        }

        return string.Join(", ", principals.Select(p =>
        {
            if (p.Identifier is not null)
            {
                return QuoteIdentifier(p.Identifier.Value);
            }
            return "PUBLIC";
        }));
    }

    private static string MapSchema(string sourceSchema, IReadOnlyDictionary<string, string> schemaMappings)
    {
        if (schemaMappings.TryGetValue(sourceSchema, out var mapped))
        {
            return mapped;
        }

        return sourceSchema;
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (identifier.All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return identifier.ToLowerInvariant();
        }
        return $"\"{identifier}\"";
    }

    private sealed class PermissionStatementVisitor : TSqlFragmentVisitor
    {
        public GrantStatement? Grant { get; private set; }
        public RevokeStatement? Revoke { get; private set; }
        public DenyStatement? Deny { get; private set; }

        public override void Visit(GrantStatement node)
        {
            Grant ??= node;
        }

        public override void Visit(RevokeStatement node)
        {
            Revoke ??= node;
        }

        public override void Visit(DenyStatement node)
        {
            Deny ??= node;
        }
    }
}
