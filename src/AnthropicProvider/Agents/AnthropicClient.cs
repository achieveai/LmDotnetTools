using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.AnthropicProvider.Utils;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;

/// <summary>
///     Client for interacting with the Anthropic API.
/// </summary>
public class AnthropicClient : BaseHttpService, IAnthropicClient
{
    private const string DefaultBaseUrl = "https://api.anthropic.com/v1";
    private const string ProviderName = "Anthropic";
    private readonly string _baseUrl;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly IPerformanceTracker _performanceTracker;
    private readonly RetryOptions _retryOptions;

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnthropicClient" /> class.
    /// </summary>
    /// <param name="apiKey">The API key to use for authentication.</param>
    /// <param name="baseUrl">Optional base URL override. Falls back to ANTHROPIC_BASE_URL env var, then default.</param>
    /// <param name="httpClient">Optional custom HTTP client to use.</param>
    /// <param name="performanceTracker">Optional performance tracker for monitoring requests.</param>
    /// <param name="logger">Optional logger for diagnostic information.</param>
    /// <param name="retryOptions">Optional retry configuration options.</param>
    public AnthropicClient(
        string apiKey,
        string? baseUrl = null,
        HttpClient? httpClient = null,
        IPerformanceTracker? performanceTracker = null,
        ILogger? logger = null,
        RetryOptions? retryOptions = null
    )
        : base(logger ?? NullLogger.Instance, httpClient ?? CreateHttpClient(apiKey, baseUrl))
    {
        ValidationHelper.ValidateApiKey(apiKey, nameof(apiKey));

        _baseUrl = ResolveBaseUrl(baseUrl);
        _performanceTracker = performanceTracker ?? new PerformanceTracker();
        _jsonOptions = AnthropicJsonSerializerOptionsFactory.CreateForProduction();
        _retryOptions = retryOptions ?? RetryOptions.Default;

        Logger.LogTrace(
            "AnthropicClient initialized (apiKey ctor) - BaseUrl: {BaseUrl}, ExplicitBaseUrl: {ExplicitBaseUrl}, HttpClientProvided: {HttpClientProvided}",
            _baseUrl,
            baseUrl,
            httpClient != null
        );
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="AnthropicClient" /> class with a pre-configured HTTP client.
    /// </summary>
    /// <param name="httpClient">Pre-configured HTTP client with authentication headers.</param>
    /// <param name="baseUrl">Optional base URL override. Falls back to ANTHROPIC_BASE_URL env var, then default.</param>
    /// <param name="performanceTracker">Optional performance tracker for monitoring requests.</param>
    /// <param name="logger">Optional logger for diagnostic information.</param>
    /// <param name="retryOptions">Optional retry configuration options.</param>
    public AnthropicClient(
        HttpClient httpClient,
        string? baseUrl = null,
        IPerformanceTracker? performanceTracker = null,
        ILogger? logger = null,
        RetryOptions? retryOptions = null
    )
        : base(logger ?? NullLogger.Instance, httpClient)
    {
        _baseUrl = ResolveBaseUrl(baseUrl);
        _performanceTracker = performanceTracker ?? new PerformanceTracker();
        _jsonOptions = AnthropicJsonSerializerOptionsFactory.CreateForProduction();
        _retryOptions = retryOptions ?? RetryOptions.Default;

        Logger.LogTrace(
            "AnthropicClient initialized (httpClient ctor) - BaseUrl: {BaseUrl}, ExplicitBaseUrl: {ExplicitBaseUrl}",
            _baseUrl,
            baseUrl
        );
    }

    /// <inheritdoc />
    public async Task<AnthropicResponse> CreateChatCompletionsAsync(
        AnthropicRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var metrics = RequestMetrics.StartNew(ProviderName, request.Model, "ChatCompletion");

        try
        {
            ValidationHelper.ValidateMessages(request.Messages, nameof(request.Messages));

            var response = await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    var requestUrl = $"{_baseUrl}/messages";
                    Logger.LogTrace("Constructing chat completion request to {RequestUrl}", requestUrl);

                    var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                    {
                        Content = content,
                    };

                    // Add beta headers for built-in tools that require them
                    AddBetaHeaders(requestMessage, request);

                    Logger.LogDebug("Sending Anthropic chat completion request for model {Model}", request.Model);
                    return await HttpClient.SendAsync(requestMessage, cancellationToken);
                },
                async httpResponse =>
                {
                    _ = httpResponse.EnsureSuccessStatusCode();
                    var responseContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
                    var anthropicResponse =
                        JsonSerializer.Deserialize<AnthropicResponse>(responseContent, _jsonOptions)
                        ?? throw new InvalidOperationException("Failed to deserialize Anthropic API response");

                    Logger.LogDebug(
                        "Received Anthropic response with {ContentCount} content blocks",
                        anthropicResponse.Content?.Count ?? 0
                    );

                    return anthropicResponse;
                },
                _retryOptions,
                cancellationToken
            );

            // Track successful request metrics
            var completedMetrics = metrics.Complete(
                statusCode: 200,
                usage: response.Usage != null
                    ? new Usage
                    {
                        PromptTokens = response.Usage.InputTokens,
                        CompletionTokens = response.Usage.OutputTokens,
                        TotalTokens = response.Usage.InputTokens + response.Usage.OutputTokens,
                    }
                    : null
            );

            _performanceTracker.TrackRequest(completedMetrics);

            return response;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in Anthropic chat completion request for model {Model}", request.Model);

            // Track failed request metrics
            var completedMetrics = metrics.Complete(0, errorMessage: ex.Message, exceptionType: ex.GetType().Name);

            _performanceTracker.TrackRequest(completedMetrics);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
        AnthropicRequest request,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        var metrics = RequestMetrics.StartNew(ProviderName, request.Model, "StreamingChatCompletion");

        try
        {
            ValidationHelper.ValidateMessages(request.Messages, nameof(request.Messages));

            // Set the streaming flag
            request = request with
            {
                Stream = true,
            };

            var streamResponse = await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
                    Logger.LogTrace("Anthropic streaming request body: {RequestBody}", requestJson);
                    var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

                    var requestUrl = $"{_baseUrl}/messages";
                    Logger.LogTrace("Constructing streaming request to {RequestUrl}", requestUrl);

                    var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUrl)
                    {
                        Content = content,
                    };

                    // Add beta headers for built-in tools that require them
                    AddBetaHeaders(requestMessage, request);

                    Logger.LogDebug(
                        "Sending Anthropic streaming chat completion request for model {Model}",
                        request.Model
                    );
                    return await HttpClient.SendAsync(
                        requestMessage,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken
                    );
                },
                httpResponse => Task.FromResult(httpResponse.Content),
                _retryOptions,
                cancellationToken
            );

            // Track successful setup
            var successMetrics = metrics.Complete(200);
            _performanceTracker.TrackRequest(successMetrics);

            Logger.LogDebug("Successfully established streaming connection for model {Model}", request.Model);

            return StreamData(streamResponse, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.LogError(
                ex,
                "Error in Anthropic streaming chat completion request for model {Model}",
                request.Model
            );

            // Track failed streaming request metrics
            var completedMetrics = metrics.Complete(0, errorMessage: ex.Message, exceptionType: ex.GetType().Name);

            _performanceTracker.TrackRequest(completedMetrics);

            var error = ex.Data.Contains("ResponseContent") ? ex.Data["ResponseContent"]?.ToString() : null;
            if (!string.IsNullOrEmpty(error))
            {
                throw new InvalidOperationException(
                    $"Error processing Anthropic streaming request for model {request.Model}. Response: {error}",
                    ex
                );
            }

            throw;
        }
    }

    /// <summary>
    ///     Resolves the base URL from explicit parameter, environment variable, or default.
    ///     The client appends /messages to this URL, so it should include the full API path prefix
    ///     (e.g. "https://api.anthropic.com/v1" or "https://api.kimi.com/coding").
    /// </summary>
    private static string ResolveBaseUrl(string? explicitBaseUrl)
    {
        if (!string.IsNullOrEmpty(explicitBaseUrl))
        {
            return explicitBaseUrl.TrimEnd('/');
        }

        var envUrl = Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL");
        return !string.IsNullOrEmpty(envUrl) ? envUrl.TrimEnd('/') : DefaultBaseUrl;
    }

    private static HttpClient CreateHttpClient(string apiKey, string? baseUrl = null)
    {
        var resolvedUrl = ResolveBaseUrl(baseUrl);
        var headers = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" };
        return HttpClientFactory.CreateForAnthropic(apiKey, resolvedUrl, null, headers);
    }

    /// <summary>
    ///     Adds required beta headers for built-in tools that require them.
    /// </summary>
    /// <param name="request">The HTTP request message to add headers to.</param>
    /// <param name="anthropicRequest">The Anthropic request to check for built-in tools.</param>
    private static void AddBetaHeaders(HttpRequestMessage request, AnthropicRequest anthropicRequest)
    {
        if (anthropicRequest.Tools == null || anthropicRequest.Tools.Count == 0)
        {
            return;
        }

        var betaFeatures = new List<string>();

        foreach (var tool in anthropicRequest.Tools)
        {
            // Check for web_fetch tool (requires beta header)
            if (tool is AnthropicWebFetchTool)
            {
                betaFeatures.Add("web-fetch-2025-09-10");
            }
            // Check for code_execution tool (requires beta header)
            else if (tool is AnthropicCodeExecutionTool)
            {
                betaFeatures.Add("code-execution-2025-08-25");
            }
        }

        if (betaFeatures.Count > 0)
        {
            // Remove duplicates and join
            var uniqueFeatures = betaFeatures.Distinct().ToList();
            _ = request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", uniqueFeatures));
        }
    }

    private async IAsyncEnumerable<AnthropicStreamEvent> StreamData(
        HttpContent content,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken);

        await foreach (var sseItem in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            // Skip empty data or check for stream end
            if (string.IsNullOrEmpty(sseItem.Data) || sseItem.Data == "[DONE]")
            {
                continue;
            }

            // Parse the event data based on the event type
            AnthropicStreamEvent? eventData = null;
            try
            {
                // First try to deserialize as the base AnthropicStreamEvent to get the type
                var baseEvent = JsonSerializer.Deserialize<AnthropicStreamEvent>(sseItem.Data, _jsonOptions);

                if (baseEvent == null)
                {
                    continue;
                }

                // Then deserialize to the appropriate specialized type based on the event type
                eventData = baseEvent.Type switch
                {
                    "message_start" => JsonSerializer.Deserialize<AnthropicMessageStartEvent>(
                        sseItem.Data,
                        _jsonOptions
                    ),
                    "content_block_start" => JsonSerializer.Deserialize<AnthropicContentBlockStartEvent>(
                        sseItem.Data,
                        _jsonOptions
                    ),
                    "content_block_delta" => JsonSerializer.Deserialize<AnthropicContentBlockDeltaEvent>(
                        sseItem.Data,
                        _jsonOptions
                    ),
                    "content_block_stop" => JsonSerializer.Deserialize<AnthropicContentBlockStopEvent>(
                        sseItem.Data,
                        _jsonOptions
                    ),
                    "message_delta" => JsonSerializer.Deserialize<AnthropicMessageDeltaEvent>(
                        sseItem.Data,
                        _jsonOptions
                    ),
                    "message_stop" => JsonSerializer.Deserialize<AnthropicMessageStopEvent>(sseItem.Data, _jsonOptions),
                    "ping" => JsonSerializer.Deserialize<AnthropicPingEvent>(sseItem.Data, _jsonOptions),
                    "error" => JsonSerializer.Deserialize<AnthropicErrorEvent>(sseItem.Data, _jsonOptions),
                    _ => baseEvent, // Use the base event if type is unknown
                };
            }
            catch (JsonException ex)
            {
                // Log exception and continue
                Logger.LogWarning(ex, "Error parsing SSE data: {Data}", sseItem.Data);
                continue;
            }

            // Return the event data if it was successfully parsed
            if (eventData != null)
            {
                yield return eventData;
            }
        }
    }
}
