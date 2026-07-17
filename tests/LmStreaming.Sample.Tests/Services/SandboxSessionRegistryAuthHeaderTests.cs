using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins issue #153 M1: every gateway-facing call the registry makes stamps the sandbox auth
/// headers (<c>X-Sbx-App-Id</c> / <c>X-Sbx-App-Key</c>) with the configured
/// <see cref="SandboxGatewayOptions.AppId"/>/<see cref="SandboxGatewayOptions.AppKey"/> — create,
/// liveness probe, list-discovered, read-workspace-file (now the direct files REST API), and destroy.
/// These are exactly the gated call sites the plan requires to carry the per-app credential.
/// </summary>
public sealed class SandboxSessionRegistryAuthHeaderTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string ConfiguredAppId = "auth-header-test-app";
    private const string ConfiguredAppKey = "configured-app-key-value";

    [Fact]
    public async Task CreateSessionAsync_SendsAuthHeaders()
    {
        var (registry, handler) = CreateRegistry();

        _ = await registry.GetOrCreateSessionAsync();

        var createRequest = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Post
                && r.RequestUri!.AbsolutePath.EndsWith("/sandboxes", StringComparison.Ordinal)
            )
            .Which;
        AssertAuthHeaders(createRequest);
    }

    [Fact]
    public async Task IsSessionAliveAsync_SendsAuthHeaders()
    {
        var (registry, handler) = CreateRegistry();
        var session = await registry.GetOrCreateSessionAsync();

        // GetOrCreateLiveSessionAsync reuses the cached session and triggers the liveness probe.
        _ = await registry.GetOrCreateLiveSessionAsync();

        var livenessRequest = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath == $"/api/v1/sandboxes/{session.SessionId}"
            )
            .Which;
        AssertAuthHeaders(livenessRequest);
    }

    [Fact]
    public async Task ListDiscoveredAsync_SendsAuthHeaders()
    {
        var (registry, handler) = CreateRegistry();
        var session = await registry.GetOrCreateSessionAsync();

        _ = await registry.ListDiscoveredAsync(session.SessionId);

        var request = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Get
                && r.RequestUri!.AbsolutePath.EndsWith("/discovered", StringComparison.Ordinal)
            )
            .Which;
        AssertAuthHeaders(request);
    }

    [Fact]
    public async Task ReadWorkspaceFileAsync_SendsAuthHeaders()
    {
        var (registry, handler) = CreateRegistry();
        var session = await registry.GetOrCreateSessionAsync();

        _ = await registry.ReadWorkspaceFileAsync(session.SessionId, "/workspace/CLAUDE.md");

        // The read now speaks the direct files REST API (GET .../files/{mount_id}?path=...). The auth
        // headers — and the X-Session-ID scoping header — must be stamped on that call.
        var request = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Get && r.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal)
            )
            .Which;
        AssertAuthHeaders(request);
        request.Headers.GetValues("X-Session-ID").Should().ContainSingle().Which.Should().Be(session.SessionId);
    }

    [Fact]
    public async Task DestroyWorkspaceSessionAsync_SendsAuthHeaders()
    {
        var (registry, handler) = CreateRegistry();
        _ = await registry.GetOrCreateSessionAsync();

        await registry.DestroyWorkspaceSessionAsync(SandboxSessionRegistry.DefaultWorkspaceId);

        var request = handler.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete).Which;
        AssertAuthHeaders(request);
    }

    private static void AssertAuthHeaders(HttpRequestMessage request)
    {
        request.Headers.GetValues("X-Sbx-App-Id").Should().ContainSingle().Which.Should().Be(ConfiguredAppId);
        request.Headers.GetValues("X-Sbx-App-Key").Should().ContainSingle().Which.Should().Be(ConfiguredAppKey);
    }

    private static (SandboxSessionRegistry Registry, RecordingHandler Handler) CreateRegistry()
    {
        var handler = new RecordingHandler(DefaultRespond);
        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            AppId = ConfiguredAppId,
            AppKey = ConfiguredAppKey,
        };

        // The gateway lifetime client only serves the /health adopt probe; the registry's own
        // client (below) is the one whose requests these tests assert on.
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(handler),
            auth,
            new AuthSharedSecret(auth)
        );

        return (registry, handler);
    }

    private static HttpResponseMessage DefaultRespond(HttpRequestMessage req)
    {
        var path = req.RequestUri!.AbsolutePath;

        if (req.Method == HttpMethod.Post && path.EndsWith("/sandboxes", StringComparison.Ordinal))
        {
            // The create response carries the workspace mount id (volumes.workspace.id) so the SDK seeds
            // its mount cache; a follow-up read then issues the direct files GET without an extra mount GET.
            const string body = """
                { "session_id": "sess-1", "container_id": "c-1",
                  "volumes": { "workspace": { "container_path": "/workspace", "read_only": false, "id": 7 } } }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }

        if (req.Method == HttpMethod.Get && path.EndsWith("/discovered", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"discovered":[]}""", Encoding.UTF8, "application/json"),
            };
        }

        if (req.Method == HttpMethod.Get && path.Contains("/files/", StringComparison.Ordinal))
        {
            // The direct files API returns the file's exact bytes as application/octet-stream. Checked
            // BEFORE the generic /sandboxes/ probe below, since a files path also contains "/sandboxes/".
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("hello")),
            };
        }

        if (req.Method == HttpMethod.Get && path.Contains("/sandboxes/", StringComparison.Ordinal))
        {
            // Liveness / mount-resolution probe: any 2xx means "still known to the gateway".
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (req.Method == HttpMethod.Delete)
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    /// <summary>
    /// Delegates the response to a lambda and records every request sent through it (unlike the
    /// single-<c>LastRequest</c> stub used elsewhere), since these tests exercise a create →
    /// liveness/discover/read/destroy sequence over one shared handler.
    /// </summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            _requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
