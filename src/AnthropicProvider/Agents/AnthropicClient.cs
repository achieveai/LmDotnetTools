using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Net.ServerSentEvents;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using System.Net;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Agents;

/// <summary>
/// Client for interacting with the Anthropic API.
/// </summary>
public class AnthropicClient : IAnthropicClient
{
    private readonly HttpClient _httpClient;
    private const string BaseUrl = "https://api.anthropic.com/v1";
    private readonly JsonSerializerOptions _jsonOptions;
    private bool _disposed = false;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicClient"/> class.
    /// </summary>
    /// <param name="apiKey">The API key to use for authentication.</param>
    /// <param name="httpClient">Optional custom HTTP client to use.</param>
    public AnthropicClient(string apiKey, HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <inheritdoc/>
    public async Task<AnthropicResponse> CreateChatCompletionsAsync(
      AnthropicRequest request,
      CancellationToken cancellationToken = default)
    {
        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<AnthropicResponse>(responseContent, _jsonOptions)
          ?? throw new InvalidOperationException("Failed to deserialize Anthropic API response");
    }

    /// <inheritdoc/>
    public async Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
      AnthropicRequest request,
      CancellationToken cancellationToken = default)
    {
        // Set the streaming flag
        request = request with { Stream = true };

        var requestJson = JsonSerializer.Serialize(request, _jsonOptions);
        var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/messages")
        {
            Content = content
        };

        var response = await _httpClient.SendAsync(
          requestMessage,
          HttpCompletionOption.ResponseHeadersRead,
          cancellationToken);

        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidProgramException(
                $"Error processing request:\n{requestJson}\nResponse:\n{error}",
                ex);
        }

        return StreamData(response.Content, cancellationToken);
    }

    private async IAsyncEnumerable<AnthropicStreamEvent> StreamData(
      HttpContent content,
      [EnumeratorCancellation] CancellationToken cancellationToken)
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
                    "message_start" => JsonSerializer.Deserialize<AnthropicMessageStartEvent>(sseItem.Data, _jsonOptions),
                    "content_block_start" => JsonSerializer.Deserialize<AnthropicContentBlockStartEvent>(sseItem.Data, _jsonOptions),
                    "content_block_delta" => JsonSerializer.Deserialize<AnthropicContentBlockDeltaEvent>(sseItem.Data, _jsonOptions),
                    "content_block_stop" => JsonSerializer.Deserialize<AnthropicContentBlockStopEvent>(sseItem.Data, _jsonOptions),
                    "message_delta" => JsonSerializer.Deserialize<AnthropicMessageDeltaEvent>(sseItem.Data, _jsonOptions),
                    "message_stop" => JsonSerializer.Deserialize<AnthropicMessageStopEvent>(sseItem.Data, _jsonOptions),
                    "ping" => JsonSerializer.Deserialize<AnthropicPingEvent>(sseItem.Data, _jsonOptions),
                    "error" => JsonSerializer.Deserialize<AnthropicErrorEvent>(sseItem.Data, _jsonOptions),
                    _ => baseEvent // Use the base event if type is unknown
                };
            }
            catch (JsonException ex)
            {
                // Log exception and continue
                Console.Error.WriteLine($"Error parsing SSE data: {ex.Message}");
                continue;
            }

            // Return the event data if it was successfully parsed
            if (eventData != null)
            {
                yield return eventData;
            }
        }
    }

    /// <summary>
    /// Disposes the HTTP client.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes the HTTP client.
    /// </summary>
    /// <param name="disposing">Whether to dispose managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _httpClient.Dispose();
            }

            _disposed = true;
        }
    }
}
