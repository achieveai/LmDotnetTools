using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.Sandbox;

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
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official, claude_plugins" };
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
            new WorkspaceRef("ws-1", DirectoryRelPath: null, Marketplaces: ["ClaudePlugins", "superpowers"])
        );

        ReadMarketplaces(capture.Body).Should().Equal("ClaudePlugins", "superpowers");
    }

    [Fact]
    public async Task Workspace_with_no_marketplaces_falls_back_to_global_config()
    {
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official" };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-2", DirectoryRelPath: null, Marketplaces: []));

        ReadMarketplaces(capture.Body).Should().Equal("official");
    }

    [Fact]
    public async Task CreateSessionAsync_ExplicitPluginSelectionOnWorkspaceRef_IsSentOnWireRequest()
    {
        // Narrowing to a subset of plugins is per-workspace state, so it has to reach the gateway on
        // the create request — asserted at the wire level, as structured {marketplace, plugin} pairs
        // rather than a flat "plugin@marketplace" string (neither name is validated against '@').
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official" };
        var (registry, capture) = CreateRegistry(options);

        _ = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("official", "code-review")])
        );

        using var doc = JsonDocument.Parse(capture.Body!);
        var selection = doc.RootElement.GetProperty("pluginSelection");
        selection.GetArrayLength().Should().Be(1);
        selection[0].GetProperty("marketplace").GetString().Should().Be("official");
        selection[0].GetProperty("plugin").GetString().Should().Be("code-review");
    }

    [Fact]
    public async Task CreateSessionAsync_PluginSelectionIsTriState_NullOmitsFieldAndEmptySendsEmptyArray()
    {
        // The single most dangerous defect on this path is collapsing null → []. They mean opposite
        // things: absent ⇒ "gateway loads all plugins" (legacy), [] ⇒ "load none". A `?? []` anywhere
        // between WorkspaceRef and the wire silently disables every plugin for every legacy workspace.
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };

        var (unselected, unselectedCapture) = CreateRegistry(options);
        _ = await unselected.GetOrCreateSessionAsync(new WorkspaceRef("ws-legacy", PluginSelection: null));
        using (var doc = JsonDocument.Parse(unselectedCapture.Body!))
        {
            doc.RootElement.TryGetProperty("pluginSelection", out _).Should().BeFalse();
        }

        var (none, noneCapture) = CreateRegistry(options);
        _ = await none.GetOrCreateSessionAsync(new WorkspaceRef("ws-none", PluginSelection: []));
        using (var doc = JsonDocument.Parse(noneCapture.Body!))
        {
            doc.RootElement.TryGetProperty("pluginSelection", out var explicitlyNone).Should().BeTrue();
            explicitlyNone.GetArrayLength().Should().Be(0);
        }
    }

    [Fact]
    public async Task CreateSessionAsync_ResponsePluginResolution_IsStoredOnSandboxSession()
    {
        // What the gateway actually resolved is the only trustworthy answer (it may drop unknown ids
        // or report that it cannot filter at all), so the session must carry the gateway's block
        // rather than an echo of what was requested.
        const string createResponse = """
            { "session_id": "sess-1", "container_id": "c-1",
              "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } },
              "pluginResolution": {
                "supported": true,
                "requested": [ { "marketplace": "official", "plugin": "code-review" } ],
                "effective": [ { "marketplace": "official", "plugin": "code-review" } ],
                "failed": [] } }
            """;
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var (registry, _) = CreateRegistry(options, createResponse);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        session.PluginResolution.Should().NotBeNull();
        session.PluginResolution!.Supported.Should().BeTrue();
        session.PluginResolution.Effective.Should().ContainSingle().Which.Plugin.Should().Be("code-review");
    }

    [Fact]
    public async Task CreateSessionAsync_NoPluginResolutionInResponse_LeavesSessionResolutionNull()
    {
        // Non-vacuity guard for the test above: a gateway that reported nothing must leave this null,
        // not a fabricated `supported: false`. "Never reported one" is a strictly weaker claim than
        // "said it cannot filter", and the app's fail-closed gate must be able to tell them apart.
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var (registry, _) = CreateRegistry(options);

        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        session.PluginResolution.Should().BeNull();
    }

    [Fact]
    public async Task FirstCreate_SendsPersistedPluginSelection_FromTheAppComposedWorkspaceRef()
    {
        // THE regression. Every other plugin-selection assertion in this file hand-builds the
        // WorkspaceRef inside the test, which proves the registry forwards a selection it was given
        // but says nothing about whether the app ever gives it one. It did not: the app composed the
        // ref for a brand-new conversation in one place and for a post-404 recreate in another, and
        // only the recreate copy mapped PluginSelection. So a fresh conversation on a workspace with
        // an explicit selection silently got the gateway's legacy "load every plugin" behaviour until
        // its session happened to die and be rebuilt. Driving the FIRST create through the app's own
        // composition helper is what closes that gap.
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = "official" };
        var (registry, capture) = CreateRegistry(options);
        var workspace = new Workspace
        {
            Id = "ws-1",
            Name = "Proj",
            DirectoryRelPath = "projA",
            Marketplaces = ["superpowers"],
            PluginSelection = [new PluginRef("superpowers", "code-review")],
        };

        // No eviction, no GetOrCreateLiveSessionAsync — this is the very first create for the session.
        _ = await registry.GetOrCreateSessionAsync(global::Program.BuildWorkspaceRef(workspace.Id, workspace));

        using var doc = JsonDocument.Parse(capture.Body!);
        var selection = doc.RootElement.GetProperty("pluginSelection");
        selection.GetArrayLength().Should().Be(1);
        selection[0].GetProperty("marketplace").GetString().Should().Be("superpowers");
        selection[0].GetProperty("plugin").GetString().Should().Be("code-review");
        // The other two persisted fields ride the same helper, so pin them here too — a helper that
        // dropped either one would otherwise regress silently behind a green plugin-selection check.
        ReadMarketplaces(capture.Body).Should().Equal("superpowers");
        doc.RootElement.GetProperty("workspace").GetString().Should().Be("projA");
    }

    [Fact]
    public async Task FirstCreate_LegacyWorkspaceWithNoSelection_OmitsPluginSelection()
    {
        // Non-vacuity guard for the test above: the app helper must preserve the tri-state, not just
        // "send something". A workspace that never used the picker has to keep reaching the gateway
        // with the field ABSENT — mapping its null to an empty list would read as "load no plugins"
        // and strip every tool from every legacy workspace on its next fresh conversation.
        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl };
        var (registry, capture) = CreateRegistry(options);
        var workspace = new Workspace
        {
            Id = "ws-legacy",
            Name = "Legacy",
            DirectoryRelPath = "legacy",
            PluginSelection = null,
        };

        _ = await registry.GetOrCreateSessionAsync(global::Program.BuildWorkspaceRef(workspace.Id, workspace));

        using var doc = JsonDocument.Parse(capture.Body!);
        doc.RootElement.TryGetProperty("pluginSelection", out _).Should().BeFalse();
    }

    [Fact]
    public void BuildWorkspaceRef_NoStoredWorkspace_YieldsBareRef()
    {
        // The first-create path resolves an id that may have no stored workspace at all (the implicit
        // "default"). That must stay a bare ref — every optional field left unset — so the gateway
        // applies its own defaults exactly as it did before this helper existed.
        var reference = global::Program.BuildWorkspaceRef("default", workspace: null);

        reference.Id.Should().Be("default");
        reference.DirectoryRelPath.Should().BeNull();
        reference.Marketplaces.Should().BeNull();
        reference.PluginSelection.Should().BeNull();
    }

    private static IReadOnlyList<string> ReadMarketplaces(string? body)
    {
        using var doc = JsonDocument.Parse(body!);
        return [.. doc.RootElement.GetProperty("marketplaces").EnumerateArray().Select(e => e.GetString()!)];
    }

    private static (SandboxSessionRegistry Registry, BodyCapture Capture) CreateRegistry(
        SandboxGatewayOptions options,
        string? createResponseOverride = null
    )
    {
        const string defaultCreateResponse = """
            { "session_id": "sess-1", "container_id": "c-1",
              "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } } }
            """;
        var createResponse = createResponseOverride ?? defaultCreateResponse;

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
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))
        );

        var auth = new AuthOptions();
        var registry = new SandboxSessionRegistry(
            gateway,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(registryHandler),
            auth,
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmstreaming-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );

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
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }
}
