using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

// =============================================================================
// CopilotAnthropicProxy.Sample
//
// A thin, loopback-only reverse proxy that accepts the Anthropic Messages API
// and forwards it to GitHub Copilot. It rewrites ONLY the JSON `model` field to a
// configured Copilot Claude (Opus) id, attaches Copilot auth/headers via the proven
// GithubCopilotProvider transport, and streams the SSE response back as raw bytes.
// It also exposes Copilot's MCP server (Streamable HTTP transport) as a transparent
// byte-level proxy on /mcp and /mcp/readonly, with Copilot auth attached the same way.
//
// Point Claude Code (or any Anthropic-Messages client) at it via ANTHROPIC_BASE_URL.
// SECURITY: binds to loopback only and attaches the developer's Copilot credentials
// outbound; never expose it on 0.0.0.0 or through a tunnel. See README.md.
// =============================================================================

var config = ProxyConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddSimpleConsole(options => options.SingleLine = true);

// Bind BOTH loopback families on the configured port. Binding both (== ListenLocalhost,
// still loopback-only) avoids the "::1 trap" when a client resolves localhost to IPv6.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, config.Port);
    options.Listen(IPAddress.IPv6Loopback, config.Port);
    // Local dev proxy on a trusted loopback socket. The forward path buffers and JsonNode-rewrites the
    // whole body in memory, so the cap matches Anthropic's own ~32 MB request limit rather than being
    // arbitrarily large; Kestrel rejects an over-limit body mid-read, before full allocation.
    options.Limits.MaxRequestBodySize = 32L * 1024 * 1024; // 32 MB (matches the Anthropic API request limit)
});

// --- Dependency injection ----------------------------------------------------
// Token provider: default to the non-interactive CLI credential provider (re-resolves per
// request, auto-picks-up re-auth, no permanent cache). Device flow is an explicit opt-in only.
builder.Services.AddSingleton<ICopilotTokenProvider>(_ =>
    config.EnableDeviceFlow ? CompositeCopilotTokenProvider.CreateDefault() : new CliCredentialCopilotTokenProvider()
);

builder.Services.AddSingleton(new CopilotSessionContext());
builder.Services.AddSingleton(
    new CopilotOptions { BaseUrl = config.BaseUrl, DefaultInteractionType = "conversation-user" }
);

// Inner transport handler: a pooled SocketsHttpHandler. AutomaticDecompression is OFF so the proxy
// relays upstream bytes verbatim (we never re-encode). Tests swap this for a fake handler.
builder.Services.AddSingleton<HttpMessageHandler>(_ => new SocketsHttpHandler
{
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = DecompressionMethods.None,
});

// Single shared HttpClient, DI-resolved (NOT a static Lazy) so tests can inject the fake handler.
// Timeout is INFINITE: HttpClient.Timeout is a total-exchange deadline even with
// ResponseHeadersRead, so any finite value would silently cap long streams and break the
// RequestAborted cancellation filter. Per-request deadlines are enforced by a linked CTS instead.
builder.Services.AddSingleton(sp =>
    CopilotHttpClientFactory.Create(
        config.BaseUrl,
        sp.GetRequiredService<ICopilotTokenProvider>(),
        sp.GetRequiredService<CopilotSessionContext>(),
        sp.GetRequiredService<CopilotOptions>(),
        timeout: Timeout.InfiniteTimeSpan,
        innerHandler: sp.GetService<HttpMessageHandler>()
    )
);

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotAnthropicProxy");

// --- Eager startup checks (fail fast) ----------------------------------------
// 1) Resolve a Copilot token once so misconfiguration surfaces at startup, not on the first request.
var tokenProvider = app.Services.GetRequiredService<ICopilotTokenProvider>();
try
{
    using var tokenCts = new CancellationTokenSource(TimeSpan.FromMinutes(config.EnableDeviceFlow ? 20 : 1));
    _ = await tokenProvider.GetTokenAsync(tokenCts.Token);
}
catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
{
    logger.LogError(
        "No GitHub Copilot token could be resolved. Sign in with the GitHub Copilot CLI or `gh auth login`, "
            + "or set GITHUB_COPILOT_TOKEN / GH_TOKEN, then restart. (set COPILOT_ANTHROPIC_ENABLE_DEVICE_FLOW=1 "
            + "to allow an interactive device-flow login at startup.) Reason: {Reason}",
        ex.Message
    );
    return 1;
}

// 2) Resolve the outbound model catalog (env wins and pins a single model; else discover every
//    /v1/messages-capable Copilot model and pick the `opus` Claude id as the default; else fail fast).
ProxyModelCatalog catalog;
try
{
    using var modelCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    catalog = await ProxyModelResolver.ResolveAsync(
        app.Services.GetRequiredService<HttpClient>(),
        config.ModelOverride,
        logger,
        modelCts.Token
    );
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or OperationCanceledException)
{
    logger.LogError(
        "Could not resolve a Copilot Opus Claude model. Set COPILOT_ANTHROPIC_MODEL to a model id exposed by "
            + "GET {BaseUrl}/models. Reason: {Reason}",
        config.BaseUrl,
        ex.Message
    );
    return 1;
}

logger.LogInformation(
    "CopilotAnthropicProxy listening on http://127.0.0.1:{Port} -> {BaseUrl} (default model: {Model}, "
        + "{Count} available)",
    config.Port,
    config.BaseUrl,
    catalog.Default,
    catalog.Available.Count
);

// --- Pipeline ----------------------------------------------------------------
// Host/loopback/cross-site guard runs FIRST.
app.Use(
    async (ctx, next) =>
    {
        if (
            !ProxyGuard.IsAllowed(
                ctx.Connection.RemoteIpAddress,
                ctx.Request.Headers.Host,
                ctx.Request.Headers.Origin,
                ctx.Request.Headers["Sec-Fetch-Site"],
                config.Port
            )
        )
        {
            await ProxyHttp.WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status403Forbidden,
                "permission_error",
                "This proxy only accepts loopback requests from a same-origin client."
            );
            return;
        }

        await next(ctx);
    }
);

app.MapGet("/health", () => Results.Ok(new { status = "healthy", model = catalog.Default }));

// GET /v1/models — Anthropic-shaped list of every available (/v1/messages-capable) model.
app.MapGet(
    "/v1/models",
    () => Results.Content(ProxyHttp.BuildModelsStub(catalog.Available), "application/json", Encoding.UTF8)
);

// POST /v1/messages and /v1/messages/count_tokens — the forward path.
app.MapPost("/v1/messages", ctx => ProxyHttp.ForwardAsync(ctx, catalog, config.IdleTimeout, isCountTokens: false));

app.MapPost(
    "/v1/messages/count_tokens",
    ctx => ProxyHttp.ForwardAsync(ctx, catalog, config.IdleTimeout, isCountTokens: true)
);

// GET/POST/DELETE /mcp and /mcp/readonly — transparent MCP (Streamable HTTP) proxy.
app.MapMethods("/mcp", ["GET", "POST", "DELETE"], ctx => ProxyMcp.ForwardAsync(ctx, config.IdleTimeout));
app.MapMethods("/mcp/readonly", ["GET", "POST", "DELETE"], ctx => ProxyMcp.ForwardAsync(ctx, config.IdleTimeout));

// Unknown route -> Anthropic-shaped 404.
app.MapFallback(ctx =>
    ProxyHttp.WriteAnthropicErrorAsync(
        ctx,
        StatusCodes.Status404NotFound,
        "not_found_error",
        $"Unknown route: {ctx.Request.Method} {ctx.Request.Path}"
    )
);

await app.RunAsync();
return 0;

// =============================================================================
// Configuration
// =============================================================================

/// <summary>Immutable proxy configuration sourced from environment variables.</summary>
internal sealed record ProxyConfig
{
    public required int Port { get; init; }
    public required string BaseUrl { get; init; }
    public required TimeSpan IdleTimeout { get; init; }
    public required bool EnableDeviceFlow { get; init; }
    public required string? ModelOverride { get; init; }

    public static ProxyConfig FromEnvironment()
    {
        return new ProxyConfig
        {
            Port = ParseInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_PORT"), 8787),
            BaseUrl =
                NullIfBlank(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_BASE_URL"))
                ?? CopilotOptions.DefaultBaseUrl,
            IdleTimeout = TimeSpan.FromSeconds(
                ParseInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS"), 120)
            ),
            EnableDeviceFlow = ParseBool(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_ENABLE_DEVICE_FLOW")),
            ModelOverride = NullIfBlank(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL")),
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static bool ParseBool(string? value) =>
        value is not null
        && (
            value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
        );
}

// =============================================================================
// Host / loopback / cross-site guard (pure, unit-testable)
// =============================================================================

/// <summary>Pure predicates for the inbound host/loopback/cross-site guard.</summary>
public static class ProxyGuard
{
    private static readonly string[] LoopbackHostNames = ["127.0.0.1", "localhost", "[::1]", "::1"];

    /// <summary>
    ///     Returns true when a request may be served. Rejects non-loopback remote IPs, foreign/missing
    ///     <c>Host</c> headers, cross-site <c>Sec-Fetch-Site</c>, and non-loopback <c>Origin</c> hosts.
    /// </summary>
    /// <param name="remote">
    ///     Connection remote IP. Null (e.g. the in-memory TestServer) skips the IP check; the Host and
    ///     Origin checks still apply. In production over Kestrel/TCP this is always populated.
    /// </param>
    /// <param name="host">Inbound <c>Host</c> header.</param>
    /// <param name="origin">Inbound <c>Origin</c> header (may be empty).</param>
    /// <param name="secFetchSite">Inbound <c>Sec-Fetch-Site</c> header (may be empty).</param>
    /// <param name="port">The configured listen port.</param>
    public static bool IsAllowed(IPAddress? remote, string? host, string? origin, string? secFetchSite, int port)
    {
        if (remote is not null)
        {
            var normalized = remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote;
            if (!IPAddress.IsLoopback(normalized))
            {
                return false;
            }
        }

        if (!IsAllowedHost(host, port))
        {
            return false;
        }

        if (
            !string.IsNullOrEmpty(secFetchSite) && secFetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return IsAllowedOrigin(origin);
    }

    /// <summary>Exact loopback Host-header allowlist: bare host or host with the configured port.</summary>
    public static bool IsAllowedHost(string? host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var portSuffix = ":" + port.ToString(CultureInfo.InvariantCulture);
        foreach (var allowed in LoopbackHostNames)
        {
            if (
                host.Equals(allowed, StringComparison.OrdinalIgnoreCase)
                || host.Equals(allowed + portSuffix, StringComparison.OrdinalIgnoreCase)
            )
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An absent Origin is fine; a present one must resolve to a loopback host.</summary>
    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var ip))
        {
            var normalized = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
            return IPAddress.IsLoopback(normalized);
        }

        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}

// =============================================================================
// Model resolution + request rewrite (pure where possible)
// =============================================================================

/// <summary>
///     The resolved outbound model set: <see cref="Default"/> is used when a request's model is missing or
///     unrecognized; <see cref="Available"/> lists every model the proxy will pass through unchanged.
/// </summary>
public sealed record ProxyModelCatalog(string Default, IReadOnlyList<string> Available);

/// <summary>Resolves the outbound Copilot model catalog and rewrites the inbound request's model field.</summary>
public static class ProxyModelResolver
{
    /// <summary>
    ///     Resolves the model catalog: <c>COPILOT_ANTHROPIC_MODEL</c> override wins and pins a single model
    ///     (no discovery, no passthrough); otherwise queries <c>GET /models</c>, filters to the ids that
    ///     support <c>/v1/messages</c>, and picks the <c>opus</c> Claude id as the default. Throws when no
    ///     override is set and no opus Claude id is available (caller fails fast).
    /// </summary>
    public static async Task<ProxyModelCatalog> ResolveAsync(
        HttpClient client,
        string? modelOverride,
        ILogger logger,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            var pinned = modelOverride.Trim();
            return new ProxyModelCatalog(pinned, [pinned]);
        }

        using var response = await client.GetAsync("/models", cancellationToken);
        _ = response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var availableIds = ParseMessagesCapableModelIds(json);

        var claudeIds = availableIds.Where(id => id.Contains("claude", StringComparison.OrdinalIgnoreCase)).ToList();
        var opus = PickHighestVersionOpusId(claudeIds);
        if (opus is not null)
        {
            return new ProxyModelCatalog(opus, availableIds);
        }

        throw new InvalidOperationException(
            "No Copilot 'opus' Claude model was found. Available Claude models: "
                + (claudeIds.Count > 0 ? string.Join(", ", claudeIds) : "(none)")
                + ". Set COPILOT_ANTHROPIC_MODEL to the exact id you want."
        );
    }

    /// <summary>
    ///     Picks the <c>opus</c> Claude id with the numerically highest version suffix (e.g.
    ///     <c>claude-opus-4.8</c> over <c>claude-opus-4.6</c>), rather than relying on upstream list
    ///     order — Copilot has shipped multiple concurrent opus versions, and list order is not
    ///     guaranteed to be oldest/newest-first.
    /// </summary>
    public static string? PickHighestVersionOpusId(IReadOnlyList<string> claudeIds)
    {
        return claudeIds
            .Where(id => id.Contains("opus", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(ExtractOpusVersion)
            .FirstOrDefault();
    }

    private static readonly Regex OpusVersionSuffixPattern = new(@"^\d+(?:\.\d+)*", RegexOptions.Compiled);

    private static Version ExtractOpusVersion(string id)
    {
        var opusIndex = id.IndexOf("opus", StringComparison.OrdinalIgnoreCase);
        var rest = id[(opusIndex + "opus".Length)..].TrimStart('-', '_', '.');
        var match = OpusVersionSuffixPattern.Match(rest);
        if (!match.Success)
        {
            return new Version(0, 0);
        }

        var versionText = match.Value.Contains('.') ? match.Value : match.Value + ".0";
        return Version.Parse(versionText);
    }

    /// <summary>Extracts model ids from an OpenAI-shaped (<c>{"data":[{"id":...}]}</c>) or bare-array list.</summary>
    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (
                    item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("id", out var idEl)
                    && idEl.ValueKind == JsonValueKind.String
                )
                {
                    var id = idEl.GetString();
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        ids.Add(id);
                    }
                }
            }
        }

        return ids;
    }

    /// <summary>
    ///     Extracts ids for models whose <c>supported_endpoints</c> includes <c>/v1/messages</c> — the only
    ///     ones this Anthropic-Messages-shaped proxy can forward to. Copilot also serves GPT/Gemini models
    ///     that only support <c>/responses</c> or <c>/chat/completions</c>; those are excluded.
    /// </summary>
    public static IReadOnlyList<string> ParseMessagesCapableModelIds(string json)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                if (
                    item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("id", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String
                )
                {
                    continue;
                }

                var id = idEl.GetString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                if (
                    item.TryGetProperty("supported_endpoints", out var endpoints)
                    && endpoints.ValueKind == JsonValueKind.Array
                    && endpoints
                        .EnumerateArray()
                        .Any(e =>
                            e.ValueKind == JsonValueKind.String
                            && string.Equals(e.GetString(), "/v1/messages", StringComparison.OrdinalIgnoreCase)
                        )
                )
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    /// <summary>
    ///     Picks the outbound model for a single request: <paramref name="incomingModel"/> passes through
    ///     unchanged (normalized to the catalog's exact casing) when it matches one of
    ///     <paramref name="catalog"/>'s available ids; otherwise falls back to the catalog's default.
    /// </summary>
    public static string SelectOutboundModel(string? incomingModel, ProxyModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (!string.IsNullOrWhiteSpace(incomingModel))
        {
            var match = catalog.Available.FirstOrDefault(id =>
                string.Equals(id, incomingModel, StringComparison.OrdinalIgnoreCase)
            );
            if (match is not null)
            {
                return match;
            }
        }

        return catalog.Default;
    }

    /// <summary>Peeks at the JSON body's <c>model</c> field without mutating it. Null on any parse failure.</summary>
    public static string? PeekModel(byte[] body)
    {
        ArgumentNullException.ThrowIfNull(body);

        if (body.Length == 0)
        {
            return null;
        }

        try
        {
            if (
                JsonNode.Parse(body) is JsonObject obj
                && obj.TryGetPropertyValue("model", out var existing)
                && existing is JsonValue value
                && value.TryGetValue<string>(out var modelString)
            )
            {
                return modelString;
            }
        }
        catch (JsonException)
        {
            // Malformed body — ForwardAsync's TryRewriteModel call will surface the 400.
        }

        return null;
    }

    /// <summary>
    ///     Raw <see cref="JsonNode"/> rewrite of the <c>model</c> field (overwrite or inject). Never
    ///     deserializes to a typed DTO, so <c>cache_control</c>, <c>thinking</c>, <c>system</c> blocks,
    ///     betas, and unknown fields are preserved verbatim.
    /// </summary>
    /// <returns>True on success; false when the body is missing, not JSON, or not a JSON object.</returns>
    public static bool TryRewriteModel(byte[] body, string model, out byte[] rewritten, out string? incomingModel)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(model);

        rewritten = body;
        incomingModel = null;

        if (body.Length == 0)
        {
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        if (root is not JsonObject obj)
        {
            return false;
        }

        if (
            obj.TryGetPropertyValue("model", out var existing)
            && existing is JsonValue value
            && value.TryGetValue<string>(out var modelString)
        )
        {
            incomingModel = modelString;
        }

        obj["model"] = model;
        rewritten = JsonSerializer.SerializeToUtf8Bytes(obj);
        return true;
    }
}

// =============================================================================
// HTTP forwarding + response shaping
// =============================================================================

/// <summary>The forward pipeline: header allowlist, response shaping, raw streaming, error envelopes.</summary>
internal static class ProxyHttp
{
    private const int BufferSize = 8192;

    // Hop-by-hop / framing / content-coding headers that must NOT be copied from the upstream response.
    // Content-Type is copied separately from the content headers.
    private static readonly HashSet<string> ExcludedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "TE",
        "Trailer",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "Content-Encoding",
        "Content-Type",
    };

    /// <summary>Forwards POST /v1/messages (and count_tokens) to Copilot and streams the response back.</summary>
    public static async Task ForwardAsync(
        HttpContext ctx,
        ProxyModelCatalog catalog,
        TimeSpan idleTimeout,
        bool isCountTokens
    )
    {
        var services = ctx.RequestServices;
        var httpClient = services.GetRequiredService<HttpClient>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotAnthropicProxy");
        var stopwatch = Stopwatch.StartNew();

        // 1) Buffer the (small) inbound body so we can rewrite it and resend without racing the stream.
        byte[] inboundBody;
        using (var memory = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(memory, ctx.RequestAborted);
            inboundBody = memory.ToArray();
        }

        // 2) Pass the requested model through unchanged when it's one of the available ids; otherwise fall
        //    back to the catalog's default. Then rewrite the body (raw JSON). Parse failure -> 400 (do NOT
        //    call upstream).
        var outboundModel = ProxyModelResolver.SelectOutboundModel(ProxyModelResolver.PeekModel(inboundBody), catalog);
        if (
            !ProxyModelResolver.TryRewriteModel(inboundBody, outboundModel, out var outboundBody, out var incomingModel)
        )
        {
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Request body must be a non-empty JSON object."
            );
            return;
        }

        // 3) Build a fresh upstream request with the positive request-header allowlist.
        var upstreamPath = isCountTokens ? "/v1/messages/count_tokens" : "/v1/messages";
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamPath)
        {
            Content = new ByteArrayContent(outboundBody),
        };
        upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApplyRequestHeaderAllowlist(ctx.Request.Headers, upstreamRequest);

        // 4) Per-request deadlines: link client-abort + a reset-per-read idle timeout.
        using var idleCts = new CancellationTokenSource();
        idleCts.CancelAfter(idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, idleCts.Token);

        // 5) Send and read response headers (status + headers lock at first byte).
        HttpResponseMessage upstream;
        try
        {
            upstream = await httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token
            );
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return; // Client disconnected before we connected; nothing to write.
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status504GatewayTimeout,
                "api_error",
                "Timed out waiting for the upstream Copilot API to respond."
            );
            return;
        }
        catch (InvalidOperationException ex)
        {
            // Token acquisition failure surfaces from CopilotHeadersHandler before the first byte.
            logger.LogError("Copilot token acquisition failed: {Reason}", ex.Message);
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status401Unauthorized,
                "authentication_error",
                "Failed to acquire a GitHub Copilot token. Re-authenticate with the GitHub Copilot CLI or "
                    + "`gh auth login`, or set GITHUB_COPILOT_TOKEN / GH_TOKEN."
            );
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Upstream connection failed: {Reason}", ex.Message);
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "api_error",
                "Failed to reach the upstream Copilot API."
            );
            return;
        }

        using (upstream)
        {
            // count_tokens: normalize an unsupported endpoint (404/405) to an Anthropic not_found_error.
            if (
                isCountTokens
                && (
                    upstream.StatusCode == HttpStatusCode.NotFound
                    || upstream.StatusCode == HttpStatusCode.MethodNotAllowed
                )
            )
            {
                await WriteAnthropicErrorAsync(
                    ctx,
                    StatusCodes.Status404NotFound,
                    "not_found_error",
                    "The upstream Copilot API does not support /v1/messages/count_tokens."
                );
                return;
            }

            // Lock status + headers verbatim (minus hop-by-hop/framing).
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            CopyResponseHeaders(upstream, ctx.Response);

            var contentType = upstream.Content.Headers.ContentType;
            if (contentType is not null)
            {
                ctx.Response.ContentType = contentType.ToString();
            }

            var isSse = string.Equals(contentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (isSse)
            {
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Response.Headers.CacheControl = "no-cache";
            }

            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await CopyBodyAsync(ctx, upstream, idleTimeout, idleCts, linked, logger);

            logger.LogInformation(
                "{Method} {Path} model {IncomingModel} -> {ResolvedModel} stream={Stream} upstream={Status} {Elapsed}ms",
                ctx.Request.Method,
                upstreamPath,
                incomingModel ?? "(none)",
                outboundModel,
                isSse,
                (int)upstream.StatusCode,
                stopwatch.ElapsedMilliseconds
            );
        }
    }

    /// <summary>Streams the upstream body to the client with an explicit, incrementally-flushed loop.</summary>
    internal static async Task CopyBodyAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        TimeSpan idleTimeout,
        CancellationTokenSource idleCts,
        CancellationTokenSource linked,
        ILogger logger
    )
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            Stream upstreamStream;
            try
            {
                upstreamStream = await upstream.Content.ReadAsStreamAsync(linked.Token);
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                return;
            }

            await using (upstreamStream.ConfigureAwait(false))
            {
                while (true)
                {
                    int read;
                    try
                    {
                        read = await upstreamStream.ReadAsync(buffer.AsMemory(0, BufferSize), linked.Token);
                    }
                    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                    {
                        return; // Client gone — normal termination.
                    }
                    catch (Exception ex)
                    {
                        // Mid-stream upstream failure (drop, idle timeout, decode error). Raw passthrough: never
                        // fabricate SSE frames. If nothing has reached the client we can still return a clean
                        // gateway error; once bytes are on the wire the status is locked, so we just stop —
                        // closing the response without a message_stop signals an incomplete stream (exactly as a
                        // raw upstream drop would), which the client detects without us inventing a terminal event.
                        logger.LogWarning("Mid-stream upstream failure: {Reason}", ex.Message);
                        if (!ctx.Response.HasStarted)
                        {
                            await WriteAnthropicErrorAsync(
                                ctx,
                                StatusCodes.Status502BadGateway,
                                "api_error",
                                "The upstream Copilot stream failed before any data was received."
                            );
                        }

                        return;
                    }

                    if (read == 0)
                    {
                        break;
                    }

                    idleCts.CancelAfter(idleTimeout); // Reset the idle deadline after each successful read.

                    try
                    {
                        await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), linked.Token);
                        await ctx.Response.Body.FlushAsync(linked.Token);
                    }
                    catch (OperationCanceledException)
                        when (ctx.RequestAborted.IsCancellationRequested || idleCts.IsCancellationRequested)
                    {
                        return; // Client gone, or the idle deadline fired on a stalled client.
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Copies the positive request-header allowlist (anthropic-version + anthropic-beta) only.</summary>
    public static void ApplyRequestHeaderAllowlist(IHeaderDictionary inbound, HttpRequestMessage upstream)
    {
        var version = inbound["anthropic-version"];
        if (StringValues.IsNullOrEmpty(version))
        {
            _ = upstream.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            foreach (var value in version)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _ = upstream.Headers.TryAddWithoutValidation("anthropic-version", value);
                }
            }
        }

        foreach (var value in inbound["anthropic-beta"])
        {
            if (!string.IsNullOrEmpty(value))
            {
                _ = upstream.Headers.TryAddWithoutValidation("anthropic-beta", value);
            }
        }
    }

    /// <summary>Copies upstream response headers verbatim except hop-by-hop/framing/content-coding.</summary>
    public static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        foreach (var header in upstream.Headers)
        {
            if (!ExcludedResponseHeaders.Contains(header.Key))
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (!ExcludedResponseHeaders.Contains(header.Key))
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }

    /// <summary>Writes an Anthropic-shaped error envelope (when the response has not started).</summary>
    public static async Task WriteAnthropicErrorAsync(HttpContext ctx, int status, string type, string message)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { type, message } });
        await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted);
    }

    /// <summary>
    ///     Builds the Anthropic-shaped GET /v1/models response listing every available model.
    ///     <paramref name="models"/> must be non-empty (the catalog always resolves at least one).
    /// </summary>
    public static string BuildModelsStub(IReadOnlyList<string> models)
    {
        var data = models
            .Select(model => new
            {
                type = "model",
                id = model,
                display_name = model,
                created_at = "2025-01-01T00:00:00Z",
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                data,
                has_more = false,
                first_id = models[0],
                last_id = models[^1],
            }
        );
    }
}

// =============================================================================
// MCP (Model Context Protocol) reverse proxy — Streamable HTTP transport
// =============================================================================

/// <summary>
///     Transparent reverse proxy for GitHub Copilot's MCP server (Streamable HTTP transport). Forwards
///     GET/POST/DELETE on <c>/mcp</c> and <c>/mcp/readonly</c> verbatim: no JSON-RPC parsing and no
///     proxy-side session bookkeeping — the <c>Mcp-Session-Id</c> the upstream server assigns on
///     <c>initialize</c> is just another response header this proxy copies through, and the caller is
///     responsible for echoing it back on subsequent requests exactly as it would talk to Copilot
///     directly.
/// </summary>
internal static class ProxyMcp
{
    // Everything is forwarded verbatim EXCEPT: Authorization (Copilot auth is attached outbound by
    // CopilotHeadersHandler instead — the caller's own auth, if any, is never forwarded) and a small
    // set of hop-by-hop/framing headers that .NET's HttpClient must own (Host, Content-Length,
    // Content-Type are handled explicitly, Connection/Transfer-Encoding/etc. are per-hop). Accept-Encoding
    // is also excluded: the shared HttpClient never negotiates compression (SocketsHttpHandler.
    // AutomaticDecompression = None) and CopyResponseHeaders always strips Content-Encoding from the
    // response, so forwarding a client's Accept-Encoding would risk an undecodable compressed body.
    private static readonly HashSet<string> ExcludedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Host",
        "Content-Length",
        "Content-Type",
        "Transfer-Encoding",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "TE",
        "Trailer",
        "Accept-Encoding",
    };

    /// <summary>Forwards GET/POST/DELETE on the MCP endpoint to Copilot and streams the response back.</summary>
    public static async Task ForwardAsync(HttpContext ctx, TimeSpan idleTimeout)
    {
        var services = ctx.RequestServices;
        var httpClient = services.GetRequiredService<HttpClient>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotAnthropicProxy");
        var stopwatch = Stopwatch.StartNew();

        var upstreamPath = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);

        if (string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            byte[] inboundBody;
            using (var memory = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(memory, ctx.RequestAborted);
                inboundBody = memory.ToArray();
            }

            upstreamRequest.Content = new ByteArrayContent(inboundBody);
            upstreamRequest.Content.Headers.ContentType = MediaTypeHeaderValue.TryParse(
                ctx.Request.ContentType,
                out var parsedContentType
            )
                ? parsedContentType
                : new MediaTypeHeaderValue("application/json");
        }

        ApplyRequestHeaderAllowlist(ctx.Request.Headers, upstreamRequest);

        // Per-request deadlines: link client-abort + a reset-per-read idle timeout, same as /v1/messages.
        // A standalone GET SSE stream lives as long as the server keeps sending events on it.
        using var idleCts = new CancellationTokenSource();
        idleCts.CancelAfter(idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, idleCts.Token);

        HttpResponseMessage upstream;
        try
        {
            upstream = await httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linked.Token
            );
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return; // Client disconnected before we connected; nothing to write.
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
            await WriteMcpErrorAsync(
                ctx,
                StatusCodes.Status504GatewayTimeout,
                "Timed out waiting for the upstream Copilot MCP server to respond."
            );
            return;
        }
        catch (InvalidOperationException ex)
        {
            // Token acquisition failure surfaces from CopilotHeadersHandler before the first byte.
            logger.LogError("Copilot token acquisition failed: {Reason}", ex.Message);
            await WriteMcpErrorAsync(
                ctx,
                StatusCodes.Status401Unauthorized,
                "Failed to acquire a GitHub Copilot token. Re-authenticate with the GitHub Copilot CLI or "
                    + "`gh auth login`, or set GITHUB_COPILOT_TOKEN / GH_TOKEN."
            );
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogError("Upstream MCP connection failed: {Reason}", ex.Message);
            await WriteMcpErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "Failed to reach the upstream Copilot MCP server."
            );
            return;
        }

        using (upstream)
        {
            // Lock status + headers verbatim (minus hop-by-hop/framing) — this is what carries
            // Mcp-Session-Id back to the client on the initialize response.
            ctx.Response.StatusCode = (int)upstream.StatusCode;
            ProxyHttp.CopyResponseHeaders(upstream, ctx.Response);

            var contentType = upstream.Content.Headers.ContentType;
            if (contentType is not null)
            {
                ctx.Response.ContentType = contentType.ToString();
            }

            var isSse = string.Equals(contentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            if (isSse)
            {
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                ctx.Response.Headers.CacheControl = "no-cache";
            }

            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await ProxyHttp.CopyBodyAsync(ctx, upstream, idleTimeout, idleCts, linked, logger);

            logger.LogInformation(
                "{Method} {Path} mcp-session={SessionId} upstream={Status} {Elapsed}ms",
                ctx.Request.Method,
                ctx.Request.Path,
                ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault() ?? "(none)",
                (int)upstream.StatusCode,
                stopwatch.ElapsedMilliseconds
            );
        }
    }

    /// <summary>Forwards every inbound header verbatim except <see cref="ExcludedRequestHeaders"/> (incl. auth).</summary>
    private static void ApplyRequestHeaderAllowlist(IHeaderDictionary inbound, HttpRequestMessage upstream)
    {
        foreach (var header in inbound)
        {
            if (ExcludedRequestHeaders.Contains(header.Key))
            {
                continue;
            }

            foreach (var value in header.Value)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    _ = upstream.Headers.TryAddWithoutValidation(header.Key, value);
                }
            }
        }
    }

    /// <summary>
    ///     Writes a JSON-RPC-shaped error for proxy-origin failures (never for upstream responses, which
    ///     are always passed through verbatim). Per the MCP spec, an error response for input the server
    ///     could not accept has no <c>id</c>.
    /// </summary>
    private static async Task WriteMcpErrorAsync(HttpContext ctx, int status, string message)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new { jsonrpc = "2.0", error = new { code = -32000, message } }
        );
        await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted);
    }
}

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this host in tests.</summary>
public partial class Program { }
