using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;

internal sealed class McpToolComposition
{
    private readonly McpJinaToolCatalog _catalog;
    private readonly McpToolSnapshotStore _snapshots;
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

    public async Task<bool> TryHandleCallAsync(HttpContext context, JsonObject request)
    {
        if (!IsEnabled || !IsToolsCall(request) || !request.ContainsKey("id") || request["id"] is null)
        {
            return false;
        }

        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        var endpoint = context.Request.Path.Value;
        var name = Text(request["params"]?["name"]);
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

        var stopwatch = Stopwatch.StartNew();
        ToolHandlerResult result;
        try
        {
            var arguments = request["params"]?["arguments"]?.ToJsonString() ?? "{}";
            result = await tool.Handler(
                arguments,
                new ToolCallContext { ToolCallId = ToolCallId(request["id"]!) },
                context.RequestAborted
            );
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local MCP tool {ToolName} failed unexpectedly", name);
            result = ToolHandlerResult.FromError("The local MCP tool failed unexpectedly.");
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

    public async Task<byte[]?> ComposeListAsync(
        HttpContext context,
        JsonObject request,
        HttpResponseMessage upstream,
        long maxBodyBytes,
        CancellationToken cancellationToken
    )
    {
        if (!IsEnabled || !IsToolsList(request))
        {
            return null;
        }

        var endpoint = context.Request.Path.Value;
        var sessionId = context.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
        if (
            sessionId is null
            && upstream.Headers.TryGetValues("Mcp-Session-Id", out var responseSessionIds)
        )
        {
            sessionId = responseSessionIds.FirstOrDefault();
        }

        void Invalidate()
        {
            if (!string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(sessionId))
            {
                _snapshots.Remove(endpoint, sessionId);
            }
        }

        var mediaType = upstream.Content.Headers.ContentType?.MediaType;
        if (
            !upstream.IsSuccessStatusCode
            || !string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
        )
        {
            Invalidate();
            return null;
        }

        var original = await ProxyHttp.ReadCappedBytesAsync(upstream.Content, maxBodyBytes, cancellationToken);
        if (original is null)
        {
            Invalidate();
            return [];
        }

        ReplaceContent(upstream, original, mediaType);
        try
        {
            var root = JsonNode.Parse(original) as JsonObject;
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
                Invalidate();
                return original;
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

            _snapshots.Set(
                endpoint,
                sessionId,
                new McpToolSnapshot(injectable.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal), headerContext)
            );

            if (injectable.Count == 0)
            {
                return original;
            }

            upstream.Headers.ETag = null;
            var composed = Encoding.UTF8.GetBytes(root!.ToJsonString());
            _logger.LogInformation(
                "MCP tools listed endpoint={Endpoint} backend={Backend} status={Status} localToolCount={LocalToolCount}",
                endpoint,
                "github+jina-local",
                (int)upstream.StatusCode,
                injectable.Count
            );
            return composed;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            Invalidate();
            _logger.LogWarning(ex, "MCP tool-list composition failed; returning the original upstream response");
            return original;
        }
    }

    public void RemoveSnapshot(string endpoint, string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _snapshots.Remove(endpoint, sessionId);
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string ToolCallId(JsonNode id) =>
        id is JsonValue value && value.TryGetValue<string>(out var text) ? text : id.ToJsonString();

    private static void ReplaceContent(HttpResponseMessage upstream, byte[] body, string? mediaType)
    {
        upstream.Content.Dispose();
        upstream.Content = new ByteArrayContent(body);
        upstream.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType ?? "application/json");
    }
}
