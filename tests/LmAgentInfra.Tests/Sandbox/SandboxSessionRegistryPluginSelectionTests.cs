using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

/// <summary>
/// Pins the prepare-then-replace primitives a plugin-selection change is built from: snapshot the live
/// partitions, create replacement candidates beside them, swap the cache entries, then retire the
/// superseded sessions. The ordering is what makes the migration safe — nothing that the user can
/// observe changes until every candidate exists, and a failure anywhere before the swap leaves the
/// original sessions serving traffic.
/// </summary>
public class SandboxSessionRegistryPluginSelectionTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";

    /// <summary>The gateway header carrying the calling app's identity, stamped by the SDK client.</summary>
    private const string AppIdHeader = "X-Sbx-App-Id";

    [Fact]
    public async Task SnapshotPluginSelectionPartitions_ReturnsOnePartitionPerCallerAppId()
    {
        // Sessions are partitioned by (workspace, caller app) — one workspace can be live under several
        // callers at once. A migration that assumed "the" session would silently strand every other
        // caller on the OLD plugin set.
        await using var registry = CreateRegistryWithFakeGateway(out _);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-b"));
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-2"), credential: CredentialFor("app-a"));

        var partitions = registry.SnapshotPluginSelectionPartitions("ws-1");

        partitions.Should().HaveCount(2);
        partitions.Select(p => p.Key.AppId).Should().BeEquivalentTo(["app-a", "app-b"]);
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitions_CapturesCallerCredentialExplicitly()
    {
        // The partition carries the caller's credential captured AT SNAPSHOT TIME — candidate creation
        // must use this value rather than re-deriving it from the session id later, where an eviction
        // racing the migration would silently fall back to the process-default identity.
        await using var registry = CreateRegistryWithFakeGateway(out _);
        var credential = CredentialFor("app-a");
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: credential);

        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        // SandboxCredential is a readonly record struct, so reference identity is meaningless here;
        // value equality plus an explicit "not the default" makes the assertion non-vacuous — otherwise
        // a buggy implementation that always returned _defaultCredential would still pass.
        partition.Credential.Should().Be(credential);
        partition.Credential.Should().NotBe(registry.DefaultCredential);
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitions_BlankWorkspaceId_NormalizesToTheDefaultWorkspace()
    {
        // Every resolve path normalizes a blank id to the default workspace before keying the cache, so
        // the snapshot must too. Skipping it makes the snapshot match no key at all — and an empty
        // partition list is not an error anywhere downstream: the migration walks zero partitions,
        // persists the new selection and reports success having changed nothing. A silent no-op is the
        // worst outcome for a user who just edited their plugin list.
        await using var registry = CreateRegistryWithFakeGateway(out _);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(SandboxSessionRegistry.DefaultWorkspaceId));

        var partitions = registry.SnapshotPluginSelectionPartitions("   ");

        partitions.Should().ContainSingle().Which.Key.WorkspaceId.Should().Be(SandboxSessionRegistry.DefaultWorkspaceId);
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitionsAsync_WaitsForAnInFlightCreation_SoItMigratesWithTheBatch()
    {
        // A session being created WHILE the user saves a new plugin selection is the common race: the
        // synchronous capture cannot see it (no session exists yet), so without the settle wait it would
        // be born on the old selection and stay there. The bounded wait pulls it into the same batch.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        gateway.HoldCreates();
        var createTask = registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));
        await gateway.WaitForHeldCreatesAsync(1);

        var snapshotTask = registry.SnapshotPluginSelectionPartitionsAsync(
            "ws-1",
            TimeSpan.FromSeconds(10),
            CancellationToken.None
        );

        // Positive witness that the wait is REAL. Without it this test would still pass against a
        // snapshot that ignored the budget and captured nothing, because by the time the assertions
        // below run the create has been released and completed anyway.
        var firstToFinish = await Task.WhenAny(snapshotTask, Task.Delay(TimeSpan.FromMilliseconds(200)));
        firstToFinish
            .Should()
            .NotBeSameAs(snapshotTask, "the snapshot must still be waiting on the in-flight creation");

        gateway.ReleaseCreates();
        _ = await createTask;
        var snapshot = await snapshotTask;

        snapshot.Partitions.Should().ContainSingle().Which.Key.AppId.Should().Be("app-a");
        snapshot.Unsettled.Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitionsAsync_SharesOneBudgetAcrossWedgedCreations_AndReportsThemUnsettled()
    {
        // The budget is spent ONCE for the whole batch, not once per entry. A per-entry budget would
        // multiply the worst case by the partition count — three wedged creations would hold the user's
        // save request open for three timeouts — and it is invisible to any test with a single pending
        // entry, which is why this one wedges three.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        gateway.HoldCreates();
        var createTasks = new[] { "app-a", "app-b", "app-c" }
            .Select(appId => registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor(appId)))
            .ToList();
        await gateway.WaitForHeldCreatesAsync(3);

        var budget = TimeSpan.FromMilliseconds(500);
        var elapsed = Stopwatch.StartNew();
        var snapshot = await registry.SnapshotPluginSelectionPartitionsAsync("ws-1", budget, CancellationToken.None);
        elapsed.Stop();

        // Three wedged creations on a per-entry budget cost 3 × 500ms; on a shared one they cost 500ms.
        // The bound sits between the two so neither a slow machine nor the bug can be mistaken for the
        // other.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(1_100));
        snapshot.Partitions.Should().BeEmpty();
        snapshot
            .Unsettled.Select(key => key.AppId)
            .Should()
            .BeEquivalentTo(["app-a", "app-b", "app-c"], "every creation still in flight is owed a reconcile");

        gateway.ReleaseCreates();
        await Task.WhenAll(createTasks);
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitionsAsync_ZeroBudget_CapturesTheSettledPartitionAndOwesOnlyTheInFlightOne()
    {
        // Every materialized entry lands in exactly one of the two lists. The pair matters more than
        // either half: a key in NEITHER list is a session that keeps serving the old plugin set while
        // looking migrated, and nothing downstream can tell the difference.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));

        gateway.HoldCreates();
        var inFlight = registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-b"));
        await gateway.WaitForHeldCreatesAsync(1);

        var elapsed = Stopwatch.StartNew();
        var snapshot = await registry.SnapshotPluginSelectionPartitionsAsync(
            "ws-1",
            TimeSpan.Zero,
            CancellationToken.None
        );
        elapsed.Stop();

        snapshot.Partitions.Should().ContainSingle().Which.Key.AppId.Should().Be("app-a");
        snapshot.Unsettled.Should().ContainSingle().Which.Should().Be(("ws-1", "app-b"));
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), "a zero budget must not wait at all");

        gateway.ReleaseCreates();
        _ = await inFlight;
    }

    [Theory]
    // Tri-state: "no explicit selection" and "explicitly none" are OPPOSITE instructions to the gateway,
    // so each matches only itself. A comparison that projected null onto an empty key set would call
    // both of the false rows below "already current", and the reconcile pass would then leave a session
    // serving exactly the plugin set the user just switched away from.
    [InlineData(null, null, true)]
    [InlineData(new string[0], new string[0], true)]
    [InlineData(null, new string[0], false)]
    [InlineData(new string[0], null, false)]
    // Within a non-empty list the comparison is a structural SET comparison: order and duplicates carry
    // no meaning on the wire, and treating them as differences would make a session reconcile forever.
    [InlineData(new[] { "a", "b" }, new[] { "b", "a" }, true)]
    [InlineData(new[] { "a", "a", "b" }, new[] { "b", "a" }, true)]
    [InlineData(new[] { "a" }, new[] { "a", "b" }, false)]
    [InlineData(new[] { "a", "b" }, new[] { "a" }, false)]
    public void ReflectsPluginSelection_ComparesRequestedAgainstDesired_PreservingTheTriState(
        string[]? requested,
        string[]? desired,
        bool expected
    )
    {
        var session = SessionWithResolution(PluginRefs(requested));

        SandboxSessionRegistry.ReflectsPluginSelection(session, PluginRefs(desired)).Should().Be(expected);
    }

    [Fact]
    public void ReflectsPluginSelection_SessionWithoutAResolution_IsNeverCurrent_EvenAgainstNoSelection()
    {
        // Fail-closed: a gateway that reported no resolution block told us nothing about what this
        // session loaded, so it cannot be PROVEN current — not even against the "load everything"
        // selection it superficially resembles. Recreating a session that was in fact already correct
        // costs one create; leaving a stale one serving costs the user the setting they just changed.
        var session = new SandboxSession("ws-1", "sess-1", "ws", "/host", PluginResolution: null);

        SandboxSessionRegistry.ReflectsPluginSelection(session, desired: null).Should().BeFalse();
        SandboxSessionRegistry.ReflectsPluginSelection(session, PluginRefs(["a"])).Should().BeFalse();
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_Success_CreatesNewSandboxSession_LeavingOldSessionUntouched()
    {
        // "Prepare" step: the candidate is built BESIDE the live session. Until the swap commits, the
        // old session must still exist on the gateway — no DELETE may have been issued.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        var candidate = await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("official", "code-review")]),
            partition,
            CancellationToken.None
        );

        candidate.SessionId.Should().NotBe(partition.Session.SessionId);
        gateway.Requests.Count(r => r.Method == HttpMethod.Delete).Should().Be(0);
        registry.TryGetSessionById(partition.Session.SessionId, out _)
            .Should()
            .BeTrue("the live session must survive candidate creation untouched");
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_UsesCapturedCredential_AndPinsThePartitionWorkspaceId()
    {
        // Two silent-corruption modes live on this one line, and neither is observable from the returned
        // session id alone, so both are asserted on the wire:
        //   1. Re-deriving the credential (instead of using the captured one) creates the replacement
        //      under the PROCESS DEFAULT identity — the new session then belongs to the wrong app.
        //   2. Trusting the caller ref's Id instead of pinning the partition's would land the candidate
        //      on a different partition than the one being migrated.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        var candidate = await registry.CreatePluginSelectionCandidateAsync(
            // Deliberately WRONG id: the primitive must ignore it and pin the partition's.
            new WorkspaceRef("some-other-workspace", PluginSelection: [new SandboxPluginRef("official", "code-review")]),
            partition,
            CancellationToken.None
        );

        var candidateCreate = gateway.Requests.Last(r => r.Method == HttpMethod.Post);

        // Non-vacuity guard for the credential assertion below: if the default app id happened to be
        // "app-a", a regression that fell back to the default would still read as a pass.
        registry.DefaultCredential.AppId.Should().NotBe("app-a");
        candidateCreate.AppId.Should().Be("app-a", "the candidate must be created under the captured caller credential");

        candidate.WorkspaceId.Should().Be("ws-1", "the workspace id is pinned from the partition key, not the caller ref");

        // The updated selection has to actually reach the wire — the gateway fixes the plugin set at
        // create time, so a candidate created without it is indistinguishable from the session it replaces.
        candidateCreate.Body.Should().Contain("official").And.Contain("code-review");
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_GatewayCreateFails_ThrowsWithoutPartialState()
    {
        // A candidate that cannot be created must fail loudly and leave the live session in place —
        // a half-migrated registry is worse than a refused migration.
        await using var registry = CreateRegistryWithFakeGateway(out _, failCreateAfter: 1);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        var act = async () =>
            await registry.CreatePluginSelectionCandidateAsync(
                new WorkspaceRef("ws-1", PluginSelection: []),
                partition,
                CancellationToken.None
            );

        // The registry translates every non-success gateway create status into this type (carrying the
        // status); the SDK's raw SandboxException never escapes CreateSessionAsync.
        await act.Should().ThrowAsync<SandboxSessionUnavailableException>();
        registry.SnapshotPluginSelectionPartitions("ws-1").Should().ContainSingle();
        registry.TryGetSessionById(partition.Session.SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_UntrackedCredential_FallsBackToTheProcessDefaultAppId()
    {
        // SandboxCredential is a readonly record struct, so the obvious
        // `_sessionCredentials.TryGetValue(id, out var credential)` leaves a ZERO-VALUED struct on a
        // miss — which boxes into a NON-null `SandboxCredential?`. CreateSessionAsync's
        // `credential ?? _defaultCredential` fallback then never fires and the candidate is created
        // under a blank identity the real gateway rejects. Only the wire can tell the two apart.
        //
        // The miss is induced through the registry's own primitive rather than by hand-building a
        // partition: retire clears the per-session credential map but deliberately leaves the
        // `_sessions` creation slot published (slot removal is each caller's own policy), so the
        // partition stays snapshot-visible with nothing tracking its credential — the same shape as a
        // session created before credential tracking existed.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        var live = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1"),
            credential: CredentialFor("app-a")
        );
        await registry.RetirePluginSelectionSessionsAsync([live]);

        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        partition.Credential.Should().BeNull("an untracked session must snapshot as null, not as a zero-valued struct");

        _ = await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: []),
            partition,
            CancellationToken.None
        );

        // Non-vacuity: the original session was created under a NON-default app id, so "fell back to
        // the default" and "kept the captured credential" are distinguishable outcomes.
        registry.DefaultCredential.AppId.Should().NotBe("app-a");
        gateway
            .Requests.Last(r => r.Method == HttpMethod.Post)
            .AppId.Should()
            .Be(registry.DefaultCredential.AppId);
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_RefOmitsWorkspaceDirectory_KeepsThePartitionsOwnDirectory()
    {
        // The workspace directory is an "omit ⇒ fall back to global configuration" field, so an
        // orchestrator that builds a ref carrying only the new plugin selection would silently move the
        // replacement onto the DEFAULT directory — here the gateway root, since no workspace is
        // configured. The session would come up healthy, mounting the wrong tree.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projA"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        _ = await registry.CreatePluginSelectionCandidateAsync(
            // Plugin selection ONLY — the partial ref this defence exists for.
            new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("official", "code-review")]),
            partition,
            CancellationToken.None
        );

        ReadWorkspace(gateway.Requests.Last(r => r.Method == HttpMethod.Post).Body).Should().Be("projA");
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_RefOmitsMarketplaces_KeepsThePartitionsOwnMarketplaces()
    {
        // Same failure mode one field over, and the more dangerous half: falling back to the GLOBAL
        // marketplace default silently changes which plugins are even available to the replacement, so
        // a migration that only meant to narrow the selection can widen the catalogue behind it.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway, marketplaces: "official");
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1", "projA", ["superpowers", "corp"]));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        _ = await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("superpowers", "code-review")]),
            partition,
            CancellationToken.None
        );

        // Non-vacuous because the global default ("official") differs from the workspace's own scope:
        // a regression reads as ["official"], not as an empty or identical list.
        ReadMarketplaces(gateway.Requests.Last(r => r.Method == HttpMethod.Post).Body)
            .Should()
            .Equal("superpowers", "corp");
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_Cancelled_CreatesNothingAndLeavesTheLiveSessionServing()
    {
        // Cancellation lands mid-migration (the user navigated away, the request timed out) and must be
        // indistinguishable from never having started: no extra gateway session to orphan, and the
        // partition still resolving the session it resolved before.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var createsBefore = gateway.Requests.Count(r => r.Method == HttpMethod.Post);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
            await registry.CreatePluginSelectionCandidateAsync(
                new WorkspaceRef("ws-1", PluginSelection: []),
                partition,
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        gateway
            .Requests.Count(r => r.Method == HttpMethod.Post)
            .Should()
            .Be(createsBefore, "a cancelled candidate must not leave a gateway session nothing references");
        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId.Should().Be(partition.Session.SessionId);
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_CancelledAfterTheGatewayCreated_DestroysTheCandidateAndLeavesTheLiveSessionServing()
    {
        // The dangerous cancellation is NOT the one above. Cancelling before the create proves only that
        // nothing was made; the window that can actually leak is the one AFTER the gateway has allocated
        // a real container but BEFORE its secret is on disk — there the registry owns a remote session
        // that no cache slot, no partition and no retire list references. Dropping the rollback in
        // CreateSessionAsync leaves that container running forever, and every pre-cancelled test stays
        // green through it.
        //
        // That window is bounded by non-yielding statements, so it is unreachable from outside; the
        // registry's internal AfterGatewayCreateBeforeSecretPersistForTest hook is the narrowest seam
        // that lands a cancellation inside it deterministically.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        var live = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var createsBefore = gateway.Requests.Count(r => r.Method == HttpMethod.Post);
        using var cts = new CancellationTokenSource();
        string? candidateSessionId = null;
        registry.AfterGatewayCreateBeforeSecretPersistForTest = session =>
        {
            // Captured here because the create never returns it — the throw below is the only way out.
            candidateSessionId = session.SessionId;
            cts.Cancel();
            return Task.CompletedTask;
        };

        var act = async () =>
            await registry.CreatePluginSelectionCandidateAsync(
                new WorkspaceRef("ws-1", PluginSelection: []),
                partition,
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();

        // Separates this test from the pre-cancelled one: the gateway really did allocate a container,
        // so the assertions below are about tearing one down rather than never creating one.
        gateway
            .Requests.Count(r => r.Method == HttpMethod.Post)
            .Should()
            .Be(createsBefore + 1, "the cancellation must land AFTER the gateway create, not before it");
        candidateSessionId.Should().NotBeNull().And.NotBe(live.SessionId);

        gateway
            .Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Delete, "the orphaned candidate must be destroyed remotely")
            .Which.Path.Should()
            .Contain(candidateSessionId!, "and the DELETE must target the CANDIDATE, never the live session");
        registry
            .TryGetSessionById(candidateSessionId!, out _)
            .Should()
            .BeFalse("the half-built candidate must leave no per-session state resolvable");
        registry
            .TryGetSessionById(live.SessionId, out _)
            .Should()
            .BeTrue("rollback must not reach through to the session the candidate would have replaced");
        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId.Should().Be(live.SessionId, "the old session keeps serving a cancelled migration");
    }

    [Fact]
    public async Task AbortPluginSelectionCandidateAsync_DestroysTheUnpublishedCandidate_LeavingTheLivePartitionServing()
    {
        // Abort runs while another failure is already propagating, so it must be silent and complete:
        // DELETE the container AND drop the per-session state, or the failed migration leaks both.
        //
        // The candidate is a REAL unpublished one — created beside the live session by the primitive
        // itself — because the mistake worth catching is abort reaching through to the partition the
        // candidate was built for. The two sessions share a workspace, and only the candidate has no
        // cache slot; a teardown that keyed off the workspace instead of the session would tear down a
        // session the user is actively holding, and a test that aborts a PUBLISHED session (as this one
        // once did) cannot tell the two apart.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        var live = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var candidate = await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: []),
            partition,
            CancellationToken.None
        );

        var act = async () => await registry.AbortPluginSelectionCandidateAsync(candidate);

        await act.Should().NotThrowAsync();
        gateway
            .Requests.Should()
            .ContainSingle(r => r.Method == HttpMethod.Delete)
            .Which.Path.Should()
            .Contain(candidate.SessionId);
        registry
            .TryGetSessionById(candidate.SessionId, out _)
            .Should()
            .BeFalse("an aborted candidate must leave no per-session state behind");
        registry
            .TryGetSessionById(live.SessionId, out _)
            .Should()
            .BeTrue("abort must not reach through to the session the candidate would have replaced");
        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId.Should().Be(live.SessionId);
    }

    [Fact]
    public async Task SwapPluginSelectionSessions_RepublishesEntry_SoTheNextResolveHandsOutTheCandidate()
    {
        // The commit point. After the swap, the very next resolve of that partition must hand out the
        // candidate — with no gateway round trip, since the session already exists.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var newSession = partition.Session with { SessionId = "candidate-session" };
        var createsBeforeSwap = gateway.Requests.Count(r => r.Method == HttpMethod.Post);

        var uncommitted = registry.SwapPluginSelectionSessions([(partition, newSession)]);

        uncommitted.Should().BeEmpty("an uncontended swap commits every candidate");
        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId.Should().Be("candidate-session");
        gateway.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(createsBeforeSwap);
    }

    [Fact]
    public async Task SwapPluginSelectionSessions_SlotChangedSinceSnapshot_SkipsTheSwapAndReturnsTheCandidate()
    {
        // Candidate creation is seconds of gateway I/O, during which the slot can legitimately be
        // republished by someone else (most commonly the gateway-404 recreate path). An unconditional
        // write would drop THAT session on the floor: unreachable through the cache, absent from this
        // migration's retire list, and therefore never deleted on the gateway.
        //
        // The first swap below stands in for that concurrent republish — it leaves the slot holding an
        // entry the stale partition no longer witnesses.
        await using var registry = CreateRegistryWithFakeGateway(out _);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var stalePartition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var winner = stalePartition.Session with { SessionId = "winner-session" };
        var loser = stalePartition.Session with { SessionId = "loser-session" };
        registry.SwapPluginSelectionSessions([(stalePartition, winner)]);

        var uncommitted = registry.SwapPluginSelectionSessions([(stalePartition, loser)]);

        uncommitted.Select(s => s.SessionId).Should().Equal("loser-session");
        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId
            .Should()
            .Be("winner-session", "a swap whose witness is stale must not clobber the session that replaced it");
    }

    [Fact]
    public async Task RetirePluginSelectionSessionsAsync_DestroysThenEvicts_BestEffort_NeverThrows()
    {
        // Retire runs AFTER the swap already committed. A gateway that refuses the DELETE must not turn
        // a successful migration into a failure — but the local state must still be evicted, or the
        // registry keeps a superseded session resolvable forever.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway, failDelete: true);
        var oldSession = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        var act = async () => await registry.RetirePluginSelectionSessionsAsync([oldSession]);

        await act.Should().NotThrowAsync();
        gateway.Requests.Should().Contain(r => r.Method == HttpMethod.Delete);
        registry.TryGetSessionById(oldSession.SessionId, out _)
            .Should()
            .BeFalse("eviction must happen even when the gateway DELETE fails");
    }

    [Fact]
    public async Task RetirePluginSelectionSessionsAsync_SendsTheDeleteUnderTheSessionsOwnAppId()
    {
        // Pins the destroy-BEFORE-evict ORDER, which nothing else here can observe. The DELETE resolves
        // its credential from the per-session credential map that eviction clears, so reversing the two
        // statements sends the DELETE under the process-default app id — which the real gateway rejects,
        // leaking the very container retire exists to remove.
        await using var registry = CreateRegistryWithFakeGateway(out var gateway);
        var oldSession = await registry.GetOrCreateSessionAsync(
            new WorkspaceRef("ws-1"),
            credential: CredentialFor("app-a")
        );

        await registry.RetirePluginSelectionSessionsAsync([oldSession]);

        // Non-vacuity guard: a fallback to the default identity must be distinguishable from success.
        registry.DefaultCredential.AppId.Should().NotBe("app-a");
        gateway.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
        gateway.Requests.Single(r => r.Method == HttpMethod.Delete).AppId.Should().Be("app-a");
    }

    private static SandboxCredential CredentialFor(string appId) => new(appId, $"{appId}-key");

    /// <summary>
    /// Projects plugin NAMES onto refs, preserving the tri-state: a null array stays null rather than
    /// becoming an empty list. Test data that could not express "unset" separately from "empty" could
    /// not exercise the distinction at all.
    /// </summary>
    private static IReadOnlyList<SandboxPluginRef>? PluginRefs(string[]? plugins) =>
        plugins?.Select(plugin => new SandboxPluginRef("official", plugin)).ToList();

    /// <summary>A session the gateway answered with the given <c>requested</c> echo.</summary>
    private static SandboxSession SessionWithResolution(IReadOnlyList<SandboxPluginRef>? requested) =>
        new("ws-1", "sess-1", "ws", "/host", new SandboxPluginResolution(supported: true, requested));


    /// <summary>The gateway's <c>workspace</c> create field — the logical directory leaf being mounted.</summary>
    private static string? ReadWorkspace(string? createBody) =>
        JsonDocument.Parse(createBody!).RootElement.GetProperty("workspace").GetString();

    /// <summary>
    /// The gateway's <c>marketplaces</c> create field, or an empty list when the field was omitted
    /// (which is how "caller expressed no preference" reaches the wire).
    /// </summary>
    private static IReadOnlyList<string> ReadMarketplaces(string? createBody) =>
        JsonDocument.Parse(createBody!).RootElement.TryGetProperty("marketplaces", out var marketplaces)
        && marketplaces.ValueKind == JsonValueKind.Array
            ? [.. marketplaces.EnumerateArray().Select(m => m.GetString()!)]
            : [];

    /// <summary>
    /// Builds a registry over a fake gateway that hands out a fresh session id per create.
    /// <paramref name="failCreateAfter"/> makes every create past the first N return 500 (so a live
    /// session can be established before the candidate create fails), and <paramref name="failDelete"/>
    /// makes teardown fail. <paramref name="marketplaces"/> sets the GLOBAL default marketplace scope,
    /// which the per-workspace scope has to be distinguishable from.
    /// <paramref name="omitPluginResolution"/> answers creates the way a gateway too old to report
    /// resolution does.
    /// </summary>
    private static SandboxSessionRegistry CreateRegistryWithFakeGateway(
        out FakeGateway gateway,
        int failCreateAfter = int.MaxValue,
        bool failDelete = false,
        string? marketplaces = null,
        bool omitPluginResolution = false
    )
    {
        var fake = new FakeGateway(failCreateAfter, failDelete, omitPluginResolution);
        gateway = fake;

        var options = new SandboxGatewayOptions { BaseUrl = GatewayBaseUrl, Marketplaces = marketplaces };
        var lifetime = new SandboxGatewayLifetime(
            options,
            NullLogger<SandboxGatewayLifetime>.Instance,
            new HttpClient(new FakeGateway(int.MaxValue, failDelete: false))
        );

        return new SandboxSessionRegistry(
            lifetime,
            options,
            NullLogger<SandboxSessionRegistry>.Instance,
            new HttpClient(fake),
            new AuthOptions(),
            new SessionSecretStore(
                Path.Combine(Path.GetTempPath(), "lmagentinfra-test-secrets", Guid.NewGuid().ToString("N")),
                NullLogger<SessionSecretStore>.Instance
            )
        );
    }

    /// <summary>
    /// One recorded gateway call. The <paramref name="AppId"/> header, the request <paramref name="Path"/>
    /// and the request <paramref name="Body"/> are captured, not just the method: the identity a session
    /// is created and destroyed under, WHICH session a teardown targeted, and the plugin selection
    /// carried on a create are invariants that are otherwise invisible to a test — a fake that recorded
    /// only methods keeps their mutations green.
    /// </summary>
    private sealed record GatewayCall(HttpMethod Method, string Path, string? AppId, string? Body);

    /// <summary>
    /// Minimal in-memory sandbox gateway: records every call, answers creates with a unique session id,
    /// and can be told to fail creates or deletes to drive the failure paths.
    /// <para>
    /// Creates echo the request's <c>pluginSelection</c> back as <c>pluginResolution.requested</c>,
    /// which is what a real gateway does and what the registry compares a live session against. The
    /// echo is verbatim and TRI-STATE preserving: a request that carried no selection produces a
    /// response with no <c>requested</c> field, so "unset" never arrives as "empty".
    /// <paramref name="omitPluginResolution"/> drops the whole block instead, standing in for a
    /// gateway too old to report resolution at all.
    /// </para>
    /// </summary>
    private sealed class FakeGateway(int failCreateAfter, bool failDelete, bool omitPluginResolution = false)
        : HttpMessageHandler
    {
        private const string ResolutionPrefix = """
            , "pluginResolution": { "supported": true, "effective": [], "failed": []
            """;
        private const string RequestedPrefix = """
            , "requested":
            """;

        private int _creates;

        /// <summary>Signals one create having parked at the hold; consumed by <see cref="WaitForHeldCreatesAsync"/>.</summary>
        private readonly SemaphoreSlim _createsParked = new(0);

        /// <summary>Non-null while creates are held. Read/written through <see cref="Volatile"/>.</summary>
        private TaskCompletionSource? _createGate;

        /// <summary>Every request seen, in order.</summary>
        public List<GatewayCall> Requests { get; } = [];

        /// <summary>
        /// Parks every create from now on until <see cref="ReleaseCreates"/>. This is the seam that makes
        /// "a session creation is still in flight" a state a test can enter deterministically, instead of
        /// racing a sleep against it.
        /// </summary>
        public void HoldCreates() =>
            Volatile.Write(ref _createGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        /// <summary>Lets every parked create through, and stops holding subsequent ones.</summary>
        public void ReleaseCreates()
        {
            var gate = Volatile.Read(ref _createGate);
            Volatile.Write(ref _createGate, null);
            gate?.TrySetResult();
        }

        /// <summary>
        /// Completes once <paramref name="count"/> creates have reached the hold. Throws rather than
        /// hanging, so a test whose creates never arrive fails with the count it did see.
        /// </summary>
        public async Task WaitForHeldCreatesAsync(int count, CancellationToken cancellationToken = default)
        {
            for (var parked = 0; parked < count; parked++)
            {
                if (!await _createsParked.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false))
                {
                    throw new TimeoutException(
                        $"Only {parked} of {count} expected creates reached the gateway hold within 10s."
                    );
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var appId = request.Headers.TryGetValues(AppIdHeader, out var values) ? values.FirstOrDefault() : null;
            var body =
                request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            lock (Requests)
            {
                Requests.Add(
                    new GatewayCall(request.Method, request.RequestUri?.AbsolutePath ?? string.Empty, appId, body)
                );
            }

            if (request.Method == HttpMethod.Delete)
            {
                return new HttpResponseMessage(failDelete ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
            }

            if (request.Method != HttpMethod.Post)
            {
                // The gateway health probe.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            // Park AFTER recording, so a held create is visible in Requests as well as through the
            // semaphore — a test can then assert what the in-flight create asked for.
            if (Volatile.Read(ref _createGate) is { } gate)
            {
                _createsParked.Release();
                await gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var created = Interlocked.Increment(ref _creates);
            if (created > failCreateAfter)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var resolution = omitPluginResolution
                ? string.Empty
                : ResolutionPrefix + RequestedField(body) + " }";
            var responseBody = $$"""
                { "session_id": "sess-{{created}}", "container_id": "c-{{created}}",
                  "volumes": { "workspace": { "container_path": "/workspace", "read_only": false } }{{resolution}} }
                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _createsParked.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Renders the <c>requested</c> field of the echoed resolution from the create body, or nothing
        /// at all when the body carried no selection. Emitting <c>"requested": []</c> there would turn
        /// "load everything" into "load nothing" between the request and the response, which is the
        /// exact confusion the tri-state comparison exists to catch.
        /// </summary>
        private static string RequestedField(string? body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return string.Empty;
            }

            using var document = JsonDocument.Parse(body);
            return
                document.RootElement.TryGetProperty("pluginSelection", out var selection)
                && selection.ValueKind is not JsonValueKind.Null
                ? RequestedPrefix + " " + selection.GetRawText()
                : string.Empty;
        }
    }
}
