using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

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

        return BuildFromHandlers(openAiHandler, anthropicHandler, urls, loggerFactory);
    }

    /// <summary>
    /// Test seam: builds the host from caller-provided <see cref="HttpMessageHandler"/> instances
    /// instead of constructing them from a <see cref="ScriptedSseResponder"/>. Tests use this to
    /// interpose tee/capture handlers between the host and the inner scripted handlers, so they
    /// can assert byte-equality between what the host streams and what the inner handlers produced.
    /// </summary>
    internal static WebApplication BuildFromHandlers(
        HttpMessageHandler openAiHandler,
        HttpMessageHandler anthropicHandler,
        string[]? urls = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(openAiHandler);
        ArgumentNullException.ThrowIfNull(anthropicHandler);

        var builder = WebApplication.CreateSlimBuilder();

        if (loggerFactory is not null)
        {
            builder.Services.AddSingleton(loggerFactory);
        }
        else
        {
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Warning);
        }

        if (urls is not null && urls.Length > 0)
        {
            builder.WebHost.UseUrls(urls);
        }

        var app = builder.Build();

        var openAiClient = new HttpClient(openAiHandler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://mock.local/"),
        };
        var anthropicClient = new HttpClient(anthropicHandler, disposeHandler: true)
        {
            BaseAddress = new Uri("http://mock.local/"),
        };

        // Dispose the in-process clients (and their wrapped handlers) when the host shuts down.
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            openAiClient.Dispose();
            anthropicClient.Dispose();
        });

        app.MapGet("/healthz", () => Results.Text("ok", "text/plain"));

        app.MapPost("/v1/chat/completions", ctx =>
            ForwardAsync(ctx, openAiClient, "/v1/chat/completions", openAiHeaders: true));

        app.MapPost("/v1/messages", ctx =>
            ForwardAsync(ctx, anthropicClient, "/v1/messages", openAiHeaders: false));

        return app;
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
