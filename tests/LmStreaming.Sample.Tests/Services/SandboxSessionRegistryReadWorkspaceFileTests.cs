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

    [Fact]
    public async Task ReadWorkspaceFile_ChunkedOverCapBody_IsRejectedWhileStreaming_ReturnsNull()
    {
        // The 64 MiB SDK read cap (SandboxClient.MaxDirectReadBytes; internal, mirrored here).
        const long cap = 64L * 1024 * 1024;
        var content = new CountingChunkedContent(cap + (8L * 1024 * 1024)); // 8 MiB over the cap, no Content-Length

        var (registry, _) = CreateRegistry(req =>
            req.Method == HttpMethod.Get && req.RequestUri!.AbsolutePath.Contains("/files/", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = content }
                : MountResolution());
        await using var _ = registry;

        var result = await registry.ReadWorkspaceFileAsync(SessionId, "/workspace/big.bin");

        // Best-effort: the over-cap body surfaces as a SandboxException the registry swallows to null.
        result.Should().BeNull();
        // Proves the borrowed-transport forwarding handler STREAMS (ResponseHeadersRead) rather than fully
        // buffering the body: the SDK stopped reading just past the cap — it did NOT drain the whole
        // 8-MiB-over body (which a buffering ResponseContentRead forward would have done).
        content.BytesRead.Should().BeLessThan(cap + (4L * 1024 * 1024));
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

    /// <summary>
    /// An <see cref="HttpContent"/> that reports NO <c>Content-Length</c> (chunked) and whose read stream
    /// lazily yields a fixed number of zero bytes, counting how many were actually read — so a test can
    /// prove the borrowed transport streamed (stopped at the cap) rather than fully buffering the body.
    /// </summary>
    private sealed class CountingChunkedContent(long length) : HttpContent
    {
        private long _bytesRead;

        public long BytesRead => Interlocked.Read(ref _bytesRead);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            CreateStream().CopyToAsync(stream);

        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(CreateStream());

        protected override bool TryComputeLength(out long length2)
        {
            length2 = 0;
            return false;
        }

        private Stream CreateStream() => new CountingZeroStream(length, n => Interlocked.Add(ref _bytesRead, n));
    }

    /// <summary>A read-only forward stream that yields <c>length</c> zero bytes (never allocating them) and reports each read.</summary>
    private sealed class CountingZeroStream(long length, Action<int> onRead) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var produced = (int)Math.Min(count, _remaining);
            Array.Clear(buffer, offset, produced);
            _remaining -= produced;
            onRead(produced);
            return produced;
        }

        public override void Flush() { }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
