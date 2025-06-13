using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.ServerSentEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmCore.Http;
using AchieveAi.LmDotnetTools.LmCore.Validation;
using AchieveAi.LmDotnetTools.LmCore.Performance;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.OpenAIProvider.Agents;

public class OpenClient : BaseHttpService, IOpenClient
{
    public readonly static JsonSerializerOptions S_jsonSerializerOptions = CreateJsonSerializerOptions();

    private readonly IPerformanceTracker _performanceTracker;
    private readonly string _baseUrl;
    private const string ProviderName = "OpenAI";

    public OpenClient(string apiKey, string baseUrl, IPerformanceTracker? performanceTracker = null, ILogger? logger = null) 
        : base(logger ?? NullLogger.Instance, CreateHttpClient(apiKey, baseUrl))
    {
        ValidationHelper.ValidateApiKey(apiKey, nameof(apiKey));
        ValidationHelper.ValidateBaseUrl(baseUrl, nameof(baseUrl));
        
        _baseUrl = baseUrl.TrimEnd('/');
        _performanceTracker = performanceTracker ?? new PerformanceTracker();
    }

    public OpenClient(HttpClient httpClient, string baseUrl, IPerformanceTracker? performanceTracker = null, ILogger? logger = null) 
        : base(logger ?? NullLogger.Instance, httpClient)
    {
        ValidationHelper.ValidateBaseUrl(baseUrl, nameof(baseUrl));
        
        _baseUrl = baseUrl.TrimEnd('/');
        _performanceTracker = performanceTracker ?? new PerformanceTracker();
    }

    private static HttpClient CreateHttpClient(string apiKey, string baseUrl)
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrEmpty(apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
        }

        httpClient.Timeout = TimeSpan.FromMinutes(5);
        return httpClient;
    }

    public async Task<ChatCompletionResponse> CreateChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        CancellationToken cancellationToken = default)
    {
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
                    return await HttpClient.SendAsync(request, cancellationToken);
                },
                async (httpResponse) =>
                {
                    httpResponse.EnsureSuccessStatusCode();
                    var responseStream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
                    return await JsonSerializer.DeserializeAsync<ChatCompletionResponse>(
                        responseStream, S_jsonSerializerOptions, cancellationToken) 
                        ?? throw new Exception("Failed to deserialize response");
                },
                cancellationToken: cancellationToken);

            // Track successful request metrics
            var completedMetrics = metrics.Complete(
                statusCode: 200,
                usage: response.Usage != null ? new AchieveAi.LmDotnetTools.LmCore.Core.Usage
                {
                    PromptTokens = response.Usage.PromptTokens,
                    CompletionTokens = response.Usage.CompletionTokens,
                    TotalTokens = response.Usage.TotalTokens
                } : null);
            
            _performanceTracker.TrackRequest(completedMetrics);
            
            return response;
        }
        catch (Exception ex)
        {
            // Track failed request metrics
            var completedMetrics = metrics.Complete(
                statusCode: 0,
                errorMessage: ex.Message,
                exceptionType: ex.GetType().Name);
            
            _performanceTracker.TrackRequest(completedMetrics);
            throw;
        }
    }

    public async IAsyncEnumerable<ChatCompletionResponse> StreamingChatCompletionsAsync(
        ChatCompletionRequest chatCompletionRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var metrics = RequestMetrics.StartNew(ProviderName, chatCompletionRequest.Model, "StreamingChatCompletion");
        
        chatCompletionRequest = chatCompletionRequest with { Stream = true };
        
        // Setup the HTTP request and get the stream
        var streamEnumerable = await SetupStreamingRequestAsync(chatCompletionRequest, metrics, cancellationToken);
        
        // Enumerate the stream and yield results
        await foreach (var response in streamEnumerable.WithCancellation(cancellationToken))
        {
            yield return response;
        }
    }

    private async Task<IAsyncEnumerable<ChatCompletionResponse>> SetupStreamingRequestAsync(
        ChatCompletionRequest chatCompletionRequest,
        RequestMetrics metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            var streamResponse = await ExecuteHttpWithRetryAsync(
                async () =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions");
                    var jsonContent = JsonSerializer.Serialize(chatCompletionRequest, S_jsonSerializerOptions);
                    request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                    return await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                },
                async (httpResponse) =>
                {
                    var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
                    return (httpResponse, stream);
                },
                cancellationToken: cancellationToken);
            
            // Track successful setup
            var successMetrics = metrics.Complete(statusCode: 200);
            _performanceTracker.TrackRequest(successMetrics);
            
            return ProcessStreamingResponseAsync(streamResponse.stream, streamResponse.httpResponse, cancellationToken);
        }
        catch (Exception ex)
        {
            // Track failed streaming request metrics
            var completedMetrics = metrics.Complete(
                statusCode: 0,
                errorMessage: ex.Message,
                exceptionType: ex.GetType().Name);
            
            _performanceTracker.TrackRequest(completedMetrics);
            throw;
        }
    }

    private async IAsyncEnumerable<ChatCompletionResponse> ProcessStreamingResponseAsync(
        Stream stream,
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
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
                    res = JsonSerializer.Deserialize<ChatCompletionResponse>(
                        sseItem.Data,
                        S_jsonSerializerOptions);

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
                                Content = res.Choices[0].Delta?.Content ?? new Union<string, Union<TextContent, ImageContent>[]>(string.Empty)
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

    private static JsonSerializerOptions CreateJsonSerializerOptions()
    {
        var jsonSerializerOptions = new JsonSerializerOptions()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            AllowTrailingCommas = true,
        };

        jsonSerializerOptions.Converters.Add(new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>());
        jsonSerializerOptions.Converters.Add(new UnionJsonConverter<TextContent, ImageContent>());
        jsonSerializerOptions.Converters.Add(new UnionJsonConverter<string, IReadOnlyList<string>>());

        return jsonSerializerOptions;
    }
}