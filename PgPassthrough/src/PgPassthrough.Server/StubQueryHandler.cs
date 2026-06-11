using PgPassthrough.Core.Abstractions;
using PgPassthrough.Core.Models;

namespace PgPassthrough.Server;

/// <summary>
/// Placeholder query handler used until Phase 6 is fully wired.
/// 
/// - For SET/USE/non-query statements: returns just a DONE token (no result set).
/// - For SELECT statements: returns a single-column diagnostic result.
/// - For RPC requests: returns a DONE token.
/// 
/// This allows real ODBC drivers (sqlcmd, etc.) to complete their initial
/// connection setup (SET QUOTED_IDENTIFIER ON, etc.) without errors.
/// </summary>
internal sealed class StubQueryHandler : IQueryHandler
{
    public async Task HandleAsync(
        ClientRequest request,
        IResponseWriter responseWriter,
        CancellationToken cancellationToken = default)
    {
        if (request is SqlBatchRequest batch)
        {
            string sql = batch.SqlText.TrimStart();

            // Non-query statements: just acknowledge with DONE
            if (IsNonQueryStatement(sql))
            {
                await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, cancellationToken);
                return;
            }

            // SELECT statements: return a diagnostic result set
            var columns = new List<ColumnMetadata>
            {
                new()
                {
                    ColumnName = "message",
                    TypeCode   = SqlServerTypeCode.NVarChar,
                    MaxLength  = 250,
                    Ordinal    = 0
                }
            };

            await responseWriter.WriteColumnsAsync(columns, cancellationToken);
            await responseWriter.WriteRowAsync(
                new object?[] { $"[STUB] Received: {batch.SqlText[..Math.Min(200, batch.SqlText.Length)]}" },
                cancellationToken);
            await responseWriter.WriteDoneAsync(DoneStatus.Final | DoneStatus.Count, 1, cancellationToken);
        }
        else if (request is RpcRequest rpc)
        {
            // For RPC, just send a done token — phase 6 will handle sproc dispatch
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, cancellationToken);
        }
        else
        {
            await responseWriter.WriteDoneAsync(DoneStatus.Final, 0, cancellationToken);
        }
    }

    private static bool IsNonQueryStatement(string sql)
    {
        // Quick prefix check for statements that don't return result sets
        return sql.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("USE ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("BEGIN ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("IF ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("DECLARE ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("PRINT ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("GO", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("EXEC ", StringComparison.OrdinalIgnoreCase)
            || sql.StartsWith("EXECUTE ", StringComparison.OrdinalIgnoreCase);
    }
}
