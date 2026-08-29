using System.Collections.Concurrent;
using System.Diagnostics;
using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.Sandbox;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Applies an explicit plugin-selection change to a workspace and migrates every live sandbox
/// session it owns.
/// <para>
/// The gateway fixes a session's plugin set at create time, so changing the selection means
/// recreating sessions. Doing that in place would tear down a session the user still holds and hope
/// the replacement comes up, so the whole flow is ordered <em>prepare-then-replace</em>: nothing
/// observable changes until every replacement already exists. Concretely, in order — validate the
/// selection, check the caller's <c>pluginsRevision</c>, snapshot the live partitions, wait
/// (bounded) for their runs to go idle, create a replacement beside each one, persist, swap the
/// registry over, and only then retire what was superseded.
/// </para>
/// <para>
/// Every failure before the swap leaves the originals serving traffic and the store untouched; the
/// only cleanup owed is deleting the candidates this call created, which each failure path does.
/// The two most delicate orderings are called out at their sites: the revision check runs
/// <em>before</em> the snapshot so a stale request costs no gateway work at all, and the retire step
/// distinguishes committed partitions from ones whose cache slot moved underneath the swap.
/// </para>
/// </summary>
public sealed class WorkspacePluginSelectionService : IWorkspacePluginSelectionService
{
    private static readonly TimeSpan DefaultIdleWaitTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DefaultIdlePollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// How long the snapshot may wait for an in-flight session creation to settle so it can be
    /// migrated with the batch. Short on purpose: whatever misses the budget is reconciled after the
    /// commit anyway, so a longer wait buys nothing but latency on every migration.
    /// </summary>
    private static readonly TimeSpan DefaultSettleBudget = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a superseded session is left alive after the swap while a run is still using it.
    /// Matches the pre-commit idle wait: a run that started during candidate creation deserves the
    /// same bounded courtesy as one that was already running when the migration began.
    /// </summary>
    private static readonly TimeSpan DefaultRetirementGrace = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How <see cref="CompletePostCommitAsync"/> is dispatched in production: hand the work to the
    /// thread pool and return an ALREADY-completed task, so the request ends at the swap.
    /// <para>
    /// Background is the default because that phase is bounded only by the retirement grace plus an
    /// uncancellable gateway create, serialized per partition — tens of seconds in the worst case, and
    /// the worst case is exactly the case the grace exists to serve. On the request that would let a
    /// reverse proxy time the caller out and report a failure for an update that committed in the first
    /// second, with no way for the caller to tell the two apart. A committed update must never be
    /// reported as a failure because its cleanup was slow.
    /// </para>
    /// <para>
    /// Deliberately NOT <c>Task.Run(work)</c> returned directly: that overload unwraps, so awaiting the
    /// returned task would put the whole phase straight back on the request. Discarding the task is safe
    /// precisely because <see cref="CompletePostCommitAsync"/> guards every stage and cannot throw.
    /// </para>
    /// </summary>
    private static readonly Func<Func<Task>, Task> BackgroundPostCommitScheduler = work =>
    {
        _ = Task.Run(work);
        return Task.CompletedTask;
    };

    /// <summary>
    /// One gate per workspace, so two migrations of the same workspace never interleave.
    /// <para>
    /// The store's compare-and-swap alone would keep the persisted state correct, but only by
    /// letting the loser get all the way to the persist call — after it had already built
    /// replacement sessions for every partition. Serializing here means the loser's revision check
    /// fails while it still holds no gateway resources, which is what makes "a stale request creates
    /// no candidates" true rather than merely "a stale request cleans up after itself".
    /// </para>
    /// <para>
    /// Entries are never removed, and that is only safe because a gate is allocated exclusively for a
    /// workspace that EXISTS — see the admission check in
    /// <see cref="ApplyPluginSelectionUpdateAsync"/>. Evicting a gate another caller is about to await is a
    /// genuine race for no real gain, so the bound has to come from admission instead.
    /// </para>
    /// <para>
    /// The bound is not incidental. This service is a singleton and the key is caller-supplied, so
    /// without that check a request naming a workspace that does not exist would allocate a
    /// <see cref="SemaphoreSlim"/> that is never removed and never disposed — and the rejection for an
    /// unknown id happens INSIDE the gate, so it would allocate on exactly the path that fails. A loop
    /// over unique ids would then grow this dictionary for the life of the process.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates = new(StringComparer.Ordinal);

    /// <summary>
    /// Number of per-workspace gates currently allocated. Exists so a test can assert the bound
    /// directly: the defect being guarded against is unbounded GROWTH, and growth is not observable
    /// from any behaviour this service exposes — a request for an unknown workspace fails identically
    /// whether or not it leaked a gate on the way out.
    /// </summary>
    internal int AllocatedWorkspaceGateCount => _workspaceGates.Count;

    private readonly IWorkspaceStore _store;
    private readonly WorkspaceCatalogCompatibilityService _compatibility;
    private readonly SandboxSessionRegistry _registry;
    private readonly IAgentRunActivityProbe _activityProbe;
    private readonly SandboxGatewayOptions _gatewayOptions;
    private readonly ILogger<WorkspacePluginSelectionService> _logger;
    private readonly TimeSpan _idleWaitTimeout;
    private readonly TimeSpan _idlePollInterval;
    private readonly TimeSpan _settleBudget;
    private readonly TimeSpan _retirementGrace;
    private readonly Func<Func<Task>, Task> _postCommitScheduler;

    /// <summary>Creates a new <see cref="WorkspacePluginSelectionService"/>.</summary>
    /// <param name="store">Workspace persistence, and the authoritative owner of the revision CAS.</param>
    /// <param name="compatibility">Validates the requested selection against the live catalog.</param>
    /// <param name="registry">Owns the prepare-then-replace session primitives.</param>
    /// <param name="activityProbe">
    /// Reports whether a conversation thread currently has a run in progress. Injected as a narrow
    /// interface because the agent pool that implements it is sealed and cannot be substituted.
    /// </param>
    /// <param name="gatewayOptions">
    /// Supplies the configured default marketplaces, so the replacement sessions are scoped to the
    /// same effective set the selection was validated against.
    /// </param>
    /// <param name="idleWaitTimeout">
    /// How long to wait for active runs to finish before giving up. Injectable so tests can reach
    /// the timeout path without a real 30-second wait.
    /// </param>
    /// <param name="idlePollInterval">How often to re-check for active runs while waiting.</param>
    /// <param name="settleBudget">
    /// Overrides <see cref="DefaultSettleBudget"/>. Injectable so a test can force the over-budget
    /// path — and therefore the reconcile pass — without a real wait.
    /// </param>
    /// <param name="retirementGrace">
    /// Overrides <see cref="DefaultRetirementGrace"/>. Injectable so a test can prove the
    /// grace-expiry path retires anyway without a real 30-second wait.
    /// </param>
    /// <param name="logger">
    /// Records the residuals this class deliberately does not turn into failures: a grace that
    /// expired with a run still live, and a reconcile pass that could not complete after the update
    /// had already committed. Optional so existing construction sites keep working; defaults to a
    /// no-op logger.
    /// </param>
    /// <param name="postCommitScheduler">
    /// How the post-commit cleanup phase is dispatched. Defaults to
    /// <see cref="BackgroundPostCommitScheduler"/>, which is what keeps that phase off the HTTP
    /// request. A test injects an inline scheduler (<c>work =&gt; work()</c>) so the phase's gateway
    /// traffic has finished by the time the call returns and can be asserted deterministically.
    /// Deliberately LAST: the parameters before it were appended in order, and positional call sites
    /// must keep compiling.
    /// </param>
    public WorkspacePluginSelectionService(
        IWorkspaceStore store,
        WorkspaceCatalogCompatibilityService compatibility,
        SandboxSessionRegistry registry,
        IAgentRunActivityProbe activityProbe,
        SandboxGatewayOptions gatewayOptions,
        TimeSpan? idleWaitTimeout = null,
        TimeSpan? idlePollInterval = null,
        TimeSpan? settleBudget = null,
        TimeSpan? retirementGrace = null,
        ILogger<WorkspacePluginSelectionService>? logger = null,
        Func<Func<Task>, Task>? postCommitScheduler = null
    )
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _compatibility = compatibility ?? throw new ArgumentNullException(nameof(compatibility));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _activityProbe = activityProbe ?? throw new ArgumentNullException(nameof(activityProbe));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _idleWaitTimeout = idleWaitTimeout ?? DefaultIdleWaitTimeout;
        _idlePollInterval = idlePollInterval ?? DefaultIdlePollInterval;
        // Guarded, unlike the three budgets around it: a zero or negative BUDGET is a meaningful
        // "do not wait", but a zero POLL INTERVAL turns both wait loops into a hot spin that pegs a
        // core for the whole grace. The default is positive, so this can only fire on an explicit
        // caller value — in practice a test — and failing at construction makes that attributable
        // instead of appearing as an unexplained CPU burn.
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_idlePollInterval, TimeSpan.Zero, nameof(idlePollInterval));
        _settleBudget = settleBudget ?? DefaultSettleBudget;
        _retirementGrace = retirementGrace ?? DefaultRetirementGrace;
        _logger = logger ?? NullLogger<WorkspacePluginSelectionService>.Instance;
        _postCommitScheduler = postCommitScheduler ?? BackgroundPostCommitScheduler;
    }

    /// <inheritdoc />
    public async Task<Workspace> ApplyPluginSelectionUpdateAsync(
        string workspaceId,
        WorkspaceUpdate dto,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
        ArgumentNullException.ThrowIfNull(dto);

        Workspace updated;
        PostCommitWork work;

        // Admission check: never allocate a gate for a workspace that does not exist. The key is
        // caller-supplied and gates are never removed, so allocating first would let unknown ids grow
        // `_workspaceGates` without bound — and since the unknown-id rejection lives inside
        // MigrateAsync, i.e. inside the gate, it would allocate precisely on the failing path.
        //
        // Deliberately NOT atomic with the gated work below, and it does not need to be: this only
        // decides whether a gate may be created. MigrateAsync repeats the lookup under the gate and
        // remains the authority, so a workspace deleted in between still produces the same
        // KeyNotFoundException from the same place it always did.
        if (await _store.GetAsync(workspaceId, ct) is null)
        {
            throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");
        }

        var gate = _workspaceGates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            (updated, work) = await MigrateAsync(workspaceId, dto, ct);
        }
        finally
        {
            _ = gate.Release();
        }

        // Deliberately AFTER the gate is released, deliberately not given `ct`, and deliberately
        // dispatched through `_postCommitScheduler` rather than awaited inline. All three are
        // load-bearing. Holding the gate across a multi-second retirement grace would make an
        // unrelated later edit to this workspace queue behind some other conversation's teardown.
        // Observing the caller's token would let a caller who disconnects immediately after the
        // commit skip deletion of a superseded session — leaking a gateway container for the life of
        // the process. And running it ON the request would put the grace plus an uncancellable
        // gateway create in front of the response for an update that has already committed. The
        // update has committed either way, so this phase can only clean up; it can never fail the
        // request.
        await _postCommitScheduler(() => CompletePostCommitAsync(workspaceId, work));

        return updated;
    }

    /// <summary>
    /// What a committed migration still owes once its workspace gate has been released. Returning it
    /// rather than acting on it inside <see cref="MigrateAsync"/> is what moves the slow, bounded
    /// cleanup out from under the gate.
    /// </summary>
    /// <param name="Uncommitted">
    /// Candidates whose per-partition compare-and-swap lost, so nothing references them. Retired
    /// immediately: an unreferenced session cannot have a run on it.
    /// </param>
    /// <param name="Superseded">
    /// Old sessions whose partitions did commit. These get the bounded idle grace, because a run may
    /// have legitimately started on one after the pre-commit idle wait passed.
    /// </param>
    /// <param name="NewRef">
    /// The workspace ref the candidates were created from. Reused by reconcile both to create its own
    /// candidate and — via <see cref="WorkspaceRef.PluginSelection"/> — to decide whether a late-settling
    /// session already reflects the new selection. Carrying the selection here rather than as a separate
    /// field keeps the two uses provably consistent: reconcile compares against the very refs the batch
    /// was built from, not a second copy that could drift from them.
    /// </param>
    /// <param name="NeverSettled">
    /// Partition keys whose session creation had not finished when the settle budget expired. They
    /// were NOT migrated with the batch and are owed exactly one reconcile pass.
    /// </param>
    /// <param name="CasLost">
    /// Partition keys whose candidate LOST the swap's compare-and-swap because a competing writer
    /// (most often the gateway-404 recreate path) had already replaced the slot with its own candidate
    /// by the time the swap ran. Distinct from <see cref="NeverSettled"/>: a session for these DOES
    /// exist and this migration's own candidate did complete — it simply never got published, because
    /// someone else's candidate is occupying the slot instead. That competing candidate may itself still
    /// be mid-creation, which is exactly why the reconcile pass gives these keys the same bounded settle
    /// wait <see cref="MigrateAsync"/> gave the original batch, rather than a zero-wait snapshot that
    /// cannot observe a winner that has not finished yet. Owed the same single reconcile pass as
    /// <see cref="NeverSettled"/>.
    /// </param>
    /// <param name="CommittedRevision">
    /// The <see cref="Workspace.PluginsRevision"/> this migration persisted. The reconcile pass re-reads
    /// the workspace and compares against it, because the pass runs AFTER the workspace gate is released
    /// (and, in production, after the request has returned): a later migration can have acquired the
    /// gate, committed its own selection and swapped in newer sessions by then. Without this witness
    /// that pass would measure those newer sessions against THIS migration's selection, judge them
    /// stale, and win the compare-and-swap against them.
    /// </param>
    private sealed record PostCommitWork(
        IReadOnlyList<SandboxSession> Uncommitted,
        IReadOnlyList<SandboxSession> Superseded,
        WorkspaceRef NewRef,
        IReadOnlyList<(string WorkspaceId, string AppId)> NeverSettled,
        IReadOnlyList<(string WorkspaceId, string AppId)> CasLost,
        int CommittedRevision
    );

    /// <summary>
    /// The migration itself, always under this workspace's gate.
    /// </summary>
    /// <returns>
    /// The persisted workspace, plus the cleanup owed once the gate is released.
    /// </returns>
    private async Task<(Workspace Updated, PostCommitWork Work)> MigrateAsync(
        string workspaceId,
        WorkspaceUpdate dto,
        CancellationToken ct
    )
    {
        var existing =
            await _store.GetAsync(workspaceId, ct)
            ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");

        // Every rejection decidable from the request alone happens here, before a single gateway
        // call: an unsupported selection, an immutable workspace, and a stale revision. The store
        // repeats the last two at persist time and remains the authority — its checks are atomic
        // with the write, whereas these are only early-outs — but by then candidates would exist,
        // sessions would have been built and torn down, and a busy run could have turned the
        // system-defined 400 into a 503 restart timeout or a stale revision into a 409. The
        // early-outs are what keep a doomed request free of side effects AND stable in its status
        // code. Each delegates to the single implementation of its rule, because two copies of one
        // rule is precisely how the marketplace-resolution bug happened.
        //
        // The ORDER here is a wire contract. Plugin validation runs first so a request that is both
        // unsupported and system-defined keeps its specific `unsupported_plugins` 400 payload rather
        // than being flattened into the bare system-defined 400.
        await _compatibility.ValidatePluginsForMutationAsync(dto.Marketplaces, dto.PluginSelection.Value, ct);
        SystemDefinedWorkspaceRule.ThrowIfSystemDefined(workspaceId, existing.IsSystemDefined);
        WorkspaceRevisionConflictException.ThrowIfMismatch(workspaceId, dto.PluginsRevision, existing.PluginsRevision);

        // Bounded, never indefinite. A session still being created has nothing to replace yet, so it
        // gets a short shared chance to settle and migrate with the batch; whatever misses the budget
        // comes back as `Unsettled` and is reconciled after the commit. Silently skipping those — the
        // synchronous snapshot's behaviour — leaves a session on the old plugin set that is
        // indistinguishable from a migrated one.
        var snapshot = await _registry.SnapshotPluginSelectionPartitionsAsync(workspaceId, _settleBudget, ct);
        var partitions = snapshot.Partitions;

        await WaitForIdleAsync(workspaceId, partitions, ct);

        // Resolve the marketplaces through the SAME rule the validation above used, so the sessions
        // are created with exactly the set the selection was checked against. Passing the raw
        // request list instead would let a workspace that names no marketplaces be validated against
        // the configured defaults and then created against nothing.
        var newRef = Program.BuildWorkspaceRef(
            workspaceId,
            existing with
            {
                Marketplaces =
                    MarketplaceAliases.ResolveEffective(dto.Marketplaces, _gatewayOptions.Marketplaces) ?? [],
                PluginSelection = dto.PluginSelection.Value,
            }
        );

        var candidates = new List<(SandboxSessionRegistry.PluginSelectionPartition Old, SandboxSession New)>(
            partitions.Count
        );

        try
        {
            foreach (var partition in partitions)
            {
                candidates.Add((partition, await _registry.CreatePluginSelectionCandidateAsync(newRef, partition, ct)));
            }
        }
        catch (Exception ex)
        {
            // Nothing is published yet, so the old sessions are still serving and the store is
            // unchanged. All that is owed is deleting the candidates already built. This runs for
            // EVERY exception, including the ones rethrown unwrapped below — the cleanup obligation
            // does not depend on how the failure is classified.
            await AbortAllAsync(candidates);

            // A caller who cancelled must see a cancellation, not a gateway failure: reporting
            // "sandbox replacement failed" for a request the caller withdrew would send a 502 for
            // something the sandbox did correctly.
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            // Only a genuine downstream failure becomes a replacement failure, because the controller
            // maps that to 502 — a claim about the GATEWAY. A NullReferenceException or an
            // ObjectDisposedException from a bug in this service is not evidence about the gateway at
            // all, and dressing it as one sends the operator to the wrong system while the stack trace
            // that names the real fault is buried as an inner exception. Anything unrecognised keeps
            // its own identity and surfaces as a 500, which is the honest answer for "we broke".
            //
            // SandboxSessionUnavailableException derives from InvalidOperationException, so the order
            // of these checks is load-bearing in the other direction too: it is matched here on its own
            // name, and a PLAIN InvalidOperationException — the shape a bug takes — does not match it.
            if (
                ex
                is SandboxSessionUnavailableException
                    or SandboxSessionRestartTimeoutException
                    or SandboxException
                    or HttpRequestException
                    or TimeoutException
                    or OperationCanceledException
            )
            {
                throw new SandboxSessionReplacementFailedException(workspaceId, ex);
            }

            throw;
        }

        Workspace updated;

        try
        {
            // Deliberately NOT cancellable. This call is the commit point for the persisted half of
            // the migration; cancelling it partway is the one way to end up with a stored selection
            // whose sessions were never swapped. The caller's token is honoured everywhere before
            // and after, where abandoning the work still leaves a consistent state.
            updated = await _store.UpdateAsync(workspaceId, dto);
        }
        catch
        {
            // Rethrow unwrapped: the common failure here is the store's own revision conflict, and
            // the caller must still see it as a conflict (409), not as a replacement failure (502).
            await AbortAllAsync(candidates);
            throw;
        }

        // The swap is a per-partition compare-and-swap, not a batch lock. A partition whose cache
        // slot changed since the snapshot (most often the gateway-404 recreate path racing this
        // migration) is skipped, and its candidate comes back here — a live gateway session nothing
        // references. So the two groups below are NOT interchangeable: retiring every old session
        // would destroy one that is still published and still serving, while dropping the returned
        // candidates would leak a container forever. They also differ in urgency, which is why they
        // are carried separately rather than concatenated: an unreferenced candidate cannot have a
        // run on it and goes immediately, whereas a superseded session may have acquired one after
        // the pre-commit idle wait and is owed the grace.
        IReadOnlyList<SandboxSession> uncommitted;
        try
        {
            uncommitted = _registry.SwapPluginSelectionSessions(candidates);
        }
        catch (Exception swapEx)
        {
            // The persisted half has already committed, so this can never become a replacement
            // failure (502, a claim about the gateway) or a conflict (409, a claim about the store) —
            // both describe the persist, which succeeded. Something AFTER it — most plausibly the
            // registry being disposed mid-migration, which makes the swap throw
            // ObjectDisposedException before touching a single partition — kept every candidate from
            // ever being published. Not one of them is referenced by anything, so they get the same
            // best-effort cleanup as every earlier failure path; the exception itself is rethrown
            // unwrapped, exactly like the persist-failure catch above.
            //
            // Logged here, not only inside the registry's own best-effort teardown: the registry may
            // be exactly what just threw (a disposed transport cannot itself report that its own
            // cleanup call failed), so this is the one place guaranteed to still have a working logger
            // when the swap fails after commit.
            _logger.LogWarning(
                swapEx,
                "Plugin-selection swap failed for workspace {WorkspaceId} after the update had already "
                    + "persisted; the selection is committed and {CandidateCount} candidate session(s) "
                    + "are being aborted best-effort.",
                workspaceId,
                candidates.Count
            );
            await AbortAllAsync(candidates);
            throw;
        }
        var uncommittedIds = new HashSet<string>(
            uncommitted.Select(session => session.SessionId),
            StringComparer.Ordinal
        );

        var work = new PostCommitWork(
            Uncommitted: uncommitted,
            Superseded:
            [
                .. candidates.Where(c => !uncommittedIds.Contains(c.New.SessionId)).Select(c => c.Old.Session),
            ],
            NewRef: newRef,
            NeverSettled: snapshot.Unsettled,
            // A partition whose slot changed underneath the swap (most often the gateway-404 recreate
            // path racing this migration) is NOT an unsettled partition, but it is owed exactly the same
            // reconcile: whoever republished the slot did so under some OTHER plugin selection, and
            // nothing else will ever revisit it. Tracked separately from NeverSettled (rather than folded
            // into one list) so the reconcile pass's residual warning can name the correct cause instead
            // of claiming every leftover key is "still being created" — a CAS-lost key already has a
            // session, it is simply not this migration's.
            CasLost: [.. candidates.Where(c => uncommittedIds.Contains(c.New.SessionId)).Select(c => c.Old.Key)],
            CommittedRevision: updated.PluginsRevision
        );

        return (updated, work);
    }

    /// <summary>
    /// Everything a committed migration still owes, run outside the workspace gate and under
    /// <see cref="CancellationToken.None"/>.
    /// <para>
    /// Nothing here may throw. The persisted update has already committed, so turning a cleanup
    /// failure into a request failure would report "the update failed" for an update that did not —
    /// and the caller would have no way to tell the difference. Failures are logged instead.
    /// </para>
    /// </summary>
    private async Task CompletePostCommitAsync(string workspaceId, PostCommitWork work)
    {
        // Unreferenced first, and without a grace: the swap that would have published these lost, so
        // no conversation can be routed to them and no run can be in progress on one.
        await RunPostCommitStageAsync(
            workspaceId,
            "retiring uncommitted candidates",
            () => _registry.RetirePluginSelectionSessionsAsync(work.Uncommitted)
        );

        await RunPostCommitStageAsync(
            workspaceId,
            "retiring superseded sessions",
            () => RetireAfterGraceAsync(workspaceId, work.Superseded)
        );

        // Keeps its own, more specific catch as well; this is only the backstop that makes the
        // "nothing here may throw" contract hold uniformly across all three stages.
        await RunPostCommitStageAsync(
            workspaceId,
            "reconciling unsettled partitions",
            () => ReconcileUnsettledOnceAsync(workspaceId, work)
        );
    }

    /// <summary>
    /// Runs one stage of <see cref="CompletePostCommitAsync"/>, absorbing anything it throws.
    /// <para>
    /// Per stage, not once around the phase: the three stages are independent cleanups, so letting the
    /// first failure skip the ones behind it turns one unlucky teardown into a leaked container AND a
    /// partition left on the old plugin selection. The concrete case is a registry disposed during the
    /// retirement grace (host shutdown): <c>GetBoundThreads</c> opens with an
    /// <see cref="ObjectDisposedException"/> throw, which would otherwise escape past an
    /// already-committed request and cancel the reconcile pass behind it.
    /// </para>
    /// </summary>
    private async Task RunPostCommitStageAsync(string workspaceId, string stage, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Post-commit stage '{Stage}' failed for workspace {WorkspaceId} after the plugin selection "
                    + "had already committed. The persisted selection stands; the remaining stages continue.",
                stage,
                workspaceId
            );
        }
    }

    /// <summary>
    /// Retires <paramref name="oldSessions"/> once no run is using them, or once the bounded grace
    /// expires — whichever comes first.
    /// <para>
    /// The grace exists because the pre-commit idle wait happens BEFORE candidate creation, and
    /// creation is sequential gateway I/O measured in seconds. Nothing re-checks idleness in
    /// between, so a run can legitimately start after the wait passed and still be live when the
    /// swap commits; retiring at that instant kills it.
    /// </para>
    /// <para>
    /// On expiry the session is retired ANYWAY, with a warning naming it and the elapsed wait. An
    /// unbounded grace would convert "a run that never ends" into "a container that is never
    /// reclaimed", which is a leak by another name. This narrows the window and makes what remains
    /// attributable; it does not close it, and a run that outlasts the grace will fail.
    /// </para>
    /// </summary>
    private async Task RetireAfterGraceAsync(string workspaceId, IReadOnlyList<SandboxSession> oldSessions)
    {
        if (oldSessions.Count == 0)
        {
            return;
        }

        var waited = Stopwatch.StartNew();

        // Elapsed is tested FIRST so a zero grace performs no wait at all, and still reaches the
        // honest residual log below rather than silently skipping it.
        //
        // The busy set is carried OUT of the loop rather than re-derived after it. Re-asking
        // IsSessionBusy once the loop has exited answers a different question at a later instant: a
        // run that starts in that gap makes a grace that never expired report "grace of 30s expired
        // ... after 00:00:00.4". The log must state the verdict the loop actually stopped on.
        var stillBusy = oldSessions.Where(IsSessionBusy).ToList();

        while (waited.Elapsed < _retirementGrace && stillBusy.Count > 0)
        {
            await Task.Delay(_idlePollInterval, CancellationToken.None);
            stillBusy = [.. oldSessions.Where(IsSessionBusy)];
        }

        foreach (var session in stillBusy)
        {
            _logger.LogWarning(
                "Retirement grace of {Grace} expired for workspace {WorkspaceId} with a run still in "
                    + "progress on superseded sandbox session {SessionId} after {Waited}. Retiring anyway; "
                    + "that run will fail.",
                _retirementGrace,
                workspaceId,
                session.SessionId,
                waited.Elapsed
            );
        }

        await _registry.RetirePluginSelectionSessionsAsync(oldSessions);
    }

    /// <summary>
    /// Migrates any partition that was still being created when the settle budget expired and has
    /// since completed, in EXACTLY one pass.
    /// <para>
    /// One pass, not a loop: a creation that is still wedged after the commit will be wedged after a
    /// retry too, and looping would let one stuck partition hold this phase open indefinitely. What
    /// this pass misses stays on the old plugin set until something recreates it — the honest
    /// residual, preferred over an unbounded retry. Because there is no second pass, that residual is
    /// LOGGED here: an owed partition that never appeared is a divergence between the persisted
    /// selection and a live session that produces no error of its own.
    /// </para>
    /// <para>
    /// It reuses only the primitives the batch used — candidate creation and the per-partition
    /// compare-and-swap — so a partition that some other writer has already claimed is detected
    /// rather than clobbered. On a lost swap this retires ITS OWN candidate and leaves the winner
    /// published; retiring the winner would destroy a session the registry is actively handing out.
    /// </para>
    /// </summary>
    private async Task ReconcileUnsettledOnceAsync(string workspaceId, PostCommitWork work)
    {
        if (work.NeverSettled.Count == 0 && work.CasLost.Count == 0)
        {
            return;
        }

        try
        {
            // The pass runs after this migration released the workspace gate, so another migration may
            // already have taken it, committed a newer selection and swapped in newer sessions. Those
            // sessions reflect ITS selection, so measuring them against `work.NewRef` below would judge
            // them stale and destroy them via the compare-and-swap — leaving the store on the new
            // selection and the live session on this migration's older one, with nothing to self-heal
            // it. A newer revision means those partitions belong to someone else and this pass has
            // nothing left to do. One read, one comparison, before anything is snapshotted. A workspace
            // that has since been deleted (null) is likewise not ours to reconcile.
            var current = await _store.GetAsync(workspaceId, CancellationToken.None);
            if (current is null || current.PluginsRevision != work.CommittedRevision)
            {
                return;
            }

            var owed = new HashSet<(string WorkspaceId, string AppId)>([.. work.NeverSettled, .. work.CasLost]);

            // Re-snapshot rather than reuse the original, bounded exactly like MigrateAsync's own
            // pre-commit snapshot (:322-ish above) — NOT the zero-budget synchronous capture this used
            // to be. A CasLost key's winner may have WON the compare-and-swap only moments ago and still
            // be mid-creation (IsValueCreated but not yet completed) at the exact instant this pass
            // re-snapshots: the very interval that cost this migration's own candidate the swap in the
            // first place. The synchronous capture cannot see that winner at all — it silently skips any
            // entry that is not both IsValueCreated AND already completed — so it would report the
            // winner as never having appeared and leave the partition permanently stuck on it. Nothing
            // may be awaited between this line and the swap inside the loop below (other than the wait
            // this call performs internally): the captured partitions carry the compare-and-swap
            // witnesses, and an await here would let a newer writer republish a partition that this pass
            // then judged against a witness it no longer holds.
            var resnapshot = await _registry.SnapshotPluginSelectionPartitionsAsync(
                workspaceId,
                _settleBudget,
                CancellationToken.None
            );
            var partitions = resnapshot.Partitions;

            var late = partitions
                .Where(partition =>
                    owed.Contains(partition.Key)
                    // Fail-closed: a session that cannot PROVE it already carries the new selection is
                    // treated as stale. The cost of being wrong is one redundant recreate; the cost of
                    // the opposite default is a session left on the old plugin set that looks migrated.
                    && !SandboxSessionRegistry.ReflectsPluginSelection(partition.Session, work.NewRef.PluginSelection)
                )
                .ToList();

            // An owed key that is STILL absent even after this bounded wait is a genuine residual. This
            // pass is the only one there will be, so this is the last moment that residual can be named —
            // after this the store says one thing and that session serves another, with no error
            // anywhere. Emitted BEFORE the loop so a partition failing mid-loop cannot suppress it. Split
            // by ORIGINAL cause — NeverSettled vs CasLost — because they are different failures with
            // different fixes: a NeverSettled key's creation is still wedged wherever it was started; a
            // CasLost key already has a live session, it is simply not this migration's, and "still being
            // created" would be a false description of it.
            var stillMissing = owed.Where(key => !partitions.Any(partition => partition.Key == key)).ToList();
            var stillNeverSettled = stillMissing.Where(key => work.NeverSettled.Contains(key)).ToList();
            var stillCasLost = stillMissing.Where(key => work.CasLost.Contains(key)).ToList();

            if (stillNeverSettled.Count > 0)
            {
                _logger.LogWarning(
                    "Post-commit reconcile pass for workspace {WorkspaceId} left {UnreconciledCount} partition(s) "
                        + "unreconciled: {UnreconciledPartitions}. Their sandbox sessions were still being created "
                        + "when the settle budget expired and had not appeared by the single reconcile pass, so they "
                        + "keep serving the previous plugin selection until something recreates them.",
                    workspaceId,
                    stillNeverSettled.Count,
                    string.Join(", ", stillNeverSettled.Select(key => $"{key.WorkspaceId}/{key.AppId}"))
                );
            }

            if (stillCasLost.Count > 0)
            {
                _logger.LogWarning(
                    "Post-commit reconcile pass for workspace {WorkspaceId} left {UnreconciledCount} partition(s) "
                        + "unreconciled: {UnreconciledPartitions}. Their candidate lost a compare-and-swap to a "
                        + "competing writer and that writer's own candidate had still not appeared by the single "
                        + "reconcile pass (the bounded settle wait was not enough), so they keep serving whatever "
                        + "selection that competing writer used until something recreates them.",
                    workspaceId,
                    stillCasLost.Count,
                    string.Join(", ", stillCasLost.Select(key => $"{key.WorkspaceId}/{key.AppId}"))
                );
            }

            foreach (var partition in late)
            {
                await ReconcilePartitionAsync(workspaceId, work.NewRef, partition);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Post-commit reconcile pass for workspace {WorkspaceId} failed. The plugin selection is "
                    + "persisted and the migrated sessions are live; any session still on the previous "
                    + "selection stays that way until it is recreated.",
                workspaceId
            );
        }
    }

    /// <summary>
    /// The single-partition body of <see cref="ReconcileUnsettledOnceAsync"/>. Isolated so one
    /// partition's failure cannot abandon the partitions after it in the pass.
    /// <para>
    /// The guard spans the WHOLE body, not just the create. The swap and the retire can throw too — a
    /// disposed registry throws from both — and this pass runs exactly once by design, so anything that
    /// escapes to the caller's catch abandons every partition after the failing one with no retry to
    /// pick them up. Contained here, one bad partition costs exactly one partition.
    /// </para>
    /// </summary>
    private async Task ReconcilePartitionAsync(
        string workspaceId,
        WorkspaceRef newRef,
        SandboxSessionRegistry.PluginSelectionPartition partition
    )
    {
        try
        {
            var candidate = await _registry.CreatePluginSelectionCandidateAsync(
                newRef,
                partition,
                CancellationToken.None
            );

            var uncommitted = _registry.SwapPluginSelectionSessions([(partition, candidate)]);

            if (uncommitted.Count > 0)
            {
                // Lost the compare-and-swap: someone else published a newer session for this partition
                // while the candidate was being built. Theirs stays; ours is the one nothing references.
                await _registry.RetirePluginSelectionSessionsAsync(uncommitted);
                return;
            }

            await RetireAfterGraceAsync(workspaceId, [partition.Session]);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Post-commit reconcile failed for workspace {WorkspaceId} app {AppId}; session "
                    + "{SessionId} may remain on the previous plugin selection.",
                workspaceId,
                partition.Key.AppId,
                partition.Session.SessionId
            );
        }
    }

    /// <summary>
    /// Waits until no thread bound to any snapshotted session has a run in progress, or until the
    /// bounded timeout expires.
    /// <para>
    /// A thread absent from the pool is idle by definition — there is no persisted "running agent"
    /// concept independent of a live pool entry — so an unknown thread never stalls this wait.
    /// </para>
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="ct"/> was cancelled. Distinct from the timeout below: the caller withdrew.
    /// </exception>
    /// <exception cref="SandboxSessionRestartTimeoutException">
    /// A run was still in progress when <see cref="_idleWaitTimeout"/> expired.
    /// </exception>
    private async Task WaitForIdleAsync(
        string workspaceId,
        IReadOnlyList<SandboxSessionRegistry.PluginSelectionPartition> partitions,
        CancellationToken ct
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_idleWaitTimeout);
        var waited = Stopwatch.StartNew();

        while (partitions.Any(partition => IsSessionBusy(partition.Session)))
        {
            try
            {
                await Task.Delay(_idlePollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // The linked source fires for either reason, so ask the caller's token directly
                // which one it was — otherwise a caller-cancelled request would be reported as a
                // sandbox restart timeout, blaming the sandbox for the caller's own decision.
                ct.ThrowIfCancellationRequested();
                throw new SandboxSessionRestartTimeoutException(workspaceId, waited.Elapsed);
            }
        }
    }

    /// <summary>
    /// Whether any conversation bound to <paramref name="session"/> currently has a run in progress.
    /// <para>
    /// The single definition of "in use", shared by the pre-commit idle wait and the post-commit
    /// retirement grace on purpose: those two answer the same question at different times, and a
    /// migration that waited on one set of conversations and then retired based on another would
    /// tear down exactly the session it had just waited for.
    /// </para>
    /// <para>
    /// Discovery goes through <see cref="SandboxSessionRegistry.GetBoundThreads"/>, NOT
    /// <c>GetThreads</c>. The latter reads only the routing map, which is populated just for
    /// sub-agent-enabled conversations, so a plain workspace-mode conversation holding a live session
    /// appears nowhere in it. Because a thread absent from the pool counts as idle, that omission
    /// reads as "idle" — the one answer that lets a session be destroyed under a running turn.
    /// </para>
    /// </summary>
    private bool IsSessionBusy(SandboxSession session) =>
        _registry.GetBoundThreads(session.SessionId).Any(_activityProbe.IsRunInProgress);

    /// <summary>
    /// Deletes every candidate built so far. Each teardown is best-effort inside the registry, so
    /// one failure cannot mask the original error that brought us here.
    /// </summary>
    private async Task AbortAllAsync(
        IReadOnlyList<(SandboxSessionRegistry.PluginSelectionPartition Old, SandboxSession New)> candidates
    )
    {
        foreach (var (_, candidate) in candidates)
        {
            await _registry.AbortPluginSelectionCandidateAsync(candidate);
        }
    }
}
