using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the gateway-read seam used to inject workspace context files (CLAUDE.md / AGENTS.md) into
/// the system prompt at boot. The backend cannot read the container's <c>/workspace</c> filesystem,
/// so it fetches content through the gateway's MCP <c>Read</c> tool (<c>POST {gateway}/mcp</c>,
/// <c>tools/call</c>, scoped by the <c>X-Session-ID</c> header). These tests pin the request shape,
/// the <c>cat -n</c> line-number stripping, and the best-effort null contract on errors.
/// </summary>
public sealed class SandboxSessionRegistryReadWorkspaceFileTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task ReadWorkspaceFile_CallsGatewayMcpRead_AndStripsLineNumbers()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;

        HttpResponseMessage Respond(HttpRequestMessage req)
        {
            captured = req;
            capturedBody = req.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            // Mock the gateway's MCP Read result — content is cat -n line-number prefixed.
            const string mcp = """
                {"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"     1\t# Hello\n     2\tWorld\n     3\t"}],"isError":false}}
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(mcp, Encoding.UTF8, "application/json"),
            };
        }

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-1", "/workspace/CLAUDE.md");

        // Line-number prefixes stripped; the empty trailing numbered line becomes a blank line.
        content.Should().Be("# Hello\nWorld\n");

        // Request shape: POST {gateway}/mcp with the session header and a tools/call Read body.
        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsoluteUri.Should().Be($"{GatewayBaseUrl}/mcp");
        captured.Headers.GetValues("X-Session-ID").Should().ContainSingle().Which.Should().Be("sess-1");

        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var root = doc.RootElement;
        root.GetProperty("method").GetString().Should().Be("tools/call");
        var p = root.GetProperty("params");
        p.GetProperty("name").GetString().Should().Be("Read");
        p.GetProperty("arguments").GetProperty("file_path").GetString().Should().Be("/workspace/CLAUDE.md");
    }

    [Fact]
    public async Task ReadWorkspaceFile_ToolReportsIsError_ReturnsNull()
    {
        // A missing file surfaces as an MCP result with isError=true (not a transport failure).
        static HttpResponseMessage Respond(HttpRequestMessage _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"File not found"}],"isError":true}}""",
                Encoding.UTF8,
                "application/json"),
        };

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-1", "/workspace/missing.md");

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadWorkspaceFile_JsonRpcError_ReturnsNull()
    {
        // The gateway returns a JSON-RPC error (e.g. evicted session) — best-effort null, never throw.
        static HttpResponseMessage Respond(HttpRequestMessage _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"Session not found"}}""",
                Encoding.UTF8,
                "application/json"),
        };

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-gone", "/workspace/CLAUDE.md");

        content.Should().BeNull();
    }

    private static SandboxSessionRegistry CreateRegistry(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        static HttpResponseMessage Healthy(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Healthy)));

        return new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(respond)),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(respond(request));
        }
    }
}
