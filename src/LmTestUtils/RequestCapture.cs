using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
///     Generic request capture that provides type-safe access to requests and responses
///     Inherits from RequestCaptureBase for backward compatibility
/// </summary>
public class RequestCapture<TRequest, TResponse> : RequestCaptureBase
    where TRequest : class
    where TResponse : class
{
    /// <summary>
    ///     Gets the most recent request as the specified request type
    /// </summary>
    public TRequest? GetRequest()
    {
        return GetRequestAs<TRequest>();
    }

    /// <summary>
    ///     Gets the most recent response as the specified response type (for non-streaming)
    /// </summary>
    public TResponse? GetResponse()
    {
        return GetResponseAs<TResponse>();
    }

    /// <summary>
    ///     Gets all responses as the specified response type (for streaming)
    /// </summary>
    public IEnumerable<TResponse> GetResponses()
    {
        return GetResponsesAs<TResponse>();
    }

    /// <summary>
    ///     Sets a response for the most recent request
    /// </summary>
    public void SetResponse(TResponse response)
    {
        SetResponse((object)response);
    }

    /// <summary>
    ///     Sets multiple responses for streaming scenarios
    /// </summary>
    public void SetResponses(IEnumerable<TResponse> responses)
    {
        SetResponse(responses);
    }
}

/// <summary>
///     Non-generic RequestCapture for backward compatibility
///     Provides the same API as the original RequestCapture class
/// </summary>
public class RequestCapture : RequestCaptureBase
{
    /// <summary>
    ///     Gets the most recent request parsed as an Anthropic request
    /// </summary>
    public AnthropicRequestCapture? GetAnthropicRequest()
    {
        var body = LastRequestBody;
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return new AnthropicRequestCapture(document.RootElement.Clone(), LastRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Gets the most recent request parsed as an OpenAI request
    /// </summary>
    public OpenAIRequestCapture? GetOpenAIRequest()
    {
        var body = LastRequestBody;
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return new OpenAIRequestCapture(document.RootElement.Clone(), LastRequest);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
///     Wrapper for Anthropic requests that provides structured access to request data
/// </summary>
public class AnthropicRequestCapture
{
    private readonly JsonElement _requestJson;

    internal AnthropicRequestCapture(JsonElement requestJson, HttpRequestMessage? httpRequest)
    {
        _requestJson = requestJson;
        HttpRequest = httpRequest;
    }

    /// <summary>
    ///     Gets the model from the request
    /// </summary>
    public string? Model => _requestJson.TryGetProperty("model", out var model) ? model.GetString() : null;

    /// <summary>
    ///     Gets the max tokens from the request
    /// </summary>
    public int? MaxTokens => _requestJson.TryGetProperty("max_tokens", out var maxTokens) ? maxTokens.GetInt32() : null;

    /// <summary>
    ///     Gets whether streaming was requested
    /// </summary>
    public bool? Stream => _requestJson.TryGetProperty("stream", out var stream) ? stream.GetBoolean() : null;

    /// <summary>
    ///     Gets the thinking configuration if present
    /// </summary>
    public ThinkingCapture? Thinking =>
        _requestJson.TryGetProperty("thinking", out var thinking) ? new ThinkingCapture(thinking) : null;

    /// <summary>
    ///     Gets the messages from the request
    /// </summary>
    public IEnumerable<MessageCapture> Messages =>
        _requestJson.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.EnumerateArray().Select(msg => new MessageCapture(msg))
            : [];

    /// <summary>
    ///     Gets the system message if present
    /// </summary>
    public string? System => _requestJson.TryGetProperty("system", out var system) ? system.GetString() : null;

    /// <summary>
    ///     Gets the tools from the request
    /// </summary>
    public IEnumerable<ToolCapture> Tools =>
        _requestJson.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array
            ? tools
                .EnumerateArray()
                .Select(tool => new ToolCapture
                {
                    Type = tool.TryGetProperty("type", out var type) ? type.GetString() : null,
                    Name = tool.TryGetProperty("name", out var name) ? name.GetString() : null,
                    Description = tool.TryGetProperty("description", out var desc) ? desc.GetString() : null,
                    InputSchema = tool.TryGetProperty("input_schema", out var schema) ? schema : null,
                })
            : [];

    /// <summary>
    ///     Gets the underlying HTTP request
    /// </summary>
    public HttpRequestMessage? HttpRequest { get; }
}

/// <summary>
///     Wrapper for OpenAI requests that provides structured access to request data
/// </summary>
public class OpenAIRequestCapture
{
    private readonly JsonElement _requestJson;

    internal OpenAIRequestCapture(JsonElement requestJson, HttpRequestMessage? httpRequest)
    {
        _requestJson = requestJson;
        HttpRequest = httpRequest;
    }

    /// <summary>
    ///     Gets the model from the request
    /// </summary>
    public string? Model => _requestJson.TryGetProperty("model", out var model) ? model.GetString() : null;

    /// <summary>
    ///     Gets the max tokens from the request
    /// </summary>
    public int? MaxTokens => _requestJson.TryGetProperty("max_tokens", out var maxTokens) ? maxTokens.GetInt32() : null;

    /// <summary>
    ///     Gets the temperature from the request
    /// </summary>
    public double? Temperature => _requestJson.TryGetProperty("temperature", out var temp) ? temp.GetDouble() : null;

    /// <summary>
    ///     Gets whether streaming was requested
    /// </summary>
    public bool? Stream => _requestJson.TryGetProperty("stream", out var stream) ? stream.GetBoolean() : null;

    /// <summary>
    ///     Gets the messages from the request
    /// </summary>
    public IEnumerable<MessageCapture> Messages =>
        _requestJson.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array
            ? messages.EnumerateArray().Select(msg => new MessageCapture(msg))
            : [];

    /// <summary>
    ///     Gets the underlying HTTP request
    /// </summary>
    public HttpRequestMessage? HttpRequest { get; }
}

/// <summary>
///     Provides access to thinking configuration data
/// </summary>
public class ThinkingCapture
{
    private readonly JsonElement _thinkingJson;

    internal ThinkingCapture(JsonElement thinkingJson)
    {
        _thinkingJson = thinkingJson;
    }

    /// <summary>
    ///     Gets the budget tokens for thinking
    /// </summary>
    public int? BudgetTokens =>
        _thinkingJson.TryGetProperty("budget_tokens", out var budget) ? budget.GetInt32() : null;
}

/// <summary>
///     Provides access to message data
/// </summary>
public class MessageCapture
{
    private readonly JsonElement _messageJson;

    internal MessageCapture(JsonElement messageJson)
    {
        _messageJson = messageJson;
    }

    /// <summary>
    ///     Gets the role of the message
    /// </summary>
    public string? Role => _messageJson.TryGetProperty("role", out var role) ? role.GetString() : null;

    /// <summary>
    ///     Gets the content of the message (simplified - may be text or complex content)
    /// </summary>
    public string? Content
    {
        get
        {
            if (_messageJson.TryGetProperty("content", out var content))
            {
                if (content.ValueKind == JsonValueKind.String)
                {
                    return content.GetString();
                }

                if (content.ValueKind == JsonValueKind.Array)
                {
                    // Try to extract text from content array
                    var textContent = content
                        .EnumerateArray()
                        .FirstOrDefault(item =>
                            item.TryGetProperty("type", out var type) && type.GetString() == "text"
                        );

                    if (textContent.TryGetProperty("text", out var text))
                    {
                        return text.GetString();
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    ///     Gets the number of content items in the message
    /// </summary>
    public int ContentItemCount =>
        _messageJson.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.GetArrayLength()
        : _messageJson.TryGetProperty("content", out _) ? 1
        : 0;
}

/// <summary>
///     Simplified tool capture class for backward compatibility
///     This avoids conflicts with the real provider types
/// </summary>
public class ToolCapture
{
    public string? Type { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public object? InputSchema { get; set; }

    /// <summary>
    ///     Checks if the tool has the specified property in its input schema
    /// </summary>
    public bool HasInputProperty(string propertyName)
    {
        if (InputSchema is JsonElement schema)
        {
            if (schema.TryGetProperty("properties", out var properties))
            {
                return properties.TryGetProperty(propertyName, out _);
            }
        }

        return false;
    }

    /// <summary>
    ///     Gets the type of a specific input property
    /// </summary>
    public string? GetInputPropertyType(string propertyName)
    {
        if (InputSchema is JsonElement schema)
        {
            if (
                schema.TryGetProperty("properties", out var properties)
                && properties.TryGetProperty(propertyName, out var property)
                && property.TryGetProperty("type", out var type)
            )
            {
                return type.GetString();
            }
        }

        return null;
    }
}
