using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using AchieveAi.LmDotnetTools.Sandbox.Wire;
using ModelContextProtocol.Client;

namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Direct access to a leased browser through the gateway relay (gateway <c>browser/relay.rs</c>): Chrome's
/// CDP endpoint (<c>/api/v1/browsers/{id}/cdp[/…]</c>, HTTP <c>/json/*</c> plus WebSocket) and the browser's
/// bundled MCP servers (<c>/api/v1/browsers/{id}/mcp/{playwright|devtools}</c>, streamable HTTP). Every
/// request binds the id to a browser implicitly (there is no lease call) and resets its idle clock; every
/// response names the serving instance in <see cref="SandboxClient.BrowserInstanceHeader"/>. A changed
/// instance means the browser was replaced and all page state, CDP sockets and MCP sessions are gone.
/// </summary>
public sealed partial class SandboxClient
{
    /// <summary>Response header naming the browser instance that served a browser relay request.</summary>
    public const string BrowserInstanceHeader = "X-Sbx-Browser-Instance";

    /// <summary>The gateway's own per-message cap on the CDP WebSocket relay (screenshots and traces are large).</summary>
    internal const int MaxCdpMessageBytes = 256 * 1024 * 1024;

    /// <summary>
    /// The gateway URL of <paramref name="browserId"/>'s CDP endpoint (<c>http(s)://…/cdp</c>), plus
    /// <paramref name="cdpPath"/> when given (for example <c>/json/list</c>). Requests to it need this
    /// client's app headers; prefer <see cref="GetBrowserVersionAsync"/> and
    /// <see cref="ConnectBrowserCdpAsync"/>, which send them.
    /// </summary>
    public Uri GetBrowserCdpUri(string browserId, string? cdpPath = null)
    {
        ValidateBrowserId(browserId);
        return ResolveRequestUri(BrowserPath(browserId, "/cdp" + ValidateCdpPath(cdpPath)));
    }

    /// <summary>
    /// The <c>ws://</c> (or <c>wss://</c> for an HTTPS gateway) form of <see cref="GetBrowserCdpUri"/>, for a
    /// CDP client that opens its own socket. That socket must send this client's app headers on the upgrade;
    /// <see cref="ConnectBrowserCdpAsync"/> does.
    /// </summary>
    public Uri GetBrowserCdpWebSocketUri(string browserId, string cdpPath)
    {
        var builder = new UriBuilder(GetBrowserCdpUri(browserId, cdpPath));
        builder.Scheme = builder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        return builder.Uri;
    }

    /// <summary>
    /// <c>GET …/cdp/json/version</c>: binds the browser if needed and returns Chrome's version, the
    /// browser-level CDP path and the serving instance.
    /// </summary>
    public async Task<BrowserCdpVersion> GetBrowserVersionAsync(string browserId, CancellationToken ct = default)
    {
        ValidateBrowserId(browserId);
        using var response = await SendRestAsync(
                HttpMethod.Get,
                BrowserPath(browserId, "/cdp/json/version"),
                null,
                null,
                ct
            )
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await MapErrorResponseAsync(response, "browser CDP version", ct).ConfigureAwait(false);
        }

        var json = await ReadBrowserJsonAsync(response, ct).ConfigureAwait(false);
        var debuggerUrl = OptionalString(json, "webSocketDebuggerUrl");
        return new BrowserCdpVersion(
            BrowserInstance(response),
            OptionalString(json, "Browser"),
            OptionalString(json, "Protocol-Version"),
            debuggerUrl is null ? null : ToCdpPath(browserId, debuggerUrl),
            json
        );
    }

    /// <summary>
    /// Opens a CDP WebSocket to <paramref name="browserId"/> through the gateway, with this client's app
    /// headers on the upgrade. <paramref name="cdpPath"/> is the target below <c>/cdp</c> (for example
    /// <c>/devtools/page/&lt;id&gt;</c>) or a <c>webSocketDebuggerUrl</c> the gateway returned from
    /// <c>/json/*</c>; either way the socket goes to this client's <see cref="SandboxClientOptions.ServerAddress"/>,
    /// never to the host named in a URL. <c>null</c> connects to the browser-level target from
    /// <see cref="GetBrowserVersionAsync"/>.
    /// </summary>
    /// <exception cref="SandboxException">
    /// The gateway refused the upgrade (its typed <c>error_code</c>: <c>browser_capacity</c> with
    /// <see cref="SandboxException.RetryAfter"/>, <c>cdp_upgrade_rejected</c>, <c>browser_restarted</c>, …),
    /// or the transport failed.
    /// </exception>
    public async Task<BrowserCdpConnection> ConnectBrowserCdpAsync(
        string browserId,
        string? cdpPath = null,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBrowserId(browserId);
        var path = cdpPath is null
            ? (await GetBrowserVersionAsync(browserId, ct).ConfigureAwait(false)).DebuggerPath
                ?? throw new SandboxException(
                    SandboxErrorKind.Protocol,
                    "Gateway browser version carried no webSocketDebuggerUrl."
                )
            : ToCdpPath(browserId, cdpPath);
        var uri = GetBrowserCdpWebSocketUri(browserId, path);

        var handler = new BrowserRelayHandler(this, "browser CDP upgrade");
        var socket = new ClientWebSocket();
        using var budgetCts = StartTransportBudget();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, budgetCts.Token);
        try
        {
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            await socket.ConnectAsync(uri, invoker, timeoutCts.Token).ConfigureAwait(false);
            return new BrowserCdpConnection(browserId, handler.BrowserInstance, socket);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            socket.Dispose();
            if (handler.Failure is { } failure)
            {
                throw failure;
            }

            throw ex switch
            {
                SandboxException sandbox => sandbox,
                OperationCanceledException => new SandboxException(
                    SandboxErrorKind.TransportTimeout,
                    $"Browser CDP upgrade did not complete within the configured transport timeout "
                        + $"({_options.TransportTimeout})."
                ),
                HttpRequestException or WebSocketException { InnerException: HttpRequestException } =>
                    new SandboxException(
                        SandboxErrorKind.TransportTimeout,
                        $"Could not reach the sandbox gateway at '{_options.ServerAddress}'.",
                        statusCode: null,
                        ex
                    ),
                WebSocketException => new SandboxException(
                    SandboxErrorKind.Protocol,
                    "The gateway's browser CDP upgrade response was not a valid WebSocket handshake.",
                    statusCode: null,
                    ex
                ),
                _ => ex,
            };
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects an MCP client to one of <paramref name="browserId"/>'s bundled MCP servers through the
    /// gateway relay (streamable HTTP). Requests carry this client's app headers; a gateway error on the
    /// relay (for example <c>503 browser_capacity</c>) surfaces as a typed <see cref="SandboxException"/>,
    /// while the MCP server's own answers pass through to the MCP client unchanged.
    /// </summary>
    public async Task<BrowserMcpSession> ConnectBrowserMcpAsync(
        string browserId,
        BrowserMcpServer server,
        CancellationToken ct = default
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateBrowserId(browserId);
        var name = server switch
        {
            BrowserMcpServer.Playwright => "playwright",
            BrowserMcpServer.DevTools => "devtools",
            _ => throw new ArgumentOutOfRangeException(nameof(server)),
        };

        var handler = new BrowserRelayHandler(this, $"browser {name} MCP");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = $"sbx-browser-{name}",
                Endpoint = ResolveRequestUri(BrowserPath(browserId, "/mcp/" + name)),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan },
            loggerFactory: null,
            ownsHttpClient: true
        );
        try
        {
            var client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
            return new BrowserMcpSession(browserId, server, client, handler);
        }
        catch (Exception ex) when (FindSandboxException(ex) is { } typed && !ReferenceEquals(typed, ex))
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw typed;
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SandboxException? FindSandboxException(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
        {
            if (ex is SandboxException sandbox)
            {
                return sandbox;
            }
        }

        return null;
    }

    private static string? BrowserInstance(HttpResponseMessage response) =>
        response.Headers.TryGetValues(BrowserInstanceHeader, out var values) ? values.FirstOrDefault() : null;

    private static string? OptionalString(JsonElement json, string name) =>
        json.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// Reduces a CDP target to its path below <c>…/{id}/cdp</c>. Accepts that path itself, or a gateway
    /// <c>ws(s)://host/…/browsers/{id}/cdp/…</c> URL whose host is deliberately discarded: credentials go
    /// only to <see cref="SandboxClientOptions.ServerAddress"/>.
    /// </summary>
    private static string ToCdpPath(string browserId, string target)
    {
        if (target.StartsWith('/'))
        {
            return ValidateCdpPath(target);
        }

        var marker = $"/api/v1/browsers/{browserId}/cdp/";
        if (
            !Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss")
            || !uri.AbsolutePath.Contains(marker, StringComparison.Ordinal)
        )
        {
            throw new ArgumentException(
                $"A CDP target must be a path such as /devtools/page/<id> or a gateway URL for browser '{browserId}'.",
                nameof(target)
            );
        }

        var path = uri.AbsolutePath;
        return ValidateCdpPath(path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length - 1)..]);
    }

    private static string ValidateCdpPath(string? cdpPath)
    {
        if (cdpPath is null)
        {
            return string.Empty;
        }

        if (
            !cdpPath.StartsWith('/')
            || cdpPath.Contains('\\')
            || cdpPath.Contains('?')
            || cdpPath.Contains('#')
            || cdpPath.Any(char.IsControl)
            || cdpPath.Split('/').Any(segment => segment is "." or "..")
        )
        {
            throw new ArgumentException(
                "A CDP path must start with '/' and stay below the CDP endpoint.",
                nameof(cdpPath)
            );
        }

        return cdpPath;
    }

    /// <summary>
    /// The browser relay's transport seam. Every relay request goes through <see cref="StampAuthHeaders"/>
    /// and the client's own <see cref="Transport"/> (so a borrowed client's no-redirect posture holds), and
    /// every response's <see cref="BrowserInstanceHeader"/> is recorded. A non-success response that carries
    /// a gateway <c>error_code</c> becomes a typed <see cref="SandboxException"/>; anything else (an MCP
    /// server's own error, a stale <c>Mcp-Session-Id</c>) is handed back unchanged for its client to handle.
    /// </summary>
    internal sealed class BrowserRelayHandler(SandboxClient owner, string operation) : HttpMessageHandler
    {
        private readonly object _gate = new();
        private string? _firstInstance;
        private string? _latestInstance;

        /// <summary>The instance that served the first response.</summary>
        public string? BrowserInstance
        {
            get
            {
                lock (_gate)
                {
                    return _firstInstance;
                }
            }
        }

        /// <summary>The instance that served the most recent response.</summary>
        public string? LatestBrowserInstance
        {
            get
            {
                lock (_gate)
                {
                    return _latestInstance;
                }
            }
        }

        /// <summary>The typed failure of the last rejected request, for callers that cannot see the throw.</summary>
        public SandboxException? Failure { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            ObjectDisposedException.ThrowIf(owner._disposed, owner);

            // The MCP transport's own HttpClient has already marked `request` as sent, and an HttpClient
            // refuses to send a request twice, so the borrowed Transport gets an identical copy.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy,
            };
            foreach (var header in request.Headers)
            {
                _ = forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            owner.StampAuthHeaders(forwarded);
            var response = await owner
                .Transport.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (BrowserInstance(response) is { } instance)
            {
                lock (_gate)
                {
                    _firstInstance ??= instance;
                    _latestInstance = instance;
                }
            }

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.SwitchingProtocols)
            {
                return response;
            }

            return await MapRelayErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }

        private async Task<HttpResponseMessage> MapRelayErrorAsync(
            HttpResponseMessage response,
            CancellationToken cancellationToken
        )
        {
            // Redirects are refused and auth rejections are classified without reading the body, exactly
            // as on the control plane.
            var status = (int)response.StatusCode;
            if (
                status is >= 300 and < 400
                || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            )
            {
                using (response)
                {
                    throw Fail(
                        await owner.MapErrorResponseAsync(response, operation, cancellationToken).ConfigureAwait(false)
                    );
                }
            }

            byte[] body;
            try
            {
                body = await ReadStreamCappedAsync(response, "error body", null, MaxErrorBodyBytes, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (SandboxException ex) when (ex.IsDirectReadCapExceeded)
            {
                using (response)
                {
                    throw Fail(ToGatewayException(response, operation, null, operationId: null));
                }
            }

            GatewayErrorDto? error = null;
            try
            {
                error =
                    body.Length == 0
                        ? null
                        : JsonSerializer.Deserialize<GatewayErrorDto>(body, SandboxJson.RestOptions);
            }
            catch (JsonException)
            {
                // Not a gateway error body: the upstream MCP server or Chrome answered; pass it through.
            }

            if (!string.IsNullOrEmpty(error?.ErrorCode))
            {
                using (response)
                {
                    throw Fail(ToGatewayException(response, operation, error, operationId: null));
                }
            }

            var contentType = response.Content.Headers.ContentType;
            response.Content.Dispose();
            response.Content = new ByteArrayContent(body);
            if (contentType is not null)
            {
                response.Content.Headers.ContentType = contentType;
            }

            return response;
        }

        private SandboxException Fail(SandboxException failure)
        {
            Failure = failure;
            return failure;
        }
    }
}
