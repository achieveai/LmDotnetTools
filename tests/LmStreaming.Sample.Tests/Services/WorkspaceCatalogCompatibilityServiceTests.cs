using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public sealed class WorkspaceCatalogCompatibilityServiceTests
{
    [Fact]
    public async Task EvaluateAsync_ReportsSupportedAndUnsupportedInStoredOrder()
    {
        var client = new StubCatalogClient(Catalog("one", "two"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options());
        var workspace = Workspace(["three", "one", "three", "four"]);

        var result = await service.EvaluateAsync(workspace);

        result.Compatibility.Should().Be(WorkspaceCompatibility.Incompatible);
        result.UnsupportedMarketplaces.Should().Equal("three", "four");
        result.AvailableMarketplaces.Should().Equal("one", "two");
    }

    [Fact]
    public async Task EvaluateAsync_EmptyAndSupportedSelectionsAreCompatible()
    {
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(Catalog("one", "two")),
            Options()
        );

        (await service.EvaluateAsync(Workspace([]))).Compatibility
            .Should().Be(WorkspaceCompatibility.Compatible);
        (await service.EvaluateAsync(Workspace(["two"]))).Compatibility
            .Should().Be(WorkspaceCompatibility.Compatible);
    }

    [Fact]
    public async Task EvaluateAsync_UnavailableGatewayIsUnknown()
    {
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline")),
            Options()
        );

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Unknown);
        result.Error.Should().Contain("offline");
    }

    [Fact]
    public async Task ConcurrentCallsUseSingleFlightAndCache()
    {
        var client = new StubCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options());

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => service.EvaluateAsync(Workspace([]))));
        _ = await service.EvaluateAsync(Workspace([]));

        client.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task ValidateForMutationThrowsTypedErrors()
    {
        var unsupported = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(Catalog("one")),
            Options()
        );
        var unavailable = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline")),
            Options()
        );

        await FluentActions.Invoking(() => unsupported.ValidateForMutationAsync(["two"]))
            .Should().ThrowAsync<UnsupportedWorkspaceMarketplacesException>();
        await FluentActions.Invoking(() => unavailable.ValidateForMutationAsync(["one"]))
            .Should().ThrowAsync<WorkspaceGatewayCatalogUnavailableException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_NullSelection_IsAlwaysValid_NoCatalogCallNeeded()
    {
        var (service, stub) = CreateServiceWithStub(catalogAvailable: false);

        var act = async () => await service.ValidatePluginsForMutationAsync(["official"], null);

        await act.Should().NotThrowAsync();

        // "Did not throw" alone is vacuous as a no-call proof: an offline catalog also happens not to
        // throw on this path. Pinning CallCount is what actually proves the null short-circuit runs
        // BEFORE the catalog is ever consulted.
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_GatewayDoesNotSupportPluginFiltering_ThrowsGatewayPluginFilteringUnsupported()
    {
        var service = CreateService(pluginFilteringSupported: false);

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official"],
            [new PluginRef("official", "code-review")]
        );

        await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_GatewayCapabilityUnknown_ThrowsGatewayPluginFilteringUnsupported()
    {
        // null capability is treated the same as false: fail closed.
        var service = CreateService(pluginFilteringSupported: null);

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official"],
            [new PluginRef("official", "code-review")]
        );

        await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_UnknownPlugin_ThrowsUnsupportedWorkspacePlugins()
    {
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review")]
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official"],
            [new PluginRef("official", "unknown-plugin")]
        );

        await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_KnownPluginsAndSupportedGateway_Succeeds()
    {
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review")]
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official"],
            [new PluginRef("official", "code-review")]
        );

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_ExplicitEmptySelection_Succeeds_WhenGatewaySupports()
    {
        var service = CreateService(pluginFilteringSupported: true);

        var act = async () => await service.ValidatePluginsForMutationAsync(["official"], []);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_PluginFromMarketplaceNotEnabledOnWorkspace_Throws()
    {
        // The catalog is fetched UNFILTERED, so "beta/deploy" is a globally known plugin identity.
        // The workspace enables only "official", so selecting it must still be rejected — being known
        // to the gateway is not the same as being reachable from this workspace.
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review"), new PluginRef("beta", "deploy")]
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official"],
            [new PluginRef("beta", "deploy")]
        );

        var thrown = await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
        thrown.Which.UnsupportedPlugins.Should().Equal(new PluginRef("beta", "deploy"));

        // The reported set must be narrowed to the enabled marketplaces too, otherwise the payload
        // contradicts itself by offering back the very plugin it just rejected.
        thrown.Which.AvailablePlugins.Should().Equal(new PluginRef("official", "code-review"));
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_PluginUnderAnEnabledMarketplace_StillSucceeds_WhenOtherMarketplacesExist()
    {
        // Non-vacuity guard for the test above: the narrowing must reject only the non-enabled
        // marketplace, not every selection made while a second marketplace happens to exist.
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review"), new PluginRef("beta", "deploy")]
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            ["official", "beta"],
            [new PluginRef("beta", "deploy")]
        );

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_EmptyWorkspaceMarketplaces_FallsBackToConfiguredDefault()
    {
        // An empty workspace list means "no preference", and SandboxSessionRegistry resolves it to the
        // configured default before creating the session. Reading it as "enables nothing" narrowed the
        // selectable set to empty and rejected every plugin — including ones the session would load.
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review")],
            configuredMarketplaces: "official"
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            [],
            [new PluginRef("official", "code-review")]
        );

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_EmptyWorkspaceMarketplaces_StillRejectsOutsideConfiguredDefault()
    {
        // Non-vacuity guard for the test above: the fallback must SUBSTITUTE the configured default,
        // not abandon narrowing altogether. "beta" is a known catalog identity but sits outside the
        // effective set, so the session would not load it and the selection must be refused.
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review"), new PluginRef("beta", "deploy")],
            configuredMarketplaces: "official"
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            [],
            [new PluginRef("beta", "deploy")]
        );

        var thrown = await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
        thrown.Which.UnsupportedPlugins.Should().Equal(new PluginRef("beta", "deploy"));
        thrown.Which.AvailablePlugins.Should().Equal(new PluginRef("official", "code-review"));
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_NoWorkspaceOrConfiguredMarketplaces_AcceptsAnyCatalogPlugin()
    {
        // Neither side names a marketplace, so the gateway applies its own default set to the session.
        // The catalog is fetched unfiltered and already IS that set, so no narrowing applies —
        // distinguishing "resolved to nothing" (accept the catalog) from "resolved to empty" (accept
        // nothing), which is precisely the confusion this fallback exists to remove.
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review"), new PluginRef("beta", "deploy")],
            configuredMarketplaces: null
        );

        var act = async () => await service.ValidatePluginsForMutationAsync(
            [],
            [new PluginRef("beta", "deploy")]
        );

        await act.Should().NotThrowAsync();
    }

    private static Workspace Workspace(IReadOnlyList<string> aliases) => new()
    {
        Id = "id",
        Name = "name",
        DirectoryRelPath = "leaf",
        Marketplaces = aliases,
    };

    /// <summary>
    /// The gateway options the service reads the configured DEFAULT marketplace list from — the set an
    /// empty per-workspace selection falls back to. Blank by default so existing tests keep their
    /// original "no ambient configuration" meaning.
    /// </summary>
    private static SandboxGatewayOptions Options(string? configuredMarketplaces = null) =>
        new() { Marketplaces = configuredMarketplaces };

    /// <summary>
    /// Builds a service over a stub catalog. <paramref name="catalogAvailable"/> false makes the stub
    /// report the gateway as offline, so a test that still succeeds proves the code path never needed
    /// the catalog. Marketplace aliases are derived from <paramref name="availablePlugins"/> so the
    /// stubbed catalog is self-consistent (a plugin always sits under an alias that exists).
    /// </summary>
    private static WorkspaceCatalogCompatibilityService CreateService(
        bool catalogAvailable = true,
        bool? pluginFilteringSupported = true,
        IReadOnlyList<PluginRef>? availablePlugins = null,
        string? configuredMarketplaces = null) =>
        CreateServiceWithStub(
            catalogAvailable,
            pluginFilteringSupported,
            availablePlugins,
            configuredMarketplaces
        ).Service;

    /// <summary>
    /// As <see cref="CreateService"/>, but also hands back the stub so a test can assert on
    /// <see cref="StubCatalogClient.CallCount"/> — i.e. prove a code path never reached the catalog.
    /// </summary>
    private static (WorkspaceCatalogCompatibilityService Service, StubCatalogClient Stub) CreateServiceWithStub(
        bool catalogAvailable = true,
        bool? pluginFilteringSupported = true,
        IReadOnlyList<PluginRef>? availablePlugins = null,
        string? configuredMarketplaces = null)
    {
        var options = Options(configuredMarketplaces);
        if (!catalogAvailable)
        {
            var offline = new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline"));
            return (new WorkspaceCatalogCompatibilityService(offline, options), offline);
        }

        var plugins = availablePlugins ?? [];
        string[] aliases = plugins.Count > 0
            ? [.. plugins.Select(p => p.Marketplace).Distinct(StringComparer.Ordinal)]
            : ["official"];

        var stub = new StubCatalogClient(Catalog(aliases, plugins, pluginFilteringSupported));
        return (new WorkspaceCatalogCompatibilityService(stub, options), stub);
    }

    private static MarketplaceCatalog Catalog(params string[] aliases) =>
        Catalog(aliases, [], pluginFiltering: null);

    private static MarketplaceCatalog Catalog(
        IReadOnlyList<string> aliases,
        IReadOnlyList<PluginRef> plugins,
        bool? pluginFiltering)
    {
        var marketplaces = aliases.Select(alias => new CatalogMarketplace(
            alias,
            null,
            [
                .. plugins
                    .Where(p => string.Equals(p.Marketplace, alias, StringComparison.Ordinal))
                    .Select(p => new CatalogPlugin(p.Plugin, null, string.Empty, [], [])),
            ]
        ));

        return new MarketplaceCatalog(aliases, [.. marketplaces])
        {
            Capabilities = new MarketplaceCapabilities(pluginFiltering),
        };
    }

    private sealed class StubCatalogClient : IMarketplaceCatalogClient
    {
        private readonly MarketplaceCatalog? _catalog;
        private readonly Exception? _error;
        private int _calls;

        public StubCatalogClient(MarketplaceCatalog catalog) => _catalog = catalog;
        public StubCatalogClient(Exception error) => _error = error;
        public int CallCount => _calls;

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return _error is not null
                ? Task.FromException<MarketplaceCatalog>(_error)
                : Task.FromResult(_catalog!);
        }
    }
}
