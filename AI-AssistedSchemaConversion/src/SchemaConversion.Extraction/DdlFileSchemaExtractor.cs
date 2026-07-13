using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaConversion.Core.Interfaces;
using SchemaConversion.Core.Models;
using SchemaConversion.Core.Options;

namespace SchemaConversion.Extraction;

/// <summary>
/// Extracts schema objects from .sql DDL files using the ScriptDom T-SQL parser.
/// </summary>
public sealed class DdlFileSchemaExtractor : ISchemaExtractor
{
    private readonly ILogger<DdlFileSchemaExtractor> _logger;

    public DdlFileSchemaExtractor(ILogger<DdlFileSchemaExtractor> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<SchemaObject>> ExtractAsync(
        SchemaExtractionOptions options, CancellationToken ct)
    {
        if (options.FilePaths is null || options.FilePaths.Count == 0)
        {
            throw new ArgumentException(
                "FilePaths is required for DDL file extraction.", nameof(options));
        }

        _logger.LogInformation("Beginning DDL file extraction from {Count} file(s).",
            options.FilePaths.Count);

        var objects = new List<SchemaObject>();

        foreach (var filePath in options.FilePaths)
        {
            ct.ThrowIfCancellationRequested();

            ValidateFilePath(filePath);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found, skipping: {FilePath}", filePath);
                continue;
            }

            var fileObjects = await ParseFileAsync(filePath, ct);
            objects.AddRange(fileObjects);
        }

        // Filter by schema if requested
        if (options.IncludeSchemas is { Count: > 0 })
        {
            objects = objects
                .Where(o => options.IncludeSchemas.Contains(
                    o.SchemaName, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        _logger.LogInformation("Extracted {Count} schema objects from DDL files.", objects.Count);

        return objects;
    }

    /// <summary>
    /// Validates a file path to prevent directory traversal attacks.
    /// Rejects paths containing ".." segments or rooted paths outside a reasonable scope.
    /// </summary>
    internal static void ValidateFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be empty.", nameof(filePath));
        }

        // Reject paths with directory traversal sequences
        var normalized = filePath.Replace('\\', '/');
        if (normalized.Contains("/../") ||
            normalized.StartsWith("../") ||
            normalized.EndsWith("/..") ||
            normalized == "..")
        {
            throw new ArgumentException(
                $"File path contains directory traversal sequence and is not allowed: {filePath}",
                nameof(filePath));
        }

        // Also check the segments individually for ".."
        var segments = normalized.Split('/');
        if (segments.Any(s => s == ".."))
        {
            throw new ArgumentException(
                $"File path contains directory traversal sequence and is not allowed: {filePath}",
                nameof(filePath));
        }
    }

    private async Task<List<SchemaObject>> ParseFileAsync(string filePath, CancellationToken ct)
    {
        var objects = new List<SchemaObject>();

        var sql = await File.ReadAllTextAsync(filePath, ct);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);

        if (errors.Count > 0)
        {
            _logger.LogWarning(
                "Parse errors in {FilePath}: {ErrorCount} error(s). Attempting partial extraction.",
                filePath, errors.Count);
        }

        if (fragment is TSqlScript script)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                {
                    var extracted = ExtractFromStatement(statement, sql);
                    if (extracted is not null)
                    {
                        objects.Add(extracted);
                    }
                }
            }
        }

        return objects;
    }

    private SchemaObject? ExtractFromStatement(TSqlStatement statement, string fullSql)
    {
        return statement switch
        {
            CreateTableStatement create => ExtractTable(create, fullSql),
            CreateViewStatement create => ExtractView(create, fullSql),
            CreateProcedureStatement create => ExtractProcedure(create, fullSql),
            CreateFunctionStatement create => ExtractFunction(create, fullSql),
            CreateTriggerStatement create => ExtractTrigger(create, fullSql),
            CreateSequenceStatement create => ExtractSequence(create, fullSql),
            CreateTypeTableStatement create => ExtractUserDefinedType(create, fullSql),
            CreateTypeUdtStatement create => ExtractUserDefinedType(create, fullSql),
            _ => null
        };
    }

    private SchemaObject ExtractTable(CreateTableStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.SchemaObjectName);
        var definition = GetStatementText(stmt, fullSql);
        var dependencies = ExtractTableDependencies(stmt);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.Table,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = dependencies
        };
    }

    private SchemaObject ExtractView(CreateViewStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.SchemaObjectName);
        var definition = GetStatementText(stmt, fullSql);
        var dependencies = ExtractReferencedObjects(stmt);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.View,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = dependencies
        };
    }

    private SchemaObject ExtractProcedure(CreateProcedureStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.ProcedureReference.Name);
        var definition = GetStatementText(stmt, fullSql);
        var dependencies = ExtractReferencedObjects(stmt);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.StoredProcedure,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = dependencies
        };
    }

    private SchemaObject ExtractFunction(CreateFunctionStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.Name);
        var definition = GetStatementText(stmt, fullSql);
        var dependencies = ExtractReferencedObjects(stmt);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.Function,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = dependencies
        };
    }

    private SchemaObject ExtractTrigger(CreateTriggerStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.Name);
        var definition = GetStatementText(stmt, fullSql);
        var dependencies = ExtractReferencedObjects(stmt);

        // Add the target table as a dependency
        if (stmt.TriggerObject?.Name is not null)
        {
            var (targetSchema, targetName) = GetSchemaAndName(stmt.TriggerObject.Name);
            var targetRef = $"{targetSchema}.{targetName}";
            if (!dependencies.Contains(targetRef, StringComparer.OrdinalIgnoreCase))
            {
                dependencies = [.. dependencies, targetRef];
            }
        }

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.Trigger,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = dependencies
        };
    }

    private SchemaObject ExtractSequence(CreateSequenceStatement stmt, string fullSql)
    {
        var (schemaName, objectName) = GetSchemaAndName(stmt.Name);
        var definition = GetStatementText(stmt, fullSql);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.Sequence,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = []
        };
    }

    private SchemaObject ExtractUserDefinedType(TSqlStatement stmt, string fullSql)
    {
        string schemaName;
        string objectName;

        if (stmt is CreateTypeTableStatement tableType)
        {
            (schemaName, objectName) = GetSchemaAndName(tableType.Name);
        }
        else if (stmt is CreateTypeUdtStatement udtType)
        {
            (schemaName, objectName) = GetSchemaAndName(udtType.Name);
        }
        else
        {
            throw new InvalidOperationException("Unexpected statement type for UDT extraction.");
        }

        var definition = GetStatementText(stmt, fullSql);

        return new SchemaObject
        {
            Name = objectName,
            SchemaName = schemaName,
            ObjectType = SchemaObjectType.UserDefinedType,
            SourceDefinition = definition,
            SourceDefinitionHash = ComputeHash(definition),
            DependsOn = []
        };
    }

    private static (string SchemaName, string ObjectName) GetSchemaAndName(SchemaObjectName? name)
    {
        if (name is null)
        {
            return ("dbo", "Unknown");
        }

        var schemaName = name.SchemaIdentifier?.Value ?? "dbo";
        var objectName = name.BaseIdentifier?.Value ?? "Unknown";
        return (schemaName, objectName);
    }

    private static string GetStatementText(TSqlFragment fragment, string fullSql)
    {
        var start = fragment.StartOffset;
        var length = fragment.FragmentLength;

        if (start >= 0 && length > 0 && start + length <= fullSql.Length)
        {
            return fullSql.Substring(start, length);
        }

        return string.Empty;
    }

    private static List<string> ExtractTableDependencies(CreateTableStatement stmt)
    {
        var deps = new List<string>();

        if (stmt.Definition?.TableConstraints is not null)
        {
            foreach (var constraint in stmt.Definition.TableConstraints)
            {
                if (constraint is ForeignKeyConstraintDefinition fk &&
                    fk.ReferenceTableName is not null)
                {
                    var (schema, name) = GetSchemaAndName(fk.ReferenceTableName);
                    deps.Add($"{schema}.{name}");
                }
            }
        }

        // Check column-level FK constraints
        if (stmt.Definition?.ColumnDefinitions is not null)
        {
            foreach (var col in stmt.Definition.ColumnDefinitions)
            {
                if (col.Constraints is not null)
                {
                    foreach (var constraint in col.Constraints)
                    {
                        if (constraint is ForeignKeyConstraintDefinition fk &&
                            fk.ReferenceTableName is not null)
                        {
                            var (schema, name) = GetSchemaAndName(fk.ReferenceTableName);
                            deps.Add($"{schema}.{name}");
                        }
                    }
                }
            }
        }

        return deps.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> ExtractReferencedObjects(TSqlFragment fragment)
    {
        var visitor = new ObjectReferenceVisitor();
        fragment.Accept(visitor);
        return visitor.References.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string ComputeHash(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>
    /// AST visitor that collects all named object references from a T-SQL fragment.
    /// </summary>
    private sealed class ObjectReferenceVisitor : TSqlFragmentVisitor
    {
        public List<string> References { get; } = [];

        public override void Visit(NamedTableReference node)
        {
            if (node.SchemaObject is not null)
            {
                var (schema, name) = GetSchemaAndName(node.SchemaObject);
                References.Add($"{schema}.{name}");
            }
            base.Visit(node);
        }

        public override void Visit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget multiPart)
            {
                var identifiers = multiPart.MultiPartIdentifier?.Identifiers;
                if (identifiers is { Count: >= 2 })
                {
                    var schema = identifiers[0].Value;
                    var name = identifiers[1].Value;
                    References.Add($"{schema}.{name}");
                }
            }
            base.Visit(node);
        }

        public override void Visit(ExecutableProcedureReference node)
        {
            if (node.ProcedureReference?.ProcedureReference?.Name is not null)
            {
                var (schema, name) = GetSchemaAndName(
                    node.ProcedureReference.ProcedureReference.Name);
                References.Add($"{schema}.{name}");
            }
            base.Visit(node);
        }
    }
}
