using System.Collections.Concurrent;
using System.Net;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using LmStreaming.Sample.Services.Discovery;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Pins the #198 sub-agent routing behaviour of <see cref="ContextDiscoveryInjector.InjectAsync"/>:
/// the flag-gated route/fallback split, the tri-state aggregation across
/// <see cref="ISubAgentContextSink"/>s, mark-seen-by-outcome (retry-friendly for the
/// pre-registration race), per-item projection, the diagnostics counter, and the AC7 log line. All
/// routed tests reach the sink through the REAL <see cref="MultiTurnAgentPool"/> (only the pooled
/// agent is a <see cref="RecordingSinkAgent"/> double), so the injector's
/// <c>is ISubAgentContextSink</c> cast is exercised against the pool's real return value.
/// </summary>
public sealed class ContextDiscoveryInjectorRoutingTests
{
    private const string Secret = "route-shared-secret-51ab";
    private const string SessionId = "session-route";
    private const string Kind = "context_file";
    private const string Path = "sub/CLAUDE.md";
    private const string Content = "Sub-directory rules.";
    private const string AgentId = "ctx-probe";

    // --- Flag / normalization (AC1, blank-id bug guard) ---

    [Fact]
    public async Task FlagOff_AgentIdPresent_FansOutToPrimary_NotRouted()
    {
        // AC1 revert path: with the flag OFF a discovery behaves byte-identically to today —
        // fan-out via SendAsync regardless of a present agent_id; the sink is never consulted.
        using var harness = new Harness(routeToSubAgent: false);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(1);
        sink.SentMessages.Should().ContainSingle();
        sink.DeliverCallCount.Should().Be(0);
        harness.Diagnostics.RoutingSnapshot().Fallback.Should().Be(1);
        harness.Diagnostics.RoutingSnapshot().Routed.Should().Be(0);
    }

    [Fact]
    public async Task FlagOn_AgentIdNull_FansOutToPrimary()
    {
        using var harness = new Harness(routeToSubAgent: true);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: null), CancellationToken.None);

        sent.Should().Be(1);
        sink.SentMessages.Should().ContainSingle();
        sink.DeliverCallCount.Should().Be(0);
        harness.Diagnostics.RoutingSnapshot().Fallback.Should().Be(1);
    }

    [Fact]
    public async Task FlagOn_BlankAgentId_TreatedAsSession_FansOutAndDedups()
    {
        // Guards a real double-inject bug: the route predicate MUST use IsNullOrWhiteSpace, so a
        // blank agent_id folds into the __session__ dedup target instead of a rogue "" bucket.
        using var harness = new Harness(routeToSubAgent: true);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        var first = await harness.Injector.InjectAsync(Payload(agentId: "   "), CancellationToken.None);
        var second = await harness.Injector.InjectAsync(Payload(agentId: "   "), CancellationToken.None);

        first.Should().Be(1);
        sink.SentMessages.Should().ContainSingle();
        sink.DeliverCallCount.Should().Be(0, "a blank agent_id must fall back, not create a routing bucket");
        second.Should().Be(0, "the blank id dedups under __session__, not a rogue \"\" target");
        harness.Registry.TryMarkDiscoverySeen(SessionId, SandboxSessionRegistry.SessionDiscoveryTarget, Kind, Path)
            .Should().BeFalse();
    }

    // --- Routed aggregation (AC2 / AC4 / AC5) ---

    [Fact]
    public async Task Delivered_RoutesToSubAgent_PrimaryNotTouched_MarksAndDedups()
    {
        using var harness = new Harness(routeToSubAgent: true);
        var primary = harness.RegisterPrimaryThread(SessionId, "thread-primary");
        var sink = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.Delivered);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(1);
        sink.DeliverCallCount.Should().Be(1);
        sink.LastDeliveredAgentId.Should().Be(AgentId);
        sink.DeliveredMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>()
            .Which.Text.Should().Contain(Content);
        primary.SentMessages.Should().BeEmpty("routed delivery must not also pollute the primary");
        sink.SentMessages.Should().BeEmpty("routed delivery goes via TryDeliverContextAsync, not SendAsync");
        harness.Diagnostics.RoutingSnapshot().Routed.Should().Be(1);

        // Dedup marked under agent_id: a redelivery drops WITHOUT re-delivering.
        var second = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);
        second.Should().Be(0);
        sink.DeliverCallCount.Should().Be(1);
    }

    [Fact]
    public async Task TargetNotDeliverable_Drops_NoFanOut_KeepsMark()
    {
        // AC3/AC5: a known sub-agent that is finished/disposing ⇒ drop, never redirect to the primary.
        using var harness = new Harness(routeToSubAgent: true);
        var primary = harness.RegisterPrimaryThread(SessionId, "thread-primary");
        var sink = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.TargetNotDeliverable);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(0);
        primary.SentMessages.Should().BeEmpty("a known-but-dead sub-agent must never fall back to the primary");
        sink.SentMessages.Should().BeEmpty();
        harness.Diagnostics.RoutingSnapshot().Dropped.Should().Be(1);
        // Terminal ⇒ the mark is kept, so a redelivery is deduped (returns false = already seen).
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path).Should().BeFalse();
    }

    [Fact]
    public async Task AllNotOwned_Drops_PrimaryUntouched_NotMarked_AllowsRetry()
    {
        // AC4: a present-but-unresolved agent_id ⇒ drop, NEVER fall back to the primary, and DON'T keep
        // the mark so the gateway's redelivery heals the pre-registration race.
        using var harness = new Harness(routeToSubAgent: true);
        var primary = harness.RegisterPrimaryThread(SessionId, "thread-primary");
        var sink = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.NotOwned);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(0);
        primary.SentMessages.Should().BeEmpty("present-but-unresolved agent_id must never fall back to the primary");
        sink.SentMessages.Should().BeEmpty();
        harness.Diagnostics.RoutingSnapshot().Dropped.Should().Be(1);
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path)
            .Should().BeTrue("the un-delivered routed discovery was un-marked so a gateway retry can heal the race");
    }

    [Fact]
    public async Task AllNotOwned_UnmarksOnlyItsPath_LeavingConcurrentDeliveredPathIntact()
    {
        // The all-NotOwned rollback must un-mark ONLY the current (target, kind, path), never
        // every path recorded under the same agent_id. A concurrent delivery's mark for a DIFFERENT path
        // of the SAME sub-agent must survive the rollback (a target-wide eviction would erase it).
        const string DeliveredPath = "sub/CLAUDE.md";
        const string NotOwnedPath = "sub/AGENTS.md";

        using var harness = new Harness(routeToSubAgent: true);
        _ = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.NotOwned);

        // Simulate a concurrent delivery having already marked DeliveredPath under the same agent_id.
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, DeliveredPath).Should().BeTrue();

        // This routed discovery for NotOwnedPath resolves NotOwned ⇒ dropped and un-marked (retry-friendly).
        var sent = await harness.Injector.InjectAsync(
            new ContextDiscoveryPayload
            {
                SessionId = SessionId,
                Kind = Kind,
                Path = NotOwnedPath,
                Content = Content,
                AgentId = AgentId,
            },
            CancellationToken.None);
        sent.Should().Be(0);

        // The concurrent DeliveredPath mark MUST still be present — precise un-mark, not target-wide wipe.
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, DeliveredPath)
            .Should().BeFalse("the precise rollback must not erase a concurrent path's mark for the same agent_id");
        // …while NotOwnedPath's own mark WAS rolled back so a gateway redelivery can heal the race.
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, NotOwnedPath)
            .Should().BeTrue("the un-delivered path was un-marked so a gateway retry can heal the pre-registration race");
    }

    [Fact]
    public async Task Aggregation_DeliveredWins_OverNotDeliverable_RegardlessOfOrder()
    {
        using var harness = new Harness(routeToSubAgent: true);
        _ = harness.RegisterSinkThread(SessionId, "thread-a", SubAgentContextDeliveryResult.TargetNotDeliverable);
        var delivered = harness.RegisterSinkThread(SessionId, "thread-b", SubAgentContextDeliveryResult.Delivered);

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(1);
        delivered.DeliveredMessages.Should().ContainSingle("a Delivered result wins the aggregation regardless of thread order");
        harness.Diagnostics.RoutingSnapshot().Routed.Should().Be(1);
        harness.Diagnostics.RoutingSnapshot().Dropped.Should().Be(0);
    }

    [Fact]
    public async Task AmbiguousSinkException_KeepsMark_NoRetryDoubleInject()
    {
        // A sink whose TryDeliverContextAsync THROWS is AMBIGUOUS — it may have
        // accepted (delivered) the send before throwing. The injector must DROP (never fall back to the
        // primary) AND KEEP the dedup mark: un-marking would let a gateway redelivery retry and
        // double-inject the same context. Contrast AllNotOwned_…_AllowsRetry, which un-marks on a clean
        // NotOwned so the pre-registration race can heal.
        using var harness = new Harness(routeToSubAgent: true);
        var primary = harness.RegisterPrimaryThread(SessionId, "thread-primary");
        var sink = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.NotOwned);
        sink.ThrowOnDeliver = new InvalidOperationException("sink boom (may already have delivered)");

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(0);
        primary.SentMessages.Should().BeEmpty("an ambiguous sink failure must never fall back to the primary");
        sink.SentMessages.Should().BeEmpty();
        harness.Diagnostics.RoutingSnapshot().Dropped.Should().Be(1);
        // The mark is KEPT (returns false = already seen), so a gateway redelivery is deduped instead of
        // risking a second inject of a context the throwing sink may already have delivered.
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path)
            .Should().BeFalse("an ambiguous sink failure keeps the dedup mark so a retry can't double-inject");
    }

    // --- Cancellation & stop-scanning ---

    [Fact]
    public async Task PreCancelledToken_DoesNotMark_AndThrows()
    {
        // An already-cancelled call must throw BEFORE placing the routed dedup mark: cancellation is
        // checked before the dedup mark is claimed, so a cancelled request leaves no stale mark that
        // would suppress a later gateway redelivery. The sink returns TargetNotDeliverable, which
        // (absent the guard) would KEEP the mark — so this pins that the start-of-method guard, not a
        // rollback, is what leaves the ledger clean on the routed path.
        using var harness = new Harness(routeToSubAgent: true);
        _ = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.TargetNotDeliverable);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await harness.Injector.InjectAsync(Payload(agentId: AgentId), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path)
            .Should().BeTrue("a cancelled call must throw before marking, leaving the routed dedup slot free");
    }

    [Fact]
    public async Task PreCancelledToken_FallbackPath_DoesNotMarkSession()
    {
        // The same start-of-method guard covers the fallback (__session__) path: with routing OFF a
        // present agent_id fans out to the primary, and an already-cancelled call must throw before the
        // session dedup mark is claimed — so a cancelled request leaves no stale __session__ mark that
        // would suppress a later gateway redelivery. The sink is registered so a live thread exists;
        // the guard fires before it is ever consulted.
        using var harness = new Harness(routeToSubAgent: false);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await harness.Injector.InjectAsync(Payload(agentId: AgentId), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        sink.SentMessages.Should().BeEmpty("the cancellation guard fires before any fan-out send");
        harness.Registry.TryMarkDiscoverySeen(SessionId, SandboxSessionRegistry.SessionDiscoveryTarget, Kind, Path)
            .Should().BeTrue("a cancelled call must throw before marking, leaving the __session__ dedup slot free");
    }

    [Fact]
    public async Task CancelledDuringSinkDelivery_KeepsMark_AsAmbiguous_AndThrows()
    {
        // Cancellation that lands AFTER a sink was invoked is AMBIGUOUS: the sink ultimately calls
        // SendAsync, which may have accepted/enqueued the message before observing cancellation. The
        // routed mark must be KEPT (not rolled back) so a gateway retry can't inject the same context
        // twice. (A pre-delivery cancellation never reaches this branch — it throws at the top of
        // InjectAsync before the mark is created.) Modelled by a hook that cancels + throws OCE mid-delivery.
        using var harness = new Harness(routeToSubAgent: true);
        using var cts = new CancellationTokenSource();
        var sink = harness.RegisterSinkThread(SessionId, "thread-sub", SubAgentContextDeliveryResult.TargetNotDeliverable);
        sink.DeliverBehavior = (_, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        };

        var act = async () => await harness.Injector.InjectAsync(Payload(agentId: AgentId), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path)
            .Should().BeFalse("cancellation after sink invocation is ambiguous, so the mark is KEPT to prevent a retry double-inject");
    }

    [Fact]
    public async Task AmbiguousSink_StopsScanning_NoDeliveryToLaterSink()
    {
        // Once a sink throws (ambiguous — it may already have delivered), the injector must STOP
        // scanning; a later sink that would return Delivered must NEVER be consulted, else the same
        // context injects twice in one request. Registry thread order is non-deterministic, so the
        // contract is pinned to CONSULT order via a shared counter: whichever sink is consulted FIRST
        // throws; a would-be SECOND consult flips a flag and would Deliver. With the stop-scan behaviour
        // the second consult never happens. (In practice only one sink owns an id — this pins the
        // defensive contract.)
        using var harness = new Harness(routeToSubAgent: true);
        var primary = harness.RegisterPrimaryThread(SessionId, "thread-primary");

        var consultCount = 0;
        var laterSinkConsulted = false;
        Func<string, IReadOnlyList<IMessage>, SubAgentContextDeliveryResult> onDeliver = (_, _) =>
        {
            if (Interlocked.Increment(ref consultCount) == 1)
            {
                throw new InvalidOperationException("first consulted sink threw (may already have delivered)");
            }

            laterSinkConsulted = true;
            return SubAgentContextDeliveryResult.Delivered;
        };

        var sinkA = harness.RegisterSinkThread(SessionId, "thread-a", SubAgentContextDeliveryResult.Delivered);
        var sinkB = harness.RegisterSinkThread(SessionId, "thread-b", SubAgentContextDeliveryResult.Delivered);
        sinkA.DeliverBehavior = onDeliver;
        sinkB.DeliverBehavior = onDeliver;

        var sent = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(0, "an ambiguous throw drops the discovery — never routes to a later sink");
        consultCount.Should().Be(1, "the scan must stop at the first (throwing) sink");
        laterSinkConsulted.Should().BeFalse("no later sink may be consulted after an ambiguous throw");
        (sinkA.DeliverCallCount + sinkB.DeliverCallCount)
            .Should().Be(1, "exactly one sink is consulted before the scan stops");
        primary.SentMessages.Should().BeEmpty("an ambiguous sink failure must never fall back to the primary");
        harness.Diagnostics.RoutingSnapshot().Dropped.Should().Be(1);
        harness.Diagnostics.RoutingSnapshot().Routed.Should().Be(0);
        harness.Registry.TryMarkDiscoverySeen(SessionId, AgentId, Kind, Path)
            .Should().BeFalse("the ambiguous drop keeps the dedup mark so a gateway retry can't double-inject");
    }

    // --- Back-compat constructor ---

    [Fact]
    public async Task CompatCtor_RoutingDefaultsOff_PresentAgentId_FansOutToPrimary()
    {
        // The restored 4-arg (registry, pool, formatter, logger) constructor defaults routing OFF, so a
        // discovery carrying a present agent_id must fan out to the primary via SendAsync — the sink's
        // routed TryDeliverContextAsync path is never consulted.
        using var harness = new Harness(routeToSubAgent: true);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        var compatInjector = new ContextDiscoveryInjector(
            harness.Registry,
            harness.Pool,
            new ContextDiscoveryFormatter(),
            NullLogger<ContextDiscoveryInjector>.Instance);

        var sent = await compatInjector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        sent.Should().Be(1);
        sink.SentMessages.Should().ContainSingle("the compat ctor defaults routing OFF, so a present agent_id fans out to the primary");
        sink.DeliverCallCount.Should().Be(0, "routing is off, so the sink's routed delivery path is never consulted");
    }

    // --- AC7 logging ---

    [Fact]
    public async Task Delivered_LogsRoutedOutcome_AtInformation_WithAgentToken()
    {
        var capturing = new CapturingLogger<ContextDiscoveryInjector>();
        using var harness = new Harness(routeToSubAgent: true, injectorLogger: capturing);
        _ = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);

        _ = await harness.Injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);

        // AC7: assert level + a contained token, not an exact string.
        capturing.Entries.Should().Contain(e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("routed", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains(AgentId, StringComparison.Ordinal));
    }

    // --- Contract / per-item projection ---

    [Fact]
    public void Envelope_MixedAgentId_BindsPerItem()
    {
        const string json = """
            {
              "session_id": "sess-1",
              "discoveries": [
                { "kind": "context_file", "path": "sub/CLAUDE.md", "content": "x", "agent_id": "ctx-probe" },
                { "kind": "context_file", "path": "AGENTS.md", "content": "y" }
              ]
            }
            """;

        var envelope = JsonSerializer.Deserialize<ContextDiscoveryEnvelope>(json);

        envelope.Should().NotBeNull();
        envelope!.Discoveries.Should().HaveCount(2);
        envelope.Discoveries![0].AgentId.Should().Be("ctx-probe");
        envelope.Discoveries[1].AgentId.Should().BeNull("the second item carried no agent_id — projection is per item");
    }

    [Fact]
    public async Task MixedBatch_PerItemProjection_ARoutes_BFallsBack()
    {
        // The controller projects agent_id PER ITEM. Item A carries agent_id (routes via the sink);
        // item B carries none (falls back via SendAsync into the same thread). If projection copied
        // agent_id from the envelope onto every item, B would also route and SentMessages would be
        // empty — so proving both paths ran on distinct items proves the per-item copy.
        using var harness = new Harness(routeToSubAgent: true);
        var sink = harness.RegisterSinkThread(SessionId, "thread-1", SubAgentContextDeliveryResult.Delivered);
        var controller = harness.CreateController();

        var result = await controller.NotifyAsync(
            new ContextDiscoveryEnvelope
            {
                SessionId = SessionId,
                Discoveries =
                [
                    new ContextDiscoveryItem { Kind = Kind, Path = "sub/CLAUDE.md", Content = "SUB_MARKER", AgentId = AgentId },
                    new ContextDiscoveryItem { Kind = Kind, Path = "AGENTS.md", Content = "ROOT_MARKER" },
                ],
            },
            CancellationToken.None);

        result.Should().BeOfType<OkResult>();
        sink.DeliveredMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>()
            .Which.Text.Should().Contain("SUB_MARKER");
        sink.SentMessages.Should().ContainSingle().Which.Should().BeOfType<NotifyMessage>()
            .Which.Text.Should().Contain("ROOT_MARKER");
        harness.Diagnostics.RoutingSnapshot().Routed.Should().Be(1);
        harness.Diagnostics.RoutingSnapshot().Fallback.Should().Be(1);
    }

    // --- Post-merge (skipped): the injector's is-cast against the REAL MultiTurnAgentLoop ---

    [Fact]
    public async Task Routing_ReachesSink_ThroughRealMultiTurnAgentLoop()
    {
        // Pre-merge a real MultiTurnAgentLoop is NOT an ISubAgentContextSink, so BeAssignableTo fails
        // and this stays skipped. Post-merge the loop implements the sink and the injector routes to
        // the real return type of the real pool. A fresh loop owns no sub-agent named ctx-probe, so
        // the delivery resolves NotOwned ⇒ dropped (never routed to the primary) — the point is the
        // REAL sink is consulted at all.
        var mockAgent = new Mock<IStreamingAgent>();
        var functions = new FunctionRegistry();
        await using var pool = new MultiTurnAgentPool(
            (threadId, _, _) => new MultiTurnAgentPool.AgentCreationResult(
                new MultiTurnAgentLoop(
                    mockAgent.Object,
                    functions,
                    threadId,
                    logger: NullLogger<MultiTurnAgentLoop>.Instance)),
            NullLogger<MultiTurnAgentPool>.Instance);

        var mode = SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;
        var agent = pool.GetOrCreateAgent("thread-loop", mode);
        agent.Should().BeAssignableTo<ISubAgentContextSink>();

        await using var registry = CreateRegistry();
        registry.RegisterThread(SessionId, "thread-loop");
        var injector = new ContextDiscoveryInjector(
            registry,
            pool,
            new ContextDiscoveryFormatter(),
            new ContextDiscoveryOptions { RouteToOpeningSubAgent = true },
            new ContextDiscoveryDiagnostics(),
            NullLogger<ContextDiscoveryInjector>.Instance);

        var sent = await injector.InjectAsync(Payload(agentId: AgentId), CancellationToken.None);
        sent.Should().Be(0);
    }

    private static ContextDiscoveryPayload Payload(string? agentId) => new()
    {
        SessionId = SessionId,
        Kind = Kind,
        Path = Path,
        Content = Content,
        AgentId = agentId,
    };

    private sealed class Harness : IDisposable
    {
        private readonly ConcurrentDictionary<string, IMultiTurnAgent> _agents = new();
        private readonly ConcurrentDictionary<string, Func<string, IMultiTurnAgent>> _factories = new();

        public Harness(bool routeToSubAgent, ILogger<ContextDiscoveryInjector>? injectorLogger = null)
        {
            Registry = CreateRegistry();
            Pool = new MultiTurnAgentPool(
                (threadId, _, _) =>
                {
                    var agent = _agents.GetOrAdd(
                        threadId,
                        id => _factories.TryGetValue(id, out var factory) ? factory(id) : new RecordingMultiTurnAgent(id));
                    return new MultiTurnAgentPool.AgentCreationResult(agent);
                },
                NullLogger<MultiTurnAgentPool>.Instance);
            Pool.ThreadRemoved += threadId => Registry.UnregisterThreadFromAllSessions(threadId);

            Diagnostics = new ContextDiscoveryDiagnostics();
            SharedSecret = new AuthSharedSecret(new AuthOptions
            {
                Webhook = new WebhookOptions { GatewaySharedSecret = Secret },
            });
            Injector = new ContextDiscoveryInjector(
                Registry,
                Pool,
                new ContextDiscoveryFormatter(),
                new ContextDiscoveryOptions { RouteToOpeningSubAgent = routeToSubAgent },
                Diagnostics,
                injectorLogger ?? NullLogger<ContextDiscoveryInjector>.Instance);
        }

        public SandboxSessionRegistry Registry { get; }
        public MultiTurnAgentPool Pool { get; }
        public ContextDiscoveryInjector Injector { get; }
        public ContextDiscoveryDiagnostics Diagnostics { get; }
        public AuthSharedSecret SharedSecret { get; }

        public RecordingMultiTurnAgent RegisterPrimaryThread(string sessionId, string threadId)
        {
            _factories[threadId] = id => new RecordingMultiTurnAgent(id);
            _ = Pool.GetOrCreateAgent(threadId, Mode);
            Registry.RegisterThread(sessionId, threadId);
            return (RecordingMultiTurnAgent)_agents[threadId];
        }

        public RecordingSinkAgent RegisterSinkThread(
            string sessionId,
            string threadId,
            SubAgentContextDeliveryResult result)
        {
            _factories[threadId] = id => new RecordingSinkAgent(id, result);
            _ = Pool.GetOrCreateAgent(threadId, Mode);
            Registry.RegisterThread(sessionId, threadId);
            return (RecordingSinkAgent)_agents[threadId];
        }

        public ContextDiscoveryController CreateController()
        {
            var loader = new WorkspaceSubAgentLoader(Registry, NullLogger<WorkspaceSubAgentLoader>.Instance);
            var controller = new ContextDiscoveryController(
                SharedSecret,
                Registry,
                loader,
                Injector,
                Diagnostics,
                NullLogger<ContextDiscoveryController>.Instance);

            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Authorization = Secret;
            controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
            return controller;
        }

        private static AgentProfile Mode => SystemChatModes.GetById(SystemChatModes.DefaultModeId)!;

        public void Dispose()
        {
            Pool.DisposeAsync().AsTask().GetAwaiter().GetResult();
            Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static SandboxSessionRegistry CreateRegistry()
    {
        static HttpResponseMessage Unused(HttpRequestMessage _) => new(HttpStatusCode.OK);

        var gateway = new SandboxGatewayLifetime(
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new StubHandler(Unused)));

        return new SandboxSessionRegistry(
            gateway,
            new SandboxGatewayOptions { BaseUrl = "http://localhost:3000" },
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(new StubHandler(Unused)),
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
