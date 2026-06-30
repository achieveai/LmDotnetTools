using System.Net;
using System.Text;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the per-session marketplace selection added to the sandbox-create request: the configured
/// <see cref="SandboxGatewayOptions.Marketplaces"/> alias list is parsed (comma-separated, trimmed,
/// empties dropped) and sent as the gateway's <c>marketplaces</c> JSON array. When unset the field
/// is OMITTED entirely so the gateway keeps its default-set behaviour (DEFAULT_MARKETPLACES ⇒ all).
/// Asserted at the wire level by capturing the actual POST body the registry serialises.
/// </summary>
public class SandboxSessionRegistryMarketplacesTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    [Fact]
    public async Task Configured_marketplaces_are_sent_as_json_array_on_create()
    {
        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            Marketplaces = "official, claude_plugins",
        };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync();

        var marketplaces = ReadMarketplaces(capture.Body);
        marketplaces.Should().Equal("official", "claude_plugins");
    }

    [Fact]
    public async Task Marketplaces_field_omitted_when_not_configured()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = null };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync();

        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.TryGetProperty("marketplaces", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Whitespace_and_empty_entries_are_trimmed_and_dropped()
    {
        var options = new SandboxGatewayOptions
        {
            BaseUrl = GatewayBaseUrl,
            Marketplaces = "  official ,, ,  custom  ",
        };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync();

        ReadMarketplaces(capture.Body).Should().Equal("official", "custom");
    }

    [Fact]
    public async Task All_whitespace_value_omits_the_field()
    {
        // A placeholder/blank config value must behave exactly like "unset" — never an empty array,
        // which the gateway would treat as "select zero marketplaces" rather than "use the default".
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "  ,  , " };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync();

        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.TryGetProperty("marketplaces", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Workspace_marketplaces_override_the_global_config()
    {
        // Per-workspace selection is the whole point of the picker: a workspace that enables
        // specific marketplaces must send those, regardless of the global default.
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official" };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["ClaudePlugins", "superpowers"]));

        ReadMarketplaces(capture.Body).Should().Equal("ClaudePlugins", "superpowers");
    }

    [Fact]
    public async Task Workspace_with_no_marketplaces_falls_back_to_global_config()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official" };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-2", DirectoryRelPath: null, Marketplaces: []));

        ReadMarketplaces(capture.Body).Should().Equal("official");
    }

    private static IReadOnlyList<string> ReadMarketplaces(string? body)
    {
        using var doc = JsonDocument.Parse(body!);
        return [.. doc.RootElement.GetProperty("marketplaces")
            .EnumerateArray()
            .Select(e => e.GetString()!)];
    }

    private static (SandboxSessionRegistry Registry, BodyCapture Capture) CreateRegistry(SandboxGatewayOptions options)
    {
        const string createResponse = """
            { "session_id": "sess-1", "container_id": "c-1",
              "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
            """;

        var capture = new BodyCapture();
        var registryHandler = new StubHandler(req =>
        {
            // Capture the create POST body synchronously so the assertion sees exactly what was
            // serialised through the registry's JsonOptions.
            capture.Body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(createResponse, Encoding.UTF8, "application/json"),
            };
        });

        // The gateway lifetime client only ever serves the /health probe in this test; 200 ⇒ the
        // registry adopts an "existing" gateway and proceeds straight to the create POST.
        var gateway = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            auth,
            new AuthSharedSecret(auth));

        return (registry, capture);
    }

    private sealed class BodyCapture
    {
        public string? Body { get; set; }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
