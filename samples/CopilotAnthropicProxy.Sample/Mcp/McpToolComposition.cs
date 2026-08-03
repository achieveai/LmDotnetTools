using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

internal enum McpSnapshotAction
{
    None,
    Set,
    Remove,
}

internal sealed record McpComposedList(
    byte[]? Body,
    string? Endpoint,
    string? SessionId,
    McpSnapshotAction SnapshotAction,
    McpToolSnapshot? Snapshot = null,
    long Generation = 0
);

internal sealed class McpToolComposition
{
    private readonly McpJinaToolCatalog _catalog;
    private readonly McpToolSnapshotStore _snapshots;
    private readonly ConcurrentDictionary<(string Endpoint, string SessionId, string RequestId), CancellationTokenSource> _localCalls = [];
    private readonly ILogger _logger;

    public McpToolComposition(
        McpJinaToolCatalog catalog,
        McpToolSnapshotStore snapshots,
        ILoggerFactory loggerFactory
    )
    {
        _catalog = catalog;
        _snapshots = snapshots;
        _logger = loggerFactory.CreateLogger("CopilotAnthropicProxy.McpTools");
    }

    public bool IsEnabled => _catalog.IsEnabled;

    public static bool TryParseSingleRequest(ReadOnlySpan<byte> body, out JsonObject? request)
    {
        request = null;
        try
        {
            request = JsonNode.Parse(body) as JsonObject;
            return request is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsToolsList(JsonObject request) => Text(request["method"]) == "tools/list";

    public static bool IsToolsCall(JsonObject request) => Text(request["method"]) == "tools/call";

    public bool TryHandleCancellation(HttpContext context, JsonObject request)
    {
        if (Text(request["method"]) != "notifications/cancelled")
        {
            return false;
        }

        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        var endpoint = context.Request.Path.Value;
        var requestId = (request["params"] as JsonObject)?["requestId"];
        if (
            string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(sessionId)
            || requestId is null
        )
        {
            return false;
        }

        if (_localCalls.TryGetValue((endpoint, sessionId, requestId.ToJsonString()), out var cancellation))
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The local call completed between lookup and cancellation; treat it as already finished.
            }
        }

        return false;
    }

    public async Task<bool> TryHandleCallAsync(HttpContext context, JsonObject request)
    {
        if (!IsEnabled || !IsToolsCall(request) || !request.ContainsKey("id") || request["id"] is null)
        {
            return false;
        }

        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        var endpoint = context.Request.Path.Value;
        if (request["params"] is not JsonObject parameters)
        {
            return false;
        }

        var name = Text(parameters["name"]);
        if (
            string.IsNullOrWhiteSpace(sessionId)
            || string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(name)
            || !_snapshots.TryGet(endpoint, sessionId, out var snapshot)
            || !snapshot.LocalToolNames.Contains(name)
            || !_catalog.Tools.TryGetValue(name, out var tool)
        )
        {
            return false;
        }

        if (
            McpToolSnapshotStore.HasToolFilterHeaders(context.Request.Headers)
            && (
                !McpToolSnapshotStore.TryBuildHeaderContext(context.Request.Headers, out var contextHeaders)
                || !string.Equals(contextHeaders, snapshot.HeaderContext, StringComparison.Ordinal)
            )
        )
        {
            return false;
        }

        if (parameters["arguments"] is not null and not JsonObject)
        {
            await WriteInvalidParamsAsync(context, request["id"]!);
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        var requestKey = (endpoint, sessionId, request["id"]!.ToJsonString());
        using var localCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (!_localCalls.TryAdd(requestKey, localCts))
        {
            await WriteInvalidParamsAsync(context, request["id"]!);
            return true;
        }

        ToolHandlerResult result;
        try
        {
            var arguments = parameters["arguments"]?.ToJsonString() ?? "{}";
            result = await tool.Handler(
                arguments,
                new ToolCallContext { ToolCallId = ToolCallId(request["id"]!) },
                localCts.Token
            );
        }
        catch (OperationCanceledException) when (localCts.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local MCP tool {ToolName} failed unexpectedly", name);
            result = ToolHandlerResult.FromError("The local MCP tool failed unexpectedly.");
        }
        finally
        {
            _localCalls.TryRemove(requestKey, out _);
        }

        var payload = result switch
        {
            ToolHandlerResult.Resolved resolved => resolved.Payload,
            ToolHandlerResult.Deferred => new ToolHandlerResultPayload(
                "Deferred tool execution is not supported by this MCP endpoint.",
                IsError: true
            ),
            _ => new ToolHandlerResultPayload("The local MCP tool returned an unsupported result.", IsError: true),
        };

        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.Text }),
                ["isError"] = payload.IsError,
            },
        };
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        _logger.LogInformation(
            "MCP tool handled endpoint={Endpoint} backend={Backend} tool={ToolName} status={Status} elapsedMs={ElapsedMs} isError={IsError}",
            endpoint,
            "jina-local",
            name,
            StatusCodes.Status200OK,
            stopwatch.ElapsedMilliseconds,
            payload.IsError
        );
        return true;
    }

    public long CaptureListGeneration(HttpContext context, JsonObject request)
    {
        var endpoint = context.Request.Path.Value;
        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        return IsToolsList(request) && !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(sessionId)
            ? _snapshots.Generation(endpoint, sessionId)
            : 0;
    }

    public async Task<McpComposedList?> ComposeListAsync(
        HttpContext context,
        JsonObject request,
        HttpResponseMessage upstream,
        long maxBodyBytes,
        CancellationToken cancellationToken,
        CancellationTokenSource idleCts,
        TimeSpan idleTimeout,
        long generation
    )
    {
        if (!IsEnabled || !IsToolsList(request))
        {
            return null;
        }

        var endpoint = context.Request.Path.Value;
        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();

        var mediaType = upstream.Content.Headers.ContentType?.MediaType;
        var isJson = string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
        var isSse = string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
        var hasBoundedSseBody = isSse && upstream.Content.Headers.ContentLength is { } sseLength && sseLength <= maxBodyBytes;
        if (!upstream.IsSuccessStatusCode || (!isJson && !hasBoundedSseBody))
        {
            return null;
        }

        var original = await ProxyHttp.ReadCappedBytesAsync(
            upstream.Content,
            maxBodyBytes,
            cancellationToken,
            idleCts,
            idleTimeout
        );
        if (original is null)
        {
            return new McpComposedList([], endpoint, sessionId, McpSnapshotAction.None, Generation: generation);
        }

        ReplaceContent(upstream, original, mediaType);
        var jsonBytes = original;
        string? ssePrefix = null;
        string? sseSuffix = null;
        if (isSse)
        {
            var sse = Encoding.UTF8.GetString(original);
            if (!TryReadSingleSseMessage(sse, out ssePrefix, out var json, out sseSuffix))
            {
                return new McpComposedList(original, endpoint, sessionId, McpSnapshotAction.None, Generation: generation);
            }

            jsonBytes = Encoding.UTF8.GetBytes(json);
        }

        try
        {
            var root = JsonNode.Parse(jsonBytes) as JsonObject;
            var result = root?["result"] as JsonObject;
            var tools = result?["tools"] as JsonArray;
            if (
                root?["error"] is not null
                || result is null
                || tools is null
                || !string.IsNullOrWhiteSpace(Text(result["nextCursor"]))
                || string.IsNullOrWhiteSpace(endpoint)
                || string.IsNullOrWhiteSpace(sessionId)
                || !McpToolSnapshotStore.TryBuildHeaderContext(context.Request.Headers, out var headerContext)
            )
            {
                return new McpComposedList(original, endpoint, sessionId, McpSnapshotAction.None, Generation: generation);
            }

            var githubNames = tools
                .OfType<JsonObject>()
                .Select(tool => Text(tool["name"]))
                .Where(name => name is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.Ordinal);
            var injectable = _catalog.SelectInjectable(context.Request.Headers, githubNames);
            foreach (var local in injectable)
            {
                tools.Add(local.Definition.DeepClone());
            }

            var snapshot = new McpToolSnapshot(
                injectable.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal),
                headerContext
            );

            if (injectable.Count == 0)
            {
                return new McpComposedList(original, endpoint, sessionId, McpSnapshotAction.Set, snapshot, generation);
            }

            upstream.Headers.ETag = null;
            var composedJson = root!.ToJsonString();
            var composed = isSse
                ? Encoding.UTF8.GetBytes(ssePrefix + composedJson + sseSuffix)
                : Encoding.UTF8.GetBytes(composedJson);
            _logger.LogInformation(
                "MCP tools listed endpoint={Endpoint} backend={Backend} status={Status} localToolCount={LocalToolCount}",
                endpoint,
                "github+jina-local",
                (int)upstream.StatusCode,
                injectable.Count
            );
            return new McpComposedList(composed, endpoint, sessionId, McpSnapshotAction.Set, snapshot, generation);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "MCP tool-list composition failed; returning the original upstream response");
            return new McpComposedList(original, endpoint, sessionId, McpSnapshotAction.None, Generation: generation);
        }
    }

    public void PublishSnapshot(McpComposedList composed)
    {
        if (string.IsNullOrWhiteSpace(composed.Endpoint) || string.IsNullOrWhiteSpace(composed.SessionId))
        {
            return;
        }

        switch (composed.SnapshotAction)
        {
            case McpSnapshotAction.Set when composed.Snapshot is not null:
                _snapshots.SetIfGeneration(
                    composed.Endpoint,
                    composed.SessionId,
                    composed.Generation,
                    composed.Snapshot
                );
                break;
            case McpSnapshotAction.Set:
                throw new InvalidOperationException("A snapshot is required for the set action.");
            case McpSnapshotAction.Remove:
                _snapshots.Remove(composed.Endpoint, composed.SessionId);
                break;
            case McpSnapshotAction.None:
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    public void RemoveSnapshot(string endpoint, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _snapshots.Remove(endpoint, sessionId);
        }
    }

    private static async Task WriteInvalidParamsAsync(HttpContext context, JsonNode id)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Invalid params" },
        };
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes(response.ToJsonString()),
            context.RequestAborted
        );
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string ToolCallId(JsonNode id) =>
        id is JsonValue value && value.TryGetValue<string>(out var text) ? text : id.ToJsonString();

    private static bool TryReadSingleSseMessage(
        string sse,
        out string prefix,
        out string json,
        out string suffix
    )
    {
        prefix = string.Empty;
        json = string.Empty;
        suffix = string.Empty;
        var newline = sse.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var terminator = newline + newline;
        if (!sse.EndsWith(terminator, StringComparison.Ordinal))
        {
            return false;
        }

        var lines = sse[..^terminator.Length].Split(newline, StringSplitOptions.None);
        var dataIndexes = lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.StartsWith("data:", StringComparison.Ordinal))
            .Select(item => item.index)
            .ToArray();
        if (dataIndexes.Length != 1 || lines.Any(string.IsNullOrWhiteSpace))
        {
            return false;
        }

        var dataIndex = dataIndexes[0];
        var dataLine = lines[dataIndex];
        var separatorLength = dataLine.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5;
        json = dataLine[separatorLength..];
        prefix = string.Join(newline, lines[..dataIndex]);
        if (dataIndex > 0)
        {
            prefix += newline;
        }
        prefix += dataLine[..separatorLength];
        suffix = dataIndex + 1 < lines.Length
            ? newline + string.Join(newline, lines[(dataIndex + 1)..]) + terminator
            : terminator;
        return true;
    }

    private static void ReplaceContent(HttpResponseMessage upstream, byte[] body, string? mediaType)
    {
        upstream.Content.Dispose();
        upstream.Content = new ByteArrayContent(body);
        upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType ?? "application/json");
    }
}
