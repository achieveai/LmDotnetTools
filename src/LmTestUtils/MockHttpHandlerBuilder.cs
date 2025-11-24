using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
/// Interface for classes that provide HTTP responses in the mock system
/// </summary>
public interface IResponseProvider
{
    /// <summary>
    /// Determines if this provider can handle the given request
    /// </summary>
    bool CanHandle(HttpRequestMessage request, int requestIndex);

    /// <summary>
    /// Creates an HTTP response for the given request
    /// </summary>
    Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex);
}

/// <summary>
/// Interface for classes that process requests (for capture, validation, etc.)
/// </summary>
public interface IRequestProcessor
{
    /// <summary>
    /// Processes a request before response generation
    /// </summary>
    Task ProcessRequestAsync(HttpRequestMessage request, int requestIndex);
}

/// <summary>
/// Interface for HTTP handler middleware that can process requests in a chain
/// </summary>
public interface IHttpHandlerMiddleware
{
    /// <summary>
    /// Handles the HTTP request, optionally calling the next middleware in the chain
    /// </summary>
    /// <param name="request">The HTTP request</param>
    /// <param name="requestIndex">The index of this request in the sequence</param>
    /// <param name="next">Function to call the next middleware in the chain, null if this is the last middleware</param>
    /// <returns>HttpResponseMessage if handled, null to indicate no handling</returns>
    Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    );
}

/// <summary>
/// Adapter that converts IResponseProvider to IHttpHandlerMiddleware
/// </summary>
internal class ResponseProviderMiddleware : IHttpHandlerMiddleware
{
    private readonly IResponseProvider _provider;

    public ResponseProviderMiddleware(IResponseProvider provider)
    {
        _provider = provider;
    }

    public async Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        if (_provider.CanHandle(request, requestIndex))
        {
            try
            {
                return await _provider.CreateResponseAsync(request, requestIndex);
            }
            catch
            {
                // If provider fails, try next middleware
                return next != null ? await next() : null;
            }
        }

        return next != null ? await next() : null;
    }
}

/// <summary>
/// Adapter that converts IRequestProcessor to IHttpHandlerMiddleware
/// </summary>
internal class RequestProcessorMiddleware : IHttpHandlerMiddleware
{
    private readonly IRequestProcessor _processor;

    public RequestProcessorMiddleware(IRequestProcessor processor)
    {
        _processor = processor;
    }

    public async Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        await _processor.ProcessRequestAsync(request, requestIndex);
        return next != null ? await next() : null;
    }
}

/// <summary>
/// Adapter that converts RequestCaptureBase to IHttpHandlerMiddleware
/// </summary>
internal class RequestCaptureMiddleware : IHttpHandlerMiddleware
{
    private readonly RequestCaptureBase _capture;

    public RequestCaptureMiddleware(RequestCaptureBase capture)
    {
        _capture = capture;
    }

    public async Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        await _capture.CaptureAsync(request);
        return next != null ? await next() : null;
    }
}

/// <summary>
/// Middleware that forwards requests to a real HTTP handler
/// </summary>
internal class RealHttpHandlerMiddleware : IHttpHandlerMiddleware, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly Action<RecordedInteraction>? _onNewInteraction;
    private bool _disposed;

    public RealHttpHandlerMiddleware(
        string baseUrl,
        string apiKey,
        Action<RecordedInteraction>? onNewInteraction = null
    )
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _onNewInteraction = onNewInteraction;
        _httpClient = new HttpClient();
    }

    public async Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RealHttpHandlerMiddleware));
        }

        try
        {
            // Clone the request for forwarding
            var forwardRequest = await CloneRequestAsync(request);

            // Set authentication
            forwardRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                _apiKey
            );

            // Forward to real API
            var response = await _httpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);

            // Record the interaction if callback provided
            if (_onNewInteraction != null && request.Content != null)
            {
                await RecordInteractionAsync(request, response);
            }

            return response;
        }
        catch
        {
            // If real API fails, try next middleware
            return next != null ? await next() : null;
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers efficiently
        foreach (var header in request.Headers)
        {
            _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content efficiently using byte arrays
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            var byteContent = new ByteArrayContent(contentBytes);

            // Copy content headers
            foreach (var header in request.Content.Headers)
            {
                _ = byteContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = byteContent;
        }

        return clone;
    }

    private async Task RecordInteractionAsync(HttpRequestMessage request, HttpResponseMessage response)
    {
        try
        {
            if (request.Content == null || _onNewInteraction == null)
            {
                return;
            }

            // Read request and response content
            var requestContent = await request.Content.ReadAsStringAsync();
            var responseContent = await response.Content.ReadAsStringAsync();

            // Parse to JsonElements
            using var requestDoc = JsonDocument.Parse(requestContent);
            using var responseDoc = JsonDocument.Parse(responseContent);

            var interaction = new RecordedInteraction
            {
                SerializedRequest = requestDoc.RootElement.Clone(),
                SerializedResponse = responseDoc.RootElement.Clone(),
                IsStreaming = false,
                RecordedAt = DateTime.UtcNow,
                Provider = DetermineProvider(request),
            };

            _onNewInteraction(interaction);
        }
        catch
        {
            // Ignore recording errors - don't fail the actual request
        }
    }

    private static string DetermineProvider(HttpRequestMessage request)
    {
        return request.IsAnthropicRequest() ? "Anthropic" : request.IsOpenAIRequest() ? "OpenAI" : "OpenAI";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// Middleware that supports delegate functions
/// </summary>
internal class DelegateMiddleware : IHttpHandlerMiddleware
{
    private readonly Func<
        HttpRequestMessage,
        int,
        Func<Task<HttpResponseMessage?>>?,
        Task<HttpResponseMessage?>
    > _middleware;

    public DelegateMiddleware(
        Func<HttpRequestMessage, int, Func<Task<HttpResponseMessage?>>?, Task<HttpResponseMessage?>> middleware
    )
    {
        _middleware = middleware;
    }

    public Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        return _middleware(request, requestIndex, next);
    }
}

/// <summary>
/// Extensions for request matching and analysis
/// </summary>
public static class RequestExtensions
{
    /// <summary>
    /// Checks if this is the first request (index 0)
    /// </summary>
    public static bool IsFirstMessage(this HttpRequestMessage request, int requestIndex)
    {
        return requestIndex == 0;
    }

    /// <summary>
    /// Checks if this is the second request (index 1)
    /// </summary>
    public static bool IsSecondMessage(this HttpRequestMessage request, int requestIndex)
    {
        return requestIndex == 1;
    }

    /// <summary>
    /// Checks if this is the nth request (index n-1)
    /// </summary>
    public static bool IsNthMessage(this HttpRequestMessage request, int requestIndex, int n)
    {
        return requestIndex == n - 1;
    }

    /// <summary>
    /// Checks if the request contains tool results
    /// </summary>
    public static bool HasToolResults(this HttpRequestMessage request)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var content = request.Content.ReadAsStringAsync().Result;
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            // Check if there's a messages array with tool_result content
            if (
                root.TryGetProperty("messages", out var messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var message in messagesElement.EnumerateArray())
                {
                    if (
                        message.TryGetProperty("content", out var contentElement)
                        && contentElement.ValueKind == JsonValueKind.Array
                    )
                    {
                        foreach (var contentItem in contentElement.EnumerateArray())
                        {
                            if (
                                contentItem.TryGetProperty("type", out var typeElement)
                                && typeElement.GetString() == "tool_result"
                            )
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the request contains a specific text in any message content
    /// </summary>
    public static bool ContainsText(this HttpRequestMessage request, string text)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var content = request.Content.ReadAsStringAsync().Result;
            return content.Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if the request has a specific role in messages
    /// </summary>
    public static bool HasRole(this HttpRequestMessage request, string role)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var content = request.Content.ReadAsStringAsync().Result;
            if (string.IsNullOrEmpty(content))
            {
                return false;
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (
                root.TryGetProperty("messages", out var messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array
            )
            {
                foreach (var message in messagesElement.EnumerateArray())
                {
                    if (message.TryGetProperty("role", out var roleElement) && roleElement.GetString() == role)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the count of messages in the request
    /// </summary>
    public static int GetMessageCount(this HttpRequestMessage request)
    {
        try
        {
            if (request.Content == null)
            {
                return 0;
            }

            var content = request.Content.ReadAsStringAsync().Result;
            if (string.IsNullOrEmpty(content))
            {
                return 0;
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            return root.TryGetProperty("messages", out var messagesElement)
                && messagesElement.ValueKind == JsonValueKind.Array
                ? messagesElement.GetArrayLength()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Checks if the request mentions a specific tool name
    /// </summary>
    public static bool MentionsTool(this HttpRequestMessage request, string toolName)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var content = request.Content.ReadAsStringAsync().Result;
            return content.Contains(toolName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks if this is an Anthropic API request
    /// </summary>
    public static bool IsAnthropicRequest(this HttpRequestMessage request)
    {
        return request.RequestUri?.Host?.Contains("anthropic", StringComparison.OrdinalIgnoreCase) == true
            || request.Headers.Any(h => h.Key.Contains("anthropic", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Checks if this is an OpenAI API request
    /// </summary>
    public static bool IsOpenAIRequest(this HttpRequestMessage request)
    {
        return request.RequestUri?.Host?.Contains("openai", StringComparison.OrdinalIgnoreCase) == true
            || request.Headers.Any(h => h.Key.Contains("openai", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Multi-conditional response provider for complex scenarios
/// </summary>
internal class MultiConditionalResponseProvider : IResponseProvider
{
    private readonly List<(
        Func<HttpRequestMessage, int, bool> condition,
        IResponseProvider provider
    )> _conditionalProviders = [];
    private readonly IResponseProvider? _defaultProvider;

    public MultiConditionalResponseProvider(IResponseProvider? defaultProvider = null)
    {
        _defaultProvider = defaultProvider;
    }

    public void AddCondition(Func<HttpRequestMessage, int, bool> condition, IResponseProvider provider)
    {
        _conditionalProviders.Add((condition, provider));
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        // Check if any condition matches
        foreach (var (condition, _) in _conditionalProviders)
        {
            if (condition(request, requestIndex))
            {
                return true;
            }
        }

        // If no condition matches, check if we have a default provider
        return _defaultProvider != null;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        // Find the first matching condition
        foreach (var (condition, provider) in _conditionalProviders)
        {
            if (condition(request, requestIndex))
            {
                return provider.CreateResponseAsync(request, requestIndex);
            }
        }

        // Fall back to default provider if available
        if (_defaultProvider != null)
        {
            return _defaultProvider.CreateResponseAsync(request, requestIndex);
        }

        // If no default, return a generic error
        var errorResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "No matching condition found and no default response configured",
                Encoding.UTF8,
                "text/plain"
            ),
        };
        return Task.FromResult(errorResponse);
    }
}

/// <summary>
/// State management for tracking conversation state across requests
/// </summary>
public class ConversationState
{
    private readonly Dictionary<string, object> _state = [];
    private int _requestCount = 0;
    private int _currentRequestIndex = -1;

    public int RequestCount => _requestCount;

    public int IncrementRequestCount()
    {
        return Interlocked.Increment(ref _requestCount);
    }

    public int DecrementRequestCount()
    {
        return Interlocked.Decrement(ref _requestCount);
    }

    /// <summary>
    /// Ensures request count is incremented only once per request index
    /// </summary>
    public void EnsureRequestCounted(int requestIndex)
    {
        if (_currentRequestIndex != requestIndex)
        {
            _currentRequestIndex = requestIndex;
            _ = IncrementRequestCount();
        }
    }

    public void Set<T>(string key, T value)
        where T : notnull
    {
        _state[key] = value;
    }

    public T? Get<T>(string key)
        where T : class
    {
        return _state.TryGetValue(key, out var value) ? value as T : null;
    }

    public T GetValue<T>(string key, T defaultValue = default!)
        where T : struct
    {
        return _state.TryGetValue(key, out var value) && value is T typedValue ? typedValue : defaultValue;
    }

    public bool Has(string key)
    {
        return _state.ContainsKey(key);
    }

    public void Clear()
    {
        _state.Clear();
    }
}

/// <summary>
/// Stateful response provider that tracks conversation state
/// </summary>
internal class StatefulResponseProvider : IResponseProvider
{
    private readonly ConversationState _state;
    private readonly Func<HttpRequestMessage, int, ConversationState, bool> _condition;
    private readonly IResponseProvider _innerProvider;

    public StatefulResponseProvider(
        ConversationState state,
        Func<HttpRequestMessage, int, ConversationState, bool> condition,
        IResponseProvider innerProvider
    )
    {
        _state = state;
        _condition = condition;
        _innerProvider = innerProvider;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        // Ensure request count is accurate for condition evaluation
        _state.EnsureRequestCounted(requestIndex);
        return _condition(request, requestIndex, _state);
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        return _innerProvider.CreateResponseAsync(request, requestIndex);
    }
}

/// <summary>
/// Simple JSON response provider
/// </summary>
internal class SimpleJsonResponseProvider : IResponseProvider, IDisposable
{
    private readonly string _jsonResponse;
    private readonly HttpStatusCode _statusCode;
    private readonly byte[] _cachedContentBytes;
    private readonly object _lock = new();
    private bool _disposed;

    public SimpleJsonResponseProvider(string jsonResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        _jsonResponse = jsonResponse;
        _statusCode = statusCode;
        // Pre-encode the JSON content bytes once during construction
        _cachedContentBytes = Encoding.UTF8.GetBytes(_jsonResponse);
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimpleJsonResponseProvider));
        }

        // Create a new response with cached content bytes
        // This avoids encoding the string to bytes each time
        var response = new HttpResponseMessage(_statusCode);
        var content = new ByteArrayContent(_cachedContentBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        response.Content = content;

        return Task.FromResult(response);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Error response provider for testing error scenarios
/// </summary>
internal class ErrorResponseProvider : IResponseProvider, IDisposable
{
    private readonly HttpStatusCode _statusCode;
    private readonly string? _errorMessage;
    private readonly byte[]? _cachedContentBytes;
    private bool _disposed;

    public ErrorResponseProvider(HttpStatusCode statusCode, string? errorMessage = null)
    {
        _statusCode = statusCode;
        _errorMessage = errorMessage;

        // Pre-encode the error message bytes once during construction if provided
        if (!string.IsNullOrEmpty(_errorMessage))
        {
            _cachedContentBytes = Encoding.UTF8.GetBytes(_errorMessage);
        }
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ErrorResponseProvider));
        }

        var response = new HttpResponseMessage(_statusCode);

        if (_cachedContentBytes != null)
        {
            var content = new ByteArrayContent(_cachedContentBytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            response.Content = content;
        }

        return Task.FromResult(response);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Provider for Anthropic-specific error response formats
/// </summary>
internal class AnthropicErrorResponseProvider : IResponseProvider, IDisposable
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _errorType;
    private readonly string _errorMessage;
    private readonly byte[] _cachedContentBytes;
    private bool _disposed;

    public AnthropicErrorResponseProvider(HttpStatusCode statusCode, string errorType, string errorMessage)
    {
        _statusCode = statusCode;
        _errorType = errorType;
        _errorMessage = errorMessage;

        // Pre-serialize the JSON response once during construction
        var errorResponse = new { type = "error", error = new { type = _errorType, message = _errorMessage } };

        var jsonContent = JsonSerializer.Serialize(errorResponse);
        _cachedContentBytes = Encoding.UTF8.GetBytes(jsonContent);
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AnthropicErrorResponseProvider));
        }

        var response = new HttpResponseMessage(_statusCode);
        var content = new ByteArrayContent(_cachedContentBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        response.Content = content;

        return Task.FromResult(response);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Provider for OpenAI-specific error response formats
/// </summary>
internal class OpenAIErrorResponseProvider : IResponseProvider, IDisposable
{
    private readonly HttpStatusCode _statusCode;
    private readonly string _errorType;
    private readonly string _errorMessage;
    private readonly string? _param;
    private readonly string? _code;
    private readonly byte[] _cachedContentBytes;
    private bool _disposed;

    public OpenAIErrorResponseProvider(
        HttpStatusCode statusCode,
        string errorType,
        string errorMessage,
        string? param = null,
        string? code = null
    )
    {
        _statusCode = statusCode;
        _errorType = errorType;
        _errorMessage = errorMessage;
        _param = param;
        _code = code;

        // Pre-serialize the JSON response once during construction
        var errorResponse = new
        {
            error = new
            {
                message = _errorMessage,
                type = _errorType,
                param = _param,
                code = _code,
            },
        };

        var jsonContent = JsonSerializer.Serialize(errorResponse);
        _cachedContentBytes = Encoding.UTF8.GetBytes(jsonContent);
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenAIErrorResponseProvider));
        }

        var response = new HttpResponseMessage(_statusCode);
        var content = new ByteArrayContent(_cachedContentBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        response.Content = content;

        return Task.FromResult(response);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// Provider for status code sequence testing (useful for retry scenarios)
/// </summary>
internal class StatusCodeSequenceResponseProvider : IResponseProvider
{
    private readonly HttpStatusCode[] _statusCodes;
    private readonly string? _finalSuccessMessage;
    private int _requestCount = 0;

    public StatusCodeSequenceResponseProvider(HttpStatusCode[] statusCodes, string? finalSuccessMessage = null)
    {
        _statusCodes = statusCodes ?? throw new ArgumentNullException(nameof(statusCodes));
        _finalSuccessMessage = finalSuccessMessage;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        var currentIndex = Interlocked.Increment(ref _requestCount) - 1;
        var statusCodeIndex = Math.Min(currentIndex, _statusCodes.Length - 1);
        var statusCode = _statusCodes[statusCodeIndex];

        var response = new HttpResponseMessage(statusCode);

        // If this is the final successful response and we have a success message
        if (statusCode == HttpStatusCode.OK && !string.IsNullOrEmpty(_finalSuccessMessage))
        {
            response.Content = new StringContent(_finalSuccessMessage, Encoding.UTF8, "application/json");
        }
        else if (statusCode != HttpStatusCode.OK)
        {
            // Generate appropriate error message based on status code
            var errorMessage = statusCode switch
            {
                HttpStatusCode.TooManyRequests => "Rate limit exceeded",
                HttpStatusCode.ServiceUnavailable => "Service temporarily unavailable",
                HttpStatusCode.InternalServerError => "Internal server error",
                HttpStatusCode.BadRequest => "Bad request",
                HttpStatusCode.Unauthorized => "Authentication required",
                HttpStatusCode.Forbidden => "Access forbidden",
                HttpStatusCode.NotFound => "Resource not found",
                _ => $"HTTP {(int)statusCode} error",
            };

            response.Content = new StringContent(
                $"{{\"error\": {{\"message\": \"{errorMessage}\", \"status\": {(int)statusCode}}}}}",
                Encoding.UTF8,
                "application/json"
            );
        }

        return Task.FromResult(response);
    }
}

/// <summary>
/// Provider for rate limiting error responses with Retry-After header
/// </summary>
internal class RateLimitErrorResponseProvider : IResponseProvider
{
    private readonly int _retryAfterSeconds;
    private readonly string _providerType;

    public RateLimitErrorResponseProvider(int retryAfterSeconds, string providerType = "generic")
    {
        _retryAfterSeconds = retryAfterSeconds;
        _providerType = providerType;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        response.Headers.Add("Retry-After", _retryAfterSeconds.ToString());

        var jsonContent = _providerType.ToLowerInvariant() switch
        {
            "anthropic" => JsonSerializer.Serialize(
                new
                {
                    type = "error",
                    error = new
                    {
                        type = "rate_limit_error",
                        message = $"Rate limit exceeded. Please retry after {_retryAfterSeconds} seconds.",
                    },
                }
            ),
            "openai" => JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        message = $"Rate limit exceeded. Please retry after {_retryAfterSeconds} seconds.",
                        type = "rate_limit_error",
                        param = (string?)null,
                        code = "rate_limit_exceeded",
                    },
                }
            ),
            _ => JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        message = $"Rate limit exceeded. Please retry after {_retryAfterSeconds} seconds.",
                        retry_after = _retryAfterSeconds,
                    },
                }
            ),
        };

        response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        return Task.FromResult(response);
    }
}

/// <summary>
/// Provider for authentication error responses
/// </summary>
internal class AuthenticationErrorResponseProvider : IResponseProvider
{
    private readonly string _providerType;

    public AuthenticationErrorResponseProvider(string providerType = "generic")
    {
        _providerType = providerType;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var jsonContent = _providerType.ToLowerInvariant() switch
        {
            "anthropic" => JsonSerializer.Serialize(
                new
                {
                    type = "error",
                    error = new { type = "authentication_error", message = "There's an issue with your API key." },
                }
            ),
            "openai" => JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        message = "Invalid API key provided.",
                        type = "invalid_request_error",
                        param = (string?)null,
                        code = "invalid_api_key",
                    },
                }
            ),
            _ => JsonSerializer.Serialize(
                new { error = new { message = "Authentication failed. Please check your API key.", status = 401 } }
            ),
        };

        response.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        return Task.FromResult(response);
    }
}

/// <summary>
/// Provider for timeout simulation
/// </summary>
internal class TimeoutResponseProvider : IResponseProvider
{
    private readonly int _timeoutMilliseconds;

    public TimeoutResponseProvider(int timeoutMilliseconds = 30000)
    {
        _timeoutMilliseconds = timeoutMilliseconds;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public async Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        // Simulate a timeout by delaying longer than typical client timeout
        await Task.Delay(_timeoutMilliseconds);

        return new HttpResponseMessage(HttpStatusCode.RequestTimeout)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = new { message = "Request timeout", status = 408 } }),
                Encoding.UTF8,
                "application/json"
            ),
        };
    }
}

/// <summary>
/// Streaming file response provider (placeholder for WI-MM004)
/// </summary>
internal class StreamingFileResponseProvider : IResponseProvider, IDisposable
{
    private readonly string _filePath;
    private string? _cachedFileContent;
    private readonly int _streamingDelayMs;
    private bool _disposed;

    // Static cache for file contents to avoid repeated disk reads
    private static readonly ConcurrentDictionary<string, string> _fileContentCache = new();

    public StreamingFileResponseProvider(string filePath, int streamingDelayMs = 5)
    {
        _filePath = filePath;
        _streamingDelayMs = streamingDelayMs;

        // Preload file content if file exists
        if (File.Exists(_filePath))
        {
            _cachedFileContent = _fileContentCache.GetOrAdd(_filePath, File.ReadAllText);
        }
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return _disposed ? throw new ObjectDisposedException(nameof(StreamingFileResponseProvider)) : true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamingFileResponseProvider));
        }

        // Check if file exists and content is cached
        if (_cachedFileContent == null)
        {
            if (!File.Exists(_filePath))
            {
                throw new FileNotFoundException($"SSE file not found: {_filePath}");
            }

            // Cache miss - load file content
            _cachedFileContent = _fileContentCache.GetOrAdd(_filePath, File.ReadAllText);
        }

        // Create SSE stream from cached content
        var sseStream = new SseFileStream(_cachedFileContent, _streamingDelayMs);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(sseStream) };

        // Set the correct content type for Server-Sent Events
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        response.Headers.Add("Connection", "keep-alive");

        return Task.FromResult(response);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _cachedFileContent = null;
        }
    }
}

/// <summary>
/// Retry scenario response provider for testing retry logic
/// </summary>
internal class RetryScenarioResponseProvider : IResponseProvider
{
    private readonly int _failureCount;
    private readonly HttpStatusCode _failureStatus;
    private int _requestCount = 0;

    public RetryScenarioResponseProvider(int failureCount, HttpStatusCode failureStatus)
    {
        _failureCount = failureCount;
        _failureStatus = failureStatus;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        var currentCount = Interlocked.Increment(ref _requestCount);

        if (currentCount <= _failureCount)
        {
            // Return failure response
            return Task.FromResult(new HttpResponseMessage(_failureStatus));
        }

        // Return success response after failures
        var successResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"success": true}""", Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(successResponse);
    }
}

/// <summary>
/// Streaming sequence response provider (placeholder for WI-MM004)
/// </summary>
internal class StreamingSequenceResponseProvider : IResponseProvider, IDisposable
{
    private readonly object _sequence;
    private readonly int _streamingDelayMs;
    private readonly byte[] _cachedContentBytes;
    private bool _disposed;

    public StreamingSequenceResponseProvider(object sequence, int streamingDelayMs = 5)
    {
        _sequence = sequence;
        _streamingDelayMs = streamingDelayMs;

        // Pre-serialize the sequence to JSON and cache the bytes
        var json = JsonSerializer.Serialize(_sequence);
        _cachedContentBytes = Encoding.UTF8.GetBytes(json);
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return _disposed ? throw new ObjectDisposedException(nameof(StreamingSequenceResponseProvider)) : true;
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(StreamingSequenceResponseProvider));
        }

        // Convert the sequence to SSE format instead of raw JSON
        var sseContent = ConvertSequenceToSSE(_sequence);
        var sseBytes = Encoding.UTF8.GetBytes(sseContent);
        var memoryStream = new MemoryStream(sseBytes);

        // Create a custom stream that will simulate streaming with delays
        var streamingStream = new StreamingMemoryStream(memoryStream, _streamingDelayMs);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(streamingStream) };

        // Set appropriate headers for Server-Sent Events
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        response.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        response.Headers.Add("Connection", "keep-alive");

        return Task.FromResult(response);
    }

    private static string ConvertSequenceToSSE(object sequence)
    {
        var sseBuilder = new StringBuilder();

        try
        {
            // If the sequence is an array or enumerable, convert each item to an SSE event
            if (sequence is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    var jsonData = JsonSerializer.Serialize(item);
                    _ = sseBuilder.AppendLine($"data: {jsonData}");
                    _ = sseBuilder.AppendLine(); // Empty line to separate events
                }
            }
            else
            {
                // Single object - convert to one SSE event
                var jsonData = JsonSerializer.Serialize(sequence);
                _ = sseBuilder.AppendLine($"data: {jsonData}");
                _ = sseBuilder.AppendLine();
            }

            // Add final [DONE] marker for streaming completion
            _ = sseBuilder.AppendLine("data: [DONE]");
            _ = sseBuilder.AppendLine();
        }
        catch
        {
            // If serialization fails, return a simple error event
            _ = sseBuilder.AppendLine("data: {\"error\": \"Failed to serialize sequence\"}");
            _ = sseBuilder.AppendLine();
        }

        return sseBuilder.ToString();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// A memory stream wrapper that adds configurable delays to simulate streaming
    /// </summary>
    private class StreamingMemoryStream : Stream
    {
        private readonly MemoryStream _innerStream;
        private readonly int _delayMs;
        private bool _disposed;

        public StreamingMemoryStream(MemoryStream innerStream, int delayMs)
        {
            _innerStream = innerStream;
            _delayMs = delayMs;
        }

        public override bool CanRead => !_disposed && _innerStream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _innerStream.Length;
        public override long Position
        {
            get => _innerStream.Position;
            set => throw new NotSupportedException();
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken
        )
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(StreamingMemoryStream));
            }

            // Add optional delay to simulate streaming
            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            return _innerStream.Read(buffer, offset, count);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return _disposed ? throw new ObjectDisposedException(nameof(StreamingMemoryStream)) : _innerStream.Read(buffer, offset, count);
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _innerStream.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Tool result response provider for handling tool result conversations
/// </summary>
internal class ToolResultResponseProvider : IResponseProvider
{
    private readonly string _finalResponse;

    public ToolResultResponseProvider(string finalResponse)
    {
        _finalResponse = finalResponse;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var body = request.Content.ReadAsStringAsync().Result;
            return body.Contains("tool_result", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            model = "claude-3-sonnet-20240229",
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text = _finalResponse } },
            usage = new { input_tokens = 100, output_tokens = 30 },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        var httpResponse = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
        };
        return Task.FromResult(httpResponse);
    }
}

/// <summary>
/// Conditional response provider that wraps another provider with a condition
/// </summary>
internal class ConditionalResponseProvider : IResponseProvider
{
    private readonly Func<HttpRequestMessage, bool> _condition;
    private readonly IResponseProvider _innerProvider;

    public ConditionalResponseProvider(Func<HttpRequestMessage, bool> condition, IResponseProvider innerProvider)
    {
        _condition = condition;
        _innerProvider = innerProvider;
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return _condition(request) && _innerProvider.CanHandle(request, requestIndex);
    }

    public Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        return _innerProvider.CreateResponseAsync(request, requestIndex);
    }
}

/// <summary>
/// Builder for streaming sequences (placeholder for WI-MM004)
/// </summary>
public class StreamingSequenceBuilder
{
    public object Build()
    {
        // TODO: Implement in WI-MM004
        throw new NotImplementedException("StreamingSequenceBuilder will be implemented in WI-MM004");
    }
}

/// <summary>
/// Enhanced conditional builder for multi-conditional scenarios
/// </summary>
public class EnhancedConditionalBuilder
{
    private readonly MockHttpHandlerBuilder _parent;
    private readonly MultiConditionalResponseProvider _multiProvider;
    private readonly ConversationState? _state;

    public EnhancedConditionalBuilder(MockHttpHandlerBuilder parent, ConversationState? state = null)
    {
        _parent = parent;
        _state = state;
        _multiProvider = new MultiConditionalResponseProvider();
    }

    /// <summary>
    /// Add a condition with response - supports multiple conditions
    /// </summary>
    public EnhancedConditionalBuilder When(Func<HttpRequestMessage, int, bool> condition, string jsonResponse)
    {
        var provider = new SimpleJsonResponseProvider(jsonResponse);
        _multiProvider.AddCondition(condition, provider);
        return this;
    }

    /// <summary>
    /// Add a condition with Anthropic message response
    /// </summary>
    public EnhancedConditionalBuilder WhenAnthropicMessage(
        Func<HttpRequestMessage, int, bool> condition,
        string content = "Hello! How can I help you?",
        string model = "claude-3-sonnet-20240229",
        int inputTokens = 10,
        int outputTokens = 20
    )
    {
        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            model,
            stop_reason = "end_turn",
            content = new object[] { new { type = "text", text = content } },
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        var provider = new SimpleJsonResponseProvider(jsonResponse);
        _multiProvider.AddCondition(condition, provider);
        return this;
    }

    /// <summary>
    /// Add a stateful condition that has access to conversation state
    /// </summary>
    public EnhancedConditionalBuilder WhenStateful(
        Func<HttpRequestMessage, int, ConversationState, bool> condition,
        string jsonResponse
    )
    {
        if (_state == null)
        {
            throw new InvalidOperationException("State is required for stateful conditions. Use WithState() first.");
        }

        var innerProvider = new SimpleJsonResponseProvider(jsonResponse);
        var statefulProvider = new StatefulResponseProvider(_state, condition, innerProvider);
        _multiProvider.AddCondition(statefulProvider.CanHandle, statefulProvider);
        return this;
    }

    /// <summary>
    /// Set default response when no conditions match
    /// </summary>
    public MockHttpHandlerBuilder Otherwise(string jsonResponse)
    {
        var defaultProvider = new SimpleJsonResponseProvider(jsonResponse);
        var newMultiProvider = new MultiConditionalResponseProvider(defaultProvider);

        // Copy existing conditions via reflection
        var field = typeof(MultiConditionalResponseProvider).GetField(
            "_conditionalProviders",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        if (
            field?.GetValue(_multiProvider) is List<(Func<HttpRequestMessage, int, bool>, IResponseProvider)> conditions
        )
        {
            foreach (var (condition, provider) in conditions)
            {
                newMultiProvider.AddCondition(condition, provider);
            }
        }

        _parent.AddResponseProvider(newMultiProvider);
        return _parent;
    }

    /// <summary>
    /// Finish building and return to parent builder
    /// </summary>
    public HttpMessageHandler Build()
    {
        _parent.AddResponseProvider(_multiProvider);
        return _parent.Build();
    }
}

/// <summary>
/// Builder for conditional responses
/// </summary>
public class ConditionalBuilder
{
    private readonly MockHttpHandlerBuilder _parent;
    private readonly Func<HttpRequestMessage, bool> _condition;

    public ConditionalBuilder(MockHttpHandlerBuilder parent, Func<HttpRequestMessage, bool> condition)
    {
        _parent = parent;
        _condition = condition;
    }

    public MockHttpHandlerBuilder ThenRespondWith(string jsonResponse)
    {
        _parent.AddResponseProvider(
            new ConditionalResponseProvider(_condition, new SimpleJsonResponseProvider(jsonResponse))
        );
        return _parent;
    }

    public MockHttpHandlerBuilder ThenRespondWithAnthropicMessage(
        string content = "Hello! How can I help you?",
        string model = "claude-3-sonnet-20240229",
        int inputTokens = 10,
        int outputTokens = 20
    )
    {
        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            content = new[] { new { type = "text", text = content } },
            model,
            stop_reason = "end_turn",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        _parent.AddResponseProvider(
            new ConditionalResponseProvider(_condition, new SimpleJsonResponseProvider(jsonResponse))
        );
        return _parent;
    }

    public MockHttpHandlerBuilder ThenRespondWithToolUse(string toolName, object inputData, string? textContent = null)
    {
        textContent ??= $"I'll help you use the {toolName} tool.";

        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            model = "claude-3-sonnet-20240229",
            stop_reason = "tool_use",
            content = new object[]
            {
                new { type = "text", text = textContent },
                new
                {
                    type = "tool_use",
                    id = "toolu_" + Guid.NewGuid().ToString("N")[..8],
                    name = toolName,
                    input = inputData,
                },
            },
            usage = new { input_tokens = 50, output_tokens = 25 },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        _parent.AddResponseProvider(
            new ConditionalResponseProvider(_condition, new SimpleJsonResponseProvider(jsonResponse))
        );
        return _parent;
    }
}

/// <summary>
/// Builder for sequential responses (placeholder for WI-MM005)
/// </summary>
public class SequentialBuilder
{
    private readonly MockHttpHandlerBuilder _parent;
    private readonly int _requestIndex;

    public SequentialBuilder(MockHttpHandlerBuilder parent, int requestIndex)
    {
        _parent = parent;
        _requestIndex = requestIndex;
    }

    public MockHttpHandlerBuilder RespondWith(string jsonResponse)
    {
        // TODO: Implement sequential logic in WI-MM005
        throw new NotImplementedException("Sequential responses will be implemented in WI-MM005");
    }
}

/// <summary>
/// The actual HttpMessageHandler that processes requests using configured middleware
/// </summary>
internal class MockHttpHandler : HttpMessageHandler, IDisposable
{
    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage?>> _middlewarePipeline;
    private int _requestIndex = 0;

    public MockHttpHandler(IEnumerable<IHttpHandlerMiddleware> middlewares)
    {
        _middlewarePipeline = BuildPipeline(middlewares);
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var requestIndex = Interlocked.Increment(ref _requestIndex) - 1;

        var response = await _middlewarePipeline(request, requestIndex);

        return response == null
            ? throw new InvalidOperationException(
                $"No middleware handled request #{requestIndex} to {request.RequestUri}"
            )
            : response;
    }

    private static Func<HttpRequestMessage, int, Task<HttpResponseMessage?>> BuildPipeline(
        IEnumerable<IHttpHandlerMiddleware> middlewares
    )
    {
        var middlewareList = middlewares.ToList();

        return async (request, index) =>
        {
            // Build the pipeline dynamically for each request
            Func<Task<HttpResponseMessage?>>? next = null;

            // Build the chain from the end backwards
            for (var i = middlewareList.Count - 1; i >= 0; i--)
            {
                var middleware = middlewareList[i];
                var currentNext = next;
                next = () => middleware.HandleAsync(request, index, currentNext);
            }

            // Execute the pipeline
            return next != null ? await next() : null;
        };
    }

    public new void Dispose()
    {
        // Dispose any middleware that implement IDisposable
        // Note: We can't access the middleware list here, but they should be disposed by their owners

        // Call base dispose
        base.Dispose();
    }
}

/// <summary>
/// Fluent builder for creating sophisticated HTTP message handlers for testing
/// Provides a unified, powerful API for HTTP mocking
/// </summary>
public class MockHttpHandlerBuilder
{
    private readonly List<IHttpHandlerMiddleware> _middlewares = [];
    private string? _recordPlaybackPath;
    private bool _allowAdditionalRequests = false;
    private string? _forwardToBaseUrl;
    private string? _forwardToApiKey;

    private MockHttpHandlerBuilder() { }

    /// <summary>
    /// Creates a new builder instance
    /// </summary>
    public static MockHttpHandlerBuilder Create()
    {
        return new();
    }

    #region Request Handling

    /// <summary>
    /// Captures requests for detailed inspection
    /// </summary>
    /// <param name="capture">Output parameter that receives the capture object</param>
    public MockHttpHandlerBuilder CaptureRequests(out RequestCapture capture)
    {
        capture = new RequestCapture();
        _middlewares.Add(new RequestCaptureMiddleware(capture));
        return this;
    }

    /// <summary>
    /// Captures requests with type safety for inspection and returns the builder for chaining
    /// </summary>
    public MockHttpHandlerBuilder CaptureRequests<TRequest, TResponse>(out RequestCapture<TRequest, TResponse> capture)
        where TRequest : class
        where TResponse : class
    {
        capture = new RequestCapture<TRequest, TResponse>();
        _middlewares.Add(new RequestCaptureMiddleware(capture));
        return this;
    }

    /// <summary>
    /// Enables record/playback functionality
    /// </summary>
    /// <param name="filePath">Path to test data file</param>
    /// <param name="allowAdditional">Allow additional requests beyond recorded ones</param>
    public MockHttpHandlerBuilder WithRecordPlayback(string filePath, bool allowAdditional = false)
    {
        _recordPlaybackPath = filePath;
        _allowAdditionalRequests = allowAdditional;
        return this;
    }

    /// <summary>
    /// Configure API forwarding for unmatched requests in record/playback mode
    /// </summary>
    /// <param name="baseUrl">Base URL for the real API</param>
    /// <param name="apiKey">API key for authentication</param>
    public MockHttpHandlerBuilder ForwardToApi(string baseUrl, string apiKey)
    {
        _forwardToBaseUrl = baseUrl;
        _forwardToApiKey = apiKey;
        return this;
    }

    /// <summary>
    /// Allows collecting additional requests when predefined ones are exhausted
    /// </summary>
    public MockHttpHandlerBuilder AllowAdditionalRequests()
    {
        _allowAdditionalRequests = true;
        return this;
    }

    #endregion

    #region Simple Response Methods

    /// <summary>
    /// Responds with a simple JSON response
    /// </summary>
    public MockHttpHandlerBuilder RespondWithJson(string jsonResponse, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        var provider = new SimpleJsonResponseProvider(jsonResponse, statusCode);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with JSON, automatically detecting arrays and converting them to SSE format
    /// </summary>
    public MockHttpHandlerBuilder RespondWithJsonOrSSE(
        string jsonResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        bool forceSSE = false
    )
    {
        // Check if the JSON represents an array or if SSE is forced
        var shouldUseSSE = forceSSE || IsJsonArray(jsonResponse);

        if (shouldUseSSE)
        {
            try
            {
                // Parse the JSON and convert to SSE
                var jsonElement = JsonDocument.Parse(jsonResponse).RootElement;
                var provider = new StreamingSequenceResponseProvider(jsonElement);
                _middlewares.Add(new ResponseProviderMiddleware(provider));
                return this;
            }
            catch
            {
                // If parsing fails, fall back to regular JSON
            }
        }

        // Use regular JSON response
        return RespondWithJson(jsonResponse, statusCode);
    }

    private static bool IsJsonArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var trimmed = json.Trim();
        return trimmed.StartsWith('[') && trimmed.EndsWith(']');
    }

    /// <summary>
    /// Responds with an Anthropic-formatted message
    /// </summary>
    public MockHttpHandlerBuilder RespondWithAnthropicMessage(
        string content = "Hello! How can I help you?",
        string model = "claude-3-sonnet-20240229",
        int inputTokens = 10,
        int outputTokens = 20
    )
    {
        var response = new
        {
            type = "message",
            id = "msg_test" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            content = new[] { new { type = "text", text = content } },
            model,
            stop_reason = "end_turn",
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        return RespondWithJson(jsonResponse);
    }

    /// <summary>
    /// Responds with an OpenAI-formatted chat completion
    /// </summary>
    public MockHttpHandlerBuilder RespondWithOpenAIMessage(
        string content = "Hello! How can I help you?",
        string model = "gpt-4",
        int promptTokens = 10,
        int completionTokens = 20
    )
    {
        var response = new
        {
            id = "chatcmpl_test" + Guid.NewGuid().ToString("N")[..8],
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content },
                    finish_reason = "stop",
                },
            },
            usage = new
            {
                prompt_tokens = promptTokens,
                completion_tokens = completionTokens,
                total_tokens = promptTokens + completionTokens,
            },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        return RespondWithJson(jsonResponse);
    }

    #endregion

    #region Tool Response Methods

    /// <summary>
    /// Responds with tool use
    /// </summary>
    public MockHttpHandlerBuilder RespondWithToolUse(string toolName, object inputData)
    {
        return RespondWithToolUse(toolName, inputData, $"I'll help you use the {toolName} tool.");
    }

    /// <summary>
    /// Responds with tool use with custom text
    /// </summary>
    public MockHttpHandlerBuilder RespondWithToolUse(string toolName, object inputData, string textContent)
    {
        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            model = "claude-3-sonnet-20240229",
            stop_reason = "tool_use",
            content = new object[]
            {
                new { type = "text", text = textContent },
                new
                {
                    type = "tool_use",
                    id = "toolu_" + Guid.NewGuid().ToString("N")[..8],
                    name = toolName,
                    input = inputData,
                },
            },
            usage = new { input_tokens = 50, output_tokens = 25 },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        return RespondWithJson(jsonResponse);
    }

    /// <summary>
    /// Responds with multiple tool uses in a single response
    /// </summary>
    public MockHttpHandlerBuilder RespondWithMultipleToolUse(params (string toolName, object inputData)[] toolCalls)
    {
        var contentList = new List<object>
        {
            new { type = "text", text = $"I'll help you by using {toolCalls.Length} tool(s)." },
        };

        foreach (var (toolName, inputData) in toolCalls)
        {
            contentList.Add(
                new
                {
                    type = "tool_use",
                    id = "toolu_" + Guid.NewGuid().ToString("N")[..8],
                    name = toolName,
                    input = inputData,
                }
            );
        }

        var response = new
        {
            type = "message",
            id = "msg_" + Guid.NewGuid().ToString("N")[..8],
            role = "assistant",
            model = "claude-3-sonnet-20240229",
            stop_reason = "tool_use",
            content = contentList.ToArray(),
            usage = new { input_tokens = 75, output_tokens = 35 },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        return RespondWithJson(jsonResponse);
    }

    /// <summary>
    /// Responds with a tool use based on Anthropic's Python MCP pattern
    /// </summary>
    public MockHttpHandlerBuilder RespondWithPythonMcpTool(string baseName, object inputData)
    {
        return RespondWithToolUse(
            $"python_mcp-{baseName}",
            inputData,
            $"I'll help you by using the {baseName} function."
        );
    }

    /// <summary>
    /// Detects tool result messages and responds appropriately
    /// </summary>
    public MockHttpHandlerBuilder RespondToToolResults(
        string finalResponse = "Based on the tool results, here's what I found."
    )
    {
        var provider = new ToolResultResponseProvider(finalResponse);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Creates a conditional response based on whether the request contains tool results
    /// </summary>
    public MockHttpHandlerBuilder WhenToolResults(string responseText)
    {
        return When(RequestContainsToolResults).ThenRespondWithAnthropicMessage(responseText);
    }

    /// <summary>
    /// Creates a conditional response based on whether the request is a first tool request
    /// </summary>
    public MockHttpHandlerBuilder WhenFirstToolRequest(string toolName, object inputData)
    {
        return When(req => !RequestContainsToolResults(req) && RequestMentionsTool(req, toolName))
            .ThenRespondWithToolUse(toolName, inputData);
    }

    private static bool RequestContainsToolResults(HttpRequestMessage request)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var body = request.Content.ReadAsStringAsync().Result;
            return body.Contains("tool_result", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool RequestMentionsTool(HttpRequestMessage request, string toolName)
    {
        try
        {
            if (request.Content == null)
            {
                return false;
            }

            var body = request.Content.ReadAsStringAsync().Result;
            return body.Contains(toolName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Streaming Methods

    /// <summary>
    /// Responds with streaming data from a file
    /// </summary>
    public MockHttpHandlerBuilder RespondWithStreamingFile(string filePath)
    {
        var provider = new StreamingFileResponseProvider(filePath);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a programmatically built streaming sequence
    /// </summary>
    public MockHttpHandlerBuilder RespondWithStreamingSequence(
        Func<StreamingSequenceBuilder, StreamingSequenceBuilder> sequenceBuilder
    )
    {
        var builder = new StreamingSequenceBuilder();
        var sequence = sequenceBuilder(builder);
        var provider = new StreamingSequenceResponseProvider(sequence.Build());
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with an array of items as Server-Sent Events
    /// </summary>
    public MockHttpHandlerBuilder RespondWithSSEArray<T>(IEnumerable<T> items)
    {
        var provider = new StreamingSequenceResponseProvider(items);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with an array of JSON objects as Server-Sent Events
    /// </summary>
    public MockHttpHandlerBuilder RespondWithSSEArray(params object[] items)
    {
        var provider = new StreamingSequenceResponseProvider(items);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a single object as a Server-Sent Event
    /// </summary>
    public MockHttpHandlerBuilder RespondWithSSE(object item)
    {
        var provider = new StreamingSequenceResponseProvider(new[] { item });
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    #endregion

    #region Error and Retry Methods

    /// <summary>
    /// Simulates retry scenarios with eventual success
    /// </summary>
    public MockHttpHandlerBuilder RetryScenario(
        int failureCount,
        HttpStatusCode failureStatus = HttpStatusCode.InternalServerError
    )
    {
        var provider = new RetryScenarioResponseProvider(failureCount, failureStatus);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a specific HTTP status code and optional error message
    /// </summary>
    public MockHttpHandlerBuilder RespondWithError(HttpStatusCode statusCode, string? errorMessage = null)
    {
        var provider = new ErrorResponseProvider(statusCode, errorMessage);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with an Anthropic-specific error response
    /// </summary>
    public MockHttpHandlerBuilder RespondWithAnthropicError(
        HttpStatusCode statusCode,
        string errorType,
        string errorMessage
    )
    {
        var provider = new AnthropicErrorResponseProvider(statusCode, errorType, errorMessage);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a common Anthropic rate limit error
    /// </summary>
    public MockHttpHandlerBuilder RespondWithAnthropicRateLimit(string errorMessage = "Rate limit exceeded")
    {
        return RespondWithAnthropicError(HttpStatusCode.TooManyRequests, "rate_limit_error", errorMessage);
    }

    /// <summary>
    /// Responds with a common Anthropic authentication error
    /// </summary>
    public MockHttpHandlerBuilder RespondWithAnthropicAuthError(string errorMessage = "Invalid API key")
    {
        return RespondWithAnthropicError(HttpStatusCode.Unauthorized, "authentication_error", errorMessage);
    }

    /// <summary>
    /// Responds with an OpenAI-specific error response
    /// </summary>
    public MockHttpHandlerBuilder RespondWithOpenAIError(
        HttpStatusCode statusCode,
        string errorType,
        string errorMessage,
        string? param = null,
        string? code = null
    )
    {
        var provider = new OpenAIErrorResponseProvider(statusCode, errorType, errorMessage, param, code);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a common OpenAI rate limit error
    /// </summary>
    public MockHttpHandlerBuilder RespondWithOpenAIRateLimit(string errorMessage = "Rate limit exceeded")
    {
        return RespondWithOpenAIError(HttpStatusCode.TooManyRequests, "rate_limit_error", errorMessage);
    }

    /// <summary>
    /// Responds with a common OpenAI authentication error
    /// </summary>
    public MockHttpHandlerBuilder RespondWithOpenAIAuthError(string errorMessage = "Invalid API key")
    {
        return RespondWithOpenAIError(
            HttpStatusCode.Unauthorized,
            "invalid_request_error",
            errorMessage,
            null,
            "invalid_api_key"
        );
    }

    /// <summary>
    /// Responds with a sequence of HTTP status codes for testing retry logic
    /// </summary>
    public MockHttpHandlerBuilder RespondWithStatusCodeSequence(
        HttpStatusCode[] statusCodes,
        string? finalSuccessMessage = null
    )
    {
        var provider = new StatusCodeSequenceResponseProvider(statusCodes, finalSuccessMessage);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a retry sequence (503, 429, then success)
    /// </summary>
    public MockHttpHandlerBuilder RespondWithRetrySequence(string? finalSuccessMessage = null)
    {
        var statusCodes = new[]
        {
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.TooManyRequests,
            HttpStatusCode.OK,
        };
        return RespondWithStatusCodeSequence(statusCodes, finalSuccessMessage);
    }

    /// <summary>
    /// Responds with a rate limit error including Retry-After header
    /// </summary>
    public MockHttpHandlerBuilder RespondWithRateLimitError(int retryAfterSeconds, string providerType = "generic")
    {
        var provider = new RateLimitErrorResponseProvider(retryAfterSeconds, providerType);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with an authentication error (401)
    /// </summary>
    public MockHttpHandlerBuilder RespondWithAuthenticationError(string providerType = "generic")
    {
        var provider = new AuthenticationErrorResponseProvider(providerType);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Responds with a timeout after specified delay
    /// </summary>
    public MockHttpHandlerBuilder RespondWithTimeout(int timeoutMilliseconds = 30000)
    {
        var provider = new TimeoutResponseProvider(timeoutMilliseconds);
        _middlewares.Add(new ResponseProviderMiddleware(provider));
        return this;
    }

    /// <summary>
    /// Follows a retry scenario with a success response
    /// </summary>
    public MockHttpHandlerBuilder ThenRespondWithAnthropicMessage(string content = "Success!")
    {
        return RespondWithAnthropicMessage(content);
    }

    #endregion

    #region Conditional Methods

    /// <summary>
    /// Conditional response based on request content
    /// </summary>
    public ConditionalBuilder When(Func<HttpRequestMessage, bool> condition)
    {
        return new ConditionalBuilder(this, condition);
    }

    /// <summary>
    /// Enhanced multi-conditional builder
    /// </summary>
    public EnhancedConditionalBuilder WithConditions()
    {
        return new EnhancedConditionalBuilder(this);
    }

    /// <summary>
    /// Enhanced multi-conditional builder with conversation state
    /// </summary>
    public EnhancedConditionalBuilder WithState(ConversationState state)
    {
        return new EnhancedConditionalBuilder(this, state);
    }

    /// <summary>
    /// Conditional response with request extensions support
    /// </summary>
    public ConditionalBuilder WhenRequest(Func<HttpRequestMessage, int, bool> condition)
    {
        return new ConditionalBuilder(this, req => condition(req, 0)); // Convert to old signature for now
    }

    /// <summary>
    /// Response when request is first message
    /// </summary>
    public ConditionalBuilder WhenFirstMessage()
    {
        return new ConditionalBuilder(this, (req) => req.IsFirstMessage(0));
    }

    /// <summary>
    /// Response when request is second message
    /// </summary>
    public ConditionalBuilder WhenSecondMessage()
    {
        return new ConditionalBuilder(this, (req) => req.IsSecondMessage(1));
    }

    /// <summary>
    /// Response when request has tool results
    /// </summary>
    public ConditionalBuilder WhenHasToolResults()
    {
        return new ConditionalBuilder(this, req => req.HasToolResults());
    }

    /// <summary>
    /// Response when request contains specific text
    /// </summary>
    public ConditionalBuilder WhenContainsText(string text)
    {
        return new ConditionalBuilder(this, req => req.ContainsText(text));
    }

    /// <summary>
    /// Response when request has specific role
    /// </summary>
    public ConditionalBuilder WhenHasRole(string role)
    {
        return new ConditionalBuilder(this, req => req.HasRole(role));
    }

    /// <summary>
    /// Response when request mentions specific tool
    /// </summary>
    public ConditionalBuilder WhenMentionsTool(string toolName)
    {
        return new ConditionalBuilder(this, req => req.MentionsTool(toolName));
    }

    /// <summary>
    /// Response when message count matches
    /// </summary>
    public ConditionalBuilder WhenMessageCount(int count)
    {
        return new ConditionalBuilder(this, req => req.GetMessageCount() == count);
    }

    /// <summary>
    /// Response when request is to Anthropic API
    /// </summary>
    public ConditionalBuilder WhenAnthropicRequest()
    {
        return new ConditionalBuilder(this, req => req.IsAnthropicRequest());
    }

    /// <summary>
    /// Response when request is to OpenAI API
    /// </summary>
    public ConditionalBuilder WhenOpenAIRequest()
    {
        return new ConditionalBuilder(this, req => req.IsOpenAIRequest());
    }

    /// <summary>
    /// Response for the first request in a sequence
    /// </summary>
    public SequentialBuilder ForFirstRequest()
    {
        return new SequentialBuilder(this, 0);
    }

    /// <summary>
    /// Response for the second request in a sequence
    /// </summary>
    public SequentialBuilder ForSecondRequest()
    {
        return new SequentialBuilder(this, 1);
    }

    /// <summary>
    /// Default response for remaining requests
    /// </summary>
    public MockHttpHandlerBuilder ForRemainingRequests()
    {
        // Implementation would mark this as the default provider
        return this;
    }

    #endregion

    #region Build Method

    #region Middleware Registration Methods

    /// <summary>
    /// Registers a middleware instance
    /// </summary>
    public MockHttpHandlerBuilder UseMiddleware(IHttpHandlerMiddleware middleware)
    {
        _middlewares.Add(middleware);
        return this;
    }

    /// <summary>
    /// Registers a middleware type
    /// </summary>
    public MockHttpHandlerBuilder UseMiddleware<T>()
        where T : IHttpHandlerMiddleware, new()
    {
        return UseMiddleware(new T());
    }

    /// <summary>
    /// Registers a delegate middleware
    /// </summary>
    public MockHttpHandlerBuilder Use(
        Func<HttpRequestMessage, int, Func<Task<HttpResponseMessage?>>?, Task<HttpResponseMessage?>> middlewareFunc
    )
    {
        _middlewares.Add(new DelegateMiddleware(middlewareFunc));
        return this;
    }

    #endregion

    /// <summary>
    /// Builds the final HttpMessageHandler with all configured behaviors
    /// </summary>
    public HttpMessageHandler Build()
    {
        // Create middleware list with proper ordering
        var middlewares = new List<IHttpHandlerMiddleware>();

        // Add request capture middleware FIRST - this ensures requests are captured
        // regardless of which middleware handles the response
        var captureMiddlewares = _middlewares.OfType<RequestCaptureMiddleware>().ToList();
        middlewares.AddRange(captureMiddlewares);

        // Add record/playback middleware after capture (high priority for response handling)
        if (!string.IsNullOrEmpty(_recordPlaybackPath))
        {
            var recordPlaybackMiddleware = new RecordPlaybackMiddleware(_recordPlaybackPath);
            middlewares.Add(recordPlaybackMiddleware);
        }

        // Add real HTTP handler middleware if forwarding is configured
        if (!string.IsNullOrEmpty(_forwardToBaseUrl) && !string.IsNullOrEmpty(_forwardToApiKey))
        {
            var realHttpMiddleware = new RealHttpHandlerMiddleware(_forwardToBaseUrl, _forwardToApiKey);
            middlewares.Add(realHttpMiddleware);
        }

        // Add all other middleware (excluding capture middleware to avoid duplicates)
        var otherMiddlewares = _middlewares.Where(m => m is not RequestCaptureMiddleware).ToList();
        middlewares.AddRange(otherMiddlewares);

        return new MockHttpHandler(middlewares);
    }

    #endregion

    #region Internal Helper Methods

    internal void AddResponseProvider(IResponseProvider provider)
    {
        _middlewares.Add(new ResponseProviderMiddleware(provider));
    }

    #endregion
}

/// <summary>
/// Stream implementation for Server-Sent Events from file content
/// </summary>
internal class SseFileStream : Stream, IDisposable
{
    // Static cache for parsed SSE events to avoid repeated parsing
    private static readonly ConcurrentDictionary<string, byte[]> _parsedContentCache = new();

    private readonly byte[] _content;
    private int _position = 0;
    private bool _disposed;

    // Configurable delay for streaming simulation (0 for no delay)
    private readonly int _streamingDelayMs;

    public SseFileStream(string fileContent, int streamingDelayMs = 5)
    {
        _streamingDelayMs = streamingDelayMs;

        // Use cached content if available, otherwise parse and cache
        _content = _parsedContentCache.GetOrAdd(fileContent, ParseAndFormatSseContent);
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _content.Length;
    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    internal static readonly string[] separator = ["\r\n", "\n"];

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SseFileStream));
        }

        // Optional delay to simulate streaming
        if (_streamingDelayMs > 0)
        {
            await Task.Delay(_streamingDelayMs, cancellationToken);
        }

        if (_position >= _content.Length)
        {
            return 0; // End of stream
        }

        var bytesToRead = Math.Min(count, _content.Length - _position);
        Array.Copy(_content, _position, buffer, offset, bytesToRead);
        _position += bytesToRead;

        return bytesToRead;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Avoid blocking with Result by implementing directly
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SseFileStream));
        }

        if (_position >= _content.Length)
        {
            return 0; // End of stream
        }

        var bytesToRead = Math.Min(count, _content.Length - _position);
        Array.Copy(_content, _position, buffer, offset, bytesToRead);
        _position += bytesToRead;

        return bytesToRead;
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
        }
        base.Dispose(disposing);
    }

    // Parse SSE content and format it as bytes (only called once per unique content)
    private static byte[] ParseAndFormatSseContent(string content)
    {
        var events = ParseSseEvents(content);

        // Pre-calculate the total content size with capacity estimation
        var totalContent = new StringBuilder(content.Length);
        foreach (var sseEvent in events)
        {
            if (!string.IsNullOrEmpty(sseEvent.EventType))
            {
                _ = totalContent.AppendLine($"event: {sseEvent.EventType}");
            }
            if (!string.IsNullOrEmpty(sseEvent.Data))
            {
                _ = totalContent.AppendLine($"data: {sseEvent.Data}");
            }
            _ = totalContent.AppendLine(); // Empty line to separate events
        }

        return Encoding.UTF8.GetBytes(totalContent.ToString());
    }

    private static List<SseEvent> ParseSseEvents(string content)
    {
        var events = new List<SseEvent>();
        var lines = content.Split(separator, StringSplitOptions.None);

        SseEvent? currentEvent = null;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                // Empty line indicates the end of an event
                if (currentEvent != null)
                {
                    events.Add(currentEvent);
                    currentEvent = null;
                }
                continue;
            }

            if (line.StartsWith("event: "))
            {
                // New event
                if (currentEvent != null)
                {
                    events.Add(currentEvent);
                }

                currentEvent = new SseEvent { EventType = line[7..].Trim() };
            }
            else if (line.StartsWith("data: ") && currentEvent != null)
            {
                // Data for the current event
                currentEvent.Data = line[6..].Trim();
            }
        }

        // Add the last event if there is one
        if (currentEvent != null)
        {
            events.Add(currentEvent);
        }

        return events;
    }

    private class SseEvent
    {
        public string EventType { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }
}

/// <summary>
/// Data model for recorded request/response interactions
/// </summary>
public class RecordPlaybackData
{
    public List<RecordedInteraction> Interactions { get; set; } = [];
}

/// <summary>
/// Single recorded interaction between request and response
/// </summary>
public class RecordedInteraction
{
    public JsonElement SerializedRequest { get; set; }
    public JsonElement SerializedResponse { get; set; }
    public List<JsonElement> SerializedResponseFragments { get; set; } = [];
    public bool IsStreaming { get; set; }
    public DateTime? RecordedAt { get; set; }
    public string? Provider { get; set; }
}

/// <summary>
/// Utility for matching incoming requests against recorded requests
/// </summary>
public static class RequestMatcher
{
    /// <summary>
    /// Matches an incoming request JsonElement against a recorded request with flexible matching
    /// </summary>
    public static bool MatchesRecordedRequest(
        JsonElement incomingRequest,
        JsonElement recordedRequest,
        bool exactMatch = false
    )
    {
        try
        {
            // Handle undefined/null cases
            if (
                incomingRequest.ValueKind == JsonValueKind.Undefined
                || recordedRequest.ValueKind == JsonValueKind.Undefined
            )
            {
                return false;
            }

            // Exact match mode - compare JSON directly
            if (exactMatch)
            {
                return JsonElement.DeepEquals(incomingRequest, recordedRequest);
            }

            // Flexible match mode - match key properties
            return MatchesFlexibly(incomingRequest, recordedRequest);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesFlexibly(JsonElement incoming, JsonElement recorded)
    {
        // Match model if present
        if (
            recorded.TryGetProperty("model", out var recordedModel)
            && incoming.TryGetProperty("model", out var incomingModel)
        )
        {
            if (recordedModel.GetString() != incomingModel.GetString())
            {
                return false;
            }
        }

        // Match messages content if present
        if (
            recorded.TryGetProperty("messages", out var recordedMessages)
            && incoming.TryGetProperty("messages", out var incomingMessages)
        )
        {
            if (!MatchMessages(incomingMessages, recordedMessages))
            {
                return false;
            }
        }

        // Match tools if present
        if (
            recorded.TryGetProperty("tools", out var recordedTools)
            && incoming.TryGetProperty("tools", out var incomingTools)
        )
        {
            if (!MatchTools(incomingTools, recordedTools))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchMessages(JsonElement incoming, JsonElement recorded)
    {
        if (incoming.ValueKind != JsonValueKind.Array || recorded.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var incomingArray = incoming.EnumerateArray().ToArray();
        var recordedArray = recorded.EnumerateArray().ToArray();

        if (incomingArray.Length != recordedArray.Length)
        {
            return false;
        }

        for (var i = 0; i < incomingArray.Length; i++)
        {
            var incomingMsg = incomingArray[i];
            var recordedMsg = recordedArray[i];

            // Match role using DeepEquals for robust comparison
            if (
                recordedMsg.TryGetProperty("role", out var recordedRole)
                && incomingMsg.TryGetProperty("role", out var incomingRole)
            )
            {
                if (!JsonElement.DeepEquals(incomingRole, recordedRole))
                {
                    return false;
                }
            }

            // Match content using DeepEquals for robust comparison
            if (
                recordedMsg.TryGetProperty("content", out var recordedContent)
                && incomingMsg.TryGetProperty("content", out var incomingContent)
            )
            {
                if (!JsonElement.DeepEquals(incomingContent, recordedContent))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool MatchTools(JsonElement incoming, JsonElement recorded)
    {
        if (incoming.ValueKind != JsonValueKind.Array || recorded.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var incomingArray = incoming.EnumerateArray().ToArray();
        var recordedArray = recorded.EnumerateArray().ToArray();

        if (incomingArray.Length != recordedArray.Length)
        {
            return false;
        }

        // For tools, just check that the tool names match (parameters might vary)
        for (var i = 0; i < incomingArray.Length; i++)
        {
            var incomingTool = incomingArray[i];
            var recordedTool = recordedArray[i];

            if (
                recordedTool.TryGetProperty("function", out var recordedFunc)
                && incomingTool.TryGetProperty("function", out var incomingFunc)
            )
            {
                if (
                    recordedFunc.TryGetProperty("name", out var recordedName)
                    && incomingFunc.TryGetProperty("name", out var incomingName)
                )
                {
                    if (recordedName.GetString() != incomingName.GetString())
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }
}

/// <summary>
/// Response provider that forwards unmatched requests to real APIs
/// </summary>
internal class ApiForwardingProvider : IResponseProvider, IDisposable
{
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;
    private readonly Action<RecordedInteraction>? _onNewInteraction;
    private readonly string _baseUrlTrimmed;
    private readonly AuthenticationHeaderValue _authHeader;
    private bool _disposed;

    // Static cache for provider determination to avoid repeated string checks
    private static readonly ConcurrentDictionary<string, string> _providerCache = new();

    public ApiForwardingProvider(string baseUrl, string apiKey, Action<RecordedInteraction>? onNewInteraction = null)
    {
        _baseUrl = baseUrl;
        _apiKey = apiKey;
        _httpClient = new HttpClient();
        _onNewInteraction = onNewInteraction;

        // Pre-compute frequently used values
        _baseUrlTrimmed = _baseUrl.TrimEnd('/');
        _authHeader = new AuthenticationHeaderValue("Bearer", _apiKey);
    }

    public bool CanHandle(HttpRequestMessage request, int requestIndex)
    {
        return _disposed ? throw new ObjectDisposedException(nameof(ApiForwardingProvider)) : true;
    }

    public async Task<HttpResponseMessage> CreateResponseAsync(HttpRequestMessage request, int requestIndex)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ApiForwardingProvider));
        }

        // Clone the request for forwarding - use optimized method
        var forwardRequest = await CloneRequestAsync(request);

        // Set the base URL and authentication
        forwardRequest.RequestUri = request.RequestUri;
        forwardRequest.Headers.Authorization = _authHeader;

        // Forward to real API
        var response = await _httpClient.SendAsync(forwardRequest, HttpCompletionOption.ResponseHeadersRead);

        // Record the interaction if callback provided
        if (_onNewInteraction != null && request.Content != null)
        {
            await RecordInteractionAsync(request, response);
        }

        return response;
    }

    private async Task RecordInteractionAsync(HttpRequestMessage request, HttpResponseMessage response)
    {
        try
        {
            if (request.Content == null || _onNewInteraction == null)
            {
                return;
            }

            // Read request and response content only when needed for recording
            var requestContent = await request.Content.ReadAsStringAsync();
            var responseContent = await response.Content.ReadAsStringAsync();

            // Use JsonDocument.Parse with minimal allocations
            using var requestDoc = JsonDocument.Parse(requestContent);
            using var responseDoc = JsonDocument.Parse(responseContent);

            var interaction = new RecordedInteraction
            {
                SerializedRequest = requestDoc.RootElement.Clone(),
                SerializedResponse = responseDoc.RootElement.Clone(),
                IsStreaming = false,
                RecordedAt = DateTime.UtcNow,
                Provider = GetCachedProvider(request),
            };

            _onNewInteraction(interaction);
        }
        catch
        {
            // Ignore recording errors - don't fail the actual request
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        // Copy headers efficiently
        foreach (var header in request.Headers)
        {
            _ = clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        // Copy content efficiently using byte arrays instead of strings when possible
        if (request.Content != null)
        {
            var contentBytes = await request.Content.ReadAsByteArrayAsync();
            var byteContent = new ByteArrayContent(contentBytes);

            // Copy content headers
            foreach (var header in request.Content.Headers)
            {
                _ = byteContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            clone.Content = byteContent;
        }

        return clone;
    }

    private static string GetCachedProvider(HttpRequestMessage request)
    {
        // Create a cache key based on the request URI
        var cacheKey = request.RequestUri?.Host ?? "unknown";

        return _providerCache.GetOrAdd(cacheKey, _ => DetermineProvider(request));
    }

    private static string DetermineProvider(HttpRequestMessage request)
    {
        return request.IsAnthropicRequest() ? "Anthropic" : request.IsOpenAIRequest() ? "OpenAI" : "OpenAI";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _httpClient.Dispose();
        }
    }
}

/// <summary>
/// Middleware for record/playback functionality with proper fallback
/// </summary>
internal class RecordPlaybackMiddleware : IHttpHandlerMiddleware, IDisposable
{
    private readonly string _filePath;
    private readonly RecordPlaybackData _recordedData;
    private readonly Dictionary<string, byte[]> _cachedResponseBytes = [];
    private readonly List<RecordedInteraction> _newInteractions = [];
    private readonly object _cacheLock = new();
    private int _matchedInteractions = 0;
    private bool _disposed;
    internal static readonly string[] separator = ["\n\n", "\r\n\r\n"];
    internal static readonly char[] separatorArray = ['\n', '\r'];

    public RecordPlaybackMiddleware(string filePath)
    {
        _filePath = filePath;

        // Load existing recorded data
        _recordedData = LoadRecordedData();

        // Pre-cache all response bytes for known interactions
        PreCacheResponses();
    }

    private void PreCacheResponses()
    {
        foreach (var interaction in _recordedData.Interactions)
        {
            // Generate a cache key based on the interaction's unique properties
            var cacheKey = GenerateCacheKey(interaction);
            if (!_cachedResponseBytes.ContainsKey(cacheKey))
            {
                byte[] responseBytes;

                if (interaction.IsStreaming && interaction.SerializedResponseFragments.Count > 0)
                {
                    // For SSE responses, reconstruct the SSE format from fragments
                    var sseContent = ReconstructSSEFromFragments(interaction.SerializedResponseFragments);
                    responseBytes = Encoding.UTF8.GetBytes(sseContent);
                }
                else
                {
                    // For regular JSON responses - handle both old and new formats
                    // Old format: JsonElement that needs to be serialized
                    // New format: Raw JSON content (we'll detect this by checking if it's a simple string)
                    var responseJson = JsonSerializer.Serialize(interaction.SerializedResponse);
                    responseBytes = Encoding.UTF8.GetBytes(responseJson);
                }

                _cachedResponseBytes[cacheKey] = responseBytes;
            }
        }
    }

    private static string GenerateCacheKey(RecordedInteraction interaction)
    {
        // Generate SHA256 hash based on the complete shape of the SerializedRequest
        if (interaction.SerializedRequest.ValueKind == JsonValueKind.Undefined)
        {
            return "undefined-request";
        }

        // Serialize to canonical JSON to ensure consistent hashing
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        };

        var canonicalJson = JsonSerializer.Serialize(interaction.SerializedRequest, jsonOptions);

        // Compute SHA256 hash
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson));

        // Convert to hex string
        return Convert.ToHexString(hashBytes);
    }

    public async Task<HttpResponseMessage?> HandleAsync(
        HttpRequestMessage request,
        int requestIndex,
        Func<Task<HttpResponseMessage?>>? next
    )
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RecordPlaybackMiddleware));
        }

        try
        {
            // Get request content for matching
            var requestContent = request.Content != null ? await request.Content.ReadAsStringAsync() : "null";

            // Reset content stream for matching - preserve original content type and headers
            if (request.Content != null && !string.IsNullOrEmpty(requestContent) && requestContent != "null")
            {
                var originalContentType = request.Content.Headers.ContentType;
                var originalHeaders = request.Content.Headers.ToList();

                request.Content = new StringContent(
                    requestContent,
                    Encoding.UTF8,
                    originalContentType?.MediaType ?? "application/json"
                );

                // Restore any additional headers
                foreach (var header in originalHeaders)
                {
                    if (header.Key != "Content-Type")
                    {
                        _ = request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Parse request content to JsonElement for matching
            JsonElement requestElement;
            try
            {
                if (string.IsNullOrEmpty(requestContent) || requestContent == "null")
                {
                    requestElement = default; // JsonElement with ValueKind.Undefined
                }
                else
                {
                    using var document = JsonDocument.Parse(requestContent);
                    requestElement = document.RootElement.Clone();
                }
            }
            catch
            {
                // If parsing fails, use undefined element
                requestElement = default;
            }

            // Try to find a matching recorded interaction
            var match = FindMatchingInteraction(requestElement);
            if (match != null)
            {
                _matchedInteractions++;
                return CreateResponseFromRecording(match);
            }

            // If no match, try next middleware and record the interaction
            if (next != null)
            {
                var response = await next();
                if (response != null)
                {
                    // Record this new interaction for future playback
                    await RecordNewInteractionAsync(requestElement, response, request);
                    return response;
                }
            }

            // No match and no other middleware could handle it
            throw new InvalidOperationException($"No recorded interaction found for request to {request.RequestUri}");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No recorded interaction found"))
        {
            // Re-throw the intended exception
            throw;
        }
        catch
        {
            // If anything else fails, let next middleware try
            return next != null ? await next() : null;
        }
    }

    private RecordedInteraction? FindMatchingInteraction(JsonElement requestElement)
    {
        foreach (var interaction in _recordedData.Interactions)
        {
            if (RequestMatcher.MatchesRecordedRequest(requestElement, interaction.SerializedRequest, exactMatch: false))
            {
                return interaction;
            }
        }
        return null;
    }

    private async Task RecordNewInteractionAsync(
        JsonElement requestElement,
        HttpResponseMessage response,
        HttpRequestMessage originalRequest
    )
    {
        try
        {
            // Check if this is an SSE response
            var isSSE = IsServerSentEventsResponse(response);

            // Read response content
            var responseContent = await response.Content.ReadAsStringAsync();

            // Reset response content stream so it can be read again by the caller
            if (!string.IsNullOrEmpty(responseContent))
            {
                var originalContentType = response.Content.Headers.ContentType;
                var originalHeaders = response.Content.Headers.ToList();

                response.Content = new StringContent(
                    responseContent,
                    Encoding.UTF8,
                    originalContentType?.MediaType ?? (isSSE ? "text/event-stream" : "application/json")
                );

                // Restore any additional headers
                foreach (var header in originalHeaders)
                {
                    if (header.Key != "Content-Type")
                    {
                        _ = response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }
            }

            // Create new recorded interaction
            var interaction = new RecordedInteraction
            {
                SerializedRequest = requestElement,
                IsStreaming = isSSE,
                RecordedAt = DateTime.UtcNow,
                Provider = DetermineProvider(originalRequest),
            };

            if (isSSE)
            {
                // Parse SSE content into fragments
                var sseFragments = ParseSSEContent(responseContent);
                interaction.SerializedResponseFragments = sseFragments;

                // For SSE, set SerializedResponse to undefined since we use fragments
                interaction.SerializedResponse = default;
            }
            else
            {
                // For non-SSE responses, store the raw JSON content directly
                // This preserves all fields including reasoning tokens that might not round-trip properly
                JsonElement responseElement;
                try
                {
                    if (string.IsNullOrEmpty(responseContent))
                    {
                        responseElement = default; // JsonElement with ValueKind.Undefined
                    }
                    else
                    {
                        using var document = JsonDocument.Parse(responseContent);
                        responseElement = document.RootElement.Clone();
                    }
                }
                catch
                {
                    // If parsing fails, use undefined element
                    responseElement = default;
                }

                interaction.SerializedResponse = responseElement;
                interaction.SerializedResponseFragments = [];
            }

            // Add to new interactions list
            lock (_cacheLock)
            {
                _newInteractions.Add(interaction);

                // Cache the response bytes for this new interaction
                var cacheKey = GenerateCacheKey(interaction);
                if (!_cachedResponseBytes.ContainsKey(cacheKey))
                {
                    byte[] responseBytes;
                    if (isSSE)
                    {
                        // For SSE, cache the original SSE content
                        responseBytes = Encoding.UTF8.GetBytes(responseContent);
                    }
                    else
                    {
                        // For JSON, cache the RAW response content instead of re-serializing
                        // This preserves all fields including reasoning tokens
                        responseBytes = Encoding.UTF8.GetBytes(responseContent);
                    }
                    _cachedResponseBytes[cacheKey] = responseBytes;
                }
            }

            // Save new interactions to file
            SaveNewInteractions();
        }
        catch
        {
            // Ignore recording errors - don't fail the actual request
        }
    }

    private static string DetermineProvider(HttpRequestMessage request)
    {
        return request.IsAnthropicRequest() ? "Anthropic" : request.IsOpenAIRequest() ? "OpenAI" : "OpenAI";
    }

    private static bool IsServerSentEventsResponse(HttpResponseMessage response)
    {
        // Check if the content type indicates Server-Sent Events
        var contentType = response.Content?.Headers?.ContentType?.MediaType;
        return string.Equals(contentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
    }

    private static List<JsonElement> ParseSSEContent(string sseContent)
    {
        var fragments = new List<JsonElement>();

        if (string.IsNullOrEmpty(sseContent))
        {
            return fragments;
        }

        try
        {
            // Split SSE content by double newlines (event boundaries)
            var events = sseContent.Split(separator, StringSplitOptions.RemoveEmptyEntries);

            foreach (var eventBlock in events)
            {
                var dataLines = new List<string>();
                var lines = eventBlock.Split(separatorArray, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    // Extract data lines (lines starting with "data: ")
                    if (line.StartsWith("data: ", StringComparison.OrdinalIgnoreCase))
                    {
                        var data = line[6..]; // Remove "data: " prefix
                        if (!string.IsNullOrWhiteSpace(data) && data.Trim() != "[DONE]")
                        {
                            dataLines.Add(data.Trim());
                        }
                    }
                }

                // Parse each data line as JSON and add to fragments
                foreach (var dataLine in dataLines)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(dataLine);
                        fragments.Add(document.RootElement.Clone());
                    }
                    catch
                    {
                        // If JSON parsing fails, skip this fragment
                        // This handles malformed JSON or non-JSON data lines
                    }
                }
            }
        }
        catch
        {
            // If SSE parsing fails completely, return empty list
            // The caller will handle this gracefully
        }

        return fragments;
    }

    private static string ReconstructSSEFromFragments(List<JsonElement> fragments)
    {
        var sseBuilder = new StringBuilder();

        foreach (var fragment in fragments)
        {
            try
            {
                // Serialize each fragment back to JSON
                var jsonData = JsonSerializer.Serialize(fragment);

                // Format as SSE event
                _ = sseBuilder.AppendLine($"data: {jsonData}");
                _ = sseBuilder.AppendLine(); // Empty line to separate events
            }
            catch
            {
                // Skip malformed fragments
            }
        }

        // Add final [DONE] marker for streaming completion
        if (fragments.Count > 0)
        {
            _ = sseBuilder.AppendLine("data: [DONE]");
            _ = sseBuilder.AppendLine();
        }

        return sseBuilder.ToString();
    }

    private void SaveNewInteractions()
    {
        if (_newInteractions.Count == 0 || _disposed)
        {
            return;
        }

        try
        {
            lock (_cacheLock)
            {
                // Add new interactions to existing data
                _recordedData.Interactions.AddRange(_newInteractions);
                _newInteractions.Clear();
            }

            // Save updated data to file
            var json = JsonSerializer.Serialize(
                _recordedData,
                new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault,
                }
            );
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Ignore save errors - don't fail the actual request
        }
    }

    private HttpResponseMessage CreateResponseFromRecording(RecordedInteraction interaction)
    {
        var cacheKey = GenerateCacheKey(interaction);
        byte[] responseBytes;

        // Try to get cached response bytes
        lock (_cacheLock)
        {
            if (!_cachedResponseBytes.TryGetValue(cacheKey, out responseBytes!))
            {
                // Cache miss - serialize and cache for future use
                if (interaction.IsStreaming && interaction.SerializedResponseFragments.Count > 0)
                {
                    // For SSE responses, reconstruct the SSE format from fragments
                    var sseContent = ReconstructSSEFromFragments(interaction.SerializedResponseFragments);
                    responseBytes = Encoding.UTF8.GetBytes(sseContent);
                }
                else
                {
                    // For regular JSON responses - handle both old and new formats
                    // Old format: JsonElement that needs to be serialized
                    // New format: Raw JSON content would be stored directly (future enhancement)
                    var responseJson = JsonSerializer.Serialize(interaction.SerializedResponse);
                    responseBytes = Encoding.UTF8.GetBytes(responseJson);
                }
                _cachedResponseBytes[cacheKey] = responseBytes;
            }
        }

        // Create response with cached bytes
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        var content = new ByteArrayContent(responseBytes);

        // Set appropriate content type based on whether it's streaming
        if (interaction.IsStreaming)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
            response.Headers.Add("Connection", "keep-alive");
        }
        else
        {
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }

        response.Content = content;
        return response;
    }

    private RecordPlaybackData LoadRecordedData()
    {
        if (!File.Exists(_filePath))
        {
            return new RecordPlaybackData();
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var data = JsonSerializer.Deserialize<RecordPlaybackData>(json);
            return data ?? new RecordPlaybackData();
        }
        catch
        {
            return new RecordPlaybackData();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Save any remaining new interactions before disposing
            SaveNewInteractions();

            // Clear caches
            _cachedResponseBytes.Clear();
        }
    }
}

/*
 * Example usage for MockHttpHandlerBuilder:
 *
 * var handler = MockHttpHandlerBuilder.Create()
 *     .RespondWithAnthropicMessage("Hello from Claude!")
 *     .Build();
 *
 * var httpClient = new HttpClient(handler);
 * // Use httpClient with your provider classes
 */
