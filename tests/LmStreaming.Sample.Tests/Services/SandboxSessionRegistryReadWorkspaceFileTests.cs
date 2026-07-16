using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the gateway-read seam used to inject workspace context files (CLAUDE.md / AGENTS.md) into
/// the system prompt at boot. The backend cannot read the container's <c>/workspace</c> filesystem,
/// so it fetches content through the gateway via the typed Sandbox SDK's direct files API (ADR 0031 /
/// issue #119): the SDK resolves the session's workspace mount
/// (<c>GET /api/v1/sandboxes/{id}</c> → <c>volumes.workspace.id</c>), then a single
/// <c>GET /api/v1/sandboxes/{id}/files/{mount_id}?path=...</c> returns the file's exact bytes as
/// <c>application/octet-stream</c>, scoped by the <c>X-Session-ID</c> header. These tests drive that
/// REST protocol end-to-end: they assert the raw file bytes are returned (the SDK returns exact
/// content — no <c>cat -n</c> stripping), that a missing file yields the best-effort <c>null</c>
/// contract, and that a gateway/session error also yields <c>null</c>.
/// </summary>
public sealed class SandboxSessionRegistryReadWorkspaceFileTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string SessionId = "sess-1";
    private const long MountId = 7;

    [Fact]
    public async Task ReadWorkspaceFile_TransfersFileContent_ReturnsRawBytes()
    {
        // Raw file content — note it deliberately contains no line-number prefixes: the direct files
        // API returns the file's exact bytes, unlike the old MCP Read `cat -n` path.
        const string fileText = "# Hello\nWorld\n";
        var fileBytes = Encoding.UTF8.GetBytes(fileText);

        var (registry, handler) = CreateRegistry(req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fileBytes) }
                : MountResolution());
        await using var _ = registry;

        var content = await registry.ReadWorkspaceFileAsync(SessionId, "/workspace/CLAUDE.md");

        content.Should().Be(fileText);

        // The read resolved the workspace mount, then issued the direct files GET carrying the session
        // header — a REST call against /files/{mount_id}, never the old POST /mcp endpoint.
        var filesRequest = handler
            .Requests.Should()
            .ContainSingle(r =>
                r.Method == HttpMethod.Get
                && r.RequestUri!.AbsolutePath.Contains($"/files/{MountId}", StringComparison.Ordinal)
            )
            .Which;
        filesRequest.RequestUri!.Query.Should().Contain("path=CLAUDE.md");
        filesRequest.Headers.GetValues("X-Session-ID").Should().ContainSingle().Which.Should().Be(SessionId);
        handler.Requests.Should().NotContain(r => r.RequestUri!.AbsolutePath.EndsWith("/mcp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReadWorkspaceFile_FileMissing_ReturnsNull()
    {
        // A missing file surfaces as the direct files API's 404 `path_not_found`, which the SDK maps to
        // SandboxErrorKind.NotFound and the registry degrades to the best-effort null contract.
        var (registry, _) = CreateRegistry(req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal)
                ? Error(HttpStatusCode.NotFound, "path_not_found")
                : MountResolution());
        await using var _ = registry;

        var content = await registry.ReadWorkspaceFileAsync(SessionId, "/workspace/missing.md");

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadWorkspaceFile_GatewayError_ReturnsNull()
    {
        // The gateway rejects the read (e.g. an evicted session or an internal error) — the SDK surfaces
        // a SandboxException which the registry maps to the best-effort null contract, never a throw.
        var (registry, _) = CreateRegistry(req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : MountResolution());
        await using var _ = registry;

        var content = await registry.ReadWorkspaceFileAsync("sess-gone", "/workspace/CLAUDE.md");

        content.Should().BeNull();
    }

    /// <summary>Answers the SDK's mount-resolution GET with a workspace volume carrying <see cref="MountId"/>.</summary>
    private static HttpResponseMessage MountResolution() =>
        Json(
            """{"session_id":"sess-1","volumes":{"workspace":{"container_path":"/workspace","read_only":false,"id":7}}}"""
        );

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>Answers with the gateway's stable direct-API error body carrying <paramref name="errorCode"/>.</summary>
    private static HttpResponseMessage Error(HttpStatusCode status, string errorCode) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { error = errorCode, code = (int)status, error_code = errorCode, retryable = false }),
                Encoding.UTF8,
                "application/json"
            ),
        };

    private static (SandboxSessionRegistry Registry, RecordingHandler Handler) CreateRegistry(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    )
    {
        var handler = new RecordingHandler(respond);
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };

        // The gateway lifetime client only serves the /health adopt probe; the registry's own client
        // (below) is the one whose REST requests these tests answer and assert on.
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

    /// <summary>Delegates the response to a lambda and records every request the registry sends through it.</summary>
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
