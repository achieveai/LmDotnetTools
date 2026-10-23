using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services.RemoteMcp;

/// <summary>
/// A plugin's streamable-HTTP MCP server inside a handler, so no socket is opened: it answers <c>initialize</c>,
/// <c>tools/list</c> with <paramref name="toolsJson"/> and <c>tools/call</c>, and records each request's JSON-RPC
/// method and headers.
/// </summary>
internal sealed class FakeRemoteMcpHandler(string toolsJson) : HttpMessageHandler
{
    private const string SessionId = "remote-mcp-session";

    // One line: an SSE data field ends at a newline.
    private readonly string _tools = JsonSerializer.Serialize(JsonDocument.Parse(toolsJson).RootElement);
    private readonly object _lock = new();
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>When set, answers every request in place of the server (it may throw); the request is still recorded.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Fail { get; init; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return [.. _requests];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        string? method = null;
        var id = "1";
        if (!string.IsNullOrWhiteSpace(body))
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out var named))
            {
                method = named.GetString();
            }

            // Echo the request's id: the client matches each reply to its request.
            if (document.RootElement.TryGetProperty("id", out var given))
            {
                id = given.GetRawText();
            }
        }

        lock (_lock)
        {
            _requests.Add(
                new RecordedRequest(
                    method,
                    request.Headers.ToDictionary(
                        h => h.Key,
                        h => string.Join(",", h.Value),
                        StringComparer.OrdinalIgnoreCase
                    )
                )
            );
        }

        if (Fail is not null)
        {
            return Fail(request);
        }

        return method switch
        {
            "initialize" => Sse(
                Reply(
                    id,
                    """{"capabilities":{"tools":{}},"protocolVersion":"2025-06-18","serverInfo":{"name":"remote-test","version":"1"}}"""
                ),
                includeSession: true
            ),
            "notifications/initialized" => new HttpResponseMessage(HttpStatusCode.Accepted),
            "tools/list" => Sse(Reply(id, "{\"tools\":" + _tools + "}")),
            "tools/call" => Sse(Reply(id, """{"content":[{"type":"text","text":"ok"}]}""")),
            _ => new HttpResponseMessage(HttpStatusCode.Accepted),
        };
    }

    private static string Reply(string id, string result) => $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":{result}}}";

    private static HttpResponseMessage Sse(string json, bool includeSession = false)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($"event: message\ndata: {json}\n\n", Encoding.UTF8, "text/event-stream"),
        };
        if (includeSession)
        {
            response.Headers.TryAddWithoutValidation("Mcp-Session-Id", SessionId);
        }

        return response;
    }
}

/// <summary>One request the fake server received: its JSON-RPC method (null for a GET or DELETE) and its headers.</summary>
internal sealed record RecordedRequest(string? Method, IReadOnlyDictionary<string, string> Headers);
