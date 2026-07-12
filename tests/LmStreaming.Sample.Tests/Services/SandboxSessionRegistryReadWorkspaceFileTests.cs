using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the gateway-read seam used to inject workspace context files (CLAUDE.md / AGENTS.md) into
/// the system prompt at boot. The backend cannot read the container's <c>/workspace</c> filesystem,
/// so it fetches content through the gateway via the typed Sandbox SDK's verified transfer protocol
/// (an MCP <c>Bash</c> STAT probe + a base64 READ chunk, both scoped by the <c>X-Session-ID</c>
/// header). These tests drive that protocol end-to-end: they assert the raw file bytes are returned
/// (the SDK returns exact content — no <c>cat -n</c> stripping), that a missing file yields the
/// best-effort <c>null</c> contract, and that a gateway/tool error also yields <c>null</c>.
/// </summary>
public sealed class SandboxSessionRegistryReadWorkspaceFileTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string XferMarker = "@@LMSBX-XFER@@";

    [Fact]
    public async Task ReadWorkspaceFile_TransfersFileContent_ReturnsRawBytes()
    {
        // Raw file content — note it deliberately contains no line-number prefixes: the SDK's
        // transfer protocol returns the file's exact bytes, unlike the old MCP Read `cat -n` path.
        const string fileText = "# Hello\nWorld\n";
        var fileBytes = Encoding.UTF8.GetBytes(fileText);

        var sessionIds = new List<string>();
        var mcpUris = new List<string>();

        HttpResponseMessage Respond(HttpRequestMessage req)
        {
            mcpUris.Add(req.RequestUri!.AbsoluteUri);
            if (req.Headers.TryGetValues("X-Session-ID", out var ids))
            {
                sessionIds.AddRange(ids);
            }

            var script = ReadScript(req);
            // STAT probe → META with size/mtime/sha256; READ chunk → CHUNK with size/mtime/base64.
            var text = IsStat(script)
                ? $"{XferMarker} META {fileBytes.Length} 1 {Sha256Hex(fileBytes)}"
                : $"{XferMarker} CHUNK {fileBytes.Length} 1 {Convert.ToBase64String(fileBytes)}";
            return Mcp(text, isError: false);
        }

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-1", "/workspace/CLAUDE.md");

        content.Should().Be(fileText);

        // The transfer is a STAT then a READ — both POSTed to {gateway}/mcp with the session header.
        mcpUris.Should().OnlyContain(u => u == $"{GatewayBaseUrl}/mcp");
        mcpUris.Should().HaveCount(2);
        sessionIds.Should().OnlyContain(id => id == "sess-1");
    }

    [Fact]
    public async Task ReadWorkspaceFile_FileMissing_ReturnsNull()
    {
        // A missing file surfaces as the transfer protocol's NOTFOUND sentinel on the STAT probe.
        static HttpResponseMessage Respond(HttpRequestMessage _) => Mcp($"{XferMarker} NOTFOUND", isError: false);

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-1", "/workspace/missing.md");

        content.Should().BeNull();
    }

    [Fact]
    public async Task ReadWorkspaceFile_GatewayToolError_ReturnsNull()
    {
        // The Bash tool reports an error (e.g. evicted session) — the SDK surfaces a Protocol failure
        // which the registry maps to the best-effort null contract, never a throw.
        static HttpResponseMessage Respond(HttpRequestMessage _) => Mcp("permission denied", isError: true);

        await using var registry = CreateRegistry(Respond);

        var content = await registry.ReadWorkspaceFileAsync("sess-gone", "/workspace/CLAUDE.md");

        content.Should().BeNull();
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string ReadScript(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("params")
            .GetProperty("arguments")
            .GetProperty("command")
            .GetString() ?? string.Empty;
    }

    private static bool IsStat(string script) => script.Contains("XFER STAT", StringComparison.Ordinal);

    private static HttpResponseMessage Mcp(string text, bool isError)
    {
        var envelope = new
        {
            jsonrpc = "2.0",
            id = 1,
            result = new
            {
                content = new[] { new { type = "text", text } },
                isError,
            },
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json"),
        };
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
            new AuthSharedSecret(new AuthOptions()));
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
