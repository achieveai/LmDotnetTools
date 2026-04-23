using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace LmStreaming.Sample.E2E.Tests.Infrastructure;

/// <summary>
/// Thin wrapper around a connected <see cref="WebSocket"/> that sends chat requests and
/// collects streamed response frames up to the <c>{"$type":"done"}</c> sentinel.
/// </summary>
public sealed class WebSocketTestClient : IAsyncDisposable
{
    private readonly System.Net.WebSockets.WebSocket _socket;

    public WebSocketTestClient(System.Net.WebSockets.WebSocket socket)
    {
        _socket = socket ?? throw new ArgumentNullException(nameof(socket));
    }

    /// <summary>Send a user chat message to the server.</summary>
    /// <remarks>
    /// <c>ChatWebSocketManager</c> uses case-sensitive JSON deserialization and expects a PascalCase
    /// <c>Message</c> property — the anonymous type below emits exactly that shape with default
    /// serializer options (no policy = identity casing), so no explicit options are needed.
    /// </remarks>
    public Task SendUserMessageAsync(string text, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new { Message = text });
        var bytes = Encoding.UTF8.GetBytes(json);
        return _socket.SendAsync(
            new ArraySegment<byte>(bytes),
            WebSocketMessageType.Text,
            endOfMessage: true,
            ct);
    }

    /// <summary>
    /// Collect streamed frames until the <c>done</c> sentinel is received or
    /// <paramref name="overallTimeout"/> elapses. Returns raw JSON documents.
    /// </summary>
    public async Task<IReadOnlyList<JsonDocument>> CollectUntilDoneAsync(
        TimeSpan overallTimeout,
        CancellationToken ct = default)
    {
        using var timeoutCts = new CancellationTokenSource(overallTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var frames = new List<JsonDocument>();
        var buffer = new byte[8192];
        var sb = new StringBuilder();

        try
        {
            while (_socket.State == WebSocketState.Open && !linked.Token.IsCancellationRequested)
            {
                sb.Clear();

                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return frames;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                    {
                        sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                    }
                } while (!result.EndOfMessage);

                if (sb.Length == 0)
                {
                    continue;
                }

                var doc = JsonDocument.Parse(sb.ToString());
                frames.Add(doc);

                if (IsDoneFrame(doc))
                {
                    return frames;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Did not observe 'done' frame within {overallTimeout}. Collected {frames.Count} frame(s).");
        }

        return frames;
    }

    private static bool IsDoneFrame(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return doc.RootElement.TryGetProperty("$type", out var typeProp)
            && typeProp.ValueKind == JsonValueKind.String
            && string.Equals(typeProp.GetString(), "done", StringComparison.Ordinal);
    }

    public async Task CloseAsync(CancellationToken ct = default)
    {
        if (_socket.State == WebSocketState.Open)
        {
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test done", ct)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // best-effort close
        }

        _socket.Dispose();
    }
}
