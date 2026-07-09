using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

/// <summary>
/// In-process stand-in for the sandbox gateway HTTP API, so a Workspace-Agent E2E can run with NO
/// live gateway. It answers the endpoints the app calls during a turn:
/// <list type="bullet">
///   <item>the health probe — so <c>SandboxGatewayLifetime</c> "adopts" it;</item>
///   <item>the sandbox-create <c>POST /api/v1/sandboxes</c> — whose body it <em>captures</em> so the
///         test can assert that the per-workspace marketplace selection actually reached the wire;</item>
///   <item>the discovered-items GET (empty) and the cleanup DELETE (no-op).</item>
/// </list>
/// The gateway's MCP endpoint is deliberately NOT served here: the app dials it with its own
/// transport and degrades gracefully when it can't reach it (see <c>ConnectHttpMcpClient</c>), which
/// is exactly what this test wants — no sandbox tools, just the observable create call.
///
/// <para>
/// <b>Auth-enforcement mode (issue #153):</b> by default (<see cref="CapturingSandboxGatewayHandler(bool)"/>
/// with <c>enforceAuth: false</c>) every gated request succeeds regardless of headers — the original
/// behavior every pre-existing E2E test relies on. Pass <c>enforceAuth: true</c> to opt a NEW test into
/// modeling the real gateway's per-app bearer auth: a gated request with a missing/blank
/// <c>X-Sbx-App-Key</c> (or missing <c>X-Sbx-App-Id</c>) gets <c>401</c>, and a request addressing an
/// existing session under a foreign <c>X-Sbx-App-Id</c> gets the gateway's uniform <c>404</c> (never a
/// distinguishing <c>403</c>). The two headers are always captured (see <see cref="CapturedRequests"/>),
/// independent of whether enforcement is on.
/// </para>
/// </summary>
public sealed class CapturingSandboxGatewayHandler : HttpMessageHandler
{
    /// <summary>
    /// Session id embedded in every mocked create response (see <see cref="CreateResponse"/>) — kept as
    /// a named constant purely so auth-enforcement ownership tracking can reference it. LIMITATION: the
    /// mock always returns this SAME id for every create call, so it cannot model two different apps
    /// each owning their OWN distinct session at once — only "app A creates, then a foreign app B
    /// addresses A's session" (and vice versa) is representable. That is sufficient for the uniform-404
    /// scoping assertions this issue needs.
    /// </summary>
    private const string CreatedSessionId = "e2e-sess-1";

    // Minimal but valid create response: a session id plus the workspace volume the registry reads
    // to populate SandboxSession.HostPath. Mirrors the shape asserted in the registry unit tests.
    private const string CreateResponse =
        """
        { "session_id": "e2e-sess-1", "container_id": "e2e-c-1",
          "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
        """;

    private readonly object _gate = new();
    private readonly bool _enforceAuth;
    private readonly List<CapturedRequest> _capturedRequests = [];

    // sessionId -> the X-Sbx-App-Id that created it. First creator wins (mirrors "owned by whoever
    // created it"); only consulted when _enforceAuth is true. ConcurrentDictionary rather than the
    // _gate lock: independent of the create-body capture, and reads/writes are single key-value ops.
    private readonly ConcurrentDictionary<string, string> _sessionOwner = new(
        StringComparer.Ordinal
    );

    private string? _lastCreateBody;

    /// <summary>
    /// Creates the mock. See the class remarks for what <paramref name="enforceAuth"/> changes; default
    /// (<c>false</c>) preserves the exact behavior every existing E2E test was written against, so no
    /// existing <c>new CapturingSandboxGatewayHandler()</c> call site needs to change.
    /// </summary>
    public CapturingSandboxGatewayHandler(bool enforceAuth = false)
    {
        _enforceAuth = enforceAuth;
    }

    /// <summary>One request as seen by the mock, in arrival order, with its captured auth headers.</summary>
    public readonly record struct CapturedRequest(
        string Method,
        string Path,
        string? AppId,
        string? AppKey);

    /// <summary>
    /// Every request the mock has received so far, in arrival order. Captured unconditionally (whether
    /// or not <c>enforceAuth</c> is on) so a test can assert header presence/values independently of
    /// enforcement.
    /// </summary>
    public IReadOnlyList<CapturedRequest> CapturedRequests
    {
        get
        {
            lock (_gate)
            {
                return [.. _capturedRequests];
            }
        }
    }

    /// <summary>The <c>X-Sbx-App-Id</c> header on the most recent request, or null if none sent yet / absent.</summary>
    public string? LastAppId
    {
        get
        {
            lock (_gate)
            {
                return _capturedRequests.Count == 0 ? null : _capturedRequests[^1].AppId;
            }
        }
    }

    /// <summary>The <c>X-Sbx-App-Key</c> header on the most recent request, or null if none sent yet / absent.</summary>
    public string? LastAppKey
    {
        get
        {
            lock (_gate)
            {
                return _capturedRequests.Count == 0 ? null : _capturedRequests[^1].AppKey;
            }
        }
    }

    /// <summary>
    /// The <c>X-Sbx-App-Id</c> that owns <paramref name="sessionId"/> (i.e. the app id that created it),
    /// or null when the mock has not recorded a creator for it. Only populated while <c>enforceAuth</c>
    /// is on.
    /// </summary>
    public string? OwnerOf(string sessionId) =>
        _sessionOwner.TryGetValue(sessionId, out var owner) ? owner : null;

    /// <summary>The raw JSON body of the most recent <c>POST /api/v1/sandboxes</c>, or null if none yet.</summary>
    public string? LastCreateBody
    {
        get
        {
            lock (_gate)
            {
                return _lastCreateBody;
            }
        }
    }

    /// <summary>
    /// The <c>marketplaces</c> array from the most recent create request, or null when the field was
    /// omitted (gateway-default selection) or no create has happened yet.
    /// </summary>
    public IReadOnlyList<string>? CapturedMarketplaces()
    {
        var body = LastCreateBody;
        if (body is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("marketplaces", out var mkt) || mkt.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return [.. mkt.EnumerateArray().Select(e => e.GetString() ?? string.Empty)];
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var appId = GetHeader(request, "X-Sbx-App-Id");
        var appKey = GetHeader(request, "X-Sbx-App-Key");

        var isCreate =
            request.Method == HttpMethod.Post
            && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal);
        var isDiscovered =
            request.Method == HttpMethod.Get
            && path.EndsWith("/discovered", StringComparison.Ordinal);
        var isMcp =
            request.Method == HttpMethod.Post && path.EndsWith("/mcp", StringComparison.Ordinal);
        var isSandboxById =
            !isDiscovered
            && (request.Method == HttpMethod.Get || request.Method == HttpMethod.Delete)
            && path.Contains("/api/v1/sandboxes/", StringComparison.Ordinal);
        var isGated = isCreate || isDiscovered || isMcp || isSandboxById;

        lock (_gate)
        {
            _capturedRequests.Add(new CapturedRequest(request.Method.Method, path, appId, appKey));
        }

        if (_enforceAuth && isGated)
        {
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appKey))
            {
                return Json("{}", HttpStatusCode.Unauthorized);
            }

            if (!isCreate)
            {
                var targetSessionId = isMcp
                    ? GetHeader(request, "X-Session-ID")
                    : ExtractSessionId(path);
                if (!string.IsNullOrEmpty(targetSessionId))
                {
                    var owner = _sessionOwner.GetOrAdd(targetSessionId, appId);
                    if (!string.Equals(owner, appId, StringComparison.Ordinal))
                    {
                        // Uniform 404: the real gateway never distinguishes "unknown session" from
                        // "known session owned by a different app" — both look identical to a foreign
                        // caller, so the mock must not leak a 403 here either.
                        return Json("{}", HttpStatusCode.NotFound);
                    }
                }
            }
        }

        if (isCreate)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _lastCreateBody = body;
            }

            if (_enforceAuth && appId is not null)
            {
                // First creator wins — see the CreatedSessionId limitation note above.
                _sessionOwner.GetOrAdd(CreatedSessionId, appId);
            }

            return Json(CreateResponse);
        }

        if (isDiscovered)
        {
            return Json("""{ "items": [] }""");
        }

        // /health probe, DELETE cleanup, and anything else: 200 so the lifetime adopts and teardown
        // stays quiet. The body is a harmless empty object.
        return Json("{}");
    }

    private static string? GetHeader(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// Pulls the sandbox session id out of a gated REST request's path. <c>/mcp</c> calls carry their
    /// session id in the <c>X-Session-ID</c> header instead (see the <c>isMcp</c> branch at the call
    /// site) — there is no id in the <c>/mcp</c> URL. Returns null for requests that don't address an
    /// existing session (e.g. the create call, whose path has no id segment).
    /// </summary>
    private static string? ExtractSessionId(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return null;
        }

        if (string.Equals(segments[^1], "discovered", StringComparison.Ordinal))
        {
            return segments.Length >= 2 ? segments[^2] : null;
        }

        var sandboxesIndex = Array.LastIndexOf(segments, "sandboxes");
        return sandboxesIndex >= 0 && sandboxesIndex + 1 < segments.Length
            ? segments[sandboxesIndex + 1]
            : null;
    }

    private static HttpResponseMessage Json(
        string body,
        HttpStatusCode statusCode = HttpStatusCode.OK
    ) => new(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
