using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;
using AchieveAi.LmDotnetTools.OpenAIProvider.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

public class OpenClient : BaseHttpService, IOpenClient
{
    private const string ProviderName = "OpenAI";

    public static readonly JsonSerializerOptions S_jsonSerializerOptions =
        OpenAIJsonSerializerOptionsFactory.CreateForProduction();

    private readonly string _baseUrl;

    private readonly IPerformanceTracker _performanceTracker;

    public OpenClient(
        string apiKey,
        string baseUrl,
        IPerformanceTracker? performanceTracker = null,
        ILogger? logger = null
    )
        : base(logger ?? NullLogger.Instance, CreateHttpClient(apiKey, baseUrl))
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ValidationHelper.ValidateApiKey(apiKey, nameof(apiKey));
        ValidationHelper.ValidateBaseUrl(baseUrl, nameof(baseUrl));

        _baseUrl = baseUrl.TrimEnd('/');
        _performanceTracker = performanceTracker ?? new PerformanceTracker();

        // Log client initialization with provider details
        Logger.LogInformation(
            "OpenAI Client initialized - Provider: {ProviderName}, Endpoint: {BaseUrl}",
            GetProviderName(_baseUrl),
            _baseUrl
        );
    }

    public OpenClient(
        HttpClient httpClient,
        string baseUrl,
        IPerformanceTracker? performanceTracker = null,
        ILogger? logger = null
    )
        : base(logger ?? NullLogger.Instance, httpClient)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ValidationHelper.ValidateBaseUrl(baseUrl, nameof(baseUrl));

        _baseUrl = baseUrl.TrimEnd('/');
        _performanceTracker = performanceTracker ?? new PerformanceTracker();

        // Log client initialization with provider details
        Logger.LogInformation(
            "OpenAI Client initialized (with HttpClient) - Provider: {ProviderName}, Endpoint: {BaseUrl}",
            GetProviderName(_baseUrl),
            _baseUrl
        );
    }

    public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(chatCompletionRequest);

        var providerName = GetProviderName(_baseUrl);
        var requestId = Guid.NewGuid().ToString("N")[..8];

        // Log request details
        Logger.LogInformation(
            "Chat completion request - Provider: {Provider}, Model: {Model}, RequestId: {RequestId}, Endpoint: {Endpoint}",
            providerName,
            chatCompletionRequest.Model,
            requestId,
            _baseUrl
        );

        Logger.LogDebug(
            "Request details - RequestId: {RequestId}, Temperature: {Temperature}, MaxTokens: {MaxTokens}, Messages: {MessageCount}",
            requestId,
            chatCompletionRequest.Temperature,
            chatCompletionRequest.MaxTokens,
            chatCompletionRequest.Messages.Count
        );

        var metrics = RequestMetrics.StartNew(ProviderName, chatCompletionRequest.Model, "ChatCompletion");

        try
        {
            chatCompletionRequest = chatCompletionRequest with { Stream = false };

            var response = await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
                    var jsonContent = JsonSerializer.Serialize(chatCompletionRequest, S_jsonSerializerOptions);
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    Logger.LogTrace(
                        "Sending request - RequestId: {RequestId}, PayloadSize: {PayloadSize}",
                        requestId,
                        jsonContent.Length
                    );

                    return await HttpClient.SendAsync(request, cancellationToken);
                },
                async httpResponse =>
                {
                    _ = httpResponse.EnsureSuccessStatusCode();
                    var responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
                    var chatResponse =
                        await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
                            responseStream,
                            S_jsonSerializerOptions,
                            cancellationToken
                        ) ?? throw new Exception("Failed to deserialize response");

                    // Log response details
                    Logger.LogTrace(
                        "Chat completion response - Provider: {Provider}, Model: {Model}, CompletionId: {CompletionId}, RequestId: {RequestId}",
                        providerName,
                        chatResponse.Model ?? chatCompletionRequest.Model,
                        chatResponse.Id,
                        requestId
                    );

                    if (chatResponse.Usage != null)
                    {
                        Logger.LogTrace(
                            "Usage data received - RequestId: {RequestId}, CompletionId: {CompletionId}, PromptTokens: {PromptTokens}, CompletionTokens: {CompletionTokens}, TotalTokens: {TotalTokens}, HasCost: {HasCost}",
                            requestId,
                            chatResponse.Id,
                            chatResponse.Usage.PromptTokens,
                            chatResponse.Usage.CompletionTokens,
                            chatResponse.Usage.TotalTokens,
                            chatResponse.Usage.ExtraProperties?.ContainsKey("estimated_cost") == true
                        );
                    }
                    else
                    {
                        Logger.LogWarning(
                            "No usage data in response - Provider: {Provider}, Model: {Model}, CompletionId: {CompletionId}, RequestId: {RequestId}",
                            providerName,
                            chatResponse.Model ?? chatCompletionRequest.Model,
                            chatResponse.Id,
                            requestId
                        );
                    }

                    return chatResponse;
                },
                cancellationToken: cancellationToken
            );

            // Track successful request metrics
            var completedMetrics = metrics.Complete(
                200,
                response.Usage != null
                    ? new Usage
                    {
                        PromptTokens = response.Usage.PromptTokens,
                        CompletionTokens = response.Usage.CompletionTokens,
                        TotalTokens = response.Usage.TotalTokens,
                    }
                    : null
            );

            _performanceTracker.TrackRequest(completedMetrics);

            Logger.LogInformation(
                "Request completed successfully - Provider: {Provider}, Model: {Model}, CompletionId: {CompletionId}, RequestId: {RequestId}, Duration: {Duration}ms",
                providerName,
                response.Model ?? chatCompletionRequest.Model,
                response.Id,
                requestId,
                completedMetrics.Duration.TotalMilliseconds
            );

            return response;
        }
        catch (Exception ex)
        {
            // Track failed request metrics
            var completedMetrics = metrics.Complete(0, errorMessage: ex.Message, exceptionType: ex.GetType().Name);

            _performanceTracker.TrackRequest(completedMetrics);

            Logger.LogError(
                ex,
                "Request failed - Provider: {Provider}, Model: {Model}, RequestId: {RequestId}, Error: {ErrorMessage}",
                providerName,
                chatCompletionRequest.Model,
                requestId,
                ex.Message
            );

            throw;
        }
    }

    public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(chatCompletionRequest);

        var providerName = GetProviderName(_baseUrl);
        var requestId = Guid.NewGuid().ToString("N")[..8];

        // Log streaming request details
        Logger.LogInformation(
            "Streaming chat completion request - Provider: {Provider}, Model: {Model}, RequestId: {RequestId}, Endpoint: {Endpoint}",
            providerName,
            chatCompletionRequest.Model,
            requestId,
            _baseUrl
        );

        var metrics = RequestMetrics.StartNew(ProviderName, chatCompletionRequest.Model, "StreamingChatCompletion");

        chatCompletionRequest = chatCompletionRequest with { Stream = true };

        // Setup the HTTP request and get the stream
        var streamEnumerable = await SetupStreamingRequestAsync(
            chatCompletionRequest,
            metrics,
            requestId,
            cancellationToken
        );

        // Enumerate the stream and yield results
        await foreach (var response in streamEnumerable.WithCancellation(cancellationToken))
        {
            yield return response;
        }
    }

    private static HttpClient CreateHttpClient(string apiKey, string baseUrl)
    {
        return HttpClientFactory.CreateForOpenAI(apiKey, baseUrl);
    }

    /// <summary>
    ///     Determines provider name based on base URL
    /// </summary>
    private static string GetProviderName(string baseUrl)
    {
        return baseUrl.Contains("openrouter.ai") ? "OpenRouter"
            : baseUrl.Contains("api.openai.com") ? "OpenAI"
            : baseUrl.Contains("api.deepinfra.com") ? "DeepInfra"
            : baseUrl.Contains("api.together.xyz") ? "Together"
            : baseUrl.Contains("api.fireworks.ai") ? "Fireworks"
            : baseUrl.Contains("groq.com") ? "Groq"
            : baseUrl.Contains("cerebras.ai") ? "Cerebras"
            : baseUrl.Contains("api.anthropic.com") ? "Anthropic"
            : "Unknown";
    }

    private async Task<IAsyncEnumerable<ChatCompletionResponse>> SetupStreamingRequestAsync(
        ChatCompletionRequest chatCompletionRequest,
        RequestMetrics metrics,
        string requestId,
        CancellationToken cancellationToken
    )
    {
        var providerName = GetProviderName(_baseUrl);

        try
        {
            var streamResponse = await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
                    var jsonContent = JsonSerializer.Serialize(chatCompletionRequest, S_jsonSerializerOptions);
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    Logger.LogTrace(
                        "Sending streaming request - RequestId: {RequestId}, PayloadSize: {PayloadSize}",
                        requestId,
                        jsonContent.Length
                    );

                    return await HttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken
                    );
                },
                async httpResponse =>
                {
                    var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);

                    Logger.LogInformation(
                        "Streaming response started - Provider: {Provider}, Model: {Model}, RequestId: {RequestId}",
                        providerName,
                        chatCompletionRequest.Model,
                        requestId
                    );

                    return (httpResponse, stream);
                },
                cancellationToken: cancellationToken
            );

            // Track successful setup
            var successMetrics = metrics.Complete(200);
            _performanceTracker.TrackRequest(successMetrics);

            return ProcessStreamingResponseAsync(
                streamResponse.stream,
                streamResponse.httpResponse,
                requestId,
                cancellationToken
            );
        }
        catch (Exception ex)
        {
            // Track failed streaming request metrics
            var completedMetrics = metrics.Complete(0, errorMessage: ex.Message, exceptionType: ex.GetType().Name);

            _performanceTracker.TrackRequest(completedMetrics);

            Logger.LogError(
                ex,
                "Streaming request failed - Provider: {Provider}, Model: {Model}, RequestId: {RequestId}, Error: {ErrorMessage}",
                providerName,
                chatCompletionRequest.Model,
                requestId,
                ex.Message
            );

            throw;
        }
    }

    private async IAsyncEnumerable<ChatCompletionResponse> ProcessStreamingResponseAsync(
        Stream stream,
        HttpResponseMessage response,
        string requestId,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        try
        {
            await foreach (var sseItem in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                // Skip "[DONE]" event which indicates the end of the stream
                if (sseItem.Data == "[DONE]")
                {
                    break;
                }

                // Parse the SSE data as JSON into a ChatCompletionResponse
                ChatCompletionResponse? res = null;
                try
                {
                    res = JsonSerializer.Deserialize<ChatCompletionResponse>(sseItem.Data, S_jsonSerializerOptions);

                    if (res == null)
                    {
                        continue;
                    }

                    // Add default assistant role if not present
                    if (res.Choices?.Count > 0 && res.Choices[0].FinishReason != null)
                    {
                        if (res.Choices[0].Delta?.Role.HasValue != true)
                        {
                            res.Choices[0].Delta = new ChatMessage
                            {
                                Role = RoleEnum.Assistant,
                                Content =
                                    res.Choices[0].Delta?.Content ?? new Union<
                                        string,
                                        Union<TextContent, ImageContent>[]
                                    >(string.Empty),
                            };
                        }
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception($"Failed to deserialize SSE response: {ex.Message}, Data: {sseItem.Data}");
                }

                if (res != null)
                {
                    // Log streaming chunk details
                    if (res.Id != null && !string.IsNullOrEmpty(res.Id))
                    {
                        Logger.LogTrace(
                            "Streaming chunk received - RequestId: {RequestId}, CompletionId: {CompletionId}, Model: {Model}",
                            requestId,
                            res.Id,
                            res.Model
                        );
                    }

                    // Apply filtering logic to reduce noise in streaming output
                    if (ShouldSkipStreamingResponse(res))
                    {
                        continue;
                    }

                    yield return res;
                }
            }
        }
        finally
        {
            stream?.Dispose();
            response?.Dispose();
        }
    }

    /// <summary>
    ///     Determines whether a streaming response should be skipped to reduce noise.
    /// </summary>
    /// <param name="response">The streaming response to evaluate</param>
    /// <returns>True if the response should be skipped, false otherwise</returns>
    private static bool ShouldSkipStreamingResponse(ChatCompletionResponse response)
    {
        // Skip responses with zero-token usage ONLY if they don't have finish_reason
        // The final usage message (with non-zero tokens) carries cost information and should be preserved
        if (IsNoneUsage(response.Usage))
        {
            // Zero-token usage entries are uninformative and unnecessarily bloat the stream.
            // Providers often emit dozens or hundreds of these fragments before the real
            // (non-zero) usage summary arrives.  We therefore drop **all** zero-token usage
            // deltas regardless of finish_reason to keep the streaming output compact.

            return true;
        }

        // Skip responses with no useful information: empty content, reasoning, and tool calls.
        if (response.Choices?.Count > 0)
        {
            var delta = response.Choices[0].Delta;
            if (delta != null)
            {
                // Check if content is empty or null
                var hasContent = false;
                if (delta.Content != null)
                {
                    if (delta.Content.Is<string>())
                    {
                        var contentStr = delta.Content.Get<string>();
                        hasContent = !string.IsNullOrEmpty(contentStr);
                    }
                    else
                    {
                        // For non-string content (like image arrays), consider it as having content
                        hasContent = true;
                    }
                }

                // Check if reasoning is empty or null
                var hasReasoning =
                    !string.IsNullOrEmpty(delta.Reasoning) || !string.IsNullOrEmpty(delta.ReasoningContent);

                // Check if there are tool calls present
                var hasToolCalls = delta.ToolCalls?.Count > 0;

                // Skip if both content and reasoning are empty
                if (!hasContent && !hasReasoning && !hasToolCalls)
                {
                    return IsNoneUsage(response.Usage);
                }
            }
        }

        return false; // Don't skip this response
    }

    private static bool IsNoneUsage(OpenAIProviderUsage? usage)
    {
        return usage != null && usage.PromptTokens == 0 && usage.CompletionTokens == 0 && usage.TotalTokens == 0;
    }
}
