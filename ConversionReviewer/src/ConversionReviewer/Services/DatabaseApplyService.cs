using ConversionReviewer.Models;
using Npgsql;

namespace ConversionReviewer.Services;

/// <summary>
/// Applies converted DDL scripts to the target PostgreSQL database.
/// </summary>
public class DatabaseApplyService
{
    private readonly IConfiguration _configuration;
    private readonly SessionService _sessionService;

    public DatabaseApplyService(IConfiguration configuration, SessionService sessionService)
    {
        _configuration = configuration;
        _sessionService = sessionService;
    }

    public string GetConnectionString()
    {
        return _configuration.GetConnectionString("TargetPostgres")
            ?? "Host=localhost;Port=5432;Database=assessmenttestdb;Username=postgres;Password=postgres";
    }

    /// <summary>
    /// Tests the database connection.
    /// </summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string? connectionStringOverride = null)
    {
        var connStr = connectionStringOverride ?? GetConnectionString();
        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version();";
            var version = await cmd.ExecuteScalarAsync();
            return (true, $"Connected: {version}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies a single DDL script to the target database.
    /// </summary>
    public async Task<ApplyResult> ApplyScriptAsync(ConversionObject obj, string? connectionStringOverride = null)
    {
        var connStr = connectionStringOverride ?? GetConnectionString();
        var ddl = obj.Result.GeneratedDdl;

        if (string.IsNullOrWhiteSpace(ddl))
        {
            return new ApplyResult
            {
                Success = false,
                ErrorMessage = "No generated DDL to apply.",
                ObjectName = obj.DisplayName
            };
        }

        try
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = ddl;
            cmd.CommandTimeout = 30;
            await cmd.ExecuteNonQueryAsync();

            // Mark as applied
            obj.AppliedAt = DateTimeOffset.UtcNow;
            obj.AppliedSuccessfully = true;
            obj.ApplyError = null;
            await _sessionService.SaveObjectAsync(obj);

            return new ApplyResult
            {
                Success = true,
                ObjectName = obj.DisplayName
            };
        }
        catch (Exception ex)
        {
            // Mark the failure
            obj.AppliedAt = DateTimeOffset.UtcNow;
            obj.AppliedSuccessfully = false;
            obj.ApplyError = ex.Message;
            await _sessionService.SaveObjectAsync(obj);

            return new ApplyResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                ObjectName = obj.DisplayName
            };
        }
    }

    /// <summary>
    /// Applies multiple scripts in sequence (respecting the provided order).
    /// </summary>
    public async Task<List<ApplyResult>> ApplyBatchAsync(
        IEnumerable<ConversionObject> objects,
        string? connectionStringOverride = null,
        bool stopOnError = true)
    {
        var results = new List<ApplyResult>();

        foreach (var obj in objects)
        {
            var result = await ApplyScriptAsync(obj, connectionStringOverride);
            results.Add(result);

            if (!result.Success && stopOnError)
                break;
        }

        return results;
    }
}

public class ApplyResult
{
    public bool Success { get; init; }
    public string ObjectName { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}
