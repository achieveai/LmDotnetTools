using System.Net;
using System.Text;
using System.Text.Json;

namespace AchieveAi.LmDotnetTools.LmTestUtils;

/// <summary>
///     Fake HTTP message handler for testing HTTP requests without making real network calls
///     Based on the pattern described in mocking-httpclient.md
///     Shared utility for all LmDotnetTools provider testing
/// </summary>
public class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handlerFunc;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc)
    {
        _handlerFunc = handlerFunc ?? throw new ArgumentNullException(nameof(handlerFunc));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        return _handlerFunc(request, cancellationToken);
    }

    /// <summary>
    ///     Creates a simple fake handler with a custom response function
    /// </summary>
    /// <param name="responseFunc">Function to generate responses</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateSimpleHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFunc)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) => Task.FromResult(responseFunc(request)));
    }

    /// <summary>
    ///     Creates a simple fake handler that returns a successful response with JSON content
    /// </summary>
    /// <param name="jsonResponse">The JSON response to return</param>
    /// <param name="statusCode">The HTTP status code to return</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateSimpleJsonHandler(
        string jsonResponse,
        HttpStatusCode statusCode = HttpStatusCode.OK
    )
    {
        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var response = new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
                };
                return Task.FromResult(response);
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler that can respond to multiple different requests
    /// </summary>
    /// <param name="responses">Dictionary mapping request patterns to responses</param>
    /// <returns>A configured fake handler</returns>
    public static FakeHttpMessageHandler CreateMultiResponseHandler(
        Dictionary<string, (string json, HttpStatusCode status)> responses
    )
    {
        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var key = $"{request.Method} {request.RequestUri?.PathAndQuery}";

                if (responses.TryGetValue(key, out var response))
                {
                    var httpResponse = new HttpResponseMessage(response.status)
                    {
                        Content = new StringContent(response.json, Encoding.UTF8, "application/json"),
                    };
                    return Task.FromResult(httpResponse);
                }

                // Default: return 404
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = new StringContent("Not Found", Encoding.UTF8, "text/plain"),
                    }
                );
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler that simulates network errors or timeouts
    /// </summary>
    /// <param name="exception">The exception to throw</param>
    /// <returns>A configured fake handler that throws exceptions</returns>
    public static FakeHttpMessageHandler CreateErrorHandler(Exception exception)
    {
        return new FakeHttpMessageHandler((request, cancellationToken) => throw exception);
    }

    /// <summary>
    ///     Creates a fake handler that simulates retry scenarios
    /// </summary>
    /// <param name="failureCount">Number of times to fail before succeeding</param>
    /// <param name="successResponse">The successful response to return after failures</param>
    /// <param name="failureStatus">The HTTP status to return for failures</param>
    /// <returns>A configured fake handler for retry testing</returns>
    public static FakeHttpMessageHandler CreateRetryHandler(
        int failureCount,
        string successResponse,
        HttpStatusCode failureStatus = HttpStatusCode.InternalServerError
    )
    {
        var attemptCount = 0;

        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                attemptCount++;

                return attemptCount <= failureCount
                    ? Task.FromResult(
                        new HttpResponseMessage(failureStatus)
                        {
                            Content = new StringContent($"Failure attempt {attemptCount}", Encoding.UTF8, "text/plain"),
                        }
                    )
                    : Task.FromResult(
                        new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(successResponse, Encoding.UTF8, "application/json"),
                        }
                    );
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler that returns a sequence of status codes followed by success
    /// </summary>
    /// <param name="statusCodes">Sequence of status codes to return</param>
    /// <param name="successResponse">The successful response to return after all status codes</param>
    /// <returns>A configured fake handler for status code sequence testing</returns>
    public static FakeHttpMessageHandler CreateStatusCodeSequenceHandler(
        HttpStatusCode[] statusCodes,
        string successResponse
    )
    {
        var attemptCount = 0;

        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                if (attemptCount < statusCodes.Length)
                {
                    var statusCode = statusCodes[attemptCount];
                    attemptCount++;

                    return statusCode == HttpStatusCode.OK
                        ? Task.FromResult(
                            new HttpResponseMessage(statusCode)
                            {
                                Content = new StringContent(successResponse, Encoding.UTF8, "application/json"),
                            }
                        )
                        : Task.FromResult(
                            new HttpResponseMessage(statusCode)
                            {
                                Content = new StringContent($"Error {(int)statusCode}", Encoding.UTF8, "text/plain"),
                            }
                        );
                }

                // If we've exhausted the sequence, return success
                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(successResponse, Encoding.UTF8, "application/json"),
                    }
                );
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler with request capture capability
    ///     Essential for validating API request formatting and headers
    /// </summary>
    /// <param name="responseJson">JSON response to return</param>
    /// <param name="capturedRequest">Out parameter to receive captured request</param>
    /// <param name="statusCode">HTTP status code to return</param>
    /// <returns>A configured fake handler that captures requests for validation</returns>
    public static FakeHttpMessageHandler CreateRequestCaptureHandler(
        string responseJson,
        out CapturedRequestContainer capturedRequest,
        HttpStatusCode statusCode = HttpStatusCode.OK
    )
    {
        var container = new CapturedRequestContainer();
        capturedRequest = container;

        return new FakeHttpMessageHandler(
            async (request, cancellationToken) =>
            {
                container.Request = request;
                container.RequestBody =
                    request.Content != null ? await request.Content.ReadAsStringAsync(cancellationToken) : string.Empty;

                return new HttpResponseMessage(statusCode)
                {
                    Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
                };
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler that returns an OpenAI-formatted chat completion response
    /// </summary>
    /// <param name="content">The response content text</param>
    /// <param name="model">The model name</param>
    /// <param name="promptTokens">Number of prompt tokens</param>
    /// <param name="completionTokens">Number of completion tokens</param>
    /// <returns>A configured fake handler for OpenAI responses</returns>
    public static FakeHttpMessageHandler CreateOpenAIResponseHandler(
        string content = "Hello! How can I help you today?",
        string model = "gpt-4",
        int promptTokens = 10,
        int completionTokens = 20
    )
    {
        var response = new
        {
            id = "chatcmpl-test123",
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
        return CreateSimpleJsonHandler(jsonResponse);
    }

    /// <summary>
    ///     Creates a fake handler that returns an Anthropic-formatted message response
    /// </summary>
    /// <param name="content">The response content text</param>
    /// <param name="model">The model name</param>
    /// <param name="inputTokens">Number of input tokens</param>
    /// <param name="outputTokens">Number of output tokens</param>
    /// <returns>A configured fake handler for Anthropic responses</returns>
    public static FakeHttpMessageHandler CreateAnthropicResponseHandler(
        string content = "Hello! How can I help you today?",
        string model = "claude-3-sonnet-20240229",
        int inputTokens = 10,
        int outputTokens = 20
    )
    {
        var response = new
        {
            type = "message", // Must be first property for polymorphic deserialization
            id = "msg_test123",
            role = "assistant",
            content = new[]
            {
                new
                {
                    type = "text", // Must be first property for polymorphic deserialization
                    text = content,
                },
            },
            model,
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = inputTokens, output_tokens = outputTokens },
        };

        var jsonResponse = JsonSerializer.Serialize(response);
        return CreateSimpleJsonHandler(jsonResponse);
    }

    /// <summary>
    ///     Creates a fake handler that returns a streaming OpenAI response
    /// </summary>
    /// <param name="content">The content to stream</param>
    /// <param name="model">The model name</param>
    /// <returns>A configured fake handler for OpenAI streaming responses</returns>
    public static FakeHttpMessageHandler CreateOpenAIStreamingHandler(
        string content = "Hello world",
        string model = "gpt-4"
    )
    {
        var streamData = new StringBuilder();

        // Stream start
        _ = streamData.AppendLine(
            "data: "
                + JsonSerializer.Serialize(
                    new
                    {
                        id = "chatcmpl-test123",
                        @object = "chat.completion.chunk",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new { role = "assistant" },
                                finish_reason = (string?)null,
                            },
                        },
                    }
                )
        );

        // Content chunks
        foreach (var c in content)
        {
            _ = streamData.AppendLine(
                "data: "
                    + JsonSerializer.Serialize(
                        new
                        {
                            id = "chatcmpl-test123",
                            @object = "chat.completion.chunk",
                            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            model,
                            choices = new[]
                            {
                                new
                                {
                                    index = 0,
                                    delta = new { content = c.ToString() },
                                    finish_reason = (string?)null,
                                },
                            },
                        }
                    )
            );
        }

        // Stream end
        _ = streamData.AppendLine(
            "data: "
                + JsonSerializer.Serialize(
                    new
                    {
                        id = "chatcmpl-test123",
                        @object = "chat.completion.chunk",
                        created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        model,
                        choices = new[]
                        {
                            new
                            {
                                index = 0,
                                delta = new { },
                                finish_reason = "stop",
                            },
                        },
                    }
                )
        );

        _ = streamData.AppendLine("data: [DONE]");

        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
                Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(streamData.ToString(), Encoding.UTF8, "text/plain"),
                    }
                )
        );
    }

    /// <summary>
    ///     Creates a fake handler that returns a Server-Sent Events (SSE) stream
    /// </summary>
    /// <param name="events">Collection of SSE events to stream</param>
    /// <returns>A configured fake handler for SSE streaming responses</returns>
    public static FakeHttpMessageHandler CreateSseStreamHandler(IEnumerable<SseEvent> events)
    {
        var streamData = new StringBuilder();

        foreach (var sseEvent in events)
        {
            if (!string.IsNullOrEmpty(sseEvent.Id))
            {
                _ = streamData.AppendLine($"id: {sseEvent.Id}");
            }

            if (!string.IsNullOrEmpty(sseEvent.Event))
            {
                _ = streamData.AppendLine($"event: {sseEvent.Event}");
            }

            if (!string.IsNullOrEmpty(sseEvent.Data))
            {
                // Handle multi-line data by prefixing each line with "data: "
                var dataLines = sseEvent.Data.Split('\n');
                foreach (var line in dataLines)
                {
                    _ = streamData.AppendLine($"data: {line}");
                }
            }

            // Empty line to separate events
            _ = streamData.AppendLine();
        }

        return new FakeHttpMessageHandler(
            (request, cancellationToken) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(streamData.ToString(), Encoding.UTF8, "text/event-stream"),
                };

                // Set SSE-specific headers
                response.Headers.Add("Cache-Control", "no-cache");
                response.Headers.Add("Connection", "keep-alive");

                return Task.FromResult(response);
            }
        );
    }

    /// <summary>
    ///     Creates a fake handler that returns a simple SSE stream with text data
    /// </summary>
    /// <param name="messages">Collection of text messages to stream</param>
    /// <param name="eventType">Optional event type for all messages</param>
    /// <returns>A configured fake handler for simple SSE streaming</returns>
    public static FakeHttpMessageHandler CreateSimpleSseStreamHandler(
        IEnumerable<string> messages,
        string? eventType = null
    )
    {
        var events = messages.Select(
            (message, index) =>
                new SseEvent
                {
                    Id = (index + 1).ToString(),
                    Event = eventType,
                    Data = message,
                }
        );

        return CreateSseStreamHandler(events);
    }

    /// <summary>
    ///     Creates a fake handler that returns an SSE stream with JSON data events
    /// </summary>
    /// <param name="jsonObjects">Collection of objects to serialize as JSON events</param>
    /// <param name="eventType">Optional event type for all events</param>
    /// <returns>A configured fake handler for JSON SSE streaming</returns>
    public static FakeHttpMessageHandler CreateJsonSseStreamHandler<T>(
        IEnumerable<T> jsonObjects,
        string? eventType = null
    )
    {
        var events = jsonObjects.Select(
            (obj, index) =>
                new SseEvent
                {
                    Id = (index + 1).ToString(),
                    Event = eventType,
                    Data = JsonSerializer.Serialize(obj),
                }
        );

        return CreateSseStreamHandler(events);
    }
}

/// <summary>
///     Represents a Server-Sent Event for SSE streaming.
///     Contains Id, Event, and Data properties for SSE event information.
/// </summary>
public record SseEvent
{
    public string? Id { get; init; }
    public string? Event { get; init; }
    public string? Data { get; init; }
}

/// <summary>
///     Container for captured HTTP request data
/// </summary>
public class CapturedRequestContainer
{
    public HttpRequestMessage? Request { get; set; }
    public string RequestBody { get; set; } = string.Empty;
}
