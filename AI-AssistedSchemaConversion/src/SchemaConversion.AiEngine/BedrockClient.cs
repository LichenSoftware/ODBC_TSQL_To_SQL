using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Microsoft.Extensions.Logging;
using SchemaConversion.Core.Options;

namespace SchemaConversion.AiEngine;

/// <summary>
/// Communicates with Amazon Bedrock using the InvokeModel API.
/// Implements exponential backoff retry on throttling, server errors, timeouts, and malformed responses.
/// </summary>
public sealed class BedrockClient : IDisposable
{
    private readonly AmazonBedrockRuntimeClient _client;
    private readonly BedrockClientOptions _options;
    private readonly ILogger<BedrockClient> _logger;
    private bool _disposed;

    public BedrockClient(BedrockClientOptions options, ILogger<BedrockClient> logger)
    {
        ValidateOptions(options);
        _options = options;
        _logger = logger;
        _client = new AmazonBedrockRuntimeClient();
    }

    /// <summary>
    /// Invokes the Bedrock model with the given prompt and returns the raw response text.
    /// Retries with exponential backoff on transient failures.
    /// </summary>
    public async Task<string> InvokeModelAsync(string prompt, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);

        var attempt = 0;
        var baseDelay = TimeSpan.FromSeconds(2);

        while (true)
        {
            attempt++;
            try
            {
                _logger.LogDebug("Invoking Bedrock model {ModelId}, attempt {Attempt}", _options.ModelId, attempt);

                var response = await InvokeWithTimeoutAsync(prompt, ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(response))
                {
                    if (attempt >= _options.MaxRetryAttempts)
                    {
                        _logger.LogWarning("Empty response from model after {Attempts} attempts", attempt);
                        return string.Empty;
                    }

                    _logger.LogWarning("Empty response from model on attempt {Attempt}, retrying", attempt);
                    await DelayWithBackoff(attempt, baseDelay, ct).ConfigureAwait(false);
                    continue;
                }

                _logger.LogDebug("Successfully received response from model on attempt {Attempt}", attempt);
                return response;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Timeout from our internal CTS
                if (attempt >= _options.MaxRetryAttempts)
                {
                    _logger.LogError("Bedrock request timed out after {Attempts} attempts", attempt);
                    throw new TimeoutException(
                        $"Bedrock model invocation timed out after {_options.MaxRetryAttempts} attempts.");
                }

                _logger.LogWarning("Bedrock request timed out on attempt {Attempt}, retrying", attempt);
                await DelayWithBackoff(attempt, baseDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Caller-requested cancellation — do not retry
                throw;
            }
            catch (AmazonBedrockRuntimeException ex) when (IsRetryableStatusCode(ex))
            {
                if (attempt >= _options.MaxRetryAttempts)
                {
                    _logger.LogError("Bedrock request failed after {Attempts} attempts with status {Status}",
                        attempt, ex.StatusCode);
                    throw;
                }

                _logger.LogWarning("Bedrock returned {Status} on attempt {Attempt}, retrying",
                    ex.StatusCode, attempt);
                await DelayWithBackoff(attempt, baseDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string> InvokeWithTimeoutAsync(string prompt, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_options.Timeout);

        var request = BuildRequest(prompt);
        var response = await _client.InvokeModelAsync(request, timeoutCts.Token).ConfigureAwait(false);

        using var reader = new StreamReader(response.Body, Encoding.UTF8);
        var responseBody = await reader.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);

        return ExtractTextFromResponse(responseBody);
    }

    private InvokeModelRequest BuildRequest(string prompt)
    {
        // Build a request body suitable for Claude/Anthropic models on Bedrock
        var requestBody = new
        {
            anthropic_version = "bedrock-2023-05-31",
            max_tokens = _options.MaxOutputTokens,
            temperature = _options.Temperature,
            messages = new[]
            {
                new { role = "user", content = prompt }
            }
        };

        var json = JsonSerializer.Serialize(requestBody, SerializerOptions);

        return new InvokeModelRequest
        {
            ModelId = _options.ModelId,
            ContentType = "application/json",
            Accept = "application/json",
            Body = new MemoryStream(Encoding.UTF8.GetBytes(json))
        };
    }

    private static string ExtractTextFromResponse(string responseBody)
    {
        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        // Anthropic Claude response format
        if (root.TryGetProperty("content", out var contentArray) &&
            contentArray.ValueKind == JsonValueKind.Array &&
            contentArray.GetArrayLength() > 0)
        {
            var firstBlock = contentArray[0];
            if (firstBlock.TryGetProperty("text", out var textProp))
            {
                return textProp.GetString() ?? string.Empty;
            }
        }

        // Fallback: try to get a top-level "completion" field (older format)
        if (root.TryGetProperty("completion", out var completionProp))
        {
            return completionProp.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static bool IsRetryableStatusCode(AmazonBedrockRuntimeException ex)
    {
        return ex.StatusCode == HttpStatusCode.TooManyRequests ||
               (int)ex.StatusCode >= 500;
    }

    private static async Task DelayWithBackoff(int attempt, TimeSpan baseDelay, CancellationToken ct)
    {
        var delay = TimeSpan.FromTicks(baseDelay.Ticks * (long)Math.Pow(2, attempt - 1));
        await Task.Delay(delay, ct).ConfigureAwait(false);
    }

    private static void ValidateOptions(BedrockClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ModelId))
            throw new ArgumentException("ModelId is required.", nameof(options));

        if (options.Temperature < 0.0 || options.Temperature > 1.0)
            throw new ArgumentOutOfRangeException(nameof(options), "Temperature must be between 0.0 and 1.0.");

        if (options.MaxRetryAttempts < 1 || options.MaxRetryAttempts > 10)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxRetryAttempts must be between 1 and 10.");

        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be greater than zero.");

        if (options.MaxOutputTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxOutputTokens must be positive.");
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public void Dispose()
    {
        if (!_disposed)
        {
            _client.Dispose();
            _disposed = true;
        }
    }
}
