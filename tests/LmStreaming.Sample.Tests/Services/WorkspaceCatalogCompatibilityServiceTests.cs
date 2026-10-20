using LmStreaming.Sample.Services;
using Microsoft.Extensions.Time.Testing;

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
        var service = new WorkspaceCatalogCompatibilityService(new StubCatalogClient(Catalog("one", "two")), Options());

        (await service.EvaluateAsync(Workspace([]))).Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
        (await service.EvaluateAsync(Workspace(["two"]))).Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
    }

    /// <summary>
    /// The split this type exists for (#459): an unreadable catalog reports
    /// <see cref="WorkspaceCompatibility.Unavailable"/> — "could not check" — and specifically NOT
    /// <see cref="WorkspaceCompatibility.Incompatible"/>, which is the checked refusal that callers
    /// are entitled to act on by withholding the workspace.
    /// </summary>
    /// <remarks>
    /// The <c>NotBe(Incompatible)</c> assertion is not redundant with the <c>Be(Unavailable)</c> one
    /// for a reader: it is the sentence that says WHY the value matters, and it is what fails loudly
    /// if a later change decides an unreachable gateway should simply refuse everything.
    /// </remarks>
    [Fact]
    public async Task EvaluateAsync_UnreadableCatalogIsUnavailableNotIncompatible()
    {
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline")),
            Options()
        );

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        result.Compatibility.Should().NotBe(WorkspaceCompatibility.Incompatible);
        result.Error.Should().Contain("offline");

        // Nothing was compared, so nothing may be reported as having failed. An `Unavailable` result
        // that also listed unsupported aliases would hand a caller the very evidence it would use to
        // treat this as a refusal.
        result.UnsupportedMarketplaces.Should().BeEmpty();
    }

    /// <summary>
    /// The other half of the split, on the same input shape: a catalog that CAN be read and does not
    /// offer the alias is <see cref="WorkspaceCompatibility.Incompatible"/>. Paired with the test
    /// above deliberately — each alone would pass under an implementation that collapsed both cases
    /// into whichever single value it asserted.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReadableCatalogMissingTheAliasIsIncompatibleNotUnavailable()
    {
        var service = new WorkspaceCatalogCompatibilityService(new StubCatalogClient(Catalog("other")), Options());

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Incompatible);
        result.Compatibility.Should().NotBe(WorkspaceCompatibility.Unavailable);
        result.UnsupportedMarketplaces.Should().Equal("one");
        result.Error.Should().BeNull();
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

    /// <summary>
    /// A cold gateway takes minutes to build its catalog (47-152 s seen), and a caller that hangs up
    /// cancels the build. So a caller may give up, but the fetch must keep running to completion and
    /// warm the catalog for the next caller. The picker gives up first; a session check waits longer.
    /// </summary>
    [Fact]
    public async Task SlowCatalogFetch_OutlivesEveryCallerWait_AndTheNextCallerFindsItWarm()
    {
        var time = new FakeTimeProvider();
        var client = new GatedCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), time);
        var bound = TimeSpan.FromSeconds(5);

        var picker = service.EvaluateAsync(Workspace(["one"]));
        var session = service.ValidateForSessionAsync(Workspace(["one"]));
        time.Advance(WorkspaceCatalogCompatibilityService.BrowseWait);

        var browse = await picker.WaitAsync(bound);
        browse.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        browse.Error.Should().Contain("still loading");
        session.IsCompleted.Should().BeFalse("a session check waits longer than the picker");

        time.Advance(WorkspaceCatalogCompatibilityService.CheckWait - WorkspaceCatalogCompatibilityService.BrowseWait);
        await FluentActions
            .Awaiting(() => session.WaitAsync(bound))
            .Should()
            .ThrowAsync<WorkspaceGatewayCatalogUnavailableException>()
            .WithMessage("*still loading*");

        client.Finish();
        await service.ValidateForSessionAsync(Workspace(["one"])).WaitAsync(bound);

        client.CallCount.Should().Be(1, "the timed-out callers left the one fetch running");
    }

    /// <summary>
    /// The gap this test closes: a fetch that outlives every caller's wait used to complete with
    /// nobody left to observe it, so it never reached `_cached` and the 30s freshness check at the top
    /// of <c>GetCatalogAsync</c> was never applied to it. The NEXT caller found `_cached` null, skipped
    /// straight to the (long-since-completed) shared refresh, and got that snapshot back unconditionally
    /// — even minutes later, well past <c>CacheDuration</c>.
    /// <para>
    /// Repro: caller A times out at t=10s (BrowseWait). The fetch completes at t=47s with nobody
    /// waiting. The clock is then advanced past CacheDuration (30s) from that completion. Caller B must
    /// NOT be silently handed the t=47s snapshot as if fresh — it must trigger a brand-new fetch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SlowCatalogFetch_CompletesUnobserved_AndStaleResultIsNotServedPastCacheDuration()
    {
        var time = new FakeTimeProvider();
        var client = new GatedCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), time);
        var bound = TimeSpan.FromSeconds(5);

        // Caller A: a lone browse that times out before the fetch finishes. Nobody is left awaiting
        // the shared refresh once this returns.
        var callerA = service.EvaluateAsync(Workspace(["one"]));
        time.Advance(WorkspaceCatalogCompatibilityService.BrowseWait);
        var browseResult = await callerA.WaitAsync(bound);
        browseResult.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);

        // The fetch completes later, with no caller waiting on it. Its completion continuation is
        // registered with RunContinuationsAsynchronously (the TCS) + ExecuteSynchronously (the
        // ContinueWith), so it fires on the thread pool essentially immediately; the short real-time
        // delay below just gives it room to land before the clock is advanced.
        client.Finish();
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Advance the clock well past CacheDuration (30s) from the fetch's completion.
        time.Advance(TimeSpan.FromSeconds(60));

        // Caller B must not receive the now-stale snapshot silently: it must trigger a fresh fetch
        // rather than reuse a completed-but-unconsumed one that is older than CacheDuration. Under the
        // old bug, `_refresh` stayed pinned to the first (already-completed) task forever, so this call
        // would rejoin it directly and `CallCount` would stay 1 no matter how stale the snapshot was.
        await service.ValidateForSessionAsync(Workspace(["one"])).WaitAsync(bound);

        client
            .CallCount.Should()
            .Be(2, "the first fetch completed unobserved and is now stale, so caller B must have started a second one");
    }

    [Fact]
    public async Task ValidateForMutationThrowsTypedErrors()
    {
        var unsupported = new WorkspaceCatalogCompatibilityService(new StubCatalogClient(Catalog("one")), Options());
        var unavailable = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline")),
            Options()
        );

        await FluentActions
            .Invoking(() => unsupported.ValidateForMutationAsync(["two"]))
            .Should()
            .ThrowAsync<UnsupportedWorkspaceMarketplacesException>();
        await FluentActions
            .Invoking(() => unavailable.ValidateForMutationAsync(["one"]))
            .Should()
            .ThrowAsync<WorkspaceGatewayCatalogUnavailableException>();
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
    public async Task ValidatePluginsForMutationAsync_NullElement_ThrowsMalformed_BeforeAnyCatalogCall()
    {
        // `PluginRef` is a reference type, so `"pluginSelection": [null]` deserializes to a null
        // ELEMENT despite the non-nullable annotation. A null can never match anything selectable,
        // so it used to reach UnsupportedWorkspacePluginsException — whose message formatter
        // dereferences it, turning invalid input into a 500 instead of a controlled 400.
        var (service, stub) = CreateServiceWithStub(catalogAvailable: false);

        var act = async () => await service.ValidatePluginsForMutationAsync(["official"], [null!]);

        var thrown = await act.Should().ThrowAsync<MalformedWorkspacePluginSelectionException>();
        thrown.Which.Indexes.Should().Equal([0]);

        // The answer must not depend on the gateway: this stub's catalog is offline, and reaching it
        // would have thrown WorkspaceGatewayCatalogUnavailableException instead. Zero calls is what
        // proves the rejection is deterministic client-side, not a lucky ordering.
        stub.CallCount.Should().Be(0);
    }

    [Theory]
    [InlineData("", "code-review")]
    [InlineData("   ", "code-review")]
    [InlineData("official", "")]
    [InlineData("official", "   ")]
    public async Task ValidatePluginsForMutationAsync_BlankFields_ThrowMalformed(string marketplace, string plugin)
    {
        // A blank field is unreadable for the same reason a null element is: there is no reference to
        // compare against a catalog. Rejecting it here keeps "" out of the persisted selection, where
        // it would otherwise be sent to the gateway as a real plugin name.
        var (service, stub) = CreateServiceWithStub(catalogAvailable: false);

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef(marketplace, plugin)]);

        await act.Should().ThrowAsync<MalformedWorkspacePluginSelectionException>();
        stub.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_GatewayDoesNotSupportPluginFiltering_ThrowsGatewayPluginFilteringUnsupported()
    {
        var service = CreateService(pluginFilteringSupported: false);

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef("official", "code-review")]);

        await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_GatewayCapabilityUnknown_ThrowsGatewayPluginFilteringUnsupported()
    {
        // null capability is treated the same as false: fail closed.
        var service = CreateService(pluginFilteringSupported: null);

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef("official", "code-review")]);

        await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_UnknownPlugin_ThrowsUnsupportedWorkspacePlugins()
    {
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review")]
        );

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef("official", "unknown-plugin")]);

        await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
    }

    [Fact]
    public async Task ValidatePluginsForMutationAsync_KnownPluginsAndSupportedGateway_Succeeds()
    {
        var service = CreateService(
            pluginFilteringSupported: true,
            availablePlugins: [new PluginRef("official", "code-review")]
        );

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef("official", "code-review")]);

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

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official"], [new PluginRef("beta", "deploy")]);

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

        var act = async () =>
            await service.ValidatePluginsForMutationAsync(["official", "beta"], [new PluginRef("beta", "deploy")]);

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

        var act = async () =>
            await service.ValidatePluginsForMutationAsync([], [new PluginRef("official", "code-review")]);

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

        var act = async () => await service.ValidatePluginsForMutationAsync([], [new PluginRef("beta", "deploy")]);

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

        var act = async () => await service.ValidatePluginsForMutationAsync([], [new PluginRef("beta", "deploy")]);

        await act.Should().NotThrowAsync();
    }

    private static Workspace Workspace(IReadOnlyList<string> aliases) =>
        new()
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
        string? configuredMarketplaces = null
    ) =>
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
        string? configuredMarketplaces = null
    )
    {
        var options = Options(configuredMarketplaces);
        if (!catalogAvailable)
        {
            var offline = new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline"));
            return (new WorkspaceCatalogCompatibilityService(offline, options), offline);
        }

        var plugins = availablePlugins ?? [];
        string[] aliases =
            plugins.Count > 0 ? [.. plugins.Select(p => p.Marketplace).Distinct(StringComparer.Ordinal)] : ["official"];

        var stub = new StubCatalogClient(Catalog(aliases, plugins, pluginFilteringSupported));
        return (new WorkspaceCatalogCompatibilityService(stub, options), stub);
    }

    private static MarketplaceCatalog Catalog(params string[] aliases) => Catalog(aliases, [], pluginFiltering: null);

    private static MarketplaceCatalog Catalog(
        IReadOnlyList<string> aliases,
        IReadOnlyList<PluginRef> plugins,
        bool? pluginFiltering
    )
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
            CancellationToken ct = default
        )
        {
            _ = Interlocked.Increment(ref _calls);
            return _error is not null ? Task.FromException<MarketplaceCatalog>(_error) : Task.FromResult(_catalog!);
        }
    }

    /// <summary>
    /// A gateway whose full fetch answers only when the test says so. Its browse read fails, as the real
    /// client's does when the gateway is slow, so a service that used it could never warm the catalog.
    /// </summary>
    private sealed class GatedCatalogClient(MarketplaceCatalog catalog) : IMarketplaceCatalogClient
    {
        private readonly TaskCompletionSource<MarketplaceCatalog> _gate = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private int _calls;

        public int CallCount => _calls;

        public void Finish() => _gate.SetResult(catalog);

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Task.FromException<MarketplaceCatalog>(new MarketplaceCatalogUnavailableException("browse timed out"));

        public Task<MarketplaceCatalog> FetchCatalogToCompletionAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        )
        {
            _ = Interlocked.Increment(ref _calls);
            return _gate.Task;
        }
    }
}
