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
/// </summary>
public sealed class CapturingSandboxGatewayHandler : HttpMessageHandler
{
    // Minimal but valid create response: a session id plus the workspace volume the registry reads
    // to populate SandboxSession.HostPath. Mirrors the shape asserted in the registry unit tests.
    private const string CreateResponse =
        """
        { "session_id": "e2e-sess-1", "container_id": "e2e-c-1",
          "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
        """;

    private readonly object _gate = new();
    private string? _lastCreateBody;

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

        if (request.Method == HttpMethod.Post && path.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _lastCreateBody = body;
            }

            return Json(CreateResponse);
        }

        if (request.Method == HttpMethod.Get && path.EndsWith("/discovered", StringComparison.Ordinal))
        {
            return Json("""{ "items": [] }""");
        }

        // /health probe, DELETE cleanup, and anything else: 200 so the lifetime adopts and teardown
        // stays quiet. The body is a harmless empty object.
        return Json("{}");
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
