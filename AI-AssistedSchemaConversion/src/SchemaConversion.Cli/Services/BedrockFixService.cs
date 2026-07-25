using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace SchemaConversion.Cli.Services;

/// <summary>
/// Sends failed PostgreSQL DDL scripts along with the error message to AWS Bedrock
/// and requests a corrected version of the script.
/// Adapted from ConversionReviewer.Services.BedrockFixService for CLI usage.
/// </summary>
public class BedrockFixService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BedrockFixService> _logger;

    public BedrockFixService(IConfiguration configuration, ILogger<BedrockFixService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Submits the failed DDL script and error message to Bedrock and returns the corrected DDL.
    /// </summary>
    public async Task<FixResult> RequestFixAsync(string failedDdl, string errorMessage, string? sourceTSql = null)
    {
        var modelId = _configuration["Bedrock:ModelId"] ?? "anthropic.claude-sonnet-4-20250514-v1:0";
        var region = _configuration["Bedrock:Region"] ?? "us-east-1";

        var prompt = BuildPrompt(failedDdl, errorMessage, sourceTSql);

        try
        {
            using var client = new AmazonBedrockRuntimeClient(
                Amazon.RegionEndpoint.GetBySystemName(region));

            var request = new ConverseRequest
            {
                ModelId = modelId,
                Messages =
                [
                    new Message
                    {
                        Role = ConversationRole.User,
                        Content = [new ContentBlock { Text = prompt }]
                    }
                ],
                InferenceConfig = new InferenceConfiguration
                {
                    MaxTokens = 4096,
                    Temperature = 0.1F
                }
            };

            _logger.LogInformation("Sending fix request to Bedrock model {ModelId}", modelId);

            var response = await client.ConverseAsync(request);

            var responseText = response.Output.Message.Content
                .Where(c => c.Text != null)
                .Select(c => c.Text)
                .FirstOrDefault() ?? "";

            var fixedDdl = ExtractDdl(responseText);

            _logger.LogInformation("Received fix response from Bedrock ({Length} chars)", fixedDdl.Length);

            return new FixResult
            {
                Success = true,
                FixedDdl = fixedDdl,
                Explanation = ExtractExplanation(responseText)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bedrock fix request failed");
            return new FixResult
            {
                Success = false,
                ErrorMessage = $"AI fix request failed: {ex.Message}"
            };
        }
    }

    private static string BuildPrompt(string failedDdl, string errorMessage, string? sourceTSql)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a PostgreSQL expert. A DDL script failed to execute on PostgreSQL. Fix the script so it executes successfully.");
        sb.AppendLine();
        sb.AppendLine("## Failed PostgreSQL DDL Script");
        sb.AppendLine("```sql");
        sb.AppendLine(failedDdl);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Error Message from PostgreSQL");
        sb.AppendLine("```");
        sb.AppendLine(errorMessage);
        sb.AppendLine("```");

        if (!string.IsNullOrWhiteSpace(sourceTSql))
        {
            sb.AppendLine();
            sb.AppendLine("## Original T-SQL Source (for context)");
            sb.AppendLine("```sql");
            sb.AppendLine(sourceTSql);
            sb.AppendLine("```");
        }

        sb.AppendLine();
        sb.AppendLine("## Instructions");
        sb.AppendLine("1. Fix the PostgreSQL DDL so it executes without errors.");
        sb.AppendLine("2. Preserve the original intent and functionality.");
        sb.AppendLine("3. Return ONLY the corrected SQL in a ```sql code block.");
        sb.AppendLine("4. After the code block, briefly explain what was wrong and what you changed.");

        return sb.ToString();
    }

    /// <summary>
    /// Extracts the SQL from a ```sql ... ``` code block in the response.
    /// </summary>
    private static string ExtractDdl(string response)
    {
        const string sqlStart = "```sql";
        const string blockEnd = "```";

        var startIndex = response.IndexOf(sqlStart, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
        {
            startIndex = response.IndexOf(blockEnd);
            if (startIndex < 0) return response.Trim();
            startIndex += blockEnd.Length;
        }
        else
        {
            startIndex += sqlStart.Length;
        }

        var newline = response.IndexOf('\n', startIndex);
        if (newline >= 0) startIndex = newline + 1;

        var endIndex = response.IndexOf(blockEnd, startIndex);
        if (endIndex < 0) return response[startIndex..].Trim();

        return response[startIndex..endIndex].Trim();
    }

    /// <summary>
    /// Extracts any explanation text that appears after the code block.
    /// </summary>
    private static string ExtractExplanation(string response)
    {
        const string blockEnd = "```";

        var lastBlock = response.LastIndexOf(blockEnd);
        if (lastBlock < 0) return "";

        var afterBlock = response[(lastBlock + blockEnd.Length)..].Trim();
        return afterBlock;
    }
}

/// <summary>
/// Result of a Bedrock fix attempt.
/// </summary>
public class FixResult
{
    public bool Success { get; init; }
    public string FixedDdl { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}
