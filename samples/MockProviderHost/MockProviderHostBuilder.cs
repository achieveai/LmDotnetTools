using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;

namespace AchieveAi.LmDotnetTools.MockProviderHost;

/// <summary>
/// Builds a runnable mock-provider HTTP host that wraps a <see cref="ScriptedSseResponder"/>
/// behind OpenAI- and Anthropic-compatible endpoints. The host is a thin transport adapter:
/// every byte streamed back originates from the wrapped <c>ScriptedSseResponder</c>'s
/// in-process handlers; the host adds no new SSE framing.
/// </summary>
/// <remarks>
/// <para>
/// Designed for end-to-end tests of external CLIs (Claude Agent SDK, Codex, Copilot CLI)
/// that cannot be redirected through an in-process <see cref="HttpMessageHandler"/>.
/// Tests configure scenarios in code (<see cref="ScriptedSseResponder.New"/>), boot the host
/// on an ephemeral port, and point the CLI at <c>http://127.0.0.1:&lt;port&gt;/v1</c> via the
/// provider's standard base-URL env var.
/// </para>
/// </remarks>
public static class MockProviderHostBuilder
{
    /// <summary>
    /// Builds (but does not start) a <see cref="WebApplication"/> hosting the mock endpoints.
    /// </summary>
    /// <param name="responder">Scripted responder whose handlers serve every request.</param>
    /// <param name="urls">Optional URLs to bind to. Pass <c>http://127.0.0.1:0</c> for ephemeral.</param>
    /// <param name="loggerFactory">Optional logger factory; the host uses this for both ASP.NET
    /// Core logging and the inner scripted handlers.</param>
    public static WebApplication Build(
        ScriptedSseResponder responder,
        string[]? urls = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(responder);

        var openAiHandler = responder.AsOpenAiHandler(loggerFactory?.CreateLogger("MockProviderHost.OpenAi"));
        var anthropicHandler = responder.AsAnthropicHandler(loggerFactory?.CreateLogger("MockProviderHost.Anthropic"));
        var responsesHandler = responder.AsOpenAiResponsesHandler(loggerFactory?.CreateLogger("MockProviderHost.OpenAiResponses"));

        return BuildFromHandlers(openAiHandler, anthropicHandler, responsesHandler, responder, urls, loggerFactory);
    }

    /// <summary>
    /// Test seam: builds the host from caller-provided <see cref="HttpMessageHandler"/> instances
    /// instead of constructing them from a <see cref="ScriptedSseResponder"/>. Tests use this to
    /// interpose tee/capture handlers between the host and the inner scripted handlers, so they
    /// can assert byte-equality between what the host streams and what the inner handlers produced.
    /// </summary>
    /// <remarks>
    ///     The <paramref name="responsesResponder"/> is used directly for the WebSocket transport
    ///     (which can't go through an <see cref="HttpMessageHandler"/>), while
    ///     <paramref name="responsesHandler"/> serves the HTTP+SSE path. Tests can pass a tee
    ///     handler for byte-identity assertions while still pointing the WS path at the same
    ///     responder.
    /// </remarks>
    internal static WebApplication BuildFromHandlers(
        HttpMessageHandler openAiHandler,
        HttpMessageHandler anthropicHandler,
        HttpMessageHandler? responsesHandler = null,
        ScriptedSseResponder? responsesResponder = null,
        string[]? urls = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(openAiHandler);
        ArgumentNullException.ThrowIfNull(anthropicHandler);

        var builder = WebApplication.CreateSlimBuilder();

        if (loggerFactory is not null)
        {
            _ = builder.Services.AddSingleton(loggerFactory);
        }
        else
        {
            _ = builder.Logging.ClearProviders();
            _ = builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        if (urls is not null && urls.Length > 0)
        {
            _ = builder.WebHost.UseUrls(urls);
        }

        var app = builder.Build();
        _ = app.UseWebSockets();

        var openAiClient = new HttpClient(openAiHandler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://mock.local/"),
        };
        var anthropicClient = new HttpClient(anthropicHandler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://mock.local/"),
        };
        HttpClient? responsesClient = responsesHandler is null
            ? null
            : new HttpClient(responsesHandler, disposeHandler: true)
            {
                BaseAddress = new Uri("http://mock.local/"),
            };

        // Dispose the in-process clients (and their wrapped handlers) when the host shuts down.
        _ = app.Lifetime.ApplicationStopping.Register(() =>
        {
            openAiClient.Dispose();
            anthropicClient.Dispose();
            responsesClient?.Dispose();
        });

        _ = app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));

        _ = app.MapPost("/v1/chat/completions", ctx =>
            ForwardAsync(ctx, openAiClient, "/v1/chat/completions", openAiHeaders: true));

        _ = app.MapPost("/v1/messages", ctx =>
            ForwardAsync(ctx, anthropicClient, "/v1/messages", openAiHeaders: false));

        if (responsesClient is not null)
        {
            _ = app.MapPost("/v1/responses", ctx =>
                ForwardAsync(ctx, responsesClient, "/v1/responses", openAiHeaders: true));
        }

        if (responsesResponder is not null)
        {
            // Only GET upgrades to WebSocket — POST is handled above for HTTP+SSE, and
            // restricting this route prevents future verbs from being silently swallowed.
            _ = app.MapGet("/v1/responses", async ctx =>
            {
                var logger = ctx.RequestServices.GetService<ILoggerFactory>()
                    ?.CreateLogger("MockProviderHost.OpenAiResponsesWs");
                if (!ctx.WebSockets.IsWebSocketRequest)
                {
                    // Match real Responses API behavior: clients must upgrade to a WebSocket.
                    ctx.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                    ctx.Response.Headers["Upgrade"] = "websocket";
                    ctx.Response.Headers["Connection"] = "Upgrade";
                    await ctx.Response.WriteAsync(
                        "WebSocket upgrade required for /v1/responses (use POST for HTTP+SSE)");
                    return;
                }

                using var socket = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
                await ServeResponsesWebSocketAsync(socket, responsesResponder, logger, ctx.RequestAborted)
                    .ConfigureAwait(false);
            });
        }

        // Catch-all so mismatched paths surface a logged 404 rather than failing silently —
        // diagnostic anchor for E2E tests where a BaseUrl misconfiguration would otherwise
        // present as "the CLI completes with no rendered content" (see issue #29).
        _ = app.MapFallback(async ctx =>
        {
            var logger = ctx.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("MockProviderHost.Unmatched");
            logger?.LogWarning(
                "Unmatched request: {Method} {Path}{Query} (host expected /healthz, /v1/chat/completions, /v1/messages)",
                ctx.Request.Method,
                ctx.Request.Path,
                ctx.Request.QueryString);
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            await ctx.Response.WriteAsync(
                $"Mock provider host: no route for {ctx.Request.Method} {ctx.Request.Path}");
        });

        return app;
    }

    private static async Task ServeResponsesWebSocketAsync(
        WebSocket socket,
        ScriptedSseResponder responder,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var receiveBuffer = new byte[8 * 1024];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            string? frameText;
            try
            {
                frameText = await ReadFullTextFrameAsync(socket, receiveBuffer, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException wsEx)
            {
                logger?.LogDebug(wsEx, "WebSocket read aborted");
                return;
            }

            if (frameText is null)
            {
                return;
            }

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(frameText);
            }
            catch (JsonException ex)
            {
                logger?.LogWarning(ex, "Malformed JSON on WebSocket frame; closing");
                await socket.CloseAsync(
                    WebSocketCloseStatus.InvalidPayloadData,
                    "Invalid JSON",
                    cancellationToken
                ).ConfigureAwait(false);
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("type", out var typeProp)
                    || typeProp.ValueKind != JsonValueKind.String
                    || typeProp.GetString() != ResponseEventTypes.ClientResponseCreate)
                {
                    logger?.LogWarning("Frame missing or invalid 'type' = response.create; closing");
                    await socket.CloseAsync(
                        WebSocketCloseStatus.InvalidPayloadData,
                        "Expected response.create",
                        cancellationToken
                    ).ConfigureAwait(false);
                    return;
                }

                var ctx = BuildResponsesContext(root);
                var model = root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString()
                    : null;

                try
                {
                    await responder.EmitResponseEventsAsync(socket, ctx, model, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogError(ex, "Failure while emitting WebSocket events");
                    if (socket.State == WebSocketState.Open)
                    {
                        await socket.CloseAsync(
                            WebSocketCloseStatus.InternalServerError,
                            "Emit failure",
                            cancellationToken
                        ).ConfigureAwait(false);
                    }

                    return;
                }
            }
        }

        if (socket.State == WebSocketState.Open)
        {
            // If the request was aborted, the original token is already cancelled; pass
            // CancellationToken.None so the close frame can still be written before we exit.
            var closeToken = cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "session ended",
                    closeToken
                ).ConfigureAwait(false);
            }
            catch (WebSocketException wsEx)
            {
                logger?.LogDebug(wsEx, "Best-effort close after session end failed");
            }
        }
    }

    private static async Task<string?> ReadFullTextFrameAsync(
        WebSocket socket,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "client closed",
                        cancellationToken
                    ).ConfigureAwait(false);
                }

                return null;
            }

            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (result.EndOfMessage)
            {
                return sb.ToString();
            }
        }
    }

    private static ScriptedRequestContext BuildResponsesContext(JsonElement root)
    {
        var systemPrompt = root.TryGetProperty("instructions", out var instr)
            && instr.ValueKind == JsonValueKind.String
                ? instr.GetString() ?? string.Empty
                : string.Empty;

        string? latestUser = null;
        var tools = new List<string>();

        if (root.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("role", out var role)
                    && role.ValueKind == JsonValueKind.String
                    && string.Equals(role.GetString(), "user", StringComparison.OrdinalIgnoreCase))
                {
                    latestUser = ExtractInputText(item);
                }
            }
        }

        if (root.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolsEl.EnumerateArray())
            {
                if (tool.ValueKind == JsonValueKind.Object
                    && tool.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String)
                {
                    tools.Add(n.GetString()!);
                }
            }
        }

        return new ScriptedRequestContext
        {
            Wire = ScriptedWireFormat.OpenAiResponses,
            RequestBody = root.Clone(),
            SystemPrompt = systemPrompt,
            Tools = tools,
            LatestUserMessage = latestUser,
        };
    }

    private static string? ExtractInputText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var content))
        {
            return null;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString();
        }

        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object
                || !part.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var t = type.GetString();
            if ((t is "input_text" or "output_text" or "text")
                && part.TryGetProperty("text", out var textProp)
                && textProp.ValueKind == JsonValueKind.String)
            {
                return textProp.GetString();
            }
        }

        return null;
    }

    private static async Task ForwardAsync(
        HttpContext ctx,
        HttpClient client,
        string upstreamPath,
        bool openAiHeaders)
    {
        try
        {
            // Buffer the body up-front so the inner ScriptedHandler can read it via
            // ReadAsStringAsync without racing the original request stream's cancellation.
            ctx.Request.EnableBuffering();
            using var bodyStream = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(bodyStream, ctx.RequestAborted).ConfigureAwait(false);
            var bodyBytes = bodyStream.ToArray();

            using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamPath)
            {
                Content = new ByteArrayContent(bodyBytes),
            };
            var contentType = ctx.Request.ContentType ?? "application/json";
            upstreamRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

            // ScriptedHandler streams back token-by-token, so read response headers first and copy
            // the body as it arrives.
            using var upstreamResponse = await client.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.RequestAborted).ConfigureAwait(false);

            ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;

            // Echo the upstream's content type (text/event-stream for streaming, application/json
            // for non-streaming errors). Skip Content-Length when chunking SSE; Kestrel computes it.
            if (upstreamResponse.Content.Headers.ContentType is { } responseContentType)
            {
                ctx.Response.ContentType = responseContentType.ToString();
            }

            // Convention headers some SDKs assert on. These aren't load-bearing for the
            // ScriptedSseResponder but match real-provider responses closely enough that strict
            // SDKs don't reject the response.
            var requestId = $"mock-{Guid.NewGuid():N}";
            if (openAiHeaders)
            {
                ctx.Response.Headers["openai-version"] = "2020-10-01";
                ctx.Response.Headers["x-request-id"] = requestId;
            }
            else
            {
                ctx.Response.Headers["anthropic-version"] = "2023-06-01";
                ctx.Response.Headers["anthropic-request-id"] = requestId;
                ctx.Response.Headers["request-id"] = requestId;
            }

            // Copy the (possibly streaming) response body straight through. CopyToAsync flushes
            // periodically, which is sufficient for SSE since each `data:` frame from the inner
            // SseStreamHttpContent crosses a write boundary.
            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ctx.RequestAborted).ConfigureAwait(false);
            await upstreamStream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            // Client disconnected mid-stream — not a host error.
            throw;
        }
        catch (Exception ex)
        {
            // Mock-host failures are otherwise invisible to the test (a generic 500 with no
            // logged cause). Log so failing E2E tests have a diagnostic entry point.
            var logger = ctx.RequestServices.GetService<ILoggerFactory>()
                ?.CreateLogger("MockProviderHost.Forward");
            logger?.LogError(ex, "ForwardAsync failed for {Path}", upstreamPath);
            throw;
        }
    }
}
