using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.SSE;

/// <summary>
/// Helper class for streaming IMessage over Server-Sent Events (SSE).
/// </summary>
public sealed class IMessageSseStreamer
{
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<IMessageSseStreamer> _logger;

    public IMessageSseStreamer(
        ILogger<IMessageSseStreamer> logger,
        IOptions<LmStreamingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger;
        _jsonOptions = options.Value.WriteIndentedJson
            ? JsonSerializerOptionsFactory.CreateForTesting()
            : JsonSerializerOptionsFactory.CreateForProduction();
    }

    /// <summary>
    /// Streams IMessages to an HTTP response as SSE events.
    /// </summary>
    /// <param name="response">The HTTP response to stream to</param>
    /// <param name="messages">The messages to stream</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task StreamAsync(
        HttpResponse response,
        IAsyncEnumerable<IMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Set SSE headers
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
        response.Headers["X-Accel-Buffering"] = "no"; // Disable nginx buffering

        await foreach (var message in messages.WithCancellation(cancellationToken))
        {
            await WriteMessageAsync(response, message, cancellationToken);
        }
    }

    /// <summary>
    /// Writes a single IMessage as an SSE event.
    /// </summary>
    public async Task WriteMessageAsync(
        HttpResponse response,
        IMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(message);

        var json = JsonSerializer.Serialize(message, _jsonOptions);
        var sseData = $"data: {json}\n\n";
        var bytes = Encoding.UTF8.GetBytes(sseData);

        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);

        _logger.LogDebug("Streamed SSE message type: {MessageType}", message.GetType().Name);
    }

    /// <summary>
    /// Writes an error message as an SSE event.
    /// </summary>
    public async Task WriteErrorAsync(
        HttpResponse response,
        string error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var errorData = $"event: error\ndata: {JsonSerializer.Serialize(new { error })}\n\n";
        var bytes = Encoding.UTF8.GetBytes(errorData);

        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);

        _logger.LogDebug("Streamed SSE error: {Error}", error);
    }

    /// <summary>
    /// Writes a done event to signal stream completion.
    /// </summary>
    public async Task WriteDoneAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        var doneData = "event: done\ndata: {}\n\n";
        var bytes = Encoding.UTF8.GetBytes(doneData);

        await response.Body.WriteAsync(bytes, cancellationToken);
        await response.Body.FlushAsync(cancellationToken);

        _logger.LogDebug("Streamed SSE done event");
    }
}
