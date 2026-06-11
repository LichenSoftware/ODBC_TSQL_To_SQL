using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;
using PgPassthrough.SqlParser.Ast;
using PgPassthrough.SqlParser.Parser;
using PgPassthrough.Translation.Translators;

namespace PgPassthrough.Translation;

/// <summary>
/// Production implementation of <see cref="ISqlTranslator"/>.
///
/// Pipeline:
///   1. Parse the T-SQL text into an AST.
///   2. Compute the normalised cache key.
///   3. Check the translation cache — if hit, return immediately.
///   4. Classify the statement type.
///   5. Walk the AST with <see cref="PgSqlEmitter"/> to produce PostgreSQL SQL.
///   6. Store the result in the cache.
///   7. Return the result with warnings and statement type.
///
/// This class is thread-safe. The cache is shared across all sessions.
/// Each call to <see cref="Translate"/> creates its own emitter instance
/// (visitors carry state and are not reused).
/// </summary>
public sealed class TSqlToPgTranslator : ISqlTranslator
{
    private readonly TranslationCache _cache;
    private readonly ILogger<TSqlToPgTranslator> _logger;

    public TSqlToPgTranslator(IOptions<ServerConfiguration> config, ILogger<TSqlToPgTranslator> logger)
    {
        _cache  = new TranslationCache(config.Value.Cache.MaxEntries);
        _logger = logger;
    }

    public TranslationResult Translate(string tsql, TranslationContext context)
    {
        if (string.IsNullOrWhiteSpace(tsql))
        {
            return new TranslationResult
            {
                TranslatedSql = string.Empty,
                StatementType = StatementType.Unknown
            };
        }

        // 1. Parse
        SqlBatch batch;
        try
        {
            batch = TSqlParser.Parse(tsql);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Parse failed for SQL: {Error}", ex.Message);
            return new TranslationResult
            {
                TranslatedSql = $"-- PARSE ERROR: {ex.Message}\n-- Original: {tsql}",
                StatementType = StatementType.Unknown,
                Warnings = [new TranslationWarning { Code = "PG100", Message = $"Parse error: {ex.Message}" }]
            };
        }

        // 2. Classify early — DML with literal values must not use normalised cache keys
        //    because the normaliser strips literal values.
        var stmtType = StatementClassifier.ClassifyBatch(batch);

        // 3. Compute cache key — use normalised key only for SELECT statements.
        //    For DML (INSERT/UPDATE/DELETE), use the raw SQL as the key to avoid
        //    collisions between statements that differ only in literal values.
        string cacheKey = stmtType switch
        {
            StatementType.Select => NormalisedKeyPrinter.Normalise(batch),
            _ => tsql // raw SQL preserves literal values
        };

        // 4. Cache lookup
        if (_cache.TryGet(cacheKey, out var cached))
        {
            return new TranslationResult
            {
                TranslatedSql = cached!.TranslatedSql,
                FromCache      = true,
                StatementType  = cached.StatementType,
                Warnings       = cached.Warnings
            };
        }

        // 5. Translate
        var emitter = new PgSqlEmitter(context);
        string pgSql = batch.Accept(emitter);

        // 6. Build result
        var result = new TranslationResult
        {
            TranslatedSql = pgSql,
            FromCache      = false,
            StatementType  = stmtType,
            Warnings       = emitter.Warnings
        };

        // 7. Cache
        _cache.Set(cacheKey, result);

        return result;
    }
}
