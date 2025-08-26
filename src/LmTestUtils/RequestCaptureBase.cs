using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AnthropicProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.OpenAIProvider.Models;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Base class for request capture functionality - provides backward compatibility
/// and supports both single and streaming responses
/// </summary>
public abstract class RequestCaptureBase
{
    private readonly List<HttpRequestMessage> _capturedRequests = new();
    private readonly List<string> _capturedBodies = new();
    private readonly List<object> _capturedResponses = new();

    /// <summary>
    /// Shared JsonSerializerOptions that can handle both OpenAI and Anthropic provider types
    /// </summary>
    private static readonly JsonSerializerOptions s_jsonSerializerOptions =
        CreateUniversalOptions();

    /// <summary>
    /// Gets all captured HTTP requests
    /// </summary>
    public IReadOnlyList<HttpRequestMessage> Requests => _capturedRequests.AsReadOnly();

    /// <summary>
    /// Gets all captured request bodies as strings
    /// </summary>
    public IReadOnlyList<string> RequestBodies => _capturedBodies.AsReadOnly();

    /// <summary>
    /// Gets the most recent captured request
    /// </summary>
    public HttpRequestMessage? LastRequest => _capturedRequests.LastOrDefault();

    /// <summary>
    /// Gets the most recent captured request body
    /// </summary>
    public string? LastRequestBody => _capturedBodies.LastOrDefault();

    /// <summary>
    /// Gets the count of captured requests
    /// </summary>
    public int RequestCount => _capturedRequests.Count;

    /// <summary>
    /// Captures an HTTP request and its body
    /// </summary>
    internal async Task CaptureAsync(HttpRequestMessage request)
    {
        _capturedRequests.Add(request);

        if (request.Content != null)
        {
            var body = await request.Content.ReadAsStringAsync();
            _capturedBodies.Add(body);
        }
        else
        {
            _capturedBodies.Add(string.Empty);
        }
    }

    /// <summary>
    /// Sets a response for the most recent request
    /// </summary>
    internal void SetResponse(object response)
    {
        _capturedResponses.Add(response);
    }

    /// <summary>
    /// Gets the most recent request deserialized as the specified type
    /// </summary>
    public T? GetRequestAs<T>()
        where T : class
    {
        var body = LastRequestBody;
        if (string.IsNullOrEmpty(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<T>(body, s_jsonSerializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Gets the most recent response as the specified type (for non-streaming responses)
    /// </summary>
    public T? GetResponseAs<T>()
        where T : class
    {
        var response = _capturedResponses.LastOrDefault();
        if (response is T typedResponse)
            return typedResponse;

        if (response is string jsonResponse)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(jsonResponse, s_jsonSerializerOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets all responses as the specified type (for streaming responses)
    /// </summary>
    public IEnumerable<T> GetResponsesAs<T>()
        where T : class
    {
        foreach (var response in _capturedResponses)
        {
            if (response is T typedResponse)
            {
                yield return typedResponse;
            }
            else if (response is IEnumerable<T> typedResponses)
            {
                foreach (var item in typedResponses)
                    yield return item;
            }
            else if (response is string jsonResponse)
            {
                T? deserialized = null;
                try
                {
                    deserialized = JsonSerializer.Deserialize<T>(
                        jsonResponse,
                        s_jsonSerializerOptions
                    );
                }
                catch (JsonException)
                {
                    // Skip invalid JSON
                }

                if (deserialized != null)
                    yield return deserialized;
            }
        }
    }

    /// <summary>
    /// Checks if any captured request contains the specified text in its body
    /// </summary>
    public bool ContainsText(string text)
    {
        return _capturedBodies.Any(body => body.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if the most recent request has the specified header
    /// </summary>
    public bool HasHeader(string headerName)
    {
        return LastRequest?.Headers?.Contains(headerName) == true;
    }

    /// <summary>
    /// Gets the value of a header from the most recent request
    /// </summary>
    public string? GetHeaderValue(string headerName)
    {
        return LastRequest?.Headers?.GetValues(headerName)?.FirstOrDefault();
    }

    /// <summary>
    /// Checks if the most recent request was sent to the specified URL path
    /// </summary>
    public bool WasSentTo(string urlPath)
    {
        return LastRequest?.RequestUri?.PathAndQuery?.Contains(urlPath) == true;
    }

    /// <summary>
    /// Checks if the most recent request used the specified HTTP method
    /// </summary>
    public bool UsedMethod(HttpMethod method)
    {
        return LastRequest?.Method == method;
    }

    /// <summary>
    /// Creates JsonSerializerOptions that can handle both OpenAI and Anthropic provider types
    /// This ensures that request deserialization works correctly with Union types, polymorphic types, and other complex objects
    /// </summary>
    private static JsonSerializerOptions CreateUniversalOptions()
    {
        // Start with LmCore base configuration with case-insensitive matching
        var options = JsonSerializerOptionsFactory.CreateBase(caseInsensitive: true);

        // Add OpenAI-specific Union converters for cross-provider compatibility
        // Note: These require OpenAI types, but LmTestUtils already references OpenAI Provider
        options.Converters.Add(new UnionJsonConverter<TextContent, ImageContent>());
        options.Converters.Add(
            new UnionJsonConverter<string, Union<TextContent, ImageContent>[]>()
        );

        // Anthropic polymorphic types are handled automatically by the [JsonPolymorphic] attributes
        // No additional converters needed for AnthropicResponseContent and AnthropicStreamEvent

        return options;
    }
}
