using AchieveAi.LmDotnetTools.Sandbox;

namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Prepare-then-replace primitives for a plugin-selection change (spec Section 7).
/// <para>
/// A workspace's plugin set can only be changed by recreating its sandbox sessions — the gateway
/// fixes the plugin set at create time. Doing that in place would mean tearing down a session the
/// user is still holding and hoping the replacement comes up. Instead every step here is ordered so
/// that nothing observable changes until every replacement already exists: snapshot the live
/// partitions, create candidates beside them, swap the cache entries, then retire the superseded
/// sessions best-effort. A failure before the swap leaves the originals serving traffic; a failure
/// after it cannot fail the migration, which has already committed.
/// </para>
/// </summary>
public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// A single (workspace, caller app) session partition captured before a plugin-selection migration
    /// begins.
    /// </summary>
    /// <param name="Key">The <c>(WorkspaceId, AppId)</c> cache key this session is published under.</param>
    /// <param name="Entry">
    /// The exact cache slot observed at snapshot time. <see cref="SwapPluginSelectionSessions"/> uses it
    /// as the compare-and-swap witness, so a slot that some other code path replaced while the candidate
    /// was being created is never overwritten.
    /// </param>
    /// <param name="Session">The live session as of snapshot time.</param>
    /// <param name="Credential">
    /// The caller credential this session was created with, captured explicitly at snapshot time (see
    /// <see cref="SnapshotPluginSelectionPartitions"/>) and carried unchanged into
    /// <see cref="CreatePluginSelectionCandidateAsync"/>. Never re-derived from the live credential map
    /// later. <see langword="null"/> only when the session predates credential tracking, in which case
    /// creation falls back to the process default exactly as the original create did.
    /// </param>
    internal sealed record PluginSelectionPartition(
        (string WorkspaceId, string AppId) Key,
        Lazy<Task<SandboxSession>> Entry,
        SandboxSession Session,
        SandboxCredential? Credential
    );

    /// <summary>
    /// Captures every currently-live session partition for <paramref name="workspaceId"/> — one per
    /// distinct caller app id — as of the moment of the call. Every later step operates on this
    /// snapshot rather than a fresh query, so a session created by another caller mid-migration is
    /// neither migrated with stale intent nor accidentally retired.
    /// <para>
    /// Each partition's caller credential is read HERE, once. Re-deriving it at candidate-creation time
    /// would let a credential eviction racing the migration silently substitute the process-default
    /// identity, and the replacement session would then belong to the wrong app.
    /// </para>
    /// <para>
    /// Only sessions whose creation has already completed successfully are included: an in-flight
    /// creation has no session to replace yet, and observing its <see cref="Lazy{T}"/> would block.
    /// A caller that cannot afford to silently skip those must use
    /// <see cref="SnapshotPluginSelectionPartitionsAsync"/>, which reports them.
    /// </para>
    /// </summary>
    internal IReadOnlyList<PluginSelectionPartition> SnapshotPluginSelectionPartitions(string workspaceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveWorkspaceId = EffectiveWorkspaceId(workspaceId);

        var partitions = new List<PluginSelectionPartition>();

        foreach (var entry in _sessions)
        {
            if (
                !string.Equals(entry.Key.WorkspaceId, effectiveWorkspaceId, StringComparison.Ordinal)
                || !entry.Value.IsValueCreated
                || !entry.Value.Value.IsCompletedSuccessfully
            )
            {
                continue;
            }

            var session = entry.Value.Value.Result;
            // MUST become `null` — not `default` — when the session is not tracked. SandboxCredential is
            // a readonly record struct, so `TryGetValue(out var credential)` leaves a ZERO-VALUED struct
            // on a miss, and passing that to a `SandboxCredential?` parameter yields a non-null nullable
            // wrapping a blank app id. CreateSessionAsync's `credential ?? _defaultCredential` fallback
            // would then never fire and the candidate would be created under an empty identity that the
            // gateway rejects. The explicit conditional is what keeps the miss distinguishable.
            SandboxCredential? credential = _sessionCredentials.TryGetValue(session.SessionId, out var tracked)
                ? tracked
                : null;
            partitions.Add(new PluginSelectionPartition(entry.Key, entry.Value, session, credential));
        }

        return partitions;
    }

    /// <summary>
    /// The result of <see cref="SnapshotPluginSelectionPartitionsAsync"/>: the partitions that were
    /// capturable, plus the keys that were still being created when the settle budget ran out.
    /// </summary>
    /// <param name="Partitions">
    /// Exactly what <see cref="SnapshotPluginSelectionPartitions"/> would have returned, taken AFTER
    /// the settle wait, so a creation that finished inside the budget is migrated with the batch.
    /// </param>
    /// <param name="Unsettled">
    /// Keys for this workspace that are materialized in the session cache but are NOT in
    /// <paramref name="Partitions"/> — a creation still in flight when the capture ran, or one that
    /// started during the settle wait and so was never even waited on. These were NOT migrated; the
    /// caller owes them a post-commit reconcile pass. Reporting them is the whole point — silently
    /// dropping one leaves a session on the old plugin set forever, indistinguishable from a correctly
    /// migrated one.
    /// </param>
    internal sealed record PluginSelectionSnapshot(
        IReadOnlyList<PluginSelectionPartition> Partitions,
        IReadOnlyList<(string WorkspaceId, string AppId)> Unsettled
    );

    /// <summary>
    /// <see cref="SnapshotPluginSelectionPartitions"/>, but first gives any in-flight session creation
    /// a bounded chance to finish so it can be migrated with the batch instead of silently skipped.
    /// <para>
    /// <paramref name="settleBudget"/> is shared across ALL pending entries, not spent per entry. A
    /// per-entry budget would multiply to <c>budget × partitionCount</c> in the worst case, and — worse
    /// — one wedged creation would hold up every other partition behind it. With a shared deadline a
    /// wedged creation costs the budget once and then lands in
    /// <see cref="PluginSelectionSnapshot.Unsettled"/>, leaving its siblings untouched.
    /// </para>
    /// <para>
    /// Entries whose <see cref="Lazy{T}"/> value was never requested are not "in flight" — nobody is
    /// creating them — so they are neither waited on nor reported. Entries that fault inside the budget
    /// are likewise not reported: a failed creation leaves no session to migrate.
    /// </para>
    /// </summary>
    /// <param name="workspaceId">The workspace whose partitions to capture.</param>
    /// <param name="settleBudget">
    /// Total wall-clock to wait for in-flight creations. <see cref="TimeSpan.Zero"/> reduces this to the
    /// synchronous capture plus an honest unsettled list.
    /// </param>
    /// <param name="ct">Cancels the wait. Leaves the registry unmutated either way.</param>
    internal async Task<PluginSelectionSnapshot> SnapshotPluginSelectionPartitionsAsync(
        string workspaceId,
        TimeSpan settleBudget,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var effectiveWorkspaceId = EffectiveWorkspaceId(workspaceId);

        // "In flight" is IsValueCreated AND not yet completed. IsValueCreated alone would include
        // entries nobody has asked for; those never complete on their own and would burn the whole
        // budget for nothing.
        var pending = _sessions
            .Where(entry =>
                string.Equals(entry.Key.WorkspaceId, effectiveWorkspaceId, StringComparison.Ordinal)
                && entry.Value.IsValueCreated
                && !entry.Value.Value.IsCompleted
            )
            .ToList();

        if (pending.Count > 0 && settleBudget > TimeSpan.Zero)
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(settleBudget);

            // Each creation is observed through a continuation rather than awaited directly: a faulted
            // creation must not short-circuit the wait for its siblings, and the fault itself belongs
            // to the caller that started the creation, not to this snapshot.
            var observed = pending.Select(entry =>
                entry.Value.Value.ContinueWith(
                    static _ => { },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
            );

            _ = await Task.WhenAny(Task.WhenAll(observed), Task.Delay(Timeout.Infinite, budgetCts.Token))
                .ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
        }

        // Delegate the actual capture — including the credential rule — to the method that owns it.
        // Re-walking _sessions here would be a second copy of that rule, and its credential branch is
        // exactly the kind of subtlety that drifts.
        var partitions = SnapshotPluginSelectionPartitions(workspaceId);

        // An explicit SET DIFFERENCE against what was actually captured, deliberately NOT a filter over
        // the pre-wait `pending` list. Two entries escape that list entirely: one that was in flight when
        // the walk reached it (so excluded from Partitions) but had completed by the time a `pending`
        // filter was evaluated, and — the likelier one — a creation that STARTED during the settle wait
        // and was therefore never in `pending` at all. Both would then be in neither list, which is the
        // one outcome that leaves a session on the old plugin set looking migrated. Computing the
        // complement makes "a key in both lists" impossible BY CONSTRUCTION rather than by argument:
        // every materialized entry for this workspace is in exactly one of the two, whatever its
        // completion state. A materialized entry whose creation FAULTED is included and is harmless —
        // it has no session, so the reconcile pass's own capture simply will not see it.
        var captured = new HashSet<(string WorkspaceId, string AppId)>(partitions.Select(partition => partition.Key));

        var unsettled = _sessions
            .Where(entry =>
                string.Equals(entry.Key.WorkspaceId, effectiveWorkspaceId, StringComparison.Ordinal)
                && entry.Value.IsValueCreated
                && !captured.Contains(entry.Key)
            )
            .Select(entry => entry.Key)
            .ToList();

        return new PluginSelectionSnapshot(partitions, unsettled);
    }

    /// <summary>
    /// Whether <paramref name="session"/> can be PROVEN to already carry <paramref name="desired"/>.
    /// Used by the post-commit reconcile pass to decide whether a late-settling session still needs
    /// migrating.
    /// <para>
    /// Fail-closed by construction. A session whose <see cref="SandboxSession.PluginResolution"/> is
    /// <see langword="null"/> came from a gateway that reported no resolution block at all, so nothing
    /// is known about what it loaded. That is "cannot prove current", which this reports as NOT
    /// matching. Being wrong that way costs one redundant recreate; the opposite default leaves a
    /// session on the old plugin set while looking migrated.
    /// </para>
    /// <para>
    /// Compares the gateway's echoed <c>Requested</c> list, not <c>Effective</c>: a plugin the gateway
    /// failed to load is still part of the selection the session was created for, and counting that as
    /// a difference would make every partially-failed session reconcile forever. Within a non-empty
    /// list, order and duplicates are not significant — the comparison is a structural set comparison
    /// over <c>(marketplace, plugin)</c> pairs.
    /// </para>
    /// <para>
    /// The selection is TRI-STATE and this comparison preserves that: <see langword="null"/> means "no
    /// explicit selection — load everything the marketplaces offer", while an EMPTY list means "load
    /// nothing". Those are opposite instructions to the gateway, so <see langword="null"/> matches only
    /// <see langword="null"/> and <c>[]</c> matches only <c>[]</c>; the two never match each other.
    /// Collapsing them onto one another (as projecting <see langword="null"/> to an empty key set would)
    /// makes a <c>null</c>→<c>[]</c> or <c>[]</c>→<c>null</c> migration read as already-current, and the
    /// reconcile pass then leaves a session serving the exact plugin set the user just turned off.
    /// </para>
    /// </summary>
    internal static bool ReflectsPluginSelection(SandboxSession session, IReadOnlyList<SandboxPluginRef>? desired)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.PluginResolution is not { } resolution)
        {
            return false;
        }

        // A resolution that reports filtering unsupported cannot prove anything about the plugin set
        // this session actually serves — `Requested` is only what the gateway SAW, not what it applied.
        // A gateway that echoes the request while ignoring it therefore looks byte-identical to one
        // that honoured it, and the only field telling them apart is this one. Same fail-closed rule
        // the caller states: no proof means stale, and the cost of being wrong is one extra recreate.
        if (!resolution.Supported)
        {
            return false;
        }

        var requested = resolution.Requested;

        // Tri-state short-circuit: "unset" is a distinct state from "empty", so a null on either side
        // matches only a null on the other. Must precede the set comparison, which cannot express it.
        return requested is null || desired is null
            ? requested is null && desired is null
            : PluginSelectionKeys(requested).SetEquals(PluginSelectionKeys(desired));
    }

    /// <summary>
    /// Projects plugin refs onto a comparable key set. <see cref="SandboxPluginRef"/> is a sealed class
    /// with reference equality, so two refs naming the same plugin are not equal to each other and the
    /// lists cannot be compared directly. Projecting to a value tuple — rather than to a joined string —
    /// gives ordinal structural equality without reserving a separator character that a gateway-supplied
    /// id could itself contain, which would let two different pairs collapse onto one key.
    /// <para>
    /// The parameter is deliberately NON-nullable. Accepting a <see langword="null"/> here and mapping it
    /// to an empty set is exactly the collapse <see cref="ReflectsPluginSelection"/> exists to avoid, so
    /// the tri-state decision is forced to stay at the call site rather than leaking into this projection.
    /// </para>
    /// </summary>
    private static HashSet<(string Marketplace, string Plugin)> PluginSelectionKeys(
        IReadOnlyList<SandboxPluginRef> refs
    ) => [.. refs.Select(r => (r.Marketplace, r.Plugin))];

    /// <summary>
    /// Normalizes exactly as the resolve paths do before they key <c>_sessions</c>. Without this a
    /// blank id matches no key, and a migration reports success having changed nothing — a silent no-op
    /// is the worst possible outcome for a user who just edited their plugin list.
    /// </summary>
    private static string EffectiveWorkspaceId(string workspaceId) =>
        string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId;

    /// <summary>
    /// Creates a brand-new sandbox session for <paramref name="partition"/>'s caller under
    /// <paramref name="newRef"/>'s updated plugin selection, WITHOUT touching the cached entry for that
    /// partition — the old session stays live and resolvable until <see cref="SwapPluginSelectionSessions"/>.
    /// <para>
    /// Uses the partition's explicitly-captured credential, deliberately NOT
    /// <c>CredentialFor(partition.Session.SessionId)</c>: that lookup silently degrades to the process
    /// default when the entry has since been evicted, which would create the replacement under the
    /// wrong identity. The workspace id is pinned from the partition key so the candidate lands on the
    /// partition being migrated whatever id the caller's ref carries.
    /// </para>
    /// <para>
    /// The workspace DIRECTORY and MARKETPLACE scope are likewise pinned to what the session being
    /// replaced actually ran with whenever <paramref name="newRef"/> omits them. Both are
    /// "omit ⇒ fall back to global configuration" fields inside <c>CreateSessionAsync</c>, so a caller
    /// that passes a ref carrying only the new plugin selection would otherwise silently move the
    /// replacement onto the default workspace directory and the globally-configured marketplaces —
    /// changing far more than the plugin set this migration exists to change. Orchestrators SHOULD
    /// still pass a fully-populated ref; this is the defence for when they do not.
    /// </para>
    /// <para>
    /// Throws on failure (the gateway status surfaces as
    /// <see cref="SandboxSessionUnavailableException"/>); the caller is responsible for aborting any
    /// sibling candidates already created for other partitions.
    /// </para>
    /// </summary>
    internal async Task<SandboxSession> CreatePluginSelectionCandidateAsync(
        WorkspaceRef newRef,
        PluginSelectionPartition partition,
        CancellationToken ct
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(newRef);
        ArgumentNullException.ThrowIfNull(partition);

        var candidateRef = newRef with
        {
            Id = partition.Key.WorkspaceId,
            // Feeding the session's own resolved leaf back in is idempotent: it is exactly what
            // SandboxGatewayOptions.ResolveWorkspace produced for the original create, and
            // ResolveWorkspace(leaf) returns that same leaf. A blank leaf means the original create
            // itself fell through to the configured default, so re-supplying blank reproduces it.
            DirectoryRelPath = string.IsNullOrWhiteSpace(newRef.DirectoryRelPath)
                ? partition.Session.WorkspaceRelPath
                : newRef.DirectoryRelPath,
            // Matches CreateSessionAsync's own "non-empty wins" test, so an empty list here is treated
            // as "not specified" on both sides rather than meaning two different things.
            Marketplaces = newRef.Marketplaces is { Count: > 0 } ? newRef.Marketplaces : partition.Session.Marketplaces,
        };

        var candidate = await CreateSessionAsync(candidateRef, ct, partition.Credential).ConfigureAwait(false);

        // Drain the creation stash here. This path deliberately bypasses AwaitAndEvictOnFailureAsync —
        // there is no cache slot to evict on failure, because a candidate is not published until the
        // swap — and that method is otherwise the ONLY drain of _unreportedCreations. Skipping it would
        // strand the entry for the lifetime of the registry and suppress SandboxCreated for a session
        // that genuinely was created. No-throw by contract.
        await PublishPendingCreationAsync(candidate).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>
    /// Best-effort teardown of a candidate that will never be committed, because a sibling candidate's
    /// creation failed or the swap lost a race. Never throws: it runs while the original failure is
    /// already propagating, and an orphaned container is a cleanup nuisance, not a correctness problem —
    /// masking the real error would be far worse.
    /// </summary>
    internal Task AbortPluginSelectionCandidateAsync(SandboxSession candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return TearDownBestEffortAsync(candidate, "abort");
    }

    /// <summary>
    /// The commit point: republishes each partition's cache entry to point at its candidate, so the next
    /// resolve hands out the new session without a gateway round trip.
    /// <para>
    /// Each entry is swapped with a compare-and-swap against the <see cref="Lazy{T}"/> observed at
    /// snapshot time, because candidate creation is seconds of gateway I/O during which the slot can be
    /// legitimately replaced by someone else — most commonly the gateway-404 recreate path, which
    /// invalidates the slot and republishes a brand-new session. An unconditional write there would drop
    /// that session on the floor: unreachable through the cache, absent from this migration's retire
    /// list, and therefore never deleted on the gateway. So a partition whose slot no longer holds the
    /// snapshotted entry is SKIPPED and its candidate returned to the caller to retire.
    /// </para>
    /// <para>
    /// Per-entry atomicity comes from <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryUpdate"/>,
    /// not from a lock — no reader of <c>_sessions</c> takes one. The batch as a whole is deliberately
    /// NOT atomic: a reader resolving partition B while this loop sits between B and C sees B migrated
    /// and C not. That is acceptable because both sessions are live and serve the same workspace; what
    /// must never happen is a lost session, which the compare-and-swap prevents.
    /// </para>
    /// <para>
    /// None of this makes two concurrent migrations of the same workspace safe — it only makes the
    /// loser's candidate detectable instead of lost. Serializing migrations is the ORCHESTRATOR's job,
    /// via the workspace store's compare-and-swap on <c>pluginsRevision</c>, which must be taken before
    /// <see cref="SnapshotPluginSelectionPartitions"/> is called.
    /// </para>
    /// </summary>
    /// <returns>
    /// The candidates that could NOT be committed because their partition had moved on. The caller must
    /// retire these — they are live gateway sessions that nothing references.
    /// </returns>
    internal IReadOnlyList<SandboxSession> SwapPluginSelectionSessions(
        IReadOnlyList<(PluginSelectionPartition Old, SandboxSession New)> commits
    )
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(commits);

        var uncommitted = new List<SandboxSession>();

        foreach (var (old, replacement) in commits)
        {
            var republished = new Lazy<Task<SandboxSession>>(
                () => Task.FromResult(replacement),
                LazyThreadSafetyMode.ExecutionAndPublication
            );

            // Reference comparison against the snapshotted Lazy: succeeds only if nothing replaced the
            // slot since the snapshot.
            if (!_sessions.TryUpdate(old.Key, republished, old.Entry))
            {
                _logger.LogWarning(
                    "Plugin-selection swap skipped for workspace {WorkspaceId} app {AppId}: the session "
                        + "slot changed while candidate {SessionId} was being created. The candidate will "
                        + "be retired.",
                    old.Key.WorkspaceId,
                    old.Key.AppId,
                    replacement.SessionId
                );
                uncommitted.Add(replacement);
            }
        }

        return uncommitted;
    }

    /// <summary>
    /// Best-effort teardown of every superseded session after a successful swap. Never throws: the swap
    /// already committed, so a retire failure must never be reported as a migration failure — the worst
    /// case is an orphaned container, and the local state is dropped either way.
    /// </summary>
    internal async Task RetirePluginSelectionSessionsAsync(IReadOnlyList<SandboxSession> oldSessions)
    {
        ArgumentNullException.ThrowIfNull(oldSessions);

        foreach (var session in oldSessions)
        {
            await TearDownBestEffortAsync(session, "retire").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Destroys <paramref name="session"/> on the gateway and drops its per-session state, swallowing
    /// every failure. Shared by the abort and retire paths, whose contracts both promise not to throw.
    /// </summary>
    /// <remarks>
    /// Two things here are load-bearing.
    /// <para>
    /// <b>Destroy BEFORE evict.</b> The gateway DELETE resolves this session's creating credential
    /// through <c>_sessionCredentials</c>, which the eviction clears. Reversing the two sends the DELETE
    /// under the process-default app id, which the gateway rejects — leaking the very container this
    /// method exists to remove.
    /// </para>
    /// <para>
    /// <b>The catch is required, not defensive padding.</b> <c>DestroySessionAsync</c> swallows its own
    /// failures, but <c>EvictSessionStateAsync</c> does not: it reaches
    /// <c>DecrementSessionRefAndMaybeDispose</c>, whose final <c>Client.Dispose()</c>/
    /// <c>Transport.Dispose()</c> pair is unguarded — the same pair that <c>DisposeAsync</c> wraps
    /// per-entry precisely because it can throw. Without this catch, one failing session would abort the
    /// caller's loop and skip every remaining session, leaking their containers too.
    /// </para>
    /// </remarks>
    private async Task TearDownBestEffortAsync(SandboxSession session, string phase)
    {
        try
        {
            await DestroySessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            await EvictSessionStateAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Best-effort {Phase} of sandbox session {SessionId} failed; continuing.",
                phase,
                session.SessionId
            );
        }
    }
}
