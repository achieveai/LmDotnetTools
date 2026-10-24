using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.McpMiddleware;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace LmStreaming.Sample.Services.RemoteMcp;

/// <summary>One remote tool, renamed for the model, with the handler that calls it on its own server.</summary>
internal sealed record RemoteMcpTool(FunctionContract Contract, ToolHandler Handler);

/// <summary>What one build connected: the tools to register, and the connections the build owns.</summary>
internal sealed record RemoteMcpConnection(
    IReadOnlyList<RemoteMcpTool> Tools,
    IReadOnlyList<IAsyncDisposable> Resources
)
{
    public static readonly RemoteMcpConnection None = new([], []);
}

/// <summary>
/// SC7: connects a build to the HTTP MCP servers the sandbox's admitted plugins declare (the gateway lists them and
/// does not proxy them) and returns their tools as <c>mcp_plugin_&lt;plugin&gt;_&lt;server&gt;_&lt;tool&gt;</c> (see
/// <see cref="ToolName"/>).
/// <see cref="RemoteMcpServerPlan"/> decides each server; credentials come only from the egress key the plan matched.
/// </summary>
/// <remarks>
/// <para>No server fails the build: a failed fetch, key or connect logs a Warning and leaves those tools out.</para>
/// <para>
/// SECRET: a row's url, header values and extra, and every key header, are credential material. Logs name a server
/// by marketplace, plugin and server only; a reason is a fixed phrase, an exception type or an HTTP status, never an
/// exception message (one can echo the url). The MCP client, its transport and the function provider get no logger,
/// so the SDK never logs the endpoint. No redirect is followed, so headers never reach another origin.
/// </para>
/// </remarks>
internal static class RemoteMcpConnector
{
    /// <summary>The provider name the remote tools register under.</summary>
    internal const string ProviderName = "remote-mcp";

    /// <summary>The longest tool name every provider accepts (OpenAI's limit).</summary>
    internal const int MaxToolNameLength = 64;

    /// <summary>Key token, connect and <c>tools/list</c> of one server share this bound.</summary>
    internal static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fetches the session's plugin MCP servers, connects every one the plan admits (in parallel, each within
    /// <see cref="ConnectBound"/>) and returns their tools. Never throws for a gateway, key or server failure.
    /// </summary>
    /// <param name="fetch">The session's <c>GET …/mcp-servers</c>.</param>
    /// <param name="keys">The egress keys; matched by host and port only.</param>
    /// <param name="allowLoopbackHttp"><c>RemoteMcp:AllowLoopbackHttp</c>.</param>
    /// <param name="transportHandler">Test seam; null in production (a socket handler with no redirects).</param>
    /// <param name="logger">Names servers only.</param>
    /// <param name="sessionId">For the log lines.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="allowedOrigins">Operator-trusted origins; null/empty connects nothing.</param>
    public static async Task<RemoteMcpConnection> ConnectAsync(
        Func<CancellationToken, Task<IReadOnlyList<SandboxMcpServer>>> fetch,
        PredefinedKeyRegistry keys,
        bool allowLoopbackHttp,
        HttpMessageHandler? transportHandler,
        ILogger logger,
        string sessionId,
        CancellationToken ct,
        IReadOnlyList<string>? allowedOrigins = null
    )
    {
        ArgumentNullException.ThrowIfNull(fetch);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logger);

        IReadOnlyList<SandboxMcpServer> rows;
        try
        {
            rows = await fetch(ct).ConfigureAwait(false);
        }
        catch (SandboxException ex)
        {
            logger.LogWarning(
                "Remote MCP servers not listed {SessionId}: {ErrorKind} {StatusCode}{Retry}",
                sessionId,
                ex.Kind,
                ex.StatusCode,
                ex.Kind == SandboxErrorKind.Unavailable ? ", retryable; the next build retries" : string.Empty
            );
            return RemoteMcpConnection.None;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Remote MCP servers not listed {SessionId}: {ExceptionType}",
                sessionId,
                ex.GetType().Name
            );
            return RemoteMcpConnection.None;
        }

        logger.LogInformation("Remote MCP servers listed {SessionId} {Count}", sessionId, rows.Count);
        var entries = keys.Entries;
        var attempts =
            new List<Task<(SandboxMcpServer Row, IReadOnlyList<RemoteMcpTool> Tools, IAsyncDisposable? Resource)>>();
        foreach (var row in rows)
        {
            var decision = RemoteMcpServerPlan.Decide(row, entries, allowLoopbackHttp, allowedOrigins);
            if (decision.SkipReason is { } reason)
            {
                logger.Log(
                    decision.RoutineSkip ? LogLevel.Information : LogLevel.Warning,
                    "Remote MCP server {Marketplace}/{Plugin}/{Server} not connected: {Reason}",
                    row.Marketplace,
                    row.Plugin,
                    row.Name,
                    reason
                );
                continue;
            }

            if (decision.DroppedVariables.Count > 0)
            {
                logger.LogInformation(
                    "Remote MCP server {Marketplace}/{Plugin}/{Server} connects without the headers that name {Variables}",
                    row.Marketplace,
                    row.Plugin,
                    row.Name,
                    decision.DroppedVariables
                );
            }

            if (decision.DroppedHeaders.Count > 0)
            {
                logger.LogInformation(
                    "Remote MCP server {Marketplace}/{Plugin}/{Server} connects without the framing headers {Headers}",
                    row.Marketplace,
                    row.Plugin,
                    row.Name,
                    decision.DroppedHeaders
                );
            }

            attempts.Add(ConnectOneAsync(row, decision, keys, transportHandler, logger, ct));
        }

        var connected = await Task.WhenAll(attempts).ConfigureAwait(false);
        var tools = new List<RemoteMcpTool>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (row, serverTools, _) in connected)
        {
            foreach (var tool in serverTools)
            {
                // The same plugin and server in two marketplaces: the first in gateway order wins.
                if (!names.Add(tool.Contract.Name))
                {
                    logger.LogWarning(
                        "Remote MCP tool {Tool} of {Marketplace}/{Plugin}/{Server} skipped: another server already has the name",
                        tool.Contract.Name,
                        row.Marketplace,
                        row.Plugin,
                        row.Name
                    );
                    continue;
                }

                tools.Add(tool);
            }
        }

        return new RemoteMcpConnection(tools, [.. connected.Select(c => c.Resource).OfType<IAsyncDisposable>()]);
    }

    /// <summary>
    /// The model-facing name, <c>mcp_plugin_&lt;plugin&gt;_&lt;server&gt;_&lt;tool&gt;</c>, in the form the function
    /// registry registers it: <see cref="FunctionCollisionDetector.SanitizeName"/> replaces unsafe characters and
    /// collapses <c>__</c>, so Claude Code's <c>mcp__plugin_…__…</c> would be renamed on registration, and a renamed
    /// tool can be neither hidden by the tiers nor found by <c>ExecuteTool</c>.
    /// </summary>
    internal static string ToolName(string plugin, string server, string tool) =>
        FunctionCollisionDetector.SanitizeName($"mcp_plugin_{plugin}_{server}_{tool}");

    /// <summary>
    /// The production transport handler. It follows no redirect: a 3xx would carry the egress key's headers to
    /// another origin.
    /// </summary>
    internal static SocketsHttpHandler DefaultTransportHandler() => new() { AllowAutoRedirect = false };

    private static async Task<(
        SandboxMcpServer Row,
        IReadOnlyList<RemoteMcpTool> Tools,
        IAsyncDisposable? Resource
    )> ConnectOneAsync(
        SandboxMcpServer row,
        RemoteMcpDecision decision,
        PredefinedKeyRegistry keys,
        HttpMessageHandler? transportHandler,
        ILogger logger,
        CancellationToken ct
    )
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bound.CancelAfter(ConnectBound);
        HttpClientTransport? transport = null;
        McpClient? client = null;
        try
        {
            // Checked once here too, so a key with no valid token skips the server instead of failing each request.
            if (decision.Key is { } key && await KeyHeadersAsync(key, keys, bound.Token).ConfigureAwait(false) is null)
            {
                logger.LogWarning(
                    "Remote MCP server {Marketplace}/{Plugin}/{Server} not connected: its egress key has no valid token",
                    row.Marketplace,
                    row.Plugin,
                    row.Name
                );
                return (row, [], null);
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in decision.Headers)
            {
                headers[name] = value;
            }

            HttpMessageHandler handler = transportHandler ?? DefaultTransportHandler();
            if (decision.Key is not null)
            {
                handler = new KeyHeadersHandler(decision.Key, keys, handler, ownsInner: transportHandler is null);
            }

            transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = ProviderName,
                    Endpoint = decision.Endpoint!,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    ConnectionTimeout = ConnectBound,
                    AdditionalHeaders = headers,
                },
                new HttpClient(handler, disposeHandler: transportHandler is null || decision.Key is not null),
                loggerFactory: null,
                ownsHttpClient: true
            );
            client = await McpClient.CreateAsync(transport, cancellationToken: bound.Token).ConfigureAwait(false);
            var provider = await McpClientFunctionProvider
                .CreateAsync(
                    new Dictionary<string, McpClient> { [ProviderName] = client },
                    ProviderName,
                    NullLogger<McpClientFunctionProvider>.Instance,
                    bound.Token,
                    omitServerPrefix: true
                )
                .ConfigureAwait(false);

            var tools = new List<RemoteMcpTool>();
            foreach (var function in provider.GetFunctions())
            {
                var name = ToolName(row.Plugin, row.Name, function.Contract.Name);
                if (name.Length > MaxToolNameLength)
                {
                    logger.LogWarning(
                        "Remote MCP tool {Tool} of {Marketplace}/{Plugin}/{Server} skipped: its name is longer than {MaxLength}",
                        name,
                        row.Marketplace,
                        row.Plugin,
                        row.Name,
                        MaxToolNameLength
                    );
                    continue;
                }

                // The handler closes over the server's own tool name; only the model-facing contract is renamed.
                tools.Add(
                    new RemoteMcpTool(
                        new FunctionContract
                        {
                            Name = name,
                            Description = function.Contract.Description,
                            Namespace = function.Contract.Namespace,
                            ClassName = function.Contract.ClassName,
                            Parameters = function.Contract.Parameters,
                            ReturnType = function.Contract.ReturnType,
                            ReturnDescription = function.Contract.ReturnDescription,
                        },
                        function.Handler
                    )
                );
            }

            logger.LogInformation(
                "Remote MCP server {Marketplace}/{Plugin}/{Server} connected {ToolCount} {WithEgressKey}",
                row.Marketplace,
                row.Plugin,
                row.Name,
                tools.Count,
                decision.Key is not null
            );
            return (row, tools, new OwnedConnection(client, transport));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            await DisposeAsync(client, transport).ConfigureAwait(false);
            logger.LogWarning(
                "Remote MCP server {Marketplace}/{Plugin}/{Server} not connected: {Reason}",
                row.Marketplace,
                row.Plugin,
                row.Name,
                ReasonOf(ex, bound.IsCancellationRequested)
            );
            return (row, [], null);
        }
    }

    /// <summary>
    /// The key's headers for one revision of its entry, or null when it has no valid token (no headers set, or an
    /// OAuth key that is invalid or cannot mint) or was edited meanwhile. Never prompts: no auth banner.
    /// </summary>
    private static async Task<IReadOnlyList<KeyValuePair<string, string>>?> KeyHeadersAsync(
        PredefinedKeyEntry key,
        PredefinedKeyRegistry keys,
        CancellationToken ct
    )
    {
        var provider = keys.TryResolve(PredefinedKeyRegistry.ProviderIdPrefix + key.Id);
        if (provider is null || !ReferenceEquals(provider.Entry, key))
        {
            return null;
        }

        OAuthAccessToken token;
        try
        {
            token = await provider.GetAccessTokenAsync(scopes: null, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        // As the webhook does: never pair a token with a revision other than the one the destination matched.
        return ReferenceEquals(provider.Entry, key) ? PredefinedKeyProvider.BuildHeaders(key, token) : null;
    }

    private static string ReasonOf(Exception ex, bool timedOut) =>
        timedOut ? $"no answer within {ConnectBound.TotalSeconds:0} s"
        : ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}"
        : ex.GetType().Name;

    private static async ValueTask DisposeAsync(McpClient? client, HttpClientTransport? transport)
    {
        try
        {
            if (client is not null)
            {
                await client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Sets the matched egress key's headers on every request of one connection, over a same-named literal header.
    /// The token comes from the key provider on each request (its own cache and refresh), so no token outlives its
    /// expiry. A request is never sent once the key's entry is no longer the revision the plan matched (edited or
    /// removed) or has no valid token: an edited key's old credential stays home, and the next build matches anew.
    /// </summary>
    /// <remarks>
    /// An invoker, not a <see cref="DelegatingHandler"/>: that disposes its inner handler, and a borrowed transport
    /// handler (the test seam) must outlive the connection.
    /// </remarks>
    private sealed class KeyHeadersHandler(
        PredefinedKeyEntry key,
        PredefinedKeyRegistry keys,
        HttpMessageHandler inner,
        bool ownsInner
    ) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _inner = new(inner, ownsInner);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            // Fixed messages: one can reach the model as a failed tool call, and none names the url or a value.
            var headers =
                await KeyHeadersAsync(key, keys, cancellationToken).ConfigureAwait(false)
                ?? throw new HttpRequestException(
                    "The egress key this server connected with was changed or has no valid token."
                );
            foreach (var (name, value) in headers)
            {
                _ = request.Headers.Remove(name);
                if (!request.Headers.TryAddWithoutValidation(name, value))
                {
                    throw new HttpRequestException("An egress key header cannot be set on a request.");
                }
            }

            return await _inner.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>A connected server: the client and the transport (which owns its HttpClient), disposed together.</summary>
    private sealed class OwnedConnection(McpClient client, HttpClientTransport transport) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => RemoteMcpConnector.DisposeAsync(client, transport);
    }
}
