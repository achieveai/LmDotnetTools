using System.Collections.Concurrent;
using System.Net;
using System.Text;
using LmStreaming.Sample.Services;
using LmStreaming.Sample.Tests.TestDoubles;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Composed tests for the prepare-then-replace plugin-selection migration (spec Section 7, Task 15).
///
/// <para>
/// Nothing here is mocked at the seam under test: a REAL <see cref="SandboxSessionRegistry"/> runs
/// against an in-memory gateway, a REAL <see cref="FileWorkspaceStore"/> holds the revision, and a
/// REAL <see cref="WorkspaceCatalogCompatibilityService"/> validates the selection. That is
/// deliberate — the invariants this orchestrator exists to hold (no candidate on a stale request, no
/// leaked container on any failure path, old sessions still serving until the swap) live in the
/// INTERACTION between those three, so a suite that stubbed them would stay green through every one
/// of them. Only the two genuinely unmockable/untriggerable edges are substituted: the agent-run
/// probe (an interface precisely so this is possible) and, where a specific race must be forced, a
/// thin store decorator that fails or side-effects at the exact instant the race needs.
/// </para>
///
/// <para>
/// Assertions are written against the gateway's OBSERVED create/delete traffic rather than against
/// the orchestrator's return value, because "no candidate was created" and "every candidate was
/// deleted" are claims about the outside world that no return value can express.
/// </para>
/// </summary>
public class WorkspacePluginSelectionServiceTests
{
    private const string GatewayBaseUrl = "http://localhost:3000";
    private const string DefaultAppId = "default-app";

    /// <summary>The gateway header carrying the calling app's identity, stamped by the SDK client.</summary>
    private const string AppIdHeader = "X-Sbx-App-Id";

    /// <summary>The one plugin that is legal under the <c>official</c> marketplace in the stub catalog.</summary>
    internal static readonly PluginRef SelectedPlugin = new("official", "code-review");

    /// <summary>
    /// A plugin from the OTHER marketplace, so a second migration can name a selection that is both
    /// valid and unmistakably different from <see cref="SelectedPlugin"/>. Two migrations that asked for
    /// the same thing could not tell "left the newer sessions alone" from "recreated them identically".
    /// </summary>
    internal static readonly PluginRef OtherPlugin = new("extra", "beta");

    /// <summary>
    /// The service is a SINGLETON and the workspace id is caller-supplied, so a gate allocated for an
    /// id that does not exist is a permanent leak — gates are never removed and a
    /// <c>SemaphoreSlim</c> is never disposed.
    /// </summary>
    /// <remarks>
    /// This asserts the allocation count directly rather than any observable behaviour, and it has to:
    /// a request for an unknown workspace throws the same KeyNotFoundException from the same place
    /// whether or not it leaked a gate on the way out. There is no black-box symptom to assert until
    /// the process runs out of memory.
    /// </remarks>
    [Fact]
    public async Task UnknownWorkspaceIds_DoNotAllocateGates()
    {
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);

        // A real update allocates exactly one gate, for a workspace that exists.
        _ = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );
        h.Service.AllocatedWorkspaceGateCount.Should().Be(1);

        // Unique ids that name nothing. Each is rejected — but the rejection lives inside the gated
        // section, so an implementation that takes the gate first allocates on exactly this path.
        for (var i = 0; i < 25; i++)
        {
            var act = () =>
                h.Service.ApplyPluginSelectionUpdateAsync(
                    $"no-such-workspace-{i}",
                    Update(["official"], [SelectedPlugin], revision: 0)
                );

            _ = await act.Should().ThrowAsync<KeyNotFoundException>();
        }

        h.Service.AllocatedWorkspaceGateCount
            .Should()
            .Be(1, "only the workspace that actually exists may hold a gate");
    }

    /// <summary>
    /// A fault that is not the gateway's must not be reported as the gateway's. The controller maps
    /// <see cref="SandboxSessionReplacementFailedException"/> to 502, which is a claim ABOUT THE
    /// GATEWAY — so wrapping an InvalidOperationException from a bug in this service sends whoever
    /// reads the 502 to the wrong system, with the stack trace that names the real fault buried as an
    /// inner exception.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFailureDuringCandidateCreation_KeepsItsOwnIdentity()
    {
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        // One seeded session already used create #1, so the candidate is create #2.
        h.Gateway.CreateThrowsAfter = 1;
        h.Gateway.CreateThrows = new InvalidOperationException("a bug in the migration path");

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        var thrown = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be("a bug in the migration path");
    }

    /// <summary>
    /// The cleanup obligation is independent of the classification. Narrowing which exceptions get
    /// wrapped must not narrow which ones get cleaned up after — a candidate abandoned here is a live
    /// gateway container nothing references, for the life of the process.
    /// </summary>
    [Fact]
    public async Task AnUnexpectedFailureStillAbortsTheCandidatesAlreadyBuilt()
    {
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var originals = new[]
        {
            (await SeedSessionAsync(h, workspace, "app-a")).SessionId,
            (await SeedSessionAsync(h, workspace, "app-b")).SessionId,
        };

        // Two seeded sessions used creates #1-#2; let the first candidate (#3) be built, then break
        // the second with a fault that is not the gateway's.
        h.Gateway.CreateThrowsAfter = originals.Length + 1;
        h.Gateway.CreateThrows = new InvalidOperationException("boom");

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        _ = await act.Should().ThrowExactlyAsync<InvalidOperationException>();

        var built = h.Gateway.CreatedSessionIds.Except(originals).ToArray();
        built.Should().NotBeEmpty("the first candidate must have been created before the second failed");
        h.Gateway.DeletedSessionIds.Should().BeEquivalentTo(built, "every candidate built must be torn down");
        h.Gateway.DeletedSessionIds.Should().NotIntersectWith(originals, "the originals are still serving");
    }

    [Fact]
    public async Task StaleRevision_IsRejectedBeforeAnyCandidateIsCreated()
    {
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var session = await SeedSessionAsync(h, workspace, "app-a");

        // Revision 7 against a stored revision of 0: the request lost a race it never knew about.
        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 7)
            );

        var thrown = await act.Should().ThrowExactlyAsync<WorkspaceRevisionConflictException>();
        thrown.Which.ExpectedRevision.Should().Be(7);
        thrown.Which.ActualRevision.Should().Be(0);

        // The whole point of checking the revision BEFORE the snapshot: a doomed request must cost
        // zero gateway work. One create total is the session seeded above, so any candidate at all
        // shows up here — this is what fails if the check is moved after candidate creation.
        h.Gateway.CreateAttempts.Should().Be(1, "a stale request must create no candidate at all");
        h.Gateway.DeletedSessionIds.Should().BeEmpty();
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task ActiveRun_TimesOutWithoutChangingAnything()
    {
        await using var h = CreateHarness(idleWaitTimeout: TimeSpan.FromMilliseconds(150));
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var session = await SeedSessionAsync(h, workspace, "app-a");

        // A thread bound to the live session, and a probe that never reports it idle.
        h.Registry.RegisterThread(session.SessionId, "thread-1");
        h.Probe.Busy = true;

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        _ = await act.Should().ThrowExactlyAsync<SandboxSessionRestartTimeoutException>();

        // Non-vacuity: if GetThreads/probe wiring were broken the wait would fall straight through
        // and this test would "pass" for the wrong reason on a different assertion.
        h.Probe.Calls.Should().BeGreaterThan(0, "the wait must actually consult the run probe");
        h.Gateway.CreateAttempts.Should().Be(1, "the timeout happens before any candidate is created");
        h.Gateway.DeletedSessionIds.Should().BeEmpty();
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task CallerCancellation_IsReportedAsCancellation_NotAsARestartTimeout()
    {
        // A long idle timeout, so anything that surfaces here came from the caller's token and not
        // from the internal deadline. The two are separate exception types precisely so a client can
        // tell "I gave up" from "the sandbox was busy", and collapsing them would send a 503 for a
        // request the caller withdrew.
        await using var h = CreateHarness(idleWaitTimeout: TimeSpan.FromSeconds(30));
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var session = await SeedSessionAsync(h, workspace, "app-a");
        h.Registry.RegisterThread(session.SessionId, "thread-1");
        h.Probe.Busy = true;

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var thrown = await Record.ExceptionAsync(() =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0),
                cts.Token
            )
        );

        thrown
            .Should()
            .BeAssignableTo<OperationCanceledException>(
                "a caller who cancelled must not be told the sandbox timed out"
            );
        h.Gateway.CreateAttempts.Should().Be(1);
        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task CandidateFailure_AbortsEarlierCandidates_AndLeavesOldSessionsServing()
    {
        // Three live partitions, and a gateway that starts failing on the 5th create: creates 1-3 are
        // the originals, create 4 is the first candidate (succeeds) and create 5 is the second
        // (fails). That is the interesting shape — a failure with a sibling already built.
        await using var h = CreateHarness(failCreateAfter: 4);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var originals = new[]
        {
            (await SeedSessionAsync(h, workspace, "app-a")).SessionId,
            (await SeedSessionAsync(h, workspace, "app-b")).SessionId,
            (await SeedSessionAsync(h, workspace, "app-c")).SessionId,
        };

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        _ = await act.Should().ThrowExactlyAsync<SandboxSessionReplacementFailedException>();

        // Everything the gateway actually built beyond the originals must have been torn down, and
        // nothing else may have been. Expressed as sets rather than as "sess-4" so the assertion
        // survives any create retry the registry might do internally.
        var built = h.Gateway.CreatedSessionIds.Except(originals).ToArray();
        built.Should().NotBeEmpty("the first candidate must have been created before the second failed");
        h.Gateway
            .DeletedSessionIds.Should()
            .BeEquivalentTo(built, "every candidate built before the failure must be aborted");
        h.Gateway
            .DeletedSessionIds.Should()
            .NotIntersectWith(originals, "the originals are still serving traffic and must not be touched");

        foreach (var original in originals)
        {
            h.Registry.TryGetSessionById(original, out _).Should().BeTrue();
        }

        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task PersistenceFailure_AbortsEveryCandidate_AndRethrowsTheStoresOwnException()
    {
        var hooks = new StoreHooks { UpdateFailure = new WorkspaceRevisionConflictException("ws", 0, 1) };
        await using var h = CreateHarness(hooks: hooks);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var originals = new[]
        {
            (await SeedSessionAsync(h, workspace, "app-a")).SessionId,
            (await SeedSessionAsync(h, workspace, "app-b")).SessionId,
        };

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        // ThrowExactly, not Throw: the store's conflict must reach the controller as a conflict (409).
        // Wrapping it in SandboxSessionReplacementFailedException would turn a losing writer into a
        // 502 and tell the user the sandbox broke.
        _ = await act.Should().ThrowExactlyAsync<WorkspaceRevisionConflictException>();

        var built = h.Gateway.CreatedSessionIds.Except(originals).ToArray();
        built.Should().HaveCount(2, "both candidates are created before persistence is attempted");
        h.Gateway.DeletedSessionIds.Should().BeEquivalentTo(built);
        h.Gateway.DeletedSessionIds.Should().NotIntersectWith(originals);

        foreach (var original in originals)
        {
            h.Registry.TryGetSessionById(original, out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task RegistryDisposalInterruptingTheSwap_StillAttemptsToAbortEveryCandidate()
    {
        // The swap sits between the persisted commit and the candidates actually being published: by
        // the time it runs, the update has ALREADY committed, so a failure here cannot be reported as
        // the update failing to persist - the caller who sees the exception must still be told that
        // cleanup was attempted for every candidate, even though reaching the gateway through a
        // registry that has finished disposing is not something this service can guarantee.
        var hooks = new StoreHooks();
        await using var h = CreateHarness(hooks: hooks);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");
        hooks.AfterUpdate = () => h.Registry.DisposeAsync().AsTask();

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        _ = await act.Should().ThrowAsync<ObjectDisposedException>();

        // The SPECIFIC workspace, not merely "something was logged at Warning": the registry's own
        // best-effort teardown logs through its OWN CapturingLogger in this harness (see RegistryLogger
        // below), so this warning has to come from the SERVICE's own catch - the one place able to name
        // which workspace's swap failed.
        h.Logger
            .Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .Should()
            .ContainSingle(message => message.Contains(workspace.Id, StringComparison.Ordinal));

        // Proves AbortAllAsync actually attempted to tear the candidate down, not merely that the swap's
        // own catch logged and rethrew. By the time this runs the registry has ALREADY finished disposing
        // (the hook above awaits DisposeAsync to completion before the swap even attempts), which
        // unconditionally clears _sessionsById and disposes the shared HttpClient as part of the
        // registry's OWN teardown - so TryGetSessionById(candidate) throws ObjectDisposedException post-
        // disposal (SandboxSessionRegistry.cs:1992's ThrowIf) rather than returning false, and no gateway
        // DELETE is ever recorded regardless of whether AbortAllAsync runs at all; neither can discriminate
        // this mutation here. What DOES discriminate: DestroySessionAsync's own attempt to reach the gateway
        // through the now-disposed transport throws, and that failure is logged by the REGISTRY's logger
        // (captured separately as RegistryLogger, unlike the NullLogger a prior version of this harness
        // used) - a line that can only exist if AbortAllAsync actually invoked the teardown for this
        // candidate.
        var candidate = h.Gateway.CreatedSessionIds.Single(id => id != original.SessionId);
        h.RegistryLogger
            .Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .Should()
            .Contain(
                message => message.Contains(candidate, StringComparison.Ordinal),
                "AbortAllAsync must have actually attempted to destroy the candidate"
            );
    }

    [Fact]
    public async Task SuccessfulMigration_SwapsEveryPartition_AndRetiresTheOldSessions()
    {
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var credentialA = CredentialFor("app-a");
        var credentialB = CredentialFor("app-b");
        var originalA = await SeedSessionAsync(h, workspace, credentialA);
        var originalB = await SeedSessionAsync(h, workspace, credentialB);

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginSelection.Should().Equal(SelectedPlugin);
        updated.PluginsRevision.Should().Be(1, "an explicit selection change bumps the CAS token");
        (await h.FileStore.GetAsync(workspace.Id))!.PluginSelection.Should().Equal(SelectedPlugin);

        var originals = new[] { originalA.SessionId, originalB.SessionId };
        var candidates = h.Gateway.CreatedSessionIds.Except(originals).ToArray();
        candidates.Should().HaveCount(2, "one replacement per (workspace, app) partition");
        h.Gateway.DeletedSessionIds.Should().BeEquivalentTo(originals, "the superseded sessions are retired");

        // Every candidate must have been created with the NEW selection. Without this the migration
        // could "succeed" having recreated identical sessions.
        foreach (var body in h.Gateway.CreateBodies.Skip(originals.Length))
        {
            ReadPluginSelection(body).Should().Equal("official/code-review");
        }

        // The commit is the cache swap, so resolving again must hand out the candidates with no
        // further gateway traffic. Counting creates is what distinguishes "swapped" from "the old
        // entry was merely evicted and lazily recreated".
        var attemptsAfterMigration = h.Gateway.CreateAttempts;
        var resolvedA = await h.Registry.GetOrCreateSessionAsync(new WorkspaceRef(workspace.Id), credential: credentialA);
        var resolvedB = await h.Registry.GetOrCreateSessionAsync(new WorkspaceRef(workspace.Id), credential: credentialB);

        new[] { resolvedA.SessionId, resolvedB.SessionId }.Should().BeEquivalentTo(candidates);
        h.Gateway.CreateAttempts.Should().Be(attemptsAfterMigration, "the swapped entries must be served from cache");
    }

    [Fact]
    public async Task BackgroundPostCommit_ReturnsAtTheSwap_AndRetiresTheOldSessionOffTheRequest()
    {
        // The one test that ships the PRODUCTION scheduler. Every other test injects an inline seam to
        // keep its traffic assertions deterministic, which would otherwise leave the only modality a
        // deployed request ever runs — background dispatch — completely unexercised.
        await using var h = CreateHarness(backgroundPostCommit: true);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");

        // Retirement now blocks inside the gateway handler, so the post-commit phase cannot finish.
        // That is what makes this RED without the background default rather than merely green with it:
        // an inline scheduler awaits the held delete and the WaitAsync below fails the test.
        h.Gateway.HoldDeletes();

        var updated = await h
            .Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            )
            .WaitAsync(TimeSpan.FromSeconds(5));

        updated.PluginsRevision.Should().Be(1, "the request commits at the swap, ahead of the cleanup");
        h.Gateway.DeletedSessionIds.Should().BeEmpty("the held delete cannot have been served yet");

        h.Gateway.ReleaseDeletes();

        // The other half of the claim: dispatching the phase must not DROP it. Waiting on the observed
        // effect rather than on a duration — a sleep long enough to be reliable is paid on every run,
        // and a short one flakes on a loaded machine.
        await WaitForAsync(
            () => h.Gateway.DeletedSessionIds.Contains(original.SessionId),
            "the backgrounded phase must still retire the superseded session"
        );
    }

    [Fact]
    public async Task LostSwap_RetiresTheUncommittedCandidate()
    {
        // Force the race the compare-and-swap exists for: the partition's cache slot disappears while
        // the candidate is being prepared. The hook fires inside the store write — after the candidate
        // exists, before the swap — which is exactly the window the CAS guards.
        var hooks = new StoreHooks();
        await using var h = CreateHarness(hooks: hooks);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");
        hooks.BeforeUpdate = () => h.Registry.DestroyWorkspaceSessionAsync(workspace.Id);

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        // The migration still committed — the persisted half succeeded and a skipped swap is not a
        // failure, it is a session that someone else had already moved on from.
        updated.PluginsRevision.Should().Be(1);

        var candidate = h.Gateway.CreatedSessionIds.Single(id => id != original.SessionId);
        h.Gateway
            .DeletedSessionIds.Should()
            .Contain(
                candidate,
                "a candidate that lost its swap references nothing and leaks a container unless retired"
            );
        h.Registry.TryGetSessionById(candidate, out _).Should().BeFalse();
    }

    [Fact]
    public async Task LostSwap_ReconcilesTheWinnerToTheNewSelection()
    {
        // The other half of the CAS-loss story from LostSwap_RetiresTheUncommittedCandidate above:
        // retiring the losing candidate is only half the job when what beat it into the slot is a
        // session that does not reflect the new selection. Dropping the partition there leaves that
        // foreign winner published indefinitely - the store says the migration completed, and the
        // session actually served does not match it. The lost partition must be folded into the single
        // post-commit reconcile pass so the winner itself gets checked and, if it does not reflect the
        // selection, replaced.
        await using var h = CreateHarness();
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        h.Gateway.HoldCreatesFor("app-a");
        var updateTask = h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );
        await h.Gateway.WaitForHeldCreatesAsync(1);

        // A competing writer republishes the partition while this batch's own candidate is still being
        // built - the exact CAS-loss window LostSwap_RetiresTheUncommittedCandidate forces from the
        // other side. Materialized with a real resolve immediately after: SnapshotPluginSelectionPartitions
        // skips a Lazy whose value was never accessed, so this deliberately does NOT exercise the
        // narrower race InFlightCasLossWinner_IsStillReconciled_ViaTheBoundedSettleWait below covers -
        // a genuine competing resolve CAN leave the winner IsValueCreated but not yet completed (still
        // mid-creation) at the exact moment the reconcile pass re-snapshots, which is exactly what that
        // test forces instead of materializing here.
        var partition = h
            .Registry.SnapshotPluginSelectionPartitions(workspace.Id)
            .Single(candidate => candidate.Key.AppId == "app-a");
        var winner = partition.Session with { SessionId = "winner-session" };
        h.Registry.SwapPluginSelectionSessions([(partition, winner)]).Should().BeEmpty();
        _ = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-a")
        );

        h.Gateway.ReleaseCreatesFor("app-a");
        var updated = await updateTask.WaitAsync(TimeSpan.FromSeconds(30));

        updated.PluginsRevision.Should().Be(1, "the persisted half of the migration still committed");

        var resolved = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-a")
        );
        resolved
            .SessionId.Should()
            .NotBe(
                "winner-session",
                "a partition that lost its swap must still be reconciled to the new selection, "
                    + "not left on whoever happened to win it"
            );
    }

    [Fact]
    public async Task InFlightCasLossWinner_IsStillReconciled_ViaTheBoundedSettleWait()
    {
        // Blocker 1 (adversarial review of PR #416): the reconcile pass's re-snapshot used to be the
        // SYNCHRONOUS, zero-budget SnapshotPluginSelectionPartitions, which - per its own guard -
        // cannot see a competing writer's candidate that just WON the compare-and-swap and is still
        // mid-creation: the exact interval that caused THIS migration's own candidate to lose its swap
        // in the first place. Unlike LostSwap_ReconcilesTheWinnerToTheNewSelection above, the winner
        // here is NEVER explicitly materialized - it is left genuinely in flight (IsValueCreated but not
        // yet completed), held at the gateway, so only a snapshot that can WAIT for it will ever see it.
        Func<Task>? deferred = null;
        var hooks = new StoreHooks();
        await using var h = CreateHarness(
            hooks: hooks,
            settleBudget: TimeSpan.FromSeconds(2),
            postCommitScheduler: work =>
            {
                deferred = work;
                return Task.CompletedTask;
            }
        );
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        Task<SandboxSession>? competingWinner = null;
        hooks.BeforeUpdate = async () =>
        {
            // Lands in the exact window SwapPluginSelectionSessions needs: after this migration's own
            // candidate exists (so its swap can be raced), before the swap runs. Destroying the slot and
            // starting a REAL, held creation for it - rather than SwapPluginSelectionSessions's synthetic
            // Task.FromResult winner used above - is what keeps the winner's Lazy genuinely
            // IsValueCreated but not yet completed, instead of never-accessed (invisible to every
            // snapshot, sync or bounded) or immediately complete (this suite's existing, narrower
            // coverage above).
            await h.Registry.DestroyWorkspaceSessionAsync(workspace.Id);
            h.Gateway.HoldCreatesFor("app-a");
            competingWinner = h.Registry.GetOrCreateSessionAsync(
                new WorkspaceRef(workspace.Id),
                credential: CredentialFor("app-a")
            );
            await h.Gateway.WaitForHeldCreatesAsync(1);
        };

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1, "the persisted half of the migration still committed");
        deferred.Should().NotBeNull("a migration whose candidate lost its swap owes a post-commit phase");

        var postCommit = Task.Run(deferred!);
        // The winner is still held at the gateway when the pass starts; releasing it here - well
        // within the 2s settle budget above - is what the bounded wait exists to catch. Under the old
        // synchronous re-snapshot this release could never land in time: that call does not wait at
        // all, so it would have judged the winner absent no matter when this release ran.
        await Task.Delay(100);
        h.Gateway.ReleaseCreatesFor("app-a");
        await postCommit.WaitAsync(TimeSpan.FromSeconds(10));

        var winnerSession = await competingWinner!.WaitAsync(TimeSpan.FromSeconds(10));

        var resolved = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-a")
        );
        resolved
            .SessionId.Should()
            .NotBe(
                winnerSession.SessionId,
                "the winner did not reflect the new selection and the bounded settle wait must have "
                    + "given the reconcile pass a chance to see it and replace it, instead of the pass "
                    + "missing it entirely because it was still mid-creation at snapshot time"
            );
    }

    [Fact]
    public async Task EmptyWorkspaceMarketplaces_CreateCandidatesUnderTheConfiguredDefault()
    {
        // Task #10's rule, at the layer that acts on it: a workspace naming no marketplaces means "no
        // preference", which resolves to the configured default. If validation resolved it but session
        // creation did not, the selection would be checked against `official` and then created against
        // nothing — accepted at the API and broken in the container.
        await using var h = CreateHarness(configuredMarketplaces: "official");
        var workspace = await SeedWorkspaceAsync(h, []);
        var original = await SeedSessionAsync(h, workspace, "app-a");

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update([], [SelectedPlugin], revision: 0)
        );

        updated.PluginSelection.Should().Equal(SelectedPlugin);

        var candidateBody = h.Gateway.CreateBodies[^1];
        // Bound to a local: the params overload of Equal would read the reason as a second
        // expected element.
        IReadOnlyList<string> expectedMarketplaces = ["official"];
        ReadMarketplaces(candidateBody)
            .Should()
            .Equal(
                expectedMarketplaces,
                "an empty workspace list must resolve to the configured default, not to none"
            );
        ReadPluginSelection(candidateBody).Should().Equal("official/code-review");
        h.Gateway.DeletedSessionIds.Should().Equal(original.SessionId);
    }

    [Fact]
    public async Task EmptyWorkspaceMarketplaces_StillRejectPluginsOutsideTheConfiguredDefault()
    {
        // The other half of the same rule. Without it, "empty means fall back" would be indistinguishable
        // from "empty means anything goes".
        await using var h = CreateHarness(configuredMarketplaces: "official");
        var workspace = await SeedWorkspaceAsync(h, []);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update([], [new PluginRef("extra", "beta")], revision: 0)
            );

        var thrown = await act.Should().ThrowExactlyAsync<UnsupportedWorkspacePluginsException>();
        thrown.Which.UnsupportedPlugins.Should().Equal(new PluginRef("extra", "beta"));
        h.Gateway.CreateAttempts.Should().Be(1, "validation runs before any candidate is created");
        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task GatewayWithoutPluginFiltering_IsRejectedBeforeAnyCandidateIsCreated()
    {
        // Fail-closed: an unknown capability is not permission.
        await using var h = CreateHarness(pluginFiltering: null);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                workspace.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        _ = await act.Should().ThrowExactlyAsync<GatewayPluginFilteringUnsupportedException>();
        h.Gateway.CreateAttempts.Should().Be(1);
        await AssertWorkspaceUnchangedAsync(h, workspace.Id);
    }

    [Fact]
    public async Task UnknownWorkspace_IsReportedAsNotFound()
    {
        await using var h = CreateHarness();

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync("missing", Update(["official"], [SelectedPlugin], revision: 0));

        _ = await act.Should().ThrowExactlyAsync<KeyNotFoundException>();
        h.Gateway.CreateAttempts.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // System-defined workspaces are immutable, and that rejection outranks every later failure mode
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SystemDefinedWorkspace_IsRejected_WithNoCreateOrDeleteSideEffects()
    {
        await using var h = CreateHarness();
        var systemDefined = await SystemDefinedWorkspaceAsync(h);
        var session = await SeedSessionAsync(h, systemDefined, "app-a");

        // Everything up to here is arrangement. From this marker on, a doomed request must cost the
        // gateway nothing at all — not one container built, not one torn down.
        var checkpoint = h.Gateway.CallCount;

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                systemDefined.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        // ThrowExactly, and on the message too: WorkspacesController maps a BARE
        // InvalidOperationException to 400 in its trailing catch, so both the type and the text are the
        // wire contract. A subclass, or a reworded message, silently changes what the client sees.
        var thrown = await act.Should().ThrowExactlyAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Be($"Cannot update system-defined workspace '{systemDefined.Id}'.");

        h.Gateway
            .CallsSince(checkpoint)
            .Should()
            .BeEmpty("an immutable workspace must be rejected before a single gateway session is touched");
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
        await AssertWorkspaceUnchangedAsync(h, systemDefined.Id);
    }

    [Fact]
    public async Task SystemDefinedWorkspace_WithARunInProgress_IsStillRejectedAsImmutable_NotAsARestartTimeout()
    {
        // An idle-wait window far too short for a permanently busy run to clear: reaching the wait at
        // all converts this request's 400 into a 503 restart timeout. The status code a client sees for
        // "this workspace can never be edited" must not depend on whether someone happened to be
        // chatting at the time.
        await using var h = CreateHarness(idleWaitTimeout: TimeSpan.FromMilliseconds(50));
        var systemDefined = await SystemDefinedWorkspaceAsync(h);
        var session = await SeedSessionAsync(h, systemDefined, "app-a");

        h.Registry.RegisterThread(session.SessionId, "thread-1");
        h.Probe.SetBusyThreads("thread-1");

        var checkpoint = h.Gateway.CallCount;

        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                systemDefined.Id,
                Update(["official"], [SelectedPlugin], revision: 0)
            );

        var thrown = await act.Should()
            .ThrowExactlyAsync<InvalidOperationException>(
                "the immutability check runs before the idle wait, so a busy run cannot change the failure"
            );
        thrown.Which.Message.Should().Be($"Cannot update system-defined workspace '{systemDefined.Id}'.");

        // The direct proof of the ordering, rather than an inference from the exception type: the wait
        // is the only thing that consults the probe, so a probe that was never asked is a wait that
        // never ran.
        h.Probe.Calls.Should().Be(0, "the idle wait must never be reached for an immutable workspace");
        h.Probe.ObservedThreads.Should().BeEmpty();
        h.Gateway.CallsSince(checkpoint).Should().BeEmpty();
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task SystemDefinedWorkspace_WithAStaleRevision_IsStillRejectedAsImmutable_NotAsAConflict()
    {
        await using var h = CreateHarness();
        var systemDefined = await SystemDefinedWorkspaceAsync(h);
        var session = await SeedSessionAsync(h, systemDefined, "app-a");

        var checkpoint = h.Gateway.CallCount;

        // Revision 7 against the seeded workspace's 0 — a request that is BOTH stale and aimed at an
        // immutable workspace. Only one of those can be reported.
        var act = () =>
            h.Service.ApplyPluginSelectionUpdateAsync(
                systemDefined.Id,
                Update(["official"], [SelectedPlugin], revision: 7)
            );

        // ThrowExactly is the whole assertion: WorkspaceRevisionConflictException would surface as 409
        // ("retry with the current revision"), which is advice that can never work here. The permanent
        // 400 has to win over the retryable 409.
        var thrown = await act.Should()
            .ThrowExactlyAsync<InvalidOperationException>(
                "an immutable workspace is a permanent 400, never a retryable 409"
            );
        thrown.Which.Message.Should().Be($"Cannot update system-defined workspace '{systemDefined.Id}'.");

        h.Gateway.CallsSince(checkpoint).Should().BeEmpty();
        h.Registry.TryGetSessionById(session.SessionId, out _).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // The post-commit phase: the retirement grace, the single reconcile pass, and the cancellation
    // contract that keeps both of them running
    //
    // Everything below happens AFTER the workspace gate is released and — in production — after the
    // response has already been written. None of it can fail the request, which is exactly why a
    // defect here is silent: a container that is never reclaimed, or a session still serving the
    // plugin set the user just switched away from, with no error anywhere. Gateway traffic and the
    // service's own warnings are the only witnesses these tests have, and every wait below is on an
    // observed effect rather than on a duration.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RetirementGrace_HoldsABusySupersededSession_ThenRetiresItOnceTheRunFinishes()
    {
        // The BACKGROUND scheduler is mandatory here, not incidental. An inline one runs the whole
        // grace inside the awaited call, so by the time a test could look, the wait is already over and
        // "held while busy" and "retired immediately" are indistinguishable. Only a concurrent phase
        // leaves a window in which the first half of this claim is observable at all.
        var hooks = new StoreHooks();
        var postCommit = Task.CompletedTask;
        await using var h = CreateHarness(
            hooks: hooks,
            // Comfortably longer than the observation below, so nothing here depends on the grace
            // expiring — that is the NEXT test's subject, and letting it leak into this one would make
            // a retire-on-expiry bug read as a pass.
            retirementGrace: TimeSpan.FromSeconds(30),
            postCommitScheduler: work =>
            {
                postCommit = Task.Run(work);
                return Task.CompletedTask;
            }
        );
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");

        // The run starts INSIDE the persist window — after the pre-commit idle wait has already passed.
        // That is the only shape the grace exists for: a run that was busy any earlier would have been
        // caught by WaitForIdleAsync and this migration would never have committed at all.
        hooks.BeforeUpdate = () =>
        {
            h.Registry.RegisterThread(original.SessionId, "thread-1");
            h.Probe.SetBusyThreads("thread-1");
            return Task.CompletedTask;
        };

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1, "the request commits at the swap, ahead of the grace");

        // Half one. The wait is on the grace loop's OWN progress — three further probe consultations —
        // and NOT on a duration. The delete is part of the exit condition too, so an implementation
        // that retires immediately trips the assertion in milliseconds instead of after the deadline.
        var probeCallsAtCommit = h.Probe.Calls;
        await WaitUntilAsync(() =>
            h.Probe.Calls >= probeCallsAtCommit + 3 || h.Gateway.DeletedSessionIds.Contains(original.SessionId)
        );

        h.Probe.Calls.Should()
            .BeGreaterThan(
                probeCallsAtCommit,
                "the grace must actually be polling — otherwise the assertion below holds vacuously, "
                    + "for a post-commit phase that never started"
            );
        h.Gateway
            .DeletedSessionIds.Should()
            .NotContain(
                original.SessionId,
                "a superseded session with a live run must not be destroyed under it while the grace holds"
            );

        // Half two, and the half that stops "never retires at all" from satisfying half one: the grace
        // is a delay, not a reprieve.
        h.Probe.SetBusyThreads();
        await postCommit.WaitAsync(TimeSpan.FromSeconds(30));

        h.Gateway
            .DeletedSessionIds.Should()
            .Contain(original.SessionId, "once the run finishes the superseded session must be retired");
    }

    [Fact]
    public async Task RetirementGraceExpiry_RetiresTheStillBusySessionAnyway_AndNamesItInTheWarning()
    {
        // The honest residual. An unbounded grace turns "a run that never ends" into "a container that
        // is never reclaimed", so the session goes anyway — and because that will fail a live run, the
        // warning has to identify WHICH session, or the only trace of the failure is unattributable.
        var hooks = new StoreHooks();
        await using var h = CreateHarness(hooks: hooks, retirementGrace: TimeSpan.FromMilliseconds(50));
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");

        // Busy from the persist window onward and never released, so the loop can only exit on expiry.
        hooks.BeforeUpdate = () =>
        {
            h.Registry.RegisterThread(original.SessionId, "thread-1");
            h.Probe.SetBusyThreads("thread-1");
            return Task.CompletedTask;
        };

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1);
        h.Gateway
            .DeletedSessionIds.Should()
            .Contain(original.SessionId, "an expired grace must still reclaim the container");

        // The SPECIFIC id, not merely "something was logged at Warning": a message that reports an
        // expiry without naming the session leaves an operator with a failed run and nothing to tie it
        // to, which is the same position as no log at all.
        h.Logger
            .Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .Should()
            .ContainSingle(message => message.Contains(original.SessionId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PostCommitCleanup_IgnoresTheCallersCancellation_SoTheSupersededContainerIsStillDestroyed()
    {
        // The cleanup deliberately runs under CancellationToken.None. Observing the caller's token
        // instead would let a client that disconnects the instant its update commits skip the gateway
        // DELETE — and since the update HAS committed, nothing retries and nothing reports it: the
        // container leaks for the life of the process. The caller's own disconnect must not be able to
        // reach past the commit point.
        var hooks = new StoreHooks();
        await using var h = CreateHarness(hooks: hooks, retirementGrace: TimeSpan.FromMilliseconds(50));
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        var original = await SeedSessionAsync(h, workspace, "app-a");
        using var cts = new CancellationTokenSource();

        // AFTER the store update, so the commit is unambiguously done and the only work left is the
        // cleanup. A run is held live at the same instant on purpose: the caller token can only be
        // OBSERVED where the cleanup actually awaits, which is the retirement grace's poll delay, and a
        // cancellation landing anywhere the code never awaits proves nothing about this contract.
        hooks.AfterUpdate = () =>
        {
            h.Registry.RegisterThread(original.SessionId, "thread-1");
            h.Probe.SetBusyThreads("thread-1");
            cts.Cancel();
            return Task.CompletedTask;
        };

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0),
            cts.Token
        );

        updated.PluginsRevision.Should().Be(1, "the commit precedes the cancellation and stands");
        h.Probe
            .Calls.Should()
            .BeGreaterThan(0, "the grace must have reached the poll the caller's token would have cancelled");
        h.Gateway
            .DeletedSessionIds.Should()
            .Contain(
                original.SessionId,
                "a caller who withdrew after the commit must not be able to leak the superseded container"
            );
    }

    [Fact]
    public async Task ReconcileLosingItsSwap_RetiresItsOwnCandidate_AndLeavesTheWinnerPublished()
    {
        // The reconcile pass runs its own compare-and-swap, and it can lose one just as the batch can.
        // On a loss it owns a live gateway session that nothing references; retiring the WINNER instead
        // would destroy a session the registry is actively handing out. The window is between the
        // reconcile's create and its swap — deliberately NOT the batch's persist window, which
        // StoreHooks.BeforeUpdate reaches: a hook there would re-test the main swap and stay green
        // through anything wrong with this path.
        Func<Task>? deferred = null;
        await using var h = CreateHarness(
            settleBudget: TimeSpan.Zero,
            postCommitScheduler: work =>
            {
                deferred = work;
                return Task.CompletedTask;
            }
        );
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        // app-b's creation is still in flight when the (zero) settle budget expires, so it misses the
        // batch entirely and is owed the single reconcile pass.
        h.Gateway.HoldCreatesFor("app-b");
        var lateCreate = SeedSessionAsync(h, workspace, "app-b");
        await h.Gateway.WaitForHeldCreatesAsync(1);

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1);
        deferred.Should().NotBeNull("a migration with an unsettled partition owes a post-commit phase");

        h.Gateway.ReleaseCreatesFor("app-b");
        var lateSession = await lateCreate;

        // Park the RECONCILE's own candidate create. It is the only create left, so this hold lands
        // exactly in the create-to-swap window and nowhere else.
        h.Gateway.HoldCreatesFor("app-b");
        var postCommit = Task.Run(deferred!);
        await h.Gateway.WaitForHeldCreatesAsync(1);

        // Another writer republishes the partition while the candidate is being built. Going through
        // the registry's own primitive is what makes the reconcile's witness genuinely stale, in the
        // same way the gateway-404 recreate path does in production.
        var partition = h
            .Registry.SnapshotPluginSelectionPartitions(workspace.Id)
            .Single(candidate => candidate.Key.AppId == "app-b");
        var winner = partition.Session with { SessionId = "winner-session" };
        h.Registry.SwapPluginSelectionSessions([(partition, winner)]).Should().BeEmpty();

        h.Gateway.ReleaseCreatesFor("app-b");
        await postCommit.WaitAsync(TimeSpan.FromSeconds(30));

        var reconcileCandidate = h.Gateway.CreatedSessionIds[^1];
        reconcileCandidate.Should().NotBe(lateSession.SessionId, "the reconcile pass built a session of its own");
        h.Gateway
            .DeletedSessionIds.Should()
            .Contain(
                reconcileCandidate,
                "a candidate that lost its swap references nothing and leaks a container unless retired"
            );
        h.Gateway
            .DeletedSessionIds.Should()
            .NotContain(
                lateSession.SessionId,
                "the winner's predecessor belongs to whoever republished the slot, not to this pass"
            );

        var resolved = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-b")
        );
        resolved
            .SessionId.Should()
            .Be("winner-session", "a lost swap must leave the published session exactly where it found it");
    }

    [Fact]
    public async Task ReconcilePass_LeavesANewerMigrationsSessionsAlone()
    {
        // This pass runs after its own migration released the workspace gate, so a LATER migration can
        // have taken the gate, committed a newer selection and swapped in newer sessions by the time it
        // starts. Those sessions reflect the newer selection, so measuring them against this pass's
        // older one judges them stale — and its compare-and-swap would win, leaving the store on the new
        // selection and the live session on the old one with nothing to self-heal it.
        var hooks = new StoreHooks();
        Func<Task>? deferred = null;
        await using var h = CreateHarness(
            hooks: hooks,
            settleBudget: TimeSpan.Zero,
            postCommitScheduler: work =>
            {
                // Defers only the FIRST phase. The second migration's own phase runs inline, so its swap
                // has completed before the deferred pass is released — which is precisely the ORDERING
                // this test exists to pin, rather than a race it hopes to win.
                if (deferred is null)
                {
                    deferred = work;
                    return Task.CompletedTask;
                }

                return work();
            }
        );
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        h.Gateway.HoldCreatesFor("app-b");
        var lateCreate = SeedSessionAsync(h, workspace, "app-b");
        await h.Gateway.WaitForHeldCreatesAsync(1);

        var first = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        first.PluginsRevision.Should().Be(1);
        deferred.Should().NotBeNull("the first migration must have owed a reconcile for app-b");

        h.Gateway.ReleaseCreatesFor("app-b");
        _ = await lateCreate;

        // A second, entirely legitimate migration takes the gate and commits a DIFFERENT selection,
        // swapping in its own session for the very partition the first migration still owes.
        var second = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["extra"], [OtherPlugin], revision: 1)
        );

        second.PluginsRevision.Should().Be(2);

        var currentB = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-b")
        );
        var checkpoint = h.Gateway.CallCount;
        var readsBeforeReconcile = hooks.GetCalls;

        await Task.Run(deferred!).WaitAsync(TimeSpan.FromSeconds(30));

        // The positive witness that the guard was REACHED, not merely skipped past. Without it,
        // "nothing was built" is equally consistent with a pass that never ran — an empty unsettled
        // list, a phase that threw on an earlier stage — and every assertion below would hold
        // vacuously.
        hooks
            .GetCalls.Should()
            .BeGreaterThan(readsBeforeReconcile, "the reconcile pass must re-read the committed revision");
        h.Gateway
            .CallsSince(checkpoint)
            .Where(call => call.Kind == GatewayCallKind.Create)
            .Should()
            .BeEmpty("a superseded migration must build no replacement for a partition a newer one already owns");
        h.Gateway
            .DeletedSessionIds.Should()
            .NotContain(currentB.SessionId, "the newer migration's session must survive the older pass");

        var resolved = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-b")
        );
        resolved.SessionId.Should().Be(currentB.SessionId, "and must still be the one the partition resolves to");
    }

    [Fact]
    public async Task NeverSettledPartition_IsNamedInTheResidualWarning()
    {
        // A creation that was in flight when the settle budget expired and is STILL in flight at the
        // single reconcile pass will never be migrated by this update at all. There is no second pass,
        // so this is the last moment the divergence can be named: after it, the store says one thing,
        // that session serves another, and nothing anywhere produces an error.
        var logger = new CapturingLogger<WorkspacePluginSelectionService>();
        await using var h = CreateHarness(settleBudget: TimeSpan.Zero, logger: logger);
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        // Held across the WHOLE call, so it misses both the budget and the pass.
        h.Gateway.HoldCreatesFor("app-b");
        var wedged = SeedSessionAsync(h, workspace, "app-b");
        await h.Gateway.WaitForHeldCreatesAsync(1);

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1, "a wedged partition must not fail an otherwise valid update");

        // The partition KEY, not a count: "1 partition was left unreconciled" tells an operator that
        // something is wrong and nothing about where to look.
        logger
            .Entries.Where(entry => entry.Level == LogLevel.Warning)
            .Select(entry => entry.Message)
            .Should()
            .ContainSingle(message => message.Contains($"{workspace.Id}/app-b", StringComparison.Ordinal));

        h.Gateway.ReleaseCreatesFor("app-b");
        _ = await wedged;
    }

    [Fact]
    public async Task OverBudgetPartition_IsMigratedByTheReconcilePass_AndItsOldSessionRetired()
    {
        // The pass's whole reason to exist: a partition that settled just too late for the batch is
        // picked up afterwards and migrated properly — built, swapped, and its predecessor destroyed.
        // Asserting only the final resolved session would pass for a pass that never ran and a slot
        // that merely still held something plausible, so the CREATE and the DELETE are asserted as
        // events, at the point in the traffic log where the pass ran.
        Func<Task>? deferred = null;
        await using var h = CreateHarness(
            settleBudget: TimeSpan.Zero,
            postCommitScheduler: work =>
            {
                deferred = work;
                return Task.CompletedTask;
            }
        );
        var workspace = await SeedWorkspaceAsync(h, ["official"]);
        _ = await SeedSessionAsync(h, workspace, "app-a");

        h.Gateway.HoldCreatesFor("app-b");
        var lateCreate = SeedSessionAsync(h, workspace, "app-b");
        await h.Gateway.WaitForHeldCreatesAsync(1);

        var updated = await h.Service.ApplyPluginSelectionUpdateAsync(
            workspace.Id,
            Update(["official"], [SelectedPlugin], revision: 0)
        );

        updated.PluginsRevision.Should().Be(1);
        deferred.Should().NotBeNull();

        // Settles AFTER the commit — inside the window the pass covers.
        h.Gateway.ReleaseCreatesFor("app-b");
        var lateSession = await lateCreate;

        var createsBeforeReconcile = h.Gateway.CreatedSessionIds.Count;
        var checkpoint = h.Gateway.CallCount;

        await Task.Run(deferred!).WaitAsync(TimeSpan.FromSeconds(30));

        var built = h.Gateway.CreatedSessionIds.Skip(createsBeforeReconcile).ToArray();
        built.Should()
            .ContainSingle("the pass must build exactly one replacement for the partition that missed the batch");
        h.Gateway
            .CallsSince(checkpoint)
            .Should()
            .Contain(
                call => call.Kind == GatewayCallKind.Delete && call.SessionId == lateSession.SessionId,
                "the session that was serving the OLD selection must actually be destroyed, not merely unpublished"
            );

        // Without this the pass could "succeed" having recreated an identical session, which is the one
        // outcome indistinguishable from never having reconciled at all.
        ReadPluginSelection(h.Gateway.CreateBodies[^1]).Should().Equal("official/code-review");

        var resolved = await h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id),
            credential: CredentialFor("app-b")
        );
        resolved.SessionId.Should().Be(built.Single(), "and the partition must now resolve to the reconciled session");
    }

    // ---------------------------------------------------------------------------------------------
    // Harness
    // ---------------------------------------------------------------------------------------------

    /// <summary>The invariant every failure path shares: the persisted workspace never moved.</summary>
    internal static async Task AssertWorkspaceUnchangedAsync(Harness h, string workspaceId)
    {
        var stored = await h.FileStore.GetAsync(workspaceId);
        stored!.PluginSelection.Should().BeNull("a failed migration must not persist the selection");
        stored.PluginsRevision.Should().Be(0, "a failed migration must not consume the revision");
    }

    internal static WorkspaceUpdate Update(
        IReadOnlyList<string> marketplaces,
        IReadOnlyList<PluginRef>? selection,
        int? revision
    ) =>
        new()
        {
            Marketplaces = marketplaces,
            PluginSelection = new Optional<IReadOnlyList<PluginRef>?>(selection),
            PluginsRevision = revision,
        };

    internal static SandboxCredential CredentialFor(string appId) => new(appId, $"{appId}-key");

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or a five-second deadline passes, then asserts
    /// it — so a failure reports the condition that never became true instead of an opaque timeout.
    /// Used only where the thing being awaited is another thread finishing, which has no duration a
    /// test is entitled to name.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, string because)
    {
        await WaitUntilAsync(condition);

        condition().Should().BeTrue(because);
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it holds or a five-second deadline passes, and asserts
    /// NOTHING. For the shape <see cref="WaitForAsync"/> cannot express: a claim that something must NOT
    /// have happened yet, where the condition being waited on is the OTHER side's progress rather than
    /// the outcome being asserted. Callers pass a condition that also becomes true on the regression, so
    /// a broken implementation fails immediately instead of burning the whole deadline first.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    internal static Task<Workspace> SeedWorkspaceAsync(Harness h, IReadOnlyList<string> marketplaces) =>
        h.FileStore.CreateAsync(new WorkspaceCreate { Name = "Proj", Marketplaces = marketplaces });

    /// <summary>
    /// The store's seeded, immutable workspace — the subject of every system-defined test. Read off
    /// <see cref="IWorkspaceStore.GetAllAsync"/> rather than hard-coding the id, so these tests keep
    /// naming whatever the store actually considers system-defined.
    /// </summary>
    internal static async Task<Workspace> SystemDefinedWorkspaceAsync(Harness h) =>
        (await h.FileStore.GetAllAsync()).Single(w => w.IsSystemDefined);

    internal static Task<SandboxSession> SeedSessionAsync(Harness h, Workspace workspace, string appId) =>
        SeedSessionAsync(h, workspace, CredentialFor(appId));

    internal static Task<SandboxSession> SeedSessionAsync(
        Harness h,
        Workspace workspace,
        SandboxCredential credential
    ) =>
        h.Registry.GetOrCreateSessionAsync(
            new WorkspaceRef(workspace.Id, workspace.DirectoryRelPath, workspace.Marketplaces),
            credential: credential
        );

    /// <summary>The <c>marketplaces</c> array on a sandbox-create body, empty when the field is absent.</summary>
    private static IReadOnlyList<string> ReadMarketplaces(string createBody) =>
        JsonDocument.Parse(createBody).RootElement.TryGetProperty("marketplaces", out var marketplaces)
        && marketplaces.ValueKind == JsonValueKind.Array
            ? [.. marketplaces.EnumerateArray().Select(m => m.GetString()!)]
            : [];

    /// <summary>The <c>pluginSelection</c> array on a sandbox-create body, as <c>marketplace/plugin</c> strings.</summary>
    private static IReadOnlyList<string> ReadPluginSelection(string createBody) =>
        JsonDocument.Parse(createBody).RootElement.TryGetProperty("pluginSelection", out var plugins)
        && plugins.ValueKind == JsonValueKind.Array
            ? [
                .. plugins.EnumerateArray()
                    .Select(p => $"{p.GetProperty("marketplace").GetString()}/{p.GetProperty("plugin").GetString()}"),
            ]
            : [];

    private static Harness CreateHarness(
        int failCreateAfter = int.MaxValue,
        string? configuredMarketplaces = null,
        bool? pluginFiltering = true,
        TimeSpan? idleWaitTimeout = null,
        StoreHooks? hooks = null,
        bool backgroundPostCommit = false,
        TimeSpan? settleBudget = null,
        TimeSpan? retirementGrace = null,
        CapturingLogger<WorkspacePluginSelectionService>? logger = null,
        Func<Func<Task>, Task>? postCommitScheduler = null,
        bool omitPluginResolution = false
    ) =>
        Harness.Create(
            failCreateAfter,
            configuredMarketplaces,
            pluginFiltering,
            idleWaitTimeout,
            hooks,
            backgroundPostCommit,
            settleBudget,
            retirementGrace,
            logger,
            postCommitScheduler,
            omitPluginResolution
        );

    /// <summary>
    /// One assembled system under test: real registry, real store, real compatibility service, an
    /// in-memory gateway and a scriptable run probe. <see cref="Create"/> is the only construction
    /// path, so a later suite that needs a different timing knob adds a parameter here rather than a
    /// second, slightly-different harness.
    /// </summary>
    internal sealed class Harness : IAsyncDisposable
    {
        public required SandboxSessionRegistry Registry { get; init; }
        public required FakeGateway Gateway { get; init; }
        public required FileWorkspaceStore FileStore { get; init; }
        public required FakeActivityProbe Probe { get; init; }
        public required WorkspacePluginSelectionService Service { get; init; }

        /// <summary>Everything the service logged, for the paths whose only output IS a log line.</summary>
        public required CapturingLogger<WorkspacePluginSelectionService> Logger { get; init; }

        /// <summary>
        /// Everything the REGISTRY logged. Distinct from <see cref="Logger"/>: some behaviour (e.g. a
        /// best-effort teardown attempted against an already-disposed transport) is only ever visible
        /// through the registry's own log line, because the gateway call it makes fails silently.
        /// </summary>
        public required CapturingLogger<SandboxSessionRegistry> RegistryLogger { get; init; }

        public ValueTask DisposeAsync() => Registry.DisposeAsync();

        /// <param name="failCreateAfter">The gateway starts failing creates once this many have succeeded.</param>
        /// <param name="configuredMarketplaces">The gateway-level default marketplace list.</param>
        /// <param name="pluginFiltering">The catalog's advertised plugin-filtering capability; <c>null</c> means "unknown".</param>
        /// <param name="idleWaitTimeout">
        /// The pre-commit idle wait. Short by default so the timeout path costs milliseconds; the happy
        /// paths never wait at all because no thread is registered.
        /// </param>
        /// <param name="hooks">When supplied, the store is wrapped so a test can act inside the persist window.</param>
        /// <param name="backgroundPostCommit">
        /// <c>false</c> (default) injects an INLINE post-commit scheduler, so the awaited request also
        /// covers retirement and reconcile and a test can assert on gateway traffic immediately after it
        /// returns. <c>true</c> leaves the parameter unset so the service's real background default ships
        /// — the modality production runs, which one test must exercise or the suite is only ever proving
        /// a seam that never ends up in a deployed request.
        /// </param>
        /// <param name="settleBudget">
        /// How long the pre-commit snapshot waits on in-flight session creations. Defaults to the
        /// service's own value; a test drives it to <see cref="TimeSpan.Zero"/> to make "the creation
        /// missed the batch" a state it enters deliberately rather than one it races for.
        /// </param>
        /// <param name="retirementGrace">
        /// How long a superseded session may stay busy before it is destroyed anyway. Defaults to the
        /// service's own value; short values keep the expiry path in milliseconds.
        /// </param>
        /// <param name="logger">Supplied when the behaviour under test is a log line and nothing else.</param>
        /// <param name="postCommitScheduler">
        /// Overrides <paramref name="backgroundPostCommit"/> entirely when supplied, for tests that must
        /// hold the post-commit phase open or observe it starting.
        /// </param>
        /// <param name="omitPluginResolution">Answers creates the way a gateway too old to report resolution does.</param>
        public static Harness Create(
            int failCreateAfter = int.MaxValue,
            string? configuredMarketplaces = null,
            bool? pluginFiltering = true,
            TimeSpan? idleWaitTimeout = null,
            StoreHooks? hooks = null,
            bool backgroundPostCommit = false,
            TimeSpan? settleBudget = null,
            TimeSpan? retirementGrace = null,
            CapturingLogger<WorkspacePluginSelectionService>? logger = null,
            Func<Func<Task>, Task>? postCommitScheduler = null,
            bool omitPluginResolution = false
        )
        {
            Func<Func<Task>, Task> inlinePostCommit = work => work();
            var capturingLogger = logger ?? new CapturingLogger<WorkspacePluginSelectionService>();
            // Not NullLogger: RegistryDisposalInterruptingTheSwap_StillAttemptsToAbortEveryCandidate needs
            // to observe the registry's OWN best-effort teardown warnings — the service's post-disposal
            // gateway calls fail silently (disposed transport), so a captured log line is the only
            // observable proof that a destroy was actually attempted for a given candidate.
            var registryLogger = new CapturingLogger<SandboxSessionRegistry>();
            var gateway = new FakeGateway(failCreateAfter, omitPluginResolution);
            var options = new SandboxGatewayOptions
            {
                BaseUrl = GatewayBaseUrl,
                AppId = DefaultAppId,
                Marketplaces = configuredMarketplaces,
            };

            // The lifetime client only answers the /health adopt probe; every create/delete the tests care
            // about goes through the registry's own client, which is the one `gateway` observes.
            var registry = new SandboxSessionRegistry(
                new SandboxGatewayLifetime(
                    options,
                    NullLogger<SandboxGatewayLifetime>.Instance,
                    new HttpClient(new AlwaysOkHandler())
                ),
                options,
                registryLogger,
                new HttpClient(gateway),
                new AuthOptions(),
                new SessionSecretStore(
                    Path.Combine(Path.GetTempPath(), "lmstreaming-plugin-selection-tests", Guid.NewGuid().ToString("N")),
                    NullLogger<SessionSecretStore>.Instance
                )
            );

            var fileStore = new FileWorkspaceStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
            IWorkspaceStore store = hooks is null ? fileStore : new HookedWorkspaceStore(fileStore, hooks);
            var probe = new FakeActivityProbe();

            return new Harness
            {
                Registry = registry,
                Gateway = gateway,
                FileStore = fileStore,
                Probe = probe,
                Logger = capturingLogger,
                RegistryLogger = registryLogger,
                Service = new WorkspacePluginSelectionService(
                    store,
                    new WorkspaceCatalogCompatibilityService(new StubCatalogClient(pluginFiltering), options),
                    registry,
                    probe,
                    options,
                    idleWaitTimeout ?? TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(10),
                    settleBudget,
                    retirementGrace,
                    capturingLogger,
                    postCommitScheduler ?? (backgroundPostCommit ? null : inlinePostCommit)
                ),
            };
        }
    }

    /// <summary>
    /// Hand-written rather than mocked: the point of the interface is that the real implementation
    /// (<c>MultiTurnAgentPool</c>) is sealed, and these tests need to hold a thread "busy" for as long
    /// as the wait runs, which no canned return value can express.
    /// </summary>
    internal sealed class FakeActivityProbe : IAgentRunActivityProbe
    {
        private readonly object _gate = new();
        private readonly List<string> _observedThreads = [];
        private readonly HashSet<string> _seenThreads = new(StringComparer.Ordinal);
        private HashSet<string> _busyThreads = new(StringComparer.Ordinal);
        private int _calls;

        /// <summary>
        /// Whether EVERY thread reports a run in progress, regardless of <see cref="SetBusyThreads"/>.
        /// The blunt instrument, for tests whose subject is the wait itself rather than which session
        /// is busy.
        /// </summary>
        public bool Busy { get; set; }

        /// <summary>How many times the probe was consulted, duplicates included.</summary>
        public int Calls => Volatile.Read(ref _calls);

        /// <summary>
        /// Every thread id the probe was ASKED about, in first-asked order and de-duplicated (a poll
        /// loop would otherwise bury the answer under thousands of repeats).
        /// <para>
        /// This is the non-vacuity witness for any test claiming a particular session was — or was not
        /// — treated as busy: "the wait finished" and "the wait never looked at that thread" are
        /// indistinguishable from the outcome alone.
        /// </para>
        /// </summary>
        public IReadOnlyList<string> ObservedThreads
        {
            get
            {
                lock (_gate)
                {
                    return [.. _observedThreads];
                }
            }
        }

        /// <summary>
        /// Replaces the set of threads that report a run in progress. Per-thread rather than global so
        /// a test can hold ONE session busy while its siblings migrate — the shape every partial-race
        /// scenario needs.
        /// </summary>
        public void SetBusyThreads(params string[] threadIds)
        {
            var replacement = new HashSet<string>(threadIds, StringComparer.Ordinal);

            lock (_gate)
            {
                _busyThreads = replacement;
            }
        }

        public bool IsRunInProgress(string threadId)
        {
            _ = Interlocked.Increment(ref _calls);

            lock (_gate)
            {
                if (_seenThreads.Add(threadId))
                {
                    _observedThreads.Add(threadId);
                }

                return Busy || _busyThreads.Contains(threadId);
            }
        }
    }

    /// <summary>Mutable hooks a test sets AFTER the harness (and therefore the registry) exists.</summary>
    internal sealed class StoreHooks
    {
        private int _getCalls;

        /// <summary>Runs inside the store write — after candidates exist, before the swap.</summary>
        public Func<Task>? BeforeUpdate { get; set; }

        /// <summary>
        /// Runs inside the store write, AFTER the update has persisted. Distinct from
        /// <see cref="BeforeUpdate"/> because the two land on opposite sides of the commit point: a test
        /// about what a COMMITTED migration still owes cannot use a hook that fires while the commit
        /// could still fail. This is the only deterministic instant at which "the update has happened and
        /// nothing after it has" is true.
        /// </summary>
        public Func<Task>? AfterUpdate { get; set; }

        /// <summary>When set, the store write fails with this instead of persisting.</summary>
        public Exception? UpdateFailure { get; set; }

        /// <summary>
        /// How many times the store has been READ. The post-commit reconcile pass re-reads the workspace
        /// before it decides anything, so this counter is the positive witness that the pass got as far
        /// as its own guard — without it, a test asserting "the pass changed nothing" holds just as well
        /// for a pass that never ran at all.
        /// </summary>
        public int GetCalls => Volatile.Read(ref _getCalls);

        internal void RecordGet() => Interlocked.Increment(ref _getCalls);
    }

    /// <summary>
    /// A pass-through store that can fail or side-effect at the persist point. Only the persist point
    /// is intercepted; reads stay real, so the revision the orchestrator checks is the stored one.
    /// </summary>
    internal sealed class HookedWorkspaceStore(IWorkspaceStore inner, StoreHooks hooks) : IWorkspaceStore
    {
        public Task<IReadOnlyList<Workspace>> GetAllAsync(CancellationToken ct = default) => inner.GetAllAsync(ct);

        public Task<Workspace?> GetAsync(string id, CancellationToken ct = default)
        {
            hooks.RecordGet();
            return inner.GetAsync(id, ct);
        }

        public Task<Workspace> CreateAsync(WorkspaceCreate dto, CancellationToken ct = default) =>
            inner.CreateAsync(dto, ct);

        public async Task<Workspace> UpdateAsync(string id, WorkspaceUpdate dto, CancellationToken ct = default)
        {
            if (hooks.BeforeUpdate is not null)
            {
                await hooks.BeforeUpdate();
            }

            if (hooks.UpdateFailure is not null)
            {
                throw hooks.UpdateFailure;
            }

            var updated = await inner.UpdateAsync(id, dto, ct);

            if (hooks.AfterUpdate is not null)
            {
                await hooks.AfterUpdate();
            }

            return updated;
        }
    }

    /// <summary>
    /// Two marketplaces with one plugin each, so "the plugin exists but under the wrong marketplace"
    /// is expressible — the case that separates catalog membership from workspace selectability.
    /// </summary>
    private sealed class StubCatalogClient(bool? pluginFiltering) : IMarketplaceCatalogClient
    {
        public Task<MarketplaceCatalog> GetCatalogAsync(
            IReadOnlyList<string>? marketplaces = null,
            CancellationToken ct = default
        ) =>
            Task.FromResult(
                new MarketplaceCatalog(
                    ["official", "extra"],
                    [
                        new CatalogMarketplace("official", null, [new CatalogPlugin("code-review", null, "", [], [])]),
                        new CatalogMarketplace("extra", null, [new CatalogPlugin("beta", null, "", [], [])]),
                    ]
                )
                {
                    Capabilities = new MarketplaceCapabilities(pluginFiltering),
                }
            );
    }

    private sealed class AlwaysOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    /// <summary>Which half of the sandbox session lifecycle a recorded gateway call belongs to.</summary>
    internal enum GatewayCallKind
    {
        /// <summary>A <c>POST /sandboxes</c>, whether it was served or deliberately failed.</summary>
        Create,

        /// <summary>A <c>DELETE /sandboxes/{id}</c>.</summary>
        Delete,
    }

    /// <summary>One sandbox session lifecycle call the fake gateway was asked to serve.</summary>
    /// <param name="Kind">Create or delete.</param>
    /// <param name="SessionId">
    /// The session id handed out (create) or torn down (delete). Empty for a create the fake was told
    /// to fail — the attempt still happened, and that is exactly what a "no side effects" assertion
    /// must be able to see.
    /// </param>
    /// <param name="Body">The create request body; empty for a delete.</param>
    internal sealed record GatewayCall(GatewayCallKind Kind, string SessionId, string Body);

    /// <summary>
    /// Minimal in-memory sandbox gateway. Records the create BODIES and the deleted session ids, not
    /// just counts: "which sessions were torn down" and "what selection did the replacement carry" are
    /// the two invariants this suite exists to pin, and a fake that recorded only methods would keep
    /// their mutations green.
    /// <para>
    /// <see cref="Calls"/> is the ordered union of both halves, and exists for the opposite claim: that
    /// a request produced NO lifecycle traffic at all. Counting creates and deletes separately can
    /// prove "one create, no deletes"; only a single ordered log — checkpointed after the arrange step
    /// via <see cref="CallCount"/> and read back with <see cref="CallsSince"/> — can prove "nothing
    /// whatsoever happened after this point", without the assertion having to restate the seeding.
    /// </para>
    /// <para>
    /// Creates echo the request's <c>pluginSelection</c> back as <c>pluginResolution.requested</c>, the
    /// way a real gateway does, so the reconcile pass can tell a migrated session from a stale one. The
    /// echo preserves the TRI-STATE exactly: a create that carried no selection produces a response with
    /// no <c>requested</c> field. <paramref name="omitPluginResolution"/> drops the block entirely,
    /// standing in for a gateway too old to report resolution at all.
    /// </para>
    /// </summary>
    internal sealed class FakeGateway(int failCreateAfter, bool omitPluginResolution = false) : HttpMessageHandler
    {
        private const string ResolutionPrefix = """
            , "pluginResolution": { "supported": true, "effective": [], "failed": []
            """;
        private const string RequestedPrefix = """
            , "requested":
            """;

        private readonly object _gate = new();
        private readonly List<string> _createdSessionIds = [];
        private readonly List<string> _createBodies = [];
        private readonly List<string> _deletedSessionIds = [];
        private readonly List<GatewayCall> _calls = [];

        /// <summary>
        /// Per-caller create gates, keyed by the <c>X-Sbx-App-Id</c> header. Separate from
        /// <see cref="_createGate"/> because the migration scenarios need ONE partition's creation wedged
        /// while its siblings create normally: a blanket hold parks the migration's own candidate creates
        /// too, which never lets the call under test reach the phase being exercised.
        /// </summary>
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _appCreateGates = new(
            StringComparer.Ordinal
        );

        /// <summary>Signals one create having parked at the hold; consumed by <see cref="WaitForHeldCreatesAsync"/>.</summary>
        private readonly SemaphoreSlim _createsParked = new(0);
        private TaskCompletionSource? _deleteGate;
        private TaskCompletionSource? _createGate;
        private int _creates;

        /// <summary>
        /// When set, a create THROWS this instead of answering. Distinct from
        /// <c>failCreateAfter</c>, which returns HTTP 500 and so becomes a SandboxException — a
        /// genuine downstream failure. This models the other kind: a fault that is not evidence about
        /// the gateway at all, which must not be re-labelled as one.
        /// </summary>
        public Exception? CreateThrows { get; set; }

        /// <summary>
        /// How many creates answer normally before <see cref="CreateThrows"/> starts firing. Mirrors
        /// <c>failCreateAfter</c> so a test can let the seeded sessions — and any number of candidates —
        /// be built first, which is what makes "the ones already built were torn down" assertable.
        /// </summary>
        public int CreateThrowsAfter { get; set; }

        /// <summary>Session ids the gateway actually handed out, in create order.</summary>
        public IReadOnlyList<string> CreatedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _createdSessionIds];
                }
            }
        }

        /// <summary>Bodies of the creates that succeeded, aligned with <see cref="CreatedSessionIds"/>.</summary>
        public IReadOnlyList<string> CreateBodies
        {
            get
            {
                lock (_gate)
                {
                    return [.. _createBodies];
                }
            }
        }

        public IReadOnlyList<string> DeletedSessionIds
        {
            get
            {
                lock (_gate)
                {
                    return [.. _deletedSessionIds];
                }
            }
        }

        /// <summary>
        /// Parks every subsequent DELETE inside the handler until <see cref="ReleaseDeletes"/> runs, so
        /// retirement becomes a blocking call the post-commit phase cannot get past. Open by default, so
        /// no other test observes it. Used to tell the two schedulers apart: an inline scheduler cannot
        /// return from the request while a delete is held, a background one returns at the swap.
        /// </summary>
        public void HoldDeletes() =>
            Volatile.Write(ref _deleteGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public void ReleaseDeletes() => Volatile.Read(ref _deleteGate)?.TrySetResult();

        /// <summary>
        /// Parks every subsequent CREATE the same way, so "a session creation is still in flight" becomes
        /// a state a test enters deliberately instead of one it races a sleep against. Held creates are
        /// invisible to <see cref="CreatedSessionIds"/> and <see cref="CreateBodies"/> until released,
        /// which keeps both usable as "the phase actually finished" signals.
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
        /// Parks only the creates issued under <paramref name="appId"/>, so one partition's session
        /// creation can be wedged while the migration under test creates candidates for its siblings
        /// normally. That combination — one partition permanently in flight, everything else settled — is
        /// the only way to reach the unsettled/reconcile phases at all, and <see cref="HoldCreates"/>
        /// cannot express it.
        /// </summary>
        public void HoldCreatesFor(string appId) =>
            _appCreateGates[appId] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Releases <paramref name="appId"/>'s parked creates and stops holding its later ones.</summary>
        public void ReleaseCreatesFor(string appId)
        {
            if (_appCreateGates.TryRemove(appId, out var gate))
            {
                _ = gate.TrySetResult();
            }
        }

        /// <summary>
        /// Completes once <paramref name="count"/> creates have reached the hold. Throws rather than
        /// hanging, so a test whose creates never arrive fails naming the count it did see.
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

        /// <summary>Creates ATTEMPTED, including the ones told to fail.</summary>
        public int CreateAttempts => Volatile.Read(ref _creates);

        /// <summary>Every session-lifecycle call served, creates and deletes interleaved in served order.</summary>
        public IReadOnlyList<GatewayCall> Calls
        {
            get
            {
                lock (_gate)
                {
                    return [.. _calls];
                }
            }
        }

        /// <summary>
        /// The current length of <see cref="Calls"/>. Captured at the end of a test's arrange step and
        /// passed to <see cref="CallsSince"/>, so the act step's traffic is asserted on its own.
        /// </summary>
        public int CallCount
        {
            get
            {
                lock (_gate)
                {
                    return _calls.Count;
                }
            }
        }

        /// <summary>The lifecycle calls served after <paramref name="checkpoint"/>, in served order.</summary>
        public IReadOnlyList<GatewayCall> CallsSince(int checkpoint)
        {
            lock (_gate)
            {
                return checkpoint >= _calls.Count ? [] : [.. _calls[checkpoint..]];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (request.Method == HttpMethod.Delete && path.Contains("/sandboxes/", StringComparison.Ordinal))
            {
                var held = Volatile.Read(ref _deleteGate);
                if (held is not null)
                {
                    // Awaited BEFORE recording, so `DeletedSessionIds` only ever reports deletes that got
                    // all the way through — which makes it usable as the "the phase actually finished"
                    // signal a test can poll instead of sleeping.
                    await held.Task.ConfigureAwait(false);
                }

                var deleted = path[(path.LastIndexOf('/') + 1)..];
                lock (_gate)
                {
                    _deletedSessionIds.Add(deleted);
                    _calls.Add(new GatewayCall(GatewayCallKind.Delete, deleted, Body: string.Empty));
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (request.Method != HttpMethod.Post || !path.EndsWith("/sandboxes", StringComparison.Ordinal))
            {
                // Health probes and liveness GETs.
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var body =
                request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // Parked BEFORE anything is recorded or counted, for the same reason the delete gate is:
            // a held create has not happened yet, and every collection on this fake should say so.
            var appId = request.Headers.TryGetValues(AppIdHeader, out var appIds) ? appIds.FirstOrDefault() : null;
            var createGate =
                Volatile.Read(ref _createGate)
                ?? (appId is not null && _appCreateGates.TryGetValue(appId, out var appGate) ? appGate : null);

            if (createGate is not null)
            {
                _createsParked.Release();
                await createGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var ordinal = Interlocked.Increment(ref _creates);
            if (CreateThrows is { } injected && ordinal > CreateThrowsAfter)
            {
                throw injected;
            }

            if (ordinal > failCreateAfter)
            {
                lock (_gate)
                {
                    _calls.Add(new GatewayCall(GatewayCallKind.Create, SessionId: string.Empty, body));
                }

                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var sessionId = $"sess-{ordinal}";
            lock (_gate)
            {
                _createdSessionIds.Add(sessionId);
                _createBodies.Add(body);
                _calls.Add(new GatewayCall(GatewayCallKind.Create, sessionId, body));
            }

            var resolution = omitPluginResolution ? string.Empty : ResolutionPrefix + RequestedField(body) + " }";
            var responseBody = $$"""
                { "session_id": "{{sessionId}}", "container_id": "c-{{ordinal}}",
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
        /// "load everything" into "load nothing" between request and response — the exact confusion the
        /// tri-state comparison exists to catch, and one a fake must not manufacture on its own.
        /// </summary>
        private static string RequestedField(string body)
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
