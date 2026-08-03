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
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.Misc.Configuration;
using AchieveAi.LmDotnetTools.Misc.Web.Jina;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;
using Serilog;
using Serilog.Formatting.Compact;
using ILogger = Microsoft.Extensions.Logging.ILogger;

// =============================================================================
// CopilotAnthropicProxy.Sample
//
// A loopback-only reverse proxy in front of GitHub Copilot that speaks BOTH
// dialects: Anthropic Messages (/v1/messages and its /count_tokens twin) and
// OpenAI (/v1/chat/completions, /v1/responses). Every POST also binds its
// un-prefixed form, because clients disagree about where the /v1 belongs.
// GET /v1/models answers in a body both dialects can parse; GET /health is liveness.
//
// The servable catalog is DISCOVERED at startup, never hard-coded — see
// ProxyModelResolver.ParseServableModels. A model is kept when it advertises at
// least one of /v1/messages, /chat/completions or /responses AND its vendor is not
// denied (Google, Microsoft). Entries advertising no endpoints at all are dropped,
// which is where text-embedding-* lives, so an embedding model can never surface as
// a chat model. The default is the highest-version Claude opus among the
// Messages-capable ids; it catches only requests naming a model this account does
// not have, and then only after family mapping (see ModelFamilies).
//
// Routing is the inbound dialect against what the named model advertises
// (ModelRouter.Resolve). Three of the four combinations are byte-for-byte
// passthrough. ONE is real translation — Anthropic Messages in, for a model that
// speaks only Responses — where the request, the non-streaming reply and the SSE
// stream are each rewritten (see Translation/). A dialect the model cannot serve
// 404s rather than being guessed at.
//
// On the passthrough paths the body's `model` is rewritten to the resolved id and
// the top-level `context_management` field is stripped; on the translated path the
// outbound body is built from scratch, so it cannot carry either. `anthropic-beta`
// is dropped on both (Copilot 400s the whole request over one unrecognised value).
// Copilot auth/headers come from the proven GithubCopilotProvider transport, and
// responses stream back without buffering.
// It also exposes Copilot's MCP server (Streamable HTTP transport) on /mcp and
// /mcp/readonly. Most traffic remains byte-transparent; supported JSON tool catalogs may gain
// keyed Jina fallbacks, whose calls are handled locally against the exact advertised schema.
//
// Point Claude Code, Codex CLI or opencode at it via that client's base-URL setting.
// SECURITY: binds to loopback only and attaches the developer's Copilot credentials
// outbound; never expose it on 0.0.0.0 or through a tunnel. See README.md.
// =============================================================================

var config = ProxyConfig.FromEnvironment();

var builder = WebApplication.CreateBuilder(args);

// --- Logging -----------------------------------------------------------------
// Structured logging via Serilog: canonical JSONL (@t / @mt / @l / @x, plus enriched properties)
// written by CompactJsonFormatter to a rolling file for DuckDB-queryable diagnostics, and a
// readable single-line console for the live operator. Mirrors LmStreaming.Sample and the shared
// test logging stack. The file sink lives under the app's bin/logs (git-ignored).
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithMachineName()
    .Enrich.WithThreadId()
    .Enrich.WithProperty("application", "CopilotAnthropicProxy")
    .WriteTo.File(
        new CompactJsonFormatter(),
        Path.Combine(AppContext.BaseDirectory, "logs", "copilot-anthropic-proxy-.log.jsonl"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7,
        shared: true,
        flushToDiskInterval: TimeSpan.FromSeconds(1)
    )
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}{NewLine}    {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

builder.Host.UseSerilog();

// Bind BOTH loopback families on the configured port. Binding both (== ListenLocalhost,
// still loopback-only) avoids the "::1 trap" when a client resolves localhost to IPv6.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, config.Port);
    options.Listen(IPAddress.IPv6Loopback, config.Port);
    // Local dev proxy on a trusted loopback socket. The forward path buffers and JsonNode-rewrites the
    // whole body in memory, so the cap matches Anthropic's own ~32 MB request limit rather than being
    // arbitrarily large; Kestrel rejects an over-limit body mid-read, before full allocation. The same
    // ceiling is applied to every buffered read inside the proxy (see ProxyHttp.ReadCappedAsync).
    options.Limits.MaxRequestBodySize = config.MaxBodyBytes;
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

var webToolsOptions = WebToolsOptions.FromEnvironment();
builder.Services.AddSingleton(webToolsOptions);
builder.Services.AddSingleton(sp =>
    new JinaWebProvider(
        webToolsOptions,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<JinaWebProvider>()
    )
);
builder.Services.AddSingleton<McpJinaToolCatalog>();
builder.Services.AddSingleton<McpToolSnapshotStore>();
builder.Services.AddSingleton<McpToolComposition>();

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
    Log.CloseAndFlush();
    return 1;
}

// 2) Resolve the outbound model catalog (env wins and pins a single model; else discover every
//    servable Copilot model — any of /v1/messages, /chat/completions, /responses, minus the vendor
//    denylist — and pick the highest-version `opus` Claude id as the default; else fail fast).
ProxyModelCatalog catalog;
try
{
    using var modelCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    catalog = await ProxyModelResolver.ResolveAsync(
        app.Services.GetRequiredService<HttpClient>(),
        config.ModelOverride,
        logger,
        modelCts.Token,
        config.ModelEndpointsOverride
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
    Log.CloseAndFlush();
    return 1;
}

logger.LogInformation(
    "CopilotAnthropicProxy listening on http://127.0.0.1:{Port} -> {BaseUrl} (default model: {Model}, "
        + "{Count} available; idle {IdleSeconds:0}s, keep-alive {KeepAliveSeconds:0}s)",
    config.Port,
    config.BaseUrl,
    catalog.Default,
    catalog.Available.Count,
    config.IdleTimeout.TotalSeconds,
    config.KeepAliveInterval.TotalSeconds
);

// --- Pipeline ----------------------------------------------------------------
// Host/loopback/cross-site guard runs FIRST. Its rejection is shaped for the dialect the caller was
// speaking: an OpenAI SDK cannot parse Anthropic's top-level {type, error} envelope, so a rejected
// /chat/completions or /responses request would otherwise surface as an unparseable body rather than
// as the 403 it is.
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
            await ProxyHttp.WriteErrorAsync(
                ctx,
                ProxyHttp.DialectForPath(ctx.Request.Path),
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

// GET /v1/models — dual-dialect union list of every available (servable) model.
app.MapGet(
    "/v1/models",
    () => Results.Content(ProxyHttp.BuildModelsStub(catalog.Models), "application/json", Encoding.UTF8)
);

// Each POST also binds its un-prefixed twin. Base-URL conventions differ per client: Claude Code
// appends /v1/messages to a bare host, the AI SDK appends only /messages to a ".../v1" base, and
// Codex joins "{base}/responses". Binding both forms removes a whole class of misconfiguration.
foreach (var path in new[] { "/v1/messages", "/messages" })
{
    _ = app.MapPost(
        path,
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                config.MaxBodyBytes,
                ProxyDialect.AnthropicMessages
            )
    );
    _ = app.MapPost(
        $"{path}/count_tokens",
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                config.MaxBodyBytes,
                ProxyDialect.AnthropicMessages,
                isCountTokens: true
            )
    );
}

foreach (var path in new[] { "/v1/chat/completions", "/chat/completions" })
{
    _ = app.MapPost(
        path,
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                config.MaxBodyBytes,
                ProxyDialect.ChatCompletions
            )
    );
}

foreach (var path in new[] { "/v1/responses", "/responses" })
{
    _ = app.MapPost(
        path,
        ctx =>
            ProxyHttp.ForwardAsync(
                ctx,
                catalog,
                config.IdleTimeout,
                config.KeepAliveInterval,
                config.MaxBodyBytes,
                ProxyDialect.Responses
            )
    );
}

// /mcp and /mcp/readonly — transparent MCP (Streamable HTTP) proxy. Every HTTP method is routed
// here (not just GET/POST/DELETE) so an unsupported method gets ProxyMcp's own MCP/JSON-RPC-shaped
// 405, not the shared Anthropic-shaped fallback 404.
app.Map(
    "/mcp",
    ctx => ProxyMcp.ForwardAsync(ctx, config.IdleTimeout, config.KeepAliveInterval, config.MaxBodyBytes)
);
app.Map(
    "/mcp/readonly",
    ctx => ProxyMcp.ForwardAsync(ctx, config.IdleTimeout, config.KeepAliveInterval, config.MaxBodyBytes)
);

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
Log.CloseAndFlush();
return 0;

// =============================================================================
// Configuration
// =============================================================================

/// <summary>Immutable proxy configuration sourced from environment variables.</summary>
internal sealed record ProxyConfig
{
    /// <summary>Matches the Anthropic API's own ~32 MB request limit.</summary>
    public const long DefaultMaxBodyBytes = 32L * 1024 * 1024;

    public required int Port { get; init; }
    public required string BaseUrl { get; init; }
    public required TimeSpan IdleTimeout { get; init; }
    public required TimeSpan KeepAliveInterval { get; init; }
    public required bool EnableDeviceFlow { get; init; }
    public required string? ModelOverride { get; init; }

    /// <summary>
    ///     Endpoints the pinned <see cref="ModelOverride"/> serves, when the operator states them.
    ///     Empty means "unstated" — see ProxyModelResolver.ResolveAsync.
    /// </summary>
    public required IReadOnlyList<string> ModelEndpointsOverride { get; init; }

    /// <summary>
    ///     Ceiling on any single buffered body, inbound or upstream. The forward and translate paths
    ///     both hold a whole body in memory to rewrite it, so this is what stops one oversized reply
    ///     from turning into several large-object-heap allocations.
    /// </summary>
    public required long MaxBodyBytes { get; init; }

    public static ProxyConfig FromEnvironment()
    {
        return new ProxyConfig
        {
            Port = ParseInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_PORT"), 8787),
            BaseUrl =
                NullIfBlank(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_BASE_URL"))
                ?? CopilotOptions.DefaultBaseUrl,
            IdleTimeout = TimeSpan.FromSeconds(
                ParseInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_IDLE_TIMEOUT_SECONDS"), 180)
            ),
            KeepAliveInterval = TimeSpan.FromSeconds(
                ParseNonNegativeInt(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_KEEPALIVE_SECONDS"), 15)
            ),
            EnableDeviceFlow = ParseBool(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_ENABLE_DEVICE_FLOW")),
            ModelOverride = NullIfBlank(Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL")),
            ModelEndpointsOverride = ParseList(
                Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_MODEL_ENDPOINTS")
            ),
            MaxBodyBytes = ParseLong(
                Environment.GetEnvironmentVariable("COPILOT_ANTHROPIC_MAX_BODY_BYTES"),
                DefaultMaxBodyBytes
            ),
        };
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IReadOnlyList<string> ParseList(string? value) =>
        NullIfBlank(value) is not { } list
            ? []
            : [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    private static long ParseLong(string? value, long fallback) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    // Keep-alive accepts 0 as an explicit "disabled" (ParseInt rejects it, treating 0 as unset).
    private static int ParseNonNegativeInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
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

        return IsAllowedOrigin(origin, port);
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

    /// <summary>
    ///     An absent Origin is fine; a present one must be a loopback host on the exact configured port —
    ///     matching <see cref="IsAllowedHost"/>'s exact-port match, not just any loopback port. Otherwise a
    ///     page on a different local port (e.g. another dev server, or something malicious) could still
    ///     satisfy "loopback" and reach this proxy cross-origin.
    /// </summary>
    private static bool IsAllowedOrigin(string? origin, int port)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Port != port)
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
///     One servable Copilot model: its id, its vendor, and the transports it advertises.
///     An EMPTY <paramref name="Endpoints"/> list means "no metadata" — either the model came from a
///     <c>/models</c> response that carried no <c>supported_endpoints</c> at all, or the catalog was
///     pinned via <c>COPILOT_ANTHROPIC_MODEL</c>. Callers treat that as Anthropic-Messages-capable,
///     which is exactly how the proxy behaved before endpoint metadata existed.
/// </summary>
public sealed record ProxyModelInfo(string Id, string Vendor, IReadOnlyList<string> Endpoints)
{
    /// <summary>True when this model advertises <paramref name="endpoint"/> (case-insensitive).</summary>
    public bool Supports(string endpoint) => Endpoints.Contains(endpoint, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     True for Anthropic models. Falls back to the id when the vendor is unknown, so a pinned
    ///     Claude model is still recognised as Anthropic and keeps its <c>max_tokens</c> spelling.
    /// </summary>
    public bool IsAnthropic =>
        Vendor.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
        || (Vendor.Length == 0 && Id.Contains("claude", StringComparison.OrdinalIgnoreCase));
}

/// <summary>The models this proxy will serve, plus the id used when a request names an unknown model.</summary>
public sealed record ProxyModelCatalog(string Default, IReadOnlyList<ProxyModelInfo> Models)
{
    /// <summary>
    ///     Every available model id, in upstream order. Computed rather than cached so a
    ///     <c>with</c>-expression cannot leave it stale; only startup logging and tests read it.
    /// </summary>
    public IReadOnlyList<string> Available => [.. Models.Select(m => m.Id)];

    /// <summary>Case-insensitive lookup. Null when the id is absent, blank, or null.</summary>
    public ProxyModelInfo? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Models.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Resolves the outbound Copilot model catalog and rewrites the inbound request's model field.</summary>
public static class ProxyModelResolver
{
    /// <summary>
    ///     Resolves the model catalog: <c>COPILOT_ANTHROPIC_MODEL</c> override wins and pins a single model
    ///     (no discovery, no passthrough); otherwise queries <c>GET /models</c> and keeps everything
    ///     <see cref="ParseServableModels"/> considers servable — advertising any of <c>/v1/messages</c>,
    ///     <c>/chat/completions</c> or <c>/responses</c>, vendor not denied.
    ///
    ///     <paramref name="pinnedEndpoints"/> states what the pinned id serves. It exists because pinning
    ///     skips discovery, so nothing else can know: without it a pinned Responses-only model has no
    ///     endpoint metadata, and <c>ModelRouter</c> reads "no metadata" as Messages-capable — which makes
    ///     the Anthropic-to-Responses translation route unreachable for exactly the models it was built
    ///     for. Empty keeps the historical behaviour.
    ///
    ///     The DEFAULT is drawn from a narrower set than the catalog. It is the fallback for the Anthropic
    ///     surface, so it is picked from the Messages-capable ids only, and among those it is the
    ///     highest-version <c>opus</c> Claude id. Throws when no override is set and there is no such id
    ///     (caller fails fast) — a catalog full of Responses-only models is not enough on its own.
    /// </summary>
    public static async Task<ProxyModelCatalog> ResolveAsync(
        HttpClient client,
        string? modelOverride,
        ILogger logger,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? pinnedEndpoints = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);

        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            var pinned = modelOverride.Trim();

            // DEVIATION D1: pinning still short-circuits discovery — see the plan's D1 note. Vendor stays
            // unknown; endpoints are whatever the operator stated. An empty list still means "no metadata",
            // which routes as Anthropic Messages and Chat Completions but not Responses: we do not claim
            // Responses support nobody advertised.
            IReadOnlyList<string> endpoints = pinnedEndpoints ?? [];
            if (endpoints.Count > 0)
            {
                logger.LogInformation(
                    "Model {Model} is pinned with the endpoints {Endpoints}; discovery is skipped.",
                    pinned,
                    string.Join(", ", endpoints)
                );
            }

            return new ProxyModelCatalog(pinned, [new ProxyModelInfo(pinned, string.Empty, endpoints)]);
        }

        using var response = await client.GetAsync("/models", cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        var models = ParseServableModels(json);

        // The default is the fallback for the Anthropic surface, so it must be able to serve /v1/messages.
        // A chat-completions-only model named "opus" is servable, but it cannot be the default.
        var claudeIds = models
            .Where(m => m.Endpoints.Count == 0 || m.Supports(CopilotModelsResponse.MessagesEndpoint))
            .Select(m => m.Id)
            .Where(id => id.Contains("claude", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var opus = PickHighestVersionOpusId(claudeIds);
        if (opus is null)
        {
            throw new InvalidOperationException(
                "No Claude Opus model is available on this Copilot account. Messages-capable Claude models: "
                    + (claudeIds.Count == 0 ? "(none)" : string.Join(", ", claudeIds))
            );
        }

        return new ProxyModelCatalog(opus, models);
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
        using var doc = JsonDocument.Parse(json);

        var ids = new List<string>();
        foreach (var item in CopilotModelsResponse.EnumerateModelEntries(doc.RootElement))
        {
            var id = CopilotModelsResponse.GetString(item, "id");
            if (!string.IsNullOrWhiteSpace(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary><c>POST /chat/completions</c> — the OpenAI Chat Completions transport.</summary>
    public const string ChatCompletionsEndpoint = "/chat/completions";

    /// <summary>The transports this proxy knows how to forward to.</summary>
    private static readonly string[] ReachableEndpoints =
    [
        CopilotModelsResponse.MessagesEndpoint,
        ChatCompletionsEndpoint,
        CopilotModelsResponse.ResponsesEndpoint,
    ];

    /// <summary>
    ///     Vendors this proxy refuses to serve. A DENYLIST, not an allowlist: several <c>/models</c>
    ///     shapes omit <c>vendor</c> entirely, and an allowlist would silently drop all of them. The
    ///     advertised endpoint list is the real capability signal; vendor only vetoes.
    ///     Google is excluded by user decision (2026-07-27); Microsoft's <c>mai-code-*</c> is a
    ///     Copilot-internal router rather than a chat model.
    /// </summary>
    private static readonly string[] ExcludedVendors = ["Google", "Microsoft"];

    /// <summary>
    ///     Parses a Copilot <c>/models</c> response into the models this proxy will serve, preserving
    ///     upstream order.
    ///
    ///     A model is kept when it advertises at least one endpoint in <see cref="ReachableEndpoints"/>
    ///     and its vendor is not in <see cref="ExcludedVendors"/>. Entries advertising NO endpoints are
    ///     dropped — that set includes <c>text-embedding-*</c>, which must never surface as a chat model.
    ///
    ///     The no-metadata fallback is deliberately per RESPONSE, not per entry: only when NOT ONE entry
    ///     carries <c>supported_endpoints</c> do we conclude the response uses an older shape and keep
    ///     every id. Otherwise a partially-annotated response would resurrect the embedding models.
    /// </summary>
    public static IReadOnlyList<ProxyModelInfo> ParseServableModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var entries = CopilotModelsResponse.EnumerateModelEntries(doc.RootElement).ToList();

        if (!entries.Any(CopilotModelsResponse.HasSupportedEndpoints))
        {
            return [.. ParseModelIds(json).Select(id => new ProxyModelInfo(id, string.Empty, []))];
        }

        var models = new List<ProxyModelInfo>();
        foreach (var item in entries)
        {
            var id = CopilotModelsResponse.GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var vendor = CopilotModelsResponse.GetString(item, "vendor") ?? string.Empty;
            if (ExcludedVendors.Contains(vendor, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var endpoints = ReachableEndpoints.Where(e => CopilotModelsResponse.SupportsEndpoint(item, e)).ToArray();
            if (endpoints.Length == 0)
            {
                continue;
            }

            models.Add(new ProxyModelInfo(id, vendor, endpoints));
        }

        return models;
    }

    /// <summary>
    ///     Model families, longest name first so a shorter name cannot shadow a longer one.
    ///     Used to route side traffic that names a model this account does not have — Claude Code sends
    ///     conversation-title, classification and summarisation calls as <c>claude-3-5-haiku-*</c> to the
    ///     SAME base URL, and without this they would all bill against the default opus model.
    /// </summary>
    private static readonly string[] ModelFamilies = ["sonnet", "haiku", "opus"];

    /// <summary>
    ///     Maps the model a client asked for onto a model this proxy serves:
    ///     exact match (case-insensitive) → same-family match → catalog default.
    ///     The dialect check downstream runs against the RESOLVED model (DEVIATION D2).
    /// </summary>
    public static string SelectOutboundModel(string? incomingModel, ProxyModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        if (catalog.Find(incomingModel) is { } exact)
        {
            return exact.Id;
        }

        if (!string.IsNullOrWhiteSpace(incomingModel))
        {
            var family = ModelFamilies.FirstOrDefault(f =>
                incomingModel.Contains(f, StringComparison.OrdinalIgnoreCase)
            );
            if (family is not null)
            {
                var sameFamily = catalog.Models.FirstOrDefault(m =>
                    m.Id.Contains(family, StringComparison.OrdinalIgnoreCase)
                );
                if (sameFamily is not null)
                {
                    return sameFamily.Id;
                }
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
    ///     Tool types Copilot's <c>/responses</c> accepts. Both are client-defined: <c>function</c>
    ///     (JSON-schema arguments) and <c>custom</c> (freeform text, which is how current Codex sends
    ///     <c>apply_patch</c>). Every other type is a hosted tool executed on the server.
    /// </summary>
    /// <remarks>
    ///     An allowlist rather than a denylist, for the same reason
    ///     <c>AnthropicToResponsesRequest.BuildTools</c> keys off <c>input_schema</c>: it drops hosted
    ///     tools this proxy has never seen instead of forwarding them into a 400. Live-probed
    ///     2026-07-28 — <c>image_generation</c>, <c>local_shell</c>, <c>code_interpreter</c> and
    ///     <c>mcp</c> each fail with <c>"The requested tool &lt;type&gt; is not supported."</c>
    ///
    ///     Keep this list curated by hand. It is meant to stay narrow, so it deliberately does not
    ///     widen on its own when Copilot starts accepting a new hosted type: admitting one is a
    ///     decision, not a consequence.
    ///
    ///     <c>web_search</c> and <c>web_search_preview</c> are the two types Copilot accepts that get
    ///     dropped anyway. Both are hosted, and the translated Anthropic route already drops their
    ///     counterpart <c>web_search_20250305</c>. The planned direction (agreed, not scheduled) is to
    ///     stop treating web search as a server tool at all: the proxy would expose it as a client tool
    ///     it implements itself, servicing the call against Copilot's MCP server. That makes it an
    ///     ordinary <c>function</c> and removes the exemption rather than widening this list.
    /// </remarks>
    private static readonly HashSet<string> ClientDefinedToolTypes = new(StringComparer.Ordinal)
    {
        "function",
        "custom",
    };

    /// <summary>
    ///     Raw <see cref="JsonNode"/> rewrite of the request body: sets/injects <c>model</c> and strips
    ///     the top-level <c>context_management</c> field (Copilot's backend rejects it outright with
    ///     <c>"context_management: Extra inputs are not permitted"</c>, so it can never be forwarded).
    ///     With <paramref name="stripHostedTools"/> it also filters <c>tools</c> down to
    ///     <see cref="ClientDefinedToolTypes"/>. Never deserializes to a typed DTO, so
    ///     <c>cache_control</c>, <c>thinking</c>, <c>system</c> blocks, and every other unknown field
    ///     are preserved verbatim.
    /// </summary>
    /// <returns>True on success; false when the body is missing, not JSON, or not a JSON object.</returns>
    public static bool TryRewriteModel(
        byte[] body,
        string model,
        out byte[] rewritten,
        out string? incomingModel,
        bool renameMaxTokens = false,
        bool stripHostedTools = false
    )
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
        _ = obj.Remove("context_management");

        // GPT models on Copilot reject `max_tokens` on /chat/completions and demand
        // `max_completion_tokens` — live-confirmed 2026-07-27. Claude models accept `max_tokens` on the
        // same endpoint, so this is keyed on the model, not the route. Clone before removing: a JsonNode
        // cannot be re-parented while it still belongs to the object.
        if (renameMaxTokens && obj["max_tokens"] is { } maxTokens)
        {
            var clonedMaxTokens = maxTokens.DeepClone();
            _ = obj.Remove("max_tokens");
            if (!obj.ContainsKey("max_completion_tokens"))
            {
                obj["max_completion_tokens"] = clonedMaxTokens;
            }
        }

        // Codex CLI advertises the hosted `image_generation` tool on every request and offers no way
        // to turn it off (`-c tools.image_generation=false` has no effect), so without this filter
        // Copilot 400s the request and Codex cannot use the proxy at all.
        if (stripHostedTools && obj["tools"] is JsonArray tools)
        {
            var kept = new JsonArray();
            foreach (var tool in tools)
            {
                if (
                    tool is JsonObject toolObject
                    && toolObject["type"] is JsonValue toolType
                    && toolType.TryGetValue<string>(out var type)
                    && ClientDefinedToolTypes.Contains(type)
                )
                {
                    kept.Add(tool.DeepClone());
                }
            }

            // Only touch the body when something was actually dropped: a request that carries no
            // hosted tools must forward byte-for-byte.
            if (kept.Count != tools.Count)
            {
                if (kept.Count == 0)
                {
                    _ = obj.Remove("tools");
                }
                else
                {
                    obj["tools"] = kept;
                }

                // A tool_choice that outlived its tool is not a smaller problem than the hosted tool
                // was: Responses rejects a choice with no tools at all, and a named choice pointing at
                // a tool that was just filtered out is a 400 for a tool the client never sent.
                DropOrphanedToolChoice(obj, kept);
            }
        }

        rewritten = JsonSerializer.SerializeToUtf8Bytes(obj);
        return true;
    }

    /// <summary>
    ///     Removes <c>tool_choice</c> when the tools that survived hosted-tool filtering can no longer
    ///     satisfy it. A bare "auto"/"none"/"required" string is kept whenever any tool survived; it is
    ///     dropped only when the tools key itself went away.
    /// </summary>
    private static void DropOrphanedToolChoice(JsonObject obj, JsonArray kept)
    {
        if (obj["tool_choice"] is not { } choice)
        {
            return;
        }

        if (kept.Count == 0)
        {
            _ = obj.Remove("tool_choice");
            return;
        }

        // The object form names one tool: {"type":"function","name":"shell"}. Anything else — a hosted
        // tool type such as {"type":"image_generation"}, or a shape we do not recognise — cannot be
        // checked against what survived, so it goes with the tools it referred to.
        if (choice is not JsonObject named)
        {
            return;
        }

        var name = ScalarString(named["name"]);
        var survives = name is not null && kept.OfType<JsonObject>().Any(tool => ScalarString(tool["name"]) == name);

        if (!survives)
        {
            _ = obj.Remove("tool_choice");
        }
    }

    /// <summary>Reads a JSON string, or null when the node is absent or carries another kind.</summary>
    private static string? ScalarString(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;
}

// =============================================================================
// HTTP forwarding + response shaping
// =============================================================================

/// <summary>The forward pipeline: header allowlist, response shaping, raw streaming, error envelopes.</summary>
internal static class ProxyHttp
{
    private const int BufferSize = 8192;

    // Positive allowlist for upstream response headers. A negative list forwards by default, so every
    // header a provider or an intermediary invents — Set-Cookie, tracing identifiers, whatever the next
    // gateway adds — reaches the client until someone remembers to deny it. This list is instead the
    // complete set the client is known to need: retry pacing, rate-limit accounting, the request id that
    // makes a provider-side incident traceable, and the one MCP protocol header below.
    // Content-Type is applied separately from the content headers.
    private static readonly HashSet<string> AllowedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Retry-After",
        "request-id",
        "x-request-id",
        "ETag",
        "Cache-Control",
        "Vary",
        "openai-processing-ms",
        "openai-version",
        "openai-model",
        // MCP Streamable HTTP: the server assigns the session id on InitializeResult and the client
        // MUST echo it on every later request. Withholding it does not merely lose a diagnostic — it
        // ends the session before it starts, so this is protocol payload rather than provider noise.
        "Mcp-Session-Id",
    };

    // Rate-limit families are open-ended (Anthropic alone ships requests/tokens/input-tokens/
    // output-tokens variants and adds more), so they are matched by prefix rather than enumerated.
    private static readonly string[] AllowedResponseHeaderPrefixes =
    [
        "anthropic-ratelimit-",
        "x-ratelimit-",
        "ratelimit-",
    ];

    /// <summary>
    ///     The dialect a path speaks, for code that runs before routing (the loopback guard) and so
    ///     cannot be told by the endpoint which dialect it is serving. Anything unrecognised is
    ///     Anthropic: that is the proxy's primary surface and the shape its fallback 404 already uses.
    /// </summary>
    public static ProxyDialect DialectForPath(PathString path)
    {
        var value = path.Value ?? string.Empty;

        if (HasSegment(value, "/chat/completions"))
        {
            return ProxyDialect.ChatCompletions;
        }

        return HasSegment(value, "/responses") ? ProxyDialect.Responses : ProxyDialect.AnthropicMessages;

        // Matches the route family in both bound forms — "/v1/responses" and "/responses" — without
        // matching a longer path that merely starts the same way.
        static bool HasSegment(string path, string segment) =>
            path.Equals(segment, StringComparison.OrdinalIgnoreCase)
            || path.Equals("/v1" + segment, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Forwards a request to Copilot in the given dialect and streams the response back.</summary>
    public static async Task ForwardAsync(
        HttpContext ctx,
        ProxyModelCatalog catalog,
        TimeSpan idleTimeout,
        TimeSpan keepAliveInterval,
        long maxBodyBytes,
        ProxyDialect dialect,
        bool isCountTokens = false
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
        //    back to the catalog's default. Resolve the route for this dialect and reject a mismatch.
        var outboundModel = ProxyModelResolver.SelectOutboundModel(ProxyModelResolver.PeekModel(inboundBody), catalog);

        // A model resolved via the default/family fallback may not be in the catalog at all (pinned mode);
        // treat it as metadata-free, which routes as Anthropic Messages.
        var modelInfo = catalog.Find(outboundModel) ?? new ProxyModelInfo(outboundModel, string.Empty, []);
        var route = ModelRouter.Resolve(dialect, modelInfo);

        if (route is null)
        {
            var alternatives = ModelRouter.Servable(dialect, catalog);
            await WriteErrorAsync(
                    ctx,
                    dialect,
                    StatusCodes.Status404NotFound,
                    "not_found_error",
                    $"Model '{outboundModel}' is not available on this endpoint. "
                        + $"Models that are: {(alternatives.Count == 0 ? "(none)" : string.Join(", ", alternatives))}."
                )
                .ConfigureAwait(false);
            return;
        }

        if (route.Kind == ProxyRouteKind.TranslateAnthropicToResponses)
        {
            // count_tokens has no Responses counterpart. Translating it would answer a token-count
            // request by running a full (billed) generation, and the only alternative — inventing a
            // count — is a fabricated answer. An honest 404 is the whole option set; Claude Code
            // already falls back to a local estimate when count_tokens is unavailable.
            if (isCountTokens)
            {
                await WriteAnthropicErrorAsync(
                        ctx,
                        StatusCodes.Status404NotFound,
                        "not_found_error",
                        $"Model '{outboundModel}' is served by translating to OpenAI Responses, which has no "
                            + "token-counting endpoint."
                    )
                    .ConfigureAwait(false);
                return;
            }

            await TranslateAnthropicToResponsesAsync(
                    ctx,
                    httpClient,
                    inboundBody,
                    outboundModel,
                    idleTimeout,
                    keepAliveInterval,
                    maxBodyBytes,
                    logger
                )
                .ConfigureAwait(false);
            return;
        }

        // 3) Rewrite the body (raw JSON). Parse failure -> 400 (do NOT call upstream).
        var renameMaxTokens = dialect == ProxyDialect.ChatCompletions && !modelInfo.IsAnthropic;
        var stripHostedTools = dialect == ProxyDialect.Responses;
        if (
            !ProxyModelResolver.TryRewriteModel(
                inboundBody,
                outboundModel,
                out var outboundBody,
                out var incomingModel,
                renameMaxTokens,
                stripHostedTools
            )
        )
        {
            await WriteErrorAsync(
                ctx,
                dialect,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Request body must be a non-empty JSON object."
            );
            return;
        }

        // 4) Build a fresh upstream request with the positive request-header allowlist.
        var upstreamPath = isCountTokens ? ModelRouter.CountTokensPath : route.UpstreamPath;
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamPath)
        {
            Content = new ByteArrayContent(outboundBody),
        };
        upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        ApplyRequestHeaderAllowlist(ctx.Request.Headers, upstreamRequest);

        // 5) Per-request deadlines: link client-abort + a reset-per-read idle timeout.
        using var idleCts = new CancellationTokenSource();
        idleCts.CancelAfter(idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, idleCts.Token);

        // 6) Send and read response headers (status + headers lock at first byte).
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
            await WriteErrorAsync(
                ctx,
                dialect,
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
            await WriteErrorAsync(
                ctx,
                dialect,
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
            await WriteErrorAsync(
                ctx,
                dialect,
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

            await CopyBodyAsync(
                ctx,
                upstream,
                idleTimeout,
                idleCts,
                linked,
                logger,
                (c, status, message) => WriteErrorAsync(c, dialect, status, "api_error", message),
                isSse,
                keepAliveInterval
            );

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

    /// <summary>
    ///     Streams the upstream body to the client with an explicit, incrementally-flushed loop.
    ///     While an SSE upstream stays silent, a keep-alive comment is emitted downstream every
    ///     <paramref name="keepAliveInterval" /> so the client's own read timeout does not fire mid-
    ///     generation (Copilot can go many seconds between chunks). Keep-alives are SSE-only and never
    ///     reset the upstream idle deadline, so a genuinely dead upstream still fails at
    ///     <paramref name="idleTimeout" />.
    ///     <paramref name="writePreStartError" /> writes a protocol-shaped error envelope (Anthropic-JSON
    ///     vs. JSON-RPC) if the upstream fails before any bytes reached the client — shared by both the
    ///     <c>/v1/messages</c> and <c>/mcp</c> paths, which disagree on error shape.
    /// </summary>
    internal static async Task CopyBodyAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        TimeSpan idleTimeout,
        CancellationTokenSource idleCts,
        CancellationTokenSource linked,
        ILogger logger,
        Func<HttpContext, int, string, Task> writePreStartError,
        bool isSse,
        TimeSpan keepAliveInterval
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
                        read = await ReadWithKeepAliveAsync(
                            ctx,
                            upstreamStream,
                            buffer,
                            isSse,
                            keepAliveInterval,
                            linked.Token
                        );
                    }
                    catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                    {
                        logger.LogDebug("Client disconnected mid-stream; ending relay.");
                        return; // Client gone — normal termination.
                    }
                    catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
                    {
                        // Upstream produced nothing for the whole idle window — distinct from a client
                        // disconnect (above) and from an upstream transport error (below). Keep-alives never
                        // reset this deadline, so reaching it means the upstream really is stalled.
                        logger.LogWarning(
                            "Upstream idle timeout after {IdleSeconds:0}s with no data; ending relay.",
                            idleTimeout.TotalSeconds
                        );
                        if (!ctx.Response.HasStarted)
                        {
                            await writePreStartError(
                                ctx,
                                StatusCodes.Status504GatewayTimeout,
                                "The upstream Copilot stream produced no data before the idle timeout."
                            );
                        }

                        return;
                    }
                    catch (Exception ex)
                    {
                        // Mid-stream upstream failure (drop, decode error). Raw passthrough: never fabricate
                        // SSE frames. If nothing has reached the client we can still return a clean gateway
                        // error; once bytes are on the wire the status is locked, so we just stop — closing
                        // the response without a message_stop signals an incomplete stream (exactly as a raw
                        // upstream drop would), which the client detects without us inventing a terminal event.
                        logger.LogWarning("Mid-stream upstream failure: {Reason}", ex.Message);
                        if (!ctx.Response.HasStarted)
                        {
                            await writePreStartError(
                                ctx,
                                StatusCodes.Status502BadGateway,
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
                        logger.LogDebug(
                            "Downstream write cancelled ({Reason}); ending relay.",
                            ctx.RequestAborted.IsCancellationRequested ? "client disconnect" : "idle timeout"
                        );
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

    /// <summary>
    ///     An SSE comment line (a line beginning with <c>:</c>). Every compliant SSE client ignores it,
    ///     so it keeps the connection warm without surfacing as an event or corrupting raw passthrough.
    /// </summary>
    private static readonly byte[] SseKeepAlive = ": copilot-anthropic-proxy keep-alive\n\n"u8.ToArray();

    /// <summary>
    ///     Awaits the next upstream chunk, emitting a downstream SSE keep-alive comment every
    ///     <paramref name="keepAliveInterval" /> while the upstream stays silent. Active only for SSE
    ///     responses with a positive interval; otherwise it is a plain read. The keep-alive timer never
    ///     restarts the read and never resets the upstream idle deadline — it only nudges the client so
    ///     its read timeout does not fire during a long, quiet generation.
    /// </summary>
    private static async Task<int> ReadWithKeepAliveAsync(
        HttpContext ctx,
        Stream upstreamStream,
        byte[] buffer,
        bool isSse,
        TimeSpan keepAliveInterval,
        CancellationToken linkedToken
    )
    {
        if (!isSse || keepAliveInterval <= TimeSpan.Zero)
        {
            return await upstreamStream.ReadAsync(buffer.AsMemory(0, BufferSize), linkedToken);
        }

        var readTask = upstreamStream.ReadAsync(buffer.AsMemory(0, BufferSize), linkedToken).AsTask();
        while (true)
        {
            using var keepAliveCts = new CancellationTokenSource();
            var completed = await Task.WhenAny(readTask, Task.Delay(keepAliveInterval, keepAliveCts.Token))
                .ConfigureAwait(false);
            keepAliveCts.Cancel(); // Stop the loser's timer (a no-op if it already finished).

            if (completed == readTask)
            {
                return await readTask; // Observe the bytes read / propagate any read exception.
            }

            // Upstream still silent past the interval: emit an SSE comment so the client keeps receiving
            // bytes and resets its read timeout. A comment carries no event/data, so it is invisible to the
            // client's event stream and preserves the raw passthrough.
            await ctx.Response.Body.WriteAsync(SseKeepAlive, linkedToken).ConfigureAwait(false);
            await ctx.Response.Body.FlushAsync(linkedToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     An Anthropic <c>ping</c> event. Anthropic's own stream emits these during long generations, so
    ///     every Messages client already tolerates them. The raw passthrough uses an SSE comment instead
    ///     because it must not alter the byte stream; this path is reframing events anyway, so a real
    ///     (contentless) event is the honest shape.
    /// </summary>
    private const string AnthropicPingFrame = "event: ping\ndata: {\"type\":\"ping\"}\n\n";

    /// <summary>How much upstream-authored error text is relayed to the client, in characters.</summary>
    private const int MaxRelayedErrorLength = 500;

    /// <summary>
    ///     Serves an Anthropic Messages request from a model this Copilot account exposes only through
    ///     OpenAI Responses: the request is translated on the way out and the reply on the way back.
    ///
    ///     Bytes are reframed rather than copied, so this path cannot use <see cref="CopyBodyAsync" />
    ///     and re-implements its two obligations itself. The idle deadline is reset before EVERY upstream
    ///     read, so it measures the gap between upstream lines and never the total request duration — a
    ///     long generation must not be killed for being long — and a silent upstream is covered by
    ///     downstream pings so an intermediary does not drop the connection mid-generation.
    /// </summary>
    private static async Task TranslateAnthropicToResponsesAsync(
        HttpContext ctx,
        HttpClient httpClient,
        byte[] inboundBody,
        string outboundModel,
        TimeSpan idleTimeout,
        TimeSpan keepAliveInterval,
        long maxBodyBytes,
        ILogger logger
    )
    {
        string translatedBody;
        bool wantsStream;
        try
        {
            if (JsonNode.Parse(inboundBody) is not JsonObject source)
            {
                // Defensive only: this route was chosen by reading `model` out of the body, so the body
                // has already parsed as a JSON object once.
                await WriteAnthropicErrorAsync(
                    ctx,
                    StatusCodes.Status400BadRequest,
                    "invalid_request_error",
                    "Request body must be a non-empty JSON object."
                );
                return;
            }

            source["model"] = outboundModel;
            wantsStream = source["stream"] is JsonValue flag && flag.TryGetValue<bool>(out var asked) && asked;
            translatedBody = AnthropicToResponsesRequest.Translate(source).ToJsonString();
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // A field carrying an unexpected JSON kind (max_tokens as a string, say) is the client's
            // error, so this is a 400 — and Copilot never sees a request we could not translate.
            logger.LogWarning(
                "Could not translate an Anthropic request for {Model}: {Reason}",
                outboundModel,
                ex.Message
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status400BadRequest,
                "invalid_request_error",
                "Request body could not be translated to the OpenAI Responses API."
            );
            return;
        }

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, ModelRouter.ResponsesPath)
        {
            Content = new StringContent(translatedBody, Encoding.UTF8, "application/json"),
        };
        ApplyRequestHeaderAllowlist(ctx.Request.Headers, upstreamRequest);

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
            logger.LogInformation(
                "{Method} {Path} model {ResolvedModel} stream={Stream} upstream={Status} (Anthropic -> Responses)",
                ctx.Request.Method,
                ModelRouter.ResponsesPath,
                outboundModel,
                wantsStream,
                (int)upstream.StatusCode
            );

            if (!upstream.IsSuccessStatusCode)
            {
                // The upstream status is preserved: a 429 or 503 must stay one so the client's own
                // retry logic still works. Only the envelope is reshaped.
                var errorBody = await ReadUpstreamBodyAsync(
                    ctx,
                    upstream,
                    outboundModel,
                    idleTimeout,
                    maxBodyBytes,
                    idleCts,
                    linked,
                    logger
                );
                if (errorBody is null)
                {
                    return; // Already answered as a 504/502, or the client left.
                }

                await WriteAnthropicErrorAsync(
                    ctx,
                    (int)upstream.StatusCode,
                    "api_error",
                    ExtractErrorMessage(errorBody, logger)
                );
                return;
            }

            if (!wantsStream)
            {
                await TranslateBufferedReplyAsync(
                    ctx,
                    upstream,
                    outboundModel,
                    idleTimeout,
                    maxBodyBytes,
                    idleCts,
                    linked,
                    logger
                );
                return;
            }

            var isSse = string.Equals(
                upstream.Content.Headers.ContentType?.MediaType,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase
            );
            if (!isSse)
            {
                // Relaying a non-SSE body down an SSE-shaped path would translate to zero frames, i.e.
                // an empty 200 that looks like a successful empty turn. Fail loudly instead.
                logger.LogWarning(
                    "Upstream answered a streaming request for {Model} with {ContentType}; refusing to relay it as SSE.",
                    outboundModel,
                    upstream.Content.Headers.ContentType?.MediaType ?? "(no content type)"
                );
                await WriteAnthropicErrorAsync(
                    ctx,
                    StatusCodes.Status502BadGateway,
                    "api_error",
                    "The upstream Copilot API answered a streaming request with a non-streaming reply."
                );
                return;
            }

            await TranslateStreamedReplyAsync(
                ctx,
                upstream,
                outboundModel,
                idleTimeout,
                idleCts,
                linked,
                keepAliveInterval,
                logger
            );
        }
    }

    /// <summary>
    ///     Reads a complete upstream body under the linked (client-abort + idle) token, bounded by
    ///     <paramref name="maxBodyBytes" />. Because the send used
    ///     <see cref="HttpCompletionOption.ResponseHeadersRead" /> this read still pulls from the
    ///     socket, and the proxy's <see cref="HttpClient" /> deliberately carries no timeout — so without
    ///     the idle token nothing at all would end a reply that stops arriving half-way through. The
    ///     deadline is re-armed here so it measures the wait for the BODY rather than counting whatever
    ///     the headers already spent. It does span the whole body read: a buffered reply arrives as one
    ///     payload, so there are no inter-chunk gaps to measure at this layer.
    ///
    ///     The cap matters because an idle timeout does not bound SIZE: an upstream that keeps sending
    ///     never goes idle, so a runaway or hostile reply would be buffered whole, in a single
    ///     large-object-heap allocation per concurrent request. The inbound direction is already bounded
    ///     by Kestrel's <c>MaxRequestBodySize</c>, set from the same configured limit.
    /// </summary>
    /// <returns>
    ///     The body, or <c>null</c> once the failure has already been answered (or the client has gone).
    ///     <c>null</c> is unambiguous — a successful read of an empty body yields <c>""</c>.
    /// </returns>
    private static async Task<string?> ReadUpstreamBodyAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        string outboundModel,
        TimeSpan idleTimeout,
        long maxBodyBytes,
        CancellationTokenSource idleCts,
        CancellationTokenSource linked,
        ILogger logger
    )
    {
        idleCts.CancelAfter(idleTimeout);

        try
        {
            var body = await ReadCappedStringAsync(upstream.Content, maxBodyBytes, linked.Token);
            if (body is not null)
            {
                return body;
            }

            logger.LogError(
                "Upstream reply for {Model} exceeded the {MaxBodyBytes}-byte relay cap and was not buffered.",
                outboundModel,
                maxBodyBytes
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "api_error",
                "The upstream Copilot reply was larger than this proxy will buffer."
            );
            return null;
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
            logger.LogWarning(
                "Upstream idle timeout after {IdleSeconds:0}s while reading the reply for {Model}.",
                idleTimeout.TotalSeconds,
                outboundModel
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status504GatewayTimeout,
                "api_error",
                "The upstream Copilot API stopped sending its reply before the idle timeout."
            );
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // A body that dies part-way is an upstream fault, and gets the same 502 the raw path's relay
            // produces. Letting it escape would surface as a bare 500 with no Anthropic envelope at all.
            logger.LogError(
                "Upstream connection dropped while reading the reply for {Model}: {Reason}",
                outboundModel,
                ex.Message
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "api_error",
                "The upstream Copilot API dropped the connection before its reply was complete."
            );
            return null;
        }
    }

    /// <summary>
    ///     Buffers an upstream body as UTF-8 text, or returns <c>null</c> the moment it would exceed
    ///     <paramref name="maxBytes" />. A declared <c>Content-Length</c> short-circuits the read
    ///     entirely; a chunked reply is measured as it arrives, so nothing over the cap is ever held.
    /// </summary>
    internal static async Task<byte[]?> ReadCappedBytesAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken token,
        CancellationTokenSource? idleCts = null,
        TimeSpan? idleTimeout = null
    )
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[BufferSize];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (idleCts is not null && idleTimeout is not null)
                {
                    idleCts.CancelAfter(idleTimeout.Value);
                }

                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
    }

    private static async Task<string?> ReadCappedStringAsync(
        HttpContent content,
        long maxBytes,
        CancellationToken token
    )
    {
        if (content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        var stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[BufferSize];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            // GetBuffer avoids the second full-size copy ToArray would make of a reply that can already
            // be megabytes; the decode below is the only other full-size representation.
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>
    ///     Translates a buffered Responses reply into an Anthropic Message. A reply this proxy cannot
    ///     read answers 502 rather than leaking a 500 — the contract
    ///     <see cref="ResponsesToAnthropicJson.Translate" /> documents its <see cref="ArgumentException" />
    ///     for.
    /// </summary>
    private static async Task TranslateBufferedReplyAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        string outboundModel,
        TimeSpan idleTimeout,
        long maxBodyBytes,
        CancellationTokenSource idleCts,
        CancellationTokenSource linked,
        ILogger logger
    )
    {
        var responsesJson = await ReadUpstreamBodyAsync(
            ctx,
            upstream,
            outboundModel,
            idleTimeout,
            maxBodyBytes,
            idleCts,
            linked,
            logger
        );
        if (responsesJson is null)
        {
            return; // Already answered as a 504/502, or the client left.
        }

        string anthropicJson;
        try
        {
            anthropicJson = ResponsesToAnthropicJson.Translate(responsesJson, outboundModel);
        }
        catch (ArgumentException ex)
        {
            // 502, not 400. The client's request was accepted, forwarded, and answered with 2xx; what
            // failed is the upstream's reply. Reporting that as invalid_request_error tells the client
            // to fix a request that was never wrong, and hides a real upstream regression behind a
            // client-side error class. The exception object is logged rather than only its message, so
            // a translator defect keeps its type and stack trace.
            logger.LogError(
                ex,
                "Could not translate the Responses reply for {Model} into an Anthropic message.",
                outboundModel
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "api_error",
                "The upstream Copilot reply could not be translated into an Anthropic message."
            );
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(anthropicJson, ctx.RequestAborted);
    }

    /// <summary>
    ///     Relays a Responses SSE stream as an Anthropic Messages SSE stream, one upstream line at a
    ///     time. Nothing is ever appended when the upstream ends early: a truncated stream is exactly
    ///     what a dropped upstream looks like, and capping it with a synthetic <c>message_stop</c> would
    ///     turn a failure into a silently empty success.
    /// </summary>
    private static async Task TranslateStreamedReplyAsync(
        HttpContext ctx,
        HttpResponseMessage upstream,
        string outboundModel,
        TimeSpan idleTimeout,
        CancellationTokenSource idleCts,
        CancellationTokenSource linked,
        TimeSpan keepAliveInterval,
        ILogger logger
    )
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
        catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
        {
            // The linked token cancels for two reasons and only one of them was handled above, so an
            // idle timeout during acquisition escaped as an unhandled OperationCanceledException — a
            // bare 500 with no Anthropic envelope, on the exact path whose 504 contract is documented.
            logger.LogWarning(
                "Upstream idle timeout after {IdleSeconds:0}s while opening the stream for {Model}.",
                idleTimeout.TotalSeconds,
                outboundModel
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status504GatewayTimeout,
                "api_error",
                "The upstream Copilot stream produced no data before the idle timeout."
            );
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            logger.LogError(
                "Upstream connection dropped while opening the stream for {Model}: {Reason}",
                outboundModel,
                ex.Message
            );
            await WriteAnthropicErrorAsync(
                ctx,
                StatusCodes.Status502BadGateway,
                "api_error",
                "The upstream Copilot API dropped the connection before its stream began."
            );
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status200OK;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var translator = new ResponsesToAnthropicSse($"msg_{Guid.NewGuid():N}", outboundModel);
        var reportedSilentDrop = false;

        await using (upstreamStream.ConfigureAwait(false))
        {
            // leaveOpen: the enclosing `await using` owns the stream. Double disposal is harmless, but
            // saying so beats leaving a reader that looks like it owns something it does not.
            using var reader = new StreamReader(upstreamStream, Encoding.UTF8, leaveOpen: true);

            // SSE allows one event to span several `data:` lines, which the receiver joins with "\n" at
            // the blank line that terminates the event. Feeding each line to the translator on its own —
            // which this loop used to do — turns any such event into several fragments that individually
            // fail to parse as JSON, and the translator's "unparseable payloads produce no frames"
            // contract then drops the whole event silently. Copilot sends single-line events today;
            // nothing in the protocol promises it always will.
            var pending = new StringBuilder();

            // Emits one reassembled event. Returns false when the relay should stop.
            async Task<bool> DispatchAsync(string payload)
            {
                // The [DONE] sentinel is not JSON and produces no frames, like any other event this
                // translator does not surface.
                var frames = translator.Next(payload.Trim());
                if (frames.Count == 0)
                {
                    reportedSilentDrop = ReportSilentlyDroppedEvent(
                        logger,
                        outboundModel,
                        payload,
                        reportedSilentDrop
                    );
                    return true;
                }

                try
                {
                    foreach (var frame in frames)
                    {
                        await ctx.Response.WriteAsync(frame, linked.Token);
                    }

                    await ctx.Response.Body.FlushAsync(linked.Token);
                }
                catch (OperationCanceledException)
                    when (ctx.RequestAborted.IsCancellationRequested || idleCts.IsCancellationRequested)
                {
                    logger.LogDebug(
                        "Downstream write cancelled ({Reason}); ending translated relay.",
                        ctx.RequestAborted.IsCancellationRequested ? "client disconnect" : "idle timeout"
                    );
                    return false;
                }

                return true;
            }

            while (true)
            {
                // Reset BEFORE each read: the deadline measures the gap between upstream lines, never the
                // total request duration.
                idleCts.CancelAfter(idleTimeout);

                string? line;
                try
                {
                    line = await ReadLineWithKeepAliveAsync(ctx, reader, keepAliveInterval, linked.Token);
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    logger.LogDebug("Client disconnected mid-translated-stream; ending relay.");
                    return;
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
                {
                    logger.LogWarning(
                        "Upstream idle timeout after {IdleSeconds:0}s with no data; ending translated relay.",
                        idleTimeout.TotalSeconds
                    );
                    if (!ctx.Response.HasStarted)
                    {
                        await WriteAnthropicErrorAsync(
                            ctx,
                            StatusCodes.Status504GatewayTimeout,
                            "api_error",
                            "The upstream Copilot stream produced no data before the idle timeout."
                        );
                    }

                    return;
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or ObjectDisposedException)
                {
                    // Narrowed to transport faults, and logging the exception OBJECT: a catch-all here
                    // swallowed programming defects — a NullReferenceException in the translator became
                    // an "upstream stream failed" line with no type and no stack trace, and the relay
                    // ended looking like an ordinary truncation.
                    //
                    // The upstream read and the downstream keep-alive write share this one call, so the
                    // failing side is not knowable here — saying "upstream" would blame Copilot for a
                    // client that hung up. The envelope below is upstream-only regardless: a keep-alive
                    // write can only fail after it has started the response, which this guard excludes.
                    logger.LogWarning(
                        ex,
                        "Translated relay for {Model} failed mid-stream, on either the upstream read or the "
                            + "downstream keep-alive write.",
                        outboundModel
                    );
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

                if (line is null)
                {
                    // Upstream EOF. An event whose terminating blank line never arrived is still an
                    // event the upstream sent, so it is dispatched rather than discarded.
                    if (pending.Length > 0)
                    {
                        _ = await DispatchAsync(pending.ToString());
                    }

                    return;
                }

                // The blank line terminates the event and is the only place a payload is dispatched.
                if (line.Length == 0)
                {
                    if (pending.Length > 0)
                    {
                        var payload = pending.ToString();
                        _ = pending.Clear();
                        if (!await DispatchAsync(payload))
                        {
                            return;
                        }
                    }

                    continue;
                }

                // Only `data:` matters: the translator dispatches on the JSON `type`, and the upstream
                // `event:` line merely repeats it. Comments and other fields fall out here.
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                // SSE strips exactly ONE optional space after the colon; any further whitespace is data.
                var value = line[5..];
                if (value.StartsWith(' '))
                {
                    value = value[1..];
                }

                if (pending.Length > 0)
                {
                    _ = pending.Append('\n');
                }

                _ = pending.Append(value);
            }
        }
    }

    /// <summary>
    ///     Awaits the next upstream SSE line, emitting a downstream Anthropic <c>ping</c> every
    ///     <paramref name="keepAliveInterval" /> while the upstream stays silent. The ping timer never
    ///     restarts the read and never resets the upstream idle deadline, so a genuinely dead upstream
    ///     still fails at the idle timeout.
    /// </summary>
    private static async Task<string?> ReadLineWithKeepAliveAsync(
        HttpContext ctx,
        StreamReader reader,
        TimeSpan keepAliveInterval,
        CancellationToken linkedToken
    )
    {
        if (keepAliveInterval <= TimeSpan.Zero)
        {
            return await reader.ReadLineAsync(linkedToken);
        }

        var readTask = reader.ReadLineAsync(linkedToken).AsTask();
        while (true)
        {
            using var keepAliveCts = new CancellationTokenSource();
            var completed = await Task.WhenAny(readTask, Task.Delay(keepAliveInterval, keepAliveCts.Token))
                .ConfigureAwait(false);
            keepAliveCts.Cancel(); // Stop the loser's timer (a no-op if it already finished).

            if (completed == readTask)
            {
                return await readTask; // Observe the line / propagate any read exception.
            }

            await ctx.Response.WriteAsync(AnthropicPingFrame, linkedToken);
            await ctx.Response.Body.FlushAsync(linkedToken);
        }
    }

    /// <summary>
    ///     Reports, at most once per stream, an upstream event that translated to nothing AND whose
    ///     absence a user would notice. An upstream FAILURE is no longer one of those cases — it now
    ///     produces a real Anthropic <c>error</c> frame — so what is left is the two ways an event can
    ///     vanish without anyone deciding it should: a tool call's arguments that carried nothing
    ///     readable, and a payload that is not a JSON object at all. Neither justifies inventing a frame,
    ///     but both are otherwise completely invisible. This is the only layer with a logger in scope —
    ///     the translators deliberately take no logging dependency. Returns the new "already reported"
    ///     state.
    /// </summary>
    private static bool ReportSilentlyDroppedEvent(ILogger logger, string model, string payload, bool alreadyReported)
    {
        if (alreadyReported)
        {
            return true;
        }

        string reason;
        if (payload.Contains("response.function_call_arguments.delta", StringComparison.Ordinal))
        {
            reason =
                "a tool call's arguments produced no output — the delta was absent, empty, or carried a "
                + "JSON kind other than string";
        }
        else if (!IsReadableEventPayload(payload))
        {
            reason =
                "an upstream event could not be read as a JSON object and was dropped; a truncated or "
                + "mis-framed SSE event is the usual cause";
        }
        else
        {
            return false;
        }

        logger.LogWarning("Translated stream for {Model}: {Reason}.", model, reason);
        return true;
    }

    /// <summary>
    ///     True when a zero-frame payload is one the translator legitimately chose not to surface,
    ///     rather than one it could not read. The empty payload and the <c>[DONE]</c> sentinel are both
    ///     expected and are not JSON.
    /// </summary>
    private static bool IsReadableEventPayload(string payload)
    {
        var trimmed = payload.Trim();
        if (trimmed.Length == 0 || trimmed == "[DONE]")
        {
            return true;
        }

        try
        {
            return JsonNode.Parse(trimmed) is JsonObject;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Pulls a human-readable message out of an upstream error body. Every read is kind-safe:
    ///     <c>{"error":"boom"}</c> indexed as an object throws <see cref="InvalidOperationException" />,
    ///     which would turn a clean relayed 503 into an unhandled 500.
    ///
    ///     Only a message the upstream deliberately PUT in a message field is relayed, and only capped.
    ///     A body with no such field used to be relayed as its own raw text, which is the shape most
    ///     likely to carry something the client should not see — an intermediary's HTML error page with
    ///     internal hostnames, a stack trace, a proxy's echo of the request. That text is now logged
    ///     (truncated) for the operator, who is the person who can act on it, and the client gets a
    ///     fixed sentence plus the status code it already has.
    ///
    ///     The cap is not redaction and is not claimed to be: <c>error.message</c> can still echo
    ///     request content back. This proxy binds to loopback only and serves one local user, who is the
    ///     same person whose prompt it would be echoing — so relaying the provider's own diagnosis is
    ///     worth more than withholding it. A multi-tenant deployment would have to replace this with a
    ///     fixed message per status code.
    /// </summary>
    private static string ExtractErrorMessage(string body, ILogger logger)
    {
        const string opaque =
            "The upstream Copilot API returned an error. See the proxy log for the response body.";

        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                if (ScalarText((obj["error"] as JsonObject)?["message"]) is { } nested)
                {
                    return CapErrorText(nested);
                }

                if (ScalarText(obj["error"]) is { } plain)
                {
                    return CapErrorText(plain);
                }

                if (ScalarText(obj["message"]) is { } top)
                {
                    return CapErrorText(top);
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON at all (an HTML error page from an intermediary, say) — fall through.
        }

        var trimmed = body.Trim();
        if (trimmed.Length == 0)
        {
            return "The upstream Copilot API returned an error with no body.";
        }

        logger.LogWarning(
            "Upstream error body carried no recognised message field; first {Length} characters: {Body}",
            Math.Min(trimmed.Length, MaxRelayedErrorLength),
            CapErrorText(trimmed)
        );
        return opaque;
    }

    /// <summary>Caps upstream-authored text at <see cref="MaxRelayedErrorLength" /> characters.</summary>
    private static string CapErrorText(string text) =>
        text.Length <= MaxRelayedErrorLength ? text : text[..MaxRelayedErrorLength];

    /// <summary>Reads a JSON string, or null if <paramref name="node" /> is absent or carries another kind.</summary>
    private static string? ScalarText(JsonNode? node) =>
        node is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    ///     Copies the positive request-header allowlist: only <c>anthropic-version</c> is forwarded (a
    ///     default is injected when absent). <c>anthropic-beta</c> is intentionally dropped, never
    ///     forwarded — Copilot's backend rejects the whole request with a 400 if it doesn't recognize
    ///     every single value in the header (e.g. Claude Code's evolving beta set routinely includes
    ///     values Copilot hasn't caught up to), so passing it through breaks requests Copilot would
    ///     otherwise serve fine.
    /// </summary>
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
    }

    /// <summary>
    ///     Copies the upstream response headers the client is known to need, and nothing else. See
    ///     <see cref="AllowedResponseHeaders" /> for why this is an allowlist rather than a denylist.
    /// </summary>
    public static void CopyResponseHeaders(HttpResponseMessage upstream, HttpResponse response)
    {
        foreach (var header in upstream.Headers)
        {
            if (IsRelayableResponseHeader(header.Key))
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (IsRelayableResponseHeader(header.Key))
            {
                response.Headers[header.Key] = header.Value.ToArray();
            }
        }
    }

    private static bool IsRelayableResponseHeader(string name) =>
        AllowedResponseHeaders.Contains(name)
        || AllowedResponseHeaderPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

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
    ///     Writes an OpenAI-shaped error envelope. No-op once the response has started — a half-written
    ///     body must not be capped with an error object.
    /// </summary>
    public static async Task WriteOpenAiErrorAsync(HttpContext ctx, int status, string type, string message)
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";

        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new
            {
                error = new
                {
                    message,
                    type,
                    param = (string?)null,
                    code = (string?)null,
                },
            }
        );

        await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>Writes an error in the envelope shape the INBOUND dialect expects.</summary>
    public static Task WriteErrorAsync(HttpContext ctx, ProxyDialect dialect, int status, string type, string message) =>
        dialect == ProxyDialect.AnthropicMessages
            ? WriteAnthropicErrorAsync(ctx, status, type, message)
            : WriteOpenAiErrorAsync(ctx, status, type, message);

    /// <summary>
    ///     Builds a model list that BOTH dialects can parse. Anthropic clients read
    ///     <c>data[].type == "model"</c> and <c>display_name</c>; OpenAI clients read
    ///     <c>object == "list"</c>, <c>data[].object == "model"</c> and <c>owned_by</c>. The two shapes do
    ///     not conflict, so one body carrying every field serves both and we never branch on the caller.
    ///
    ///     This assumes the caller ignores fields it does not know, which is what both official SDKs and
    ///     every client this proxy targets do — and what the APIs themselves rely on to add fields
    ///     without a version bump. A consumer that validates against a CLOSED schema would reject the
    ///     union, and that is the documented limit of this endpoint: serving such a client would need a
    ///     per-dialect view, which is only worth building once one is actually in scope. Nothing else in
    ///     this proxy shares the assumption — every other route relays the upstream body untouched.
    ///
    ///     <c>created</c> is a fixed epoch — Copilot's /models does not report a creation time, and the
    ///     field exists only because OpenAI SDKs deserialise into a struct that requires it.
    /// </summary>
    public static string BuildModelsStub(IReadOnlyList<ProxyModelInfo> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        const long CreatedEpochSeconds = 1735689600L; // 2025-01-01T00:00:00Z
        const string CreatedIso = "2025-01-01T00:00:00Z";

        var data = models
            .Select(m => new
            {
                type = "model",
                @object = "model",
                id = m.Id,
                display_name = m.Id,
                owned_by = string.IsNullOrEmpty(m.Vendor) ? "copilot" : m.Vendor,
                created = CreatedEpochSeconds,
                created_at = CreatedIso,
            })
            .ToArray();

        return JsonSerializer.Serialize(
            new
            {
                @object = "list",
                data,
                has_more = false,
                first_id = models.Count == 0 ? null : models[0].Id,
                last_id = models.Count == 0 ? null : models[^1].Id,
            }
        );
    }
}

// =============================================================================
// MCP (Model Context Protocol) reverse proxy — Streamable HTTP transport
// =============================================================================

/// <summary>
///     Reverse proxy for GitHub Copilot's MCP server (Streamable HTTP transport). Most traffic remains
///     byte-transparent. With valid local web-tool configuration, supported JSON <c>tools/list</c>
///     responses may gain missing Jina fallbacks and matching <c>tools/call</c> requests execute locally.
///     GitHub remains the session owner; the proxy keeps only the local routing snapshot and clears it on
///     DELETE or an upstream session 404.
/// </summary>
internal static class ProxyMcp
{
    // Everything is forwarded verbatim EXCEPT: Authorization/credential headers (Copilot auth is
    // attached outbound by CopilotHeadersHandler instead — the caller's own auth, if any, is never
    // forwarded), the Copilot transport/tracking headers CopilotHeadersHandler owns (it only sets
    // these when missing, so a caller-supplied value would otherwise silently override them), and a
    // small set of hop-by-hop/framing headers that .NET's HttpClient must own (Host, Content-Length,
    // Content-Type are handled explicitly, Connection/Transfer-Encoding/etc. are per-hop). Accept-Encoding
    // is also excluded: the shared HttpClient never negotiates compression (SocketsHttpHandler.
    // AutomaticDecompression = None) and CopyResponseHeaders always strips Content-Encoding from the
    // response, so forwarding a client's Accept-Encoding would risk an undecodable compressed body.
    private static readonly HashSet<string> ExcludedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "x-api-key",
        "Cookie",
        "Proxy-Authorization",
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
        "User-Agent",
        "copilot-integration-id",
        "editor-version",
        "x-github-api-version",
        "x-client-machine-id",
        "x-client-session-id",
        "x-interaction-id",
        "x-interaction-type",
        "x-initiator",
    };

    private static readonly string[] AllowedMethods = ["GET", "POST", "DELETE"];

    /// <summary>Forwards GET/POST/DELETE on the MCP endpoint to Copilot and streams the response back.</summary>
    public static async Task ForwardAsync(
        HttpContext ctx,
        TimeSpan idleTimeout,
        TimeSpan keepAliveInterval,
        long maxBodyBytes
    )
    {
        if (!AllowedMethods.Contains(ctx.Request.Method, StringComparer.OrdinalIgnoreCase))
        {
            await WriteMcpErrorAsync(
                ctx,
                StatusCodes.Status405MethodNotAllowed,
                $"Unsupported method {ctx.Request.Method}. Use GET, POST, or DELETE."
            );
            return;
        }

        var services = ctx.RequestServices;
        var httpClient = services.GetRequiredService<HttpClient>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("CopilotAnthropicProxy");
        var stopwatch = Stopwatch.StartNew();

        var upstreamPath = ctx.Request.Path.Value + ctx.Request.QueryString.Value;
        using var upstreamRequest = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), upstreamPath);
        byte[]? inboundBody = null;
        JsonObject? rpcRequest = null;
        long listGeneration = 0;
        var composition = services.GetRequiredService<McpToolComposition>();

        if (string.Equals(ctx.Request.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            using (var memory = new MemoryStream())
            {
                await ctx.Request.Body.CopyToAsync(memory, ctx.RequestAborted);
                inboundBody = memory.ToArray();
            }

            if (
                composition.IsEnabled
                && McpToolComposition.TryParseSingleRequest(inboundBody, out rpcRequest)
                && rpcRequest is not null
            )
            {
                listGeneration = composition.CaptureListGeneration(ctx, rpcRequest);
                _ = composition.TryHandleCancellation(ctx, rpcRequest);
                if (await composition.TryHandleCallAsync(ctx, rpcRequest))
                {
                    return;
                }
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
            var endpoint = ctx.Request.Path.Value ?? string.Empty;
            var sessionId = ctx.Request.Headers["Mcp-Session-Id"].FirstOrDefault();
            if (
                !string.IsNullOrWhiteSpace(sessionId)
                && (
                    string.Equals(ctx.Request.Method, "DELETE", StringComparison.OrdinalIgnoreCase)
                    || upstream.StatusCode == HttpStatusCode.NotFound
                )
            )
            {
                composition.RemoveSnapshot(endpoint, sessionId);
            }

            McpComposedList? composedList = null;
            if (rpcRequest is not null && McpToolComposition.IsToolsList(rpcRequest))
            {
                try
                {
                    composedList = await composition.ComposeListAsync(
                        ctx,
                        rpcRequest,
                        upstream,
                        maxBodyBytes,
                        linked.Token,
                        idleCts,
                        idleTimeout,
                        listGeneration
                    );
                }
                catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
                {
                    return;
                }
                catch (OperationCanceledException) when (idleCts.IsCancellationRequested)
                {
                    logger.LogWarning("Upstream MCP tool-list body timed out while being buffered.");
                    await WriteMcpErrorAsync(
                        ctx,
                        StatusCodes.Status504GatewayTimeout,
                        "Timed out waiting for the upstream Copilot MCP server to respond.",
                        rpcRequest?["id"]
                    );
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    logger.LogWarning(ex, "Upstream MCP tool-list body failed while being buffered.");
                    await WriteMcpErrorAsync(
                        ctx,
                        StatusCodes.Status502BadGateway,
                        "The upstream Copilot MCP server dropped the connection before its reply was complete.",
                        rpcRequest?["id"]
                    );
                    return;
                }

                if (composedList?.Body is { Length: 0 })
                {
                    await WriteMcpErrorAsync(
                        ctx,
                        StatusCodes.Status502BadGateway,
                        $"Upstream MCP reply exceeded the {maxBodyBytes}-byte relay cap.",
                        rpcRequest?["id"]
                    );
                    return;
                }
            }

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

            if (composedList is not null)
            {
                await ctx.Response.Body.WriteAsync(composedList.Body, ctx.RequestAborted);
                composition.PublishSnapshot(composedList);
            }
            else
            {
                await ProxyHttp.CopyBodyAsync(
                    ctx,
                    upstream,
                    idleTimeout,
                    idleCts,
                    linked,
                    logger,
                    (errorContext, status, message) => WriteMcpErrorAsync(errorContext, status, message),
                    isSse,
                    keepAliveInterval
                );
            }

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

    /// <summary>
    ///     Forwards every inbound header verbatim except <see cref="ExcludedRequestHeaders"/> — credentials
    ///     (<c>Authorization</c>, <c>x-api-key</c>, <c>Cookie</c>, <c>Proxy-Authorization</c>), the Copilot
    ///     transport/tracking headers <c>CopilotHeadersHandler</c> owns, and hop-by-hop/framing headers.
    /// </summary>
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
    private static async Task WriteMcpErrorAsync(
        HttpContext ctx,
        int status,
        string message,
        JsonNode? requestId = null
    )
    {
        if (ctx.Response.HasStarted)
        {
            return;
        }

        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["error"] = new JsonObject { ["code"] = -32000, ["message"] = message },
        };
        if (requestId is not null)
        {
            response["id"] = requestId.DeepClone();
        }

        await ctx.Response.Body.WriteAsync(
            Encoding.UTF8.GetBytes(response.ToJsonString()),
            ctx.RequestAborted
        );
    }
}

/// <summary>Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can boot this host in tests.</summary>
public partial class Program { }
