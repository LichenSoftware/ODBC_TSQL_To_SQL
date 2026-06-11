using PgPassthrough.Core.Models;

namespace PgPassthrough.SqlParser.Ast;

/// <summary>
/// Inspects a <see cref="SqlStatement"/> and returns its <see cref="StatementType"/>.
/// Used by the translation pipeline to decide whether to call ExecuteQuery
/// or ExecuteNonQuery on the backend, and to set the TDS DONE token's curCmd field.
/// </summary>
public static class StatementClassifier
{
    public static StatementType Classify(SqlStatement statement) => statement switch
    {
        SelectStatement           => StatementType.Select,
        InsertStatement           => StatementType.Insert,
        UpdateStatement           => StatementType.Update,
        DeleteStatement           => StatementType.Delete,
        TruncateTableStatement    => StatementType.Delete,   // behaves like DELETE for row-count
        CreateTableStatement      => StatementType.Ddl,
        DropTableStatement        => StatementType.Ddl,
        BeginTransactionStatement => StatementType.Transaction,
        CommitTransactionStatement => StatementType.Transaction,
        RollbackTransactionStatement => StatementType.Transaction,
        SaveTransactionStatement  => StatementType.Transaction,
        SetOptionStatement        => StatementType.SetOption,
        UseDatabaseStatement      => StatementType.Use,
        ExecuteStatement          => StatementType.StoredProcedure,
        _                         => StatementType.Unknown
    };

    /// <summary>
    /// Classifies a batch that may contain multiple statements.
    /// Returns <see cref="StatementType.Batch"/> if more than one meaningful statement exists,
    /// otherwise the type of the single statement.
    /// </summary>
    public static StatementType ClassifyBatch(SqlBatch batch)
    {
        // Filter out SET options — they don't count as "real" statements for classification
        var meaningful = batch.Statements
            .Where(s => s is not SetOptionStatement)
            .ToList();

        return meaningful.Count switch
        {
            0 => StatementType.Unknown,
            1 => Classify(meaningful[0]),
            _ => StatementType.Batch
        };
    }
}
