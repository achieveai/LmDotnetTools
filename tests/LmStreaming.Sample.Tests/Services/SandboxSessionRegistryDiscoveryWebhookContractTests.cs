using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Services.Auth;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the OUTBOUND half of the context-discovery contract: the per-session secret the backend
/// hands the gateway in the sandbox-create request must ride under the JSON field the gateway
/// actually reads — <c>discovery.webhook.auth_header</c> — and be sent verbatim. The gateway
/// (SandboxedOstoolsMcpServer <c>WebhookConfig</c>) deserializes <c>auth_header</c> with
/// <c>#[serde(default)]</c>; if the backend sends the wrong field name the gateway parses
/// <c>None</c>, sends its callbacks with NO <c>Authorization</c> header, and every context-discovery
/// webhook is rejected 401 — the exact failure that stopped CLAUDE.md/AGENTS.md from loading.
/// </summary>
public sealed class SandboxSessionRegistryDiscoveryWebhookContractTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string DefaultLeaf = "default-leaf";

    [Fact]
    public async Task CreateSession_SendsDiscoveryWebhookSecret_UnderAuthHeaderField_NotAuth()
    {
        const string secret = "gw-shared-secret-2f9c";
        using var baseDir = new TempWorkspaceBase();
        string? capturedBody = null;

        HttpResponseMessage Respond(HttpRequestMessage req)
        {
            if (req.Method == HttpMethod.Post
                && req.RequestUri!.AbsolutePath.EndsWith("/api/v1/sandboxes", StringComparison.Ordinal))
            {
                capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"session_id\":\"sess-contract\",\"volumes\":{\"workspace\":{\"container_path\":\"/workspace\",\"read_only\":false}}}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            // Health probe (and anything else) → healthy.
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            WorkspaceBasePath = baseDir.Path,
            Workspace = DefaultLeaf,
        };
        var authOptions = new AuthOptions
        {
            Webhook = new WebhookOptions
            {
                PublicBaseUrl = "http://127.0.0.1:5000",
                GatewaySharedSecret = secret,
            },
        };

        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Respond)));

        await using var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Respond)),
            authOptions,
            new AuthSharedSecret(authOptions));

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projA"));

        capturedBody.Should().NotBeNull("the registry must POST a create request to the gateway");
        using var doc = JsonDocument.Parse(capturedBody!);
        var webhook = doc.RootElement.GetProperty("discovery").GetProperty("webhook");

        webhook.GetProperty("url").GetString()
            .Should().EndWith("/api/discovery/context_discovery");
        webhook.GetProperty("auth_header").GetString()
            .Should().Be(secret, "the gateway reads the secret from `auth_header` and sends it verbatim as Authorization");
        webhook.TryGetProperty("auth", out _)
            .Should().BeFalse("the legacy `auth` field name is ignored by the gateway → it would send no Authorization header → 401");
    }

    private sealed class TempWorkspaceBase : IDisposable
    {
        public string Path { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ws-contract-" + Guid.NewGuid().ToString("N"));

        public TempWorkspaceBase() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must not fail the test.
            }
        }
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
