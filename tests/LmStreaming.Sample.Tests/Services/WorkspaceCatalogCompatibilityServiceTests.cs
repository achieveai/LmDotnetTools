using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Services;
using Microsoft.Extensions.DependencyInjection;
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

    /// <summary>
    /// The 503 a caller receives must still name what actually went wrong. Session creation reports this
    /// exception's message to the operator and logs the exception itself, so dropping the cause turns a
    /// diagnosable "the gateway did not answer within the transport budget" into an unattributable
    /// "catalog is unavailable" — which is exactly how a HEALTHY-but-cold gateway got misdiagnosed.
    /// </summary>
    [Fact]
    public async Task ValidateForSessionAsync_UnavailableCatalog_KeepsTheCauseThatMadeItUnavailable()
    {
        var cause = new SandboxException(SandboxErrorKind.TransportTimeout, "gateway did not answer in time");
        var service = new WorkspaceCatalogCompatibilityService(
            new StubCatalogClient(new MarketplaceCatalogUnavailableException("offline", cause)),
            Options()
        );

        var thrown = await FluentActions
            .Invoking(() => service.ValidateForSessionAsync(Workspace(["one"])))
            .Should()
            .ThrowAsync<WorkspaceGatewayCatalogUnavailableException>();

        thrown.Which.InnerException.Should().BeOfType<MarketplaceCatalogUnavailableException>();
        // The whole chain, not just one link: the SandboxException is the only thing that says WHICH
        // failure mode this was (a transport timeout rather than, say, a 401), and it is two levels down.
        thrown.Which.GetBaseException().Should().BeSameAs(cause);
    }

    /// <summary>
    /// A snapshot that recorded "could not read the catalog" must age out far faster than a good one. The
    /// cache exists to spare a healthy gateway repeated reads; pinning a FAILURE for the same full window
    /// does the opposite — it keeps answering 503 from memory for 30 seconds after the gateway came up.
    /// </summary>
    [Fact]
    public async Task AnUnavailableCatalogIsRefreshedLongBeforeASuccessfulOneWouldBe()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = new ToggleCatalogClient(new MarketplaceCatalogUnavailableException("cold gateway"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), clock);

        (await service.EvaluateAsync(Workspace([]))).Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        client.CallCount.Should().Be(1);

        // Well inside the 30s window a SUCCESSFUL snapshot is held for (pinned by the paired test below),
        // so a single shared duration would serve the stale failure here and never make this call.
        clock.Advance(TimeSpan.FromSeconds(5));
        client.Succeed(Catalog("one"));

        var recovered = await service.EvaluateAsync(Workspace([]));

        client.CallCount.Should().Be(2);
        recovered.Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
    }

    /// <summary>
    /// Non-vacuity guard for the test above: shortening the UNAVAILABLE window must not collapse the cache
    /// altogether. A successful snapshot is still held for its full 30 seconds — that is what keeps a warm
    /// gateway from being re-read on every workspace listing.
    /// </summary>
    [Fact]
    public async Task ASuccessfulCatalogIsStillHeldForTheFullWindow()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var client = new ToggleCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), clock);

        _ = await service.EvaluateAsync(Workspace([]));
        clock.Advance(TimeSpan.FromSeconds(5));
        _ = await service.EvaluateAsync(Workspace([]));

        client.CallCount.Should().Be(1, "a good catalog five seconds old is still good");

        clock.Advance(TimeSpan.FromSeconds(26));
        _ = await service.EvaluateAsync(Workspace([]));

        client.CallCount.Should().Be(2, "past 30 seconds even a good catalog is re-read");
    }

    /// <summary>
    /// The split that fixes the outage: session validation reads through the session client, so a browse
    /// client that gave up after its short budget cannot decide whether a session may start. The browse
    /// stub here is offline precisely so a fallback would be LOUD — it would throw, not merely re-route.
    /// </summary>
    [Fact]
    public async Task ValidateForSessionAsync_ReadsTheSessionClient_NotTheBestEffortBrowseOne()
    {
        var browse = new StubCatalogClient(new MarketplaceCatalogUnavailableException("browse budget expired"));
        var session = new StubSessionCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(browse, Options(), sessionClient: session);

        await FluentActions
            .Invoking(() => service.ValidateForSessionAsync(Workspace(["one"])))
            .Should()
            .NotThrowAsync();

        session.CallCount.Should().Be(1);
        browse.CallCount.Should().Be(0, "the session path must not consult the fail-fast browse client at all");
    }

    /// <summary>
    /// Non-vacuity guard for the test above: registering a session client must not silently re-route the
    /// BROWSE callers onto it. A listing still uses the short-budget client — that is what keeps a page
    /// load from hanging on a gateway that is not answering.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_StillReadsTheBrowseClient_WhenASessionClientIsRegistered()
    {
        var browse = new StubCatalogClient(Catalog("one"));
        var session = new StubSessionCatalogClient(new MarketplaceCatalogUnavailableException("not this one"));
        var service = new WorkspaceCatalogCompatibilityService(browse, Options(), sessionClient: session);

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
        browse.CallCount.Should().Be(1);
        session.CallCount.Should().Be(0);
    }

    /// <summary>
    /// A host (or test) that wires only the browse client keeps exactly the behaviour it had before the
    /// split — one client, one cache, one call. Without this, the optional registration would be a
    /// silent breaking change for every deployment that has not added it.
    /// </summary>
    [Fact]
    public async Task ValidateForSessionAsync_WithNoSessionClient_FallsBackToTheBrowseClientAndItsCache()
    {
        var browse = new StubCatalogClient(Catalog("one"));
        var service = new WorkspaceCatalogCompatibilityService(browse, Options());

        await service.ValidateForSessionAsync(Workspace(["one"]));
        _ = await service.EvaluateAsync(Workspace(["one"]));

        browse.CallCount.Should().Be(1, "both paths share one cache when no session client is registered");
    }

    /// <summary>
    /// The wiring claim, proved against a real container rather than asserted in a comment: adding ONE
    /// <c>ISessionMarketplaceCatalogClient</c> registration is enough — the existing
    /// <c>AddSingleton&lt;WorkspaceCatalogCompatibilityService&gt;()</c> line is not touched, because the
    /// constructor parameter is optional and the container fills it in when (and only when) it resolves.
    /// If that assumption about optional-parameter activation were wrong, the "without" case below would
    /// fail to construct at all.
    /// </summary>
    [Fact]
    public async Task TheContainerResolvesTheServiceWithAndWithoutASessionCatalogRegistration()
    {
        using var withoutSession = new ServiceCollection()
            .AddSingleton<IMarketplaceCatalogClient>(new StubCatalogClient(Catalog("one")))
            .AddSingleton(Options())
            .AddSingleton<WorkspaceCatalogCompatibilityService>()
            .BuildServiceProvider();

        await FluentActions
            .Invoking(() =>
                withoutSession
                    .GetRequiredService<WorkspaceCatalogCompatibilityService>()
                    .ValidateForSessionAsync(Workspace(["one"]))
            )
            .Should()
            .NotThrowAsync();

        var session = new StubSessionCatalogClient(Catalog("one"));
        using var withSession = new ServiceCollection()
            .AddSingleton<IMarketplaceCatalogClient>(
                new StubCatalogClient(new MarketplaceCatalogUnavailableException("browse budget expired"))
            )
            .AddSingleton<ISessionMarketplaceCatalogClient>(session)
            .AddSingleton(Options())
            .AddSingleton<WorkspaceCatalogCompatibilityService>()
            .BuildServiceProvider();

        await withSession
            .GetRequiredService<WorkspaceCatalogCompatibilityService>()
            .ValidateForSessionAsync(Workspace(["one"]));

        session.CallCount.Should().Be(1, "the registration is what redirects session validation, nothing else");
    }

    /// <summary>
    /// A waiter whose token fires leaves its refresh behind — the cache only clears a slot whose task had
    /// already completed, and this one had not. The refresh then finishes with nobody listening, and the
    /// snapshot it recorded ages out unobserved. The NEXT caller must not adopt it: "the gateway said so,
    /// at a time when no caller was waiting for the answer" is not a current read, and adopting it would
    /// let one cancelled request pin a stale catalog for every request after it.
    /// <para>
    /// The abandoned refresh records an UNAVAILABLE snapshot deliberately. A stale SUCCESS would be caught
    /// a second time by the freshness check at the return boundary, which then re-reads — so a test
    /// written with one could not tell the join-time rejection from that later check, and passes with the
    /// join-time rejection removed. An unavailable snapshot has no such second line: adopting it lands
    /// straight on the fail-closed path, which is a different answer AND a different call count.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ACompletedRefreshAbandonedByACancelledWaiterIsRejectedOnceItHasAgedOut()
    {
        await WithoutSynchronizationContext(async () =>
        {
            var clock = new ScriptedClock(Origin);
            var client = new GatedCatalogClient(later: Catalog("two"));
            var service = new WorkspaceCatalogCompatibilityService(client, Options(), clock);

            using var abandoned = new CancellationTokenSource();
            var waiter = service.EvaluateAsync(Workspace(["two"]), abandoned.Token);
            await abandoned.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

            // The refresh nobody is waiting on now finishes, recording "the gateway is unreadable" at T0.
            client.Fault(new MarketplaceCatalogUnavailableException("cold gateway"));
            clock.Advance(TimeSpan.FromSeconds(5));

            var result = await service.EvaluateAsync(Workspace(["two"]));

            client.CallCount.Should().Be(2, "an aged-out abandoned refresh is re-read, not adopted");
            result
                .Compatibility.Should()
                .Be(
                    WorkspaceCompatibility.Compatible,
                    "the answer must come from the fresh read, whose catalog offers 'two'"
                );
        });
    }

    /// <summary>
    /// The same abandonment, but the refresh ended in an exception the cache does not model as
    /// "unavailable" (anything other than <see cref="MarketplaceCatalogUnavailableException"/> propagates).
    /// A faulted flight holds no answer at all, so handing it to the next caller converts one cancelled
    /// request into a failure for a caller that never asked for that transport.
    /// </summary>
    [Fact]
    public async Task AFaultedRefreshAbandonedByACancelledWaiterIsNotRethrownAtTheNextCaller()
    {
        await WithoutSynchronizationContext(async () =>
        {
            var client = new GatedCatalogClient(later: Catalog("two"));
            var service = new WorkspaceCatalogCompatibilityService(client, Options(), new ScriptedClock(Origin));

            using var abandoned = new CancellationTokenSource();
            var waiter = service.EvaluateAsync(Workspace(["two"]), abandoned.Token);
            await abandoned.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

            client.Fault(new InvalidOperationException("the catalog reader threw"));

            var result = await service.EvaluateAsync(Workspace(["two"]));

            client.CallCount.Should().Be(2);
            result.Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
        });
    }

    /// <summary>
    /// And the third way a flight can settle without an answer: cancelled. Kept separate from the faulted
    /// case because <c>IsFaulted</c> and <c>IsCanceled</c> are different states, and a check written
    /// against only one of them looks correct until the transport's own timeout surfaces as a
    /// <c>TaskCanceledException</c> — which is exactly how <c>HttpClient</c> reports one.
    /// </summary>
    [Fact]
    public async Task ACancelledRefreshAbandonedByACancelledWaiterIsNotRethrownAtTheNextCaller()
    {
        await WithoutSynchronizationContext(async () =>
        {
            var client = new GatedCatalogClient(later: Catalog("two"));
            var service = new WorkspaceCatalogCompatibilityService(client, Options(), new ScriptedClock(Origin));

            using var abandoned = new CancellationTokenSource();
            var waiter = service.EvaluateAsync(Workspace(["two"]), abandoned.Token);
            await abandoned.CancelAsync();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

            client.CancelRefresh();

            var result = await service.EvaluateAsync(Workspace(["two"]));

            client.CallCount.Should().Be(2);
            result.Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
        });
    }

    /// <summary>
    /// F1. A caller that burned a whole transport budget only to learn the gateway is unreadable, and that
    /// then resumed late enough for even the short unavailable window to have closed, fails closed HERE —
    /// on a caller-local answer — instead of spending a second budget. The two assertions are the whole
    /// point and neither implies the other: ONE transport (a second would be an amplifier aimed at a
    /// gateway already known to be down), and a total elapsed cost that is not a multiple of the budget
    /// (the caller that already waited longest must not be the one made to wait twice as long, because the
    /// stage deadline it is racing does not double with it).
    /// </summary>
    [Fact]
    public async Task AnUnavailableSnapshotThatAgedOutBeforeReturningFailsClosedWithoutASecondTransport()
    {
        var clock = new ScriptedClock(Origin);
        var client = new ScriptedCatalogClient(
            // The gateway holds the connection for the whole budget, then answers nothing.
            _ => Burn(clock, TransportBudget, new MarketplaceCatalogUnavailableException("gateway did not answer")),
            // The recovery read, which only the NEXT caller is entitled to.
            _ => Burn(clock, TransportBudget, Catalog("one"))
        );
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), clock);

        // Clock reads in one GetAsync: (1) the join-time freshness check, (2) the refresh's own stamp,
        // (3) the freshness check at the boundary the value is handed back on. Advancing at (3) is a
        // waiter that resumed after the unavailable window had already closed.
        clock.HookRead(3, c => c.Advance(TimeSpan.FromSeconds(5)));

        var result = await service.EvaluateAsync(Workspace(["one"]));

        result.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        client.CallCount.Should().Be(1, "recovery belongs to the next caller, not to a second transport here");
        (clock.Now - Origin)
            .Should()
            .Be(
                TransportBudget + TimeSpan.FromSeconds(5),
                "one budget plus the late resume — a second attempt would make it two budgets"
            );

        // …and the next caller really does get one, so failing closed above is not failing shut.
        var recovered = await service.EvaluateAsync(Workspace(["one"]));

        recovered.Compatibility.Should().Be(WorkspaceCompatibility.Compatible);
        client.CallCount.Should().Be(2);
    }

    /// <summary>
    /// F2. A newer snapshot lands in the cache between a waiter's own freshness check and its return. The
    /// waiter is holding an older SUCCESS; the newer snapshot says the gateway is now unreadable. The
    /// older success must not be what comes back, and must not overwrite the newer failure on the way out
    /// — either would re-authorize, for the newer snapshot's whole remaining window, exactly what the
    /// newer snapshot had just refused.
    /// <para>
    /// The interleaving is scripted rather than raced: the clock hook fires on the waiter's boundary read,
    /// which is precisely the instant between "my snapshot was fresh when I asked" and "here is my
    /// snapshot", and the newer read happens there.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ANewerUnavailableSnapshotBeatsTheOlderSuccessAWaiterIsStillHolding()
    {
        var clock = new ScriptedClock(Origin);
        var client = new ScriptedCatalogClient(
            _ => Task.FromResult(Catalog("one")),
            _ => Task.FromException<MarketplaceCatalog>(new MarketplaceCatalogUnavailableException("gateway down"))
        );
        var service = new WorkspaceCatalogCompatibilityService(client, Options(), clock);

        WorkspaceCompatibilityResult? interleaved = null;
        clock.HookRead(
            3,
            c =>
            {
                c.Advance(TimeSpan.FromSeconds(1));
                // Completes synchronously (the scripted client answers from memory), so this really is one
                // read landing inside another rather than two racing tasks.
                interleaved = service.EvaluateAsync(Workspace(["one"])).GetAwaiter().GetResult();
            }
        );

        var result = await service.EvaluateAsync(Workspace(["one"]));

        interleaved!.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        result
            .Compatibility.Should()
            .Be(
                WorkspaceCompatibility.Unavailable,
                "the older success is still inside its own 30s window, so only the ordering rules it out"
            );

        // Publication is monotonic too: the waiter must not have written its older success back over the
        // newer failure on its way out, or the next caller reads a catalog the cache had already retired.
        var next = await service.EvaluateAsync(Workspace(["one"]));

        next.Compatibility.Should().Be(WorkspaceCompatibility.Unavailable);
        client.CallCount.Should().Be(2, "the newer failure is still fresh, so nothing else is fetched");
    }

    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A whole transport budget, standing in for the session client's minutes-long one.</summary>
    private static readonly TimeSpan TransportBudget = TimeSpan.FromSeconds(120);

    /// <summary>A client call that holds the connection for <paramref name="budget"/> before answering.</summary>
    private static Task<MarketplaceCatalog> Burn(ScriptedClock clock, TimeSpan budget, MarketplaceCatalog catalog)
    {
        clock.Advance(budget);
        return Task.FromResult(catalog);
    }

    private static Task<MarketplaceCatalog> Burn(ScriptedClock clock, TimeSpan budget, Exception error)
    {
        clock.Advance(budget);
        return Task.FromException<MarketplaceCatalog>(error);
    }

    /// <summary>
    /// Runs <paramref name="body"/> with no ambient <see cref="SynchronizationContext"/>, so completing a
    /// <see cref="TaskCompletionSource{TResult}"/> drives the production continuation awaiting it INLINE
    /// rather than posting it to xUnit's async context. That turns "the abandoned refresh has settled"
    /// into a fact the next line may rely on, instead of a race a test would otherwise have to sleep on.
    /// </summary>
    private static async Task WithoutSynchronizationContext(Func<Task> body)
    {
        var prior = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            await body().ConfigureAwait(false);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(prior);
        }
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
    /// The same fixed stub, typed as the SESSION seam. A separate type rather than a flag because the
    /// production seam is a separate type: a test double that satisfied both interfaces at once could not
    /// tell which registration the service actually read.
    /// </summary>
    private sealed class StubSessionCatalogClient : ISessionMarketplaceCatalogClient
    {
        private readonly MarketplaceCatalog? _catalog;
        private readonly Exception? _error;
        private int _calls;

        public StubSessionCatalogClient(MarketplaceCatalog catalog) => _catalog = catalog;

        public StubSessionCatalogClient(Exception error) => _error = error;

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
    /// A catalog client whose answer can CHANGE between calls — the shape a cold gateway actually has
    /// (unreachable, then reachable). <see cref="StubCatalogClient"/> is fixed for its whole life, so it
    /// cannot express a recovery at all, and a cache test written against it would only ever prove that a
    /// second call returns the same thing.
    /// </summary>
    private sealed class ToggleCatalogClient : IMarketplaceCatalogClient
    {
        private MarketplaceCatalog? _catalog;
        private Exception? _error;
        private int _calls;

        public ToggleCatalogClient(MarketplaceCatalog catalog) => _catalog = catalog;

        public ToggleCatalogClient(Exception error) => _error = error;

        public int CallCount => _calls;

        /// <summary>Makes every subsequent call succeed with <paramref name="catalog"/>.</summary>
        public void Succeed(MarketplaceCatalog catalog)
        {
            _catalog = catalog;
            _error = null;
        }

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
    /// A catalog client whose FIRST call parks until the test releases it, and whose later calls answer
    /// from memory. Parking is what lets a test abandon a refresh mid-flight — the state the cache only
    /// reaches when a waiter's token fires before its refresh has completed, which no fixed or toggling
    /// stub can express.
    /// </summary>
    private sealed class GatedCatalogClient : IMarketplaceCatalogClient
    {
        private readonly TaskCompletionSource<MarketplaceCatalog> _gate = new();
        private readonly MarketplaceCatalog _later;
        private int _calls;

        public GatedCatalogClient(MarketplaceCatalog later) => _later = later;

        public int CallCount => Volatile.Read(ref _calls);

        /// <summary>Ends the parked first call with an exception, faulting or answering per its type.</summary>
        public void Fault(Exception error) => _gate.SetException(error);

        /// <summary>Ends the parked first call the way a transport timeout does — cancelled, not faulted.</summary>
        public void CancelRefresh() => _gate.SetCanceled();

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) => Interlocked.Increment(ref _calls) == 1 ? _gate.Task : Task.FromResult(_later);
    }

    /// <summary>
    /// A client that answers each successive call from its own script, repeating the last. Lets a test say
    /// "this call costs a whole budget and then fails, the next one succeeds" — the shape a cold gateway
    /// actually has, and the only way to price a second transport in clock terms.
    /// </summary>
    private sealed class ScriptedCatalogClient : IMarketplaceCatalogClient
    {
        private readonly Func<int, Task<MarketplaceCatalog>>[] _script;
        private int _calls;

        public ScriptedCatalogClient(params Func<int, Task<MarketplaceCatalog>>[] script) => _script = script;

        public int CallCount => Volatile.Read(ref _calls);

        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        )
        {
            var call = Interlocked.Increment(ref _calls);
            return _script[Math.Min(call, _script.Length) - 1](call);
        }
    }

    /// <summary>
    /// A clock that can run a test action DURING a nominated read. That is what makes an interleaving a
    /// script instead of a race: the production code reads the clock at exactly the boundaries these tests
    /// are about (the join-time freshness check, the refresh's stamp, the freshness check at the return),
    /// so hooking a read places the test's action on a named line rather than "somewhere around then".
    /// A hook is one-shot and is cleared before it runs, so a nested call inside it cannot re-enter it.
    /// </summary>
    private sealed class ScriptedClock(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly Dictionary<int, Action<ScriptedClock>> _hooks = [];
        private int _reads;

        public DateTimeOffset Now { get; private set; } = start;

        public void Advance(TimeSpan by) => Now += by;

        /// <param name="read">One-based index of the <see cref="GetUtcNow"/> call to hook.</param>
        /// <param name="hook">Runs during that read, before it returns; cleared first, so it runs once.</param>
        public void HookRead(int read, Action<ScriptedClock> hook)
        {
            lock (_gate)
            {
                _hooks[read] = hook;
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            Action<ScriptedClock>? hook;
            lock (_gate)
            {
                _ = _hooks.Remove(++_reads, out hook);
            }

            hook?.Invoke(this);
            return Now;
        }
    }
}
