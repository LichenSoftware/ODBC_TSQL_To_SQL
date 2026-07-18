using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace MigrationValidation.Runner;

/// <summary>
/// Executes individual validation tests against the target database connection.
/// </summary>
public class ValidationRunner
{
    private readonly string _connectionString;
    private readonly int _timeoutSeconds;
    private readonly bool _verbose;

    public ValidationRunner(string connectionString, int timeoutSeconds, bool verbose)
    {
        _connectionString = connectionString;
        _timeoutSeconds = timeoutSeconds;
        _verbose = verbose;
    }

    public async Task<TestResult> ExecuteTestAsync(ValidationTest test)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync();

            // Set QUOTED_IDENTIFIER ON at connection level - required for XML index operations
            await using (var setCmd = connection.CreateCommand())
            {
                setCmd.CommandText = "SET QUOTED_IDENTIFIER ON; SET ANSI_NULLS ON;";
                await setCmd.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandTimeout = _timeoutSeconds;
            command.CommandText = test.Sql;
            command.CommandType = test.IsStoredProcedure
                ? System.Data.CommandType.StoredProcedure
                : System.Data.CommandType.Text;

            // Add parameters if any
            foreach (var param in test.Parameters)
            {
                var sqlParam = command.CreateParameter();
                sqlParam.ParameterName = param.Key;
                sqlParam.Value = param.Value ?? DBNull.Value;

                if (param.IsOutput)
                {
                    sqlParam.Direction = System.Data.ParameterDirection.Output;
                    sqlParam.DbType = param.DbType;
                    sqlParam.Size = param.Size > 0 ? param.Size : 0;
                }

                command.Parameters.Add(sqlParam);
            }

            int? rowCount = null;

            if (test.ExpectsResults)
            {
                await using var reader = await command.ExecuteReaderAsync();
                int rows = 0;
                while (await reader.ReadAsync())
                {
                    rows++;
                }
                rowCount = rows;

                // Check minimum row expectation
                if (test.MinExpectedRows.HasValue && rows < test.MinExpectedRows.Value)
                {
                    sw.Stop();
                    return new TestResult
                    {
                        TestName = test.Name,
                        Passed = false,
                        ElapsedMs = sw.ElapsedMilliseconds,
                        RowCount = rows,
                        ErrorMessage = $"Expected at least {test.MinExpectedRows} rows, got {rows}"
                    };
                }
            }
            else
            {
                var affected = await command.ExecuteNonQueryAsync();
                rowCount = affected >= 0 ? affected : null;
            }

            sw.Stop();
            return new TestResult
            {
                TestName = test.Name,
                Passed = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                RowCount = rowCount
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new TestResult
            {
                TestName = test.Name,
                Passed = false,
                ElapsedMs = sw.ElapsedMilliseconds,
                ErrorMessage = ex.Message
            };
        }
    }
}
