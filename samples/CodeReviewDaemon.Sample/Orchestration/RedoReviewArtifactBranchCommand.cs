using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Git;
using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Task #82, requirement 4 — the ONLY path in the daemon that removes a retained review-artifact branch.
/// <para>
/// Requirement 5 is structural: nothing calls this. It runs when an operator types
/// <c>--redo-artifact-branch &lt;repoKey&gt; &lt;prId&gt;</c> and at no other time — no sweeper, no
/// lifecycle transition, no retry. Requirement 6 is structural too: the parameter list has no slot pool,
/// no workspace and no assignment, so source-slot cleanup and release stay exactly where they were.
/// </para>
/// <para>
/// Everything before the delete is a refusal gate, and they are ordered so that every refusal happens
/// before git is touched at all: the workflow coordinator lease must be free (which is what proves no
/// daemon is polling and no second redo is running) and the host retention checkout's repository lock must
/// be held (which is what proves no sweeper or knowledge committer is moving that working tree), then a run
/// must exist, must not already be quarantined, must carry its own durable branch receipt, must own that
/// branch and SHA, must have no active workflow assignment, must resolve to exactly one unambiguous
/// retention receipt, and must have no unresolved operation. Only then is the remote read, and only then —
/// at exactly the recorded SHA, under a server-side compare-and-swap — is the branch deleted.
/// </para>
/// <para>
/// An outcome the command cannot read as "gone" quarantines the run: a durable artifact is written, the run
/// is parked, the retention receipt is left INTACT (nothing proved the pushed branch is gone, so the
/// evidence that it exists must survive), and every later redo of the same run stops at the quarantine
/// check without issuing a single git command. There is no retry, blind or otherwise.
/// </para>
/// <para>
/// It deletes and reconciles; it never re-admits. See <see cref="Reconcile"/>.
/// </para>
/// </summary>
internal static class RedoReviewArtifactBranchCommand
{
    public static async Task<RedoReviewArtifactBranchResult> RunAsync(
        ReviewStore store,
        RepoIdentity repo,
        string prId,
        string repoRoot,
        string databasePath,
        ReviewBranchManager branchManager,
        HostGitPushAuthorization pushAuthorization,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(repo);
        ArgumentNullException.ThrowIfNull(branchManager);
        ArgumentNullException.ThrowIfNull(pushAuthorization);
        ArgumentException.ThrowIfNullOrWhiteSpace(prId);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // BEFORE every gate below, because the gates read state a running daemon is concurrently writing:
        // "no active workflow assignment" and "no unresolved operation" are both observations that a
        // poller can invalidate between the read and the delete. The daemon holds this OS-level lease for
        // its whole lifetime, so acquiring it here proves the daemon is stopped — which is also what makes
        // its polling loop provably not running — and it excludes a second concurrent redo. Fail-closed and
        // structured: a held lease is a refusal, never a crash.
        FileStream lease;
        try
        {
            lease = WorkflowCoordinatorLease.Acquire(databasePath);
        }
        catch (InvalidOperationException exception)
        {
            logger.LogError(
                exception,
                "Refusing to redo the artifact branch for {RepoKey}#{PrId}: the workflow coordinator lease is held.",
                repo.NormalizedKey,
                prId
            );
            return Refused(
                RedoReviewArtifactBranchOutcome.CoordinatorLeaseUnavailable,
                repo,
                prId,
                "Another workflow coordinator owns this database; stop the daemon before redoing a branch."
            );
        }

        using (lease)
        {
            // The SECOND lock, and it is not redundant. The coordinator lease excludes another daemon or
            // redo PROCESS; this one excludes anything sharing the host retention checkout — the knowledge
            // committer and the PR-lifecycle sweeper take it for their own checkout/merge/push sequences
            // against the very same working tree. Without it a concurrent holder could check out a branch,
            // move HEAD or re-push between the ls-remote that reads the tip, the guarded delete, and the
            // read-back that classifies the outcome, which is exactly the interleaving the compare-and-swap
            // is there to make impossible. Held across ALL git inspection and deletion, acquired before the
            // first of them, released only after the last.
            await using var repositoryLock = await HostRetentionWorkspace
                .AcquireRepositoryLockAsync(repoRoot, cancellationToken)
                .ConfigureAwait(false);
            return await RunUnderLocksAsync(
                    store,
                    repo,
                    prId,
                    repoRoot,
                    branchManager,
                    pushAuthorization,
                    logger,
                    timeProvider,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
    }

    private static async Task<RedoReviewArtifactBranchResult> RunUnderLocksAsync(
        ReviewStore store,
        RepoIdentity repo,
        string prId,
        string repoRoot,
        ReviewBranchManager branchManager,
        HostGitPushAuthorization pushAuthorization,
        ILogger logger,
        TimeProvider timeProvider,
        CancellationToken cancellationToken
    )
    {
        var run = store.GetLatestReviewRun(store.EnsureRepo(repo), prId);
        if (run is null)
        {
            return Refused(RedoReviewArtifactBranchOutcome.NoRecordedBranch, repo, prId, "No run exists for this PR.");
        }

        // First, because a quarantined run must never reach git again — not even to look.
        if (store.TryGetLatestArtifact(run.Id, ReviewArtifactKinds.ArtifactBranchQuarantineKind) is not null)
        {
            logger.LogWarning(
                "Review run {RunId} is quarantined; its artifact branch must be reconciled by hand.",
                run.Id
            );
            return Refused(
                RedoReviewArtifactBranchOutcome.Quarantined,
                repo,
                prId,
                "A previous redo could not establish the branch's state; reconcile it by hand.",
                run.Id
            );
        }

        var recorded = ReadBranchReceipt(store, run.Id);
        if (recorded is null)
        {
            return Refused(
                RedoReviewArtifactBranchOutcome.NoRecordedBranch,
                repo,
                prId,
                "This run retained no artifact branch.",
                run.Id
            );
        }

        var expectedBranch = ReviewBranchManager.BuildReviewBranchName(
            repo,
            int.Parse(prId, CultureInfo.InvariantCulture)
        );
        if (
            !string.Equals(recorded.Branch, expectedBranch, StringComparison.Ordinal)
            || !string.Equals(recorded.RepoKey, repo.NormalizedKey, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(recorded.PrId, prId, StringComparison.Ordinal)
            || !string.Equals(recorded.HeadSha, run.HeadSha, StringComparison.OrdinalIgnoreCase)
            || !IsSha(recorded.PushedSha)
        )
        {
            return Refused(
                RedoReviewArtifactBranchOutcome.OwnershipMismatch,
                repo,
                prId,
                "The recorded branch receipt does not describe this run.",
                run.Id,
                recorded
            );
        }

        if (store.ListActiveWorkflowWorkspaceRuns().Any(active => active.Id == run.Id))
        {
            return Refused(
                RedoReviewArtifactBranchOutcome.ActiveWorkflow,
                repo,
                prId,
                "A workflow workspace assignment for this run is still active.",
                run.Id,
                recorded
            );
        }

        var receipts = store.GetOutboxForRun(run.Id);
        var resolved = ResolveRetention(receipts, repo, run, recorded);
        if (resolved.Refusal is { } refusal)
        {
            return Refused(refusal.Outcome, repo, prId, refusal.Detail, run.Id, recorded);
        }
        var retention = resolved.Owning!;

        if (receipts.Any(entry => entry.Status is OutboxStatus.Pending or OutboxStatus.Sending or OutboxStatus.Leased))
        {
            return Refused(
                RedoReviewArtifactBranchOutcome.UnresolvedOperation,
                repo,
                prId,
                "An operation for this run has not resolved; its outcome is unknown.",
                run.Id,
                recorded
            );
        }

        // Every refusal gate above has passed — ownership/SHA, lock, lease, quarantine, active-workflow,
        // retention resolution and unresolved-outbox are all clear. ONLY NOW is the one guarded delete this
        // run may perform authorized (Task #85): the grant names the exact branch and exact expected SHA
        // this method just finished validating, so HostGitCommandRunner's push gate can never be reached by
        // an argv this method did not itself construct from `recorded`.
        pushAuthorization.AuthorizeGuardedDelete(recorded.Branch, recorded.PushedSha);

        var outcome = await branchManager
            .DeleteReviewBranchAsync(repoRoot, recorded.Branch, recorded.PushedSha, cancellationToken)
            .ConfigureAwait(false);

        switch (outcome)
        {
            case ReviewBranchDeletionOutcome.RemoteShaMismatch:
                return Refused(
                    RedoReviewArtifactBranchOutcome.RemoteDrift,
                    repo,
                    prId,
                    "Origin's branch is at a different commit than this run retained.",
                    run.Id,
                    recorded
                );

            case ReviewBranchDeletionOutcome.Unknown:
            default:
                Quarantine(
                    store,
                    run,
                    recorded,
                    timeProvider,
                    logger,
                    branchState: "unknown",
                    reason: "The artifact branch deletion outcome could not be established."
                );
                return Refused(
                    RedoReviewArtifactBranchOutcome.Quarantined,
                    repo,
                    prId,
                    "The deletion outcome could not be established; the run is quarantined and will not retry.",
                    run.Id,
                    recorded
                );

            case ReviewBranchDeletionOutcome.Deleted:
            case ReviewBranchDeletionOutcome.AlreadyAbsent:
                if (!Reconcile(store, run, recorded, retention, resolved.Siblings, outcome, timeProvider))
                {
                    // The branch IS gone and the durable receipt still claims it. Reporting success here
                    // would leave a receipt that can short-circuit a later retention onto a commit that no
                    // longer exists, with nothing recording that we knew. Quarantine and say so.
                    Quarantine(
                        store,
                        run,
                        recorded,
                        timeProvider,
                        logger,
                        branchState: "deleted",
                        reason: "The branch was deleted but its retention receipt could not be invalidated; "
                            + "the receipt was mutated concurrently."
                    );
                    return Refused(
                        RedoReviewArtifactBranchOutcome.Quarantined,
                        repo,
                        prId,
                        "The branch was deleted but its retention receipt could not be invalidated; the run "
                            + "is quarantined and will not retry.",
                        run.Id,
                        recorded
                    );
                }

                logger.LogInformation(
                    "Redo removed artifact branch '{Branch}' for run {RunId} ({Outcome}). The run is NOT "
                        + "re-admitted; re-run it explicitly if that is the intent.",
                    recorded.Branch,
                    run.Id,
                    outcome
                );
                // Only here: the branch is proven gone AND every receipt attesting to it was invalidated.
                // This is the single fact the one-shot run command cannot establish for itself — a
                // completed run looks identical whether or not its published output was destroyed — so the
                // side that established it records it. The quarantine and drift arms above return without
                // reaching this line, which is what "unknown never authorizes" means structurally.
                var authorization = store.TryRecordRerunAuthorization(
                    run.RepoId,
                    prId,
                    run.HeadSha,
                    run.BaseSha ?? string.Empty,
                    run.Id,
                    recorded.Branch,
                    recorded.PushedSha
                );
                if (authorization is null)
                {
                    logger.LogWarning(
                        "Run {RunId}'s branch was removed, but this pull request already has an outstanding "
                            + "rerun authorization; the existing one stands and no second was issued.",
                        run.Id
                    );
                }
                return new RedoReviewArtifactBranchResult
                {
                    Outcome =
                        outcome == ReviewBranchDeletionOutcome.Deleted
                            ? RedoReviewArtifactBranchOutcome.Deleted
                            : RedoReviewArtifactBranchOutcome.AlreadyAbsent,
                    RepoKey = repo.NormalizedKey,
                    PrId = prId,
                    ReviewRunId = run.Id,
                    Branch = recorded.Branch,
                    RecordedSha = recorded.PushedSha,
                    RerunAuthorizationId = authorization?.Id,
                    RerunWatermark = authorization?.RerunWatermark,
                };
        }
    }

    /// <summary>
    /// Picks out the retention receipt the recorded branch belongs to, and decides whether the rest of the
    /// run's retention rows make deleting the branch unambiguous.
    /// <para>
    /// A run can hold SEVERAL retention receipts: the idempotency key includes the workflow instance id, so a
    /// re-run under a new instance enqueues its own row against the same branch. Asking for "the" retention
    /// row with <c>SingleOrDefault</c> therefore threw <see cref="InvalidOperationException"/> out of an
    /// operator command on perfectly ordinary state. The receipt is identified instead by reconstructing the
    /// idempotency key of the workflow instance the branch receipt names — an exact match, not a guess.
    /// </para>
    /// <para>
    /// The siblings still matter after that. A sibling acknowledged at the SAME commit (the replay case,
    /// where a second instance's byte-identical retention left the branch where it was) attests to the very
    /// commit about to be deleted, so it is invalidated alongside the owning one. A sibling acknowledged at a
    /// DIFFERENT commit means another instance retained different content on this branch, and deleting the
    /// branch would silently destroy that too: there is no single right answer, so the command refuses and
    /// leaves everything alone.
    /// </para>
    /// </summary>
    private static RetentionResolution ResolveRetention(
        IReadOnlyList<OutboxEntry> receipts,
        RepoIdentity repo,
        ReviewRun run,
        ReviewArtifactBranchReceipt recorded
    )
    {
        var retentionRows = receipts
            .Where(entry =>
                string.Equals(entry.Operation, WorkflowArtifactOperations.RetentionOperation, StringComparison.Ordinal)
            )
            .ToList();
        var expectedKey = WorkflowArtifactOperations.BuildRetentionKey(repo, run, recorded.WorkflowInstanceId);
        var owning = retentionRows
            .Where(entry => string.Equals(entry.IdempotencyKey, expectedKey, StringComparison.Ordinal))
            .ToList();
        if (owning.Count == 0)
        {
            return RetentionResolution.Refuse(
                RedoReviewArtifactBranchOutcome.OwnershipMismatch,
                "No retention receipt belongs to the workflow instance that recorded this branch."
            );
        }
        if (owning.Count > 1)
        {
            // The idempotency key is UNIQUE, so this is unreachable through the store's own API. It is
            // handled rather than asserted because the alternative is an operator command that crashes.
            return RetentionResolution.Refuse(
                RedoReviewArtifactBranchOutcome.AmbiguousRetention,
                "Several retention receipts share one idempotency key; the durable state is ambiguous."
            );
        }

        var single = owning[0];
        if (
            single.Status != OutboxStatus.Posted
            || !string.Equals(single.ProviderResponseId, recorded.PushedSha, StringComparison.OrdinalIgnoreCase)
        )
        {
            return RetentionResolution.Refuse(
                RedoReviewArtifactBranchOutcome.OwnershipMismatch,
                "The retention receipt does not acknowledge the recorded pushed commit."
            );
        }

        var siblings = retentionRows
            .Where(entry => entry.Id != single.Id && entry.Status == OutboxStatus.Posted)
            .ToList();
        if (
            siblings.Any(entry =>
                !string.Equals(entry.ProviderResponseId, recorded.PushedSha, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return RetentionResolution.Refuse(
                RedoReviewArtifactBranchOutcome.AmbiguousRetention,
                "Another workflow instance retained a different commit on this branch; deleting it would "
                    + "discard that retention too."
            );
        }

        return new RetentionResolution(single, siblings, Refusal: null);
    }

    private sealed record RetentionResolution(
        OutboxEntry? Owning,
        IReadOnlyList<OutboxEntry> Siblings,
        (RedoReviewArtifactBranchOutcome Outcome, string Detail)? Refusal
    )
    {
        public static RetentionResolution Refuse(RedoReviewArtifactBranchOutcome outcome, string detail) =>
            new(Owning: null, Siblings: [], (outcome, detail));
    }

    /// <summary>
    /// Undoes exactly what the deleted branch attested to, and NOTHING ELSE, reporting whether it managed to.
    /// The receipts lose their acknowledgement (so nothing can later short-circuit on a commit that no longer
    /// exists) and the redo is recorded either way, because the audit record of an attempt that half-landed
    /// is worth more than the record of one that fully did.
    /// <para>
    /// Returns <c>false</c> when any conditional invalidation did not apply — the row moved between the gate
    /// that read it and this write, which is precisely the concurrent-mutation/double-invocation case. The
    /// caller must NOT report success on that: see the quarantine at its call site.
    /// </para>
    /// <para>
    /// It deliberately does not re-admit the run. Re-running a review means re-reading the pull request and
    /// re-validating its identity against what an operator approved, and this command does neither — it
    /// never contacts the provider, so the head it holds is whatever the last run recorded and may be stale.
    /// Silently returning the run to the pipeline here would re-review a PR against an identity nobody
    /// re-checked. Deletion and re-admission are separate operator intents, and the existing one-shot run
    /// command is where the second one lives, because that is the path that re-reads the PR first.
    /// </para>
    /// </summary>
    private static bool Reconcile(
        ReviewStore store,
        ReviewRun run,
        ReviewArtifactBranchReceipt recorded,
        OutboxEntry retention,
        IReadOnlyList<OutboxEntry> siblings,
        ReviewBranchDeletionOutcome outcome,
        TimeProvider timeProvider
    )
    {
        var invalidated = store.TryInvalidateOutboxReceipt(retention.Id, OutboxStatus.Posted, recorded.PushedSha);
        var invalidatedIds = new JsonArray();
        if (invalidated)
        {
            invalidatedIds.Add(retention.Id);
        }
        foreach (var sibling in siblings)
        {
            if (store.TryInvalidateOutboxReceipt(sibling.Id, OutboxStatus.Posted, recorded.PushedSha))
            {
                invalidatedIds.Add(sibling.Id);
            }
            else
            {
                invalidated = false;
            }
        }

        Record(
            store,
            run,
            ReviewArtifactKinds.ArtifactBranchRedoKind,
            new JsonObject
            {
                ["Branch"] = recorded.Branch,
                ["DeletedSha"] = recorded.PushedSha,
                ["Outcome"] = outcome.ToString(),
                ["AtUtc"] = timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                ["ReceiptsInvalidated"] = invalidatedIds,
                ["ReceiptsExpected"] = 1 + siblings.Count,
            }
        );
        return invalidated;
    }

    private static void Quarantine(
        ReviewStore store,
        ReviewRun run,
        ReviewArtifactBranchReceipt recorded,
        TimeProvider timeProvider,
        ILogger logger,
        string branchState,
        string reason
    )
    {
        var at = timeProvider.GetUtcNow();
        Record(
            store,
            run,
            ReviewArtifactKinds.ArtifactBranchQuarantineKind,
            new JsonObject
            {
                ["Branch"] = recorded.Branch,
                ["RecordedSha"] = recorded.PushedSha,
                ["AtUtc"] = at.ToString("O", CultureInfo.InvariantCulture),
                // "unknown" (the branch may or may not still be published) vs "deleted" (it is provably gone
                // and only the durable bookkeeping is behind). An operator reconciling by hand needs to know
                // which, and the two require opposite first steps.
                ["BranchState"] = branchState,
                ["Reason"] = reason,
            }
        );
        _ = store.TryMarkReviewRunParked(run.Id, at, "artifact-branch redo outcome unknown");
        logger.LogError(
            "Quarantined review run {RunId}: artifact branch '{Branch}' is in state '{BranchState}'. {Reason}",
            run.Id,
            recorded.Branch,
            branchState,
            reason
        );
    }

    private static void Record(ReviewStore store, ReviewRun run, string kind, JsonObject payload) =>
        _ = store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = kind,
                ArtifactSchemaVersion = ReviewArtifactKinds.ArtifactBranchSchemaVersion,
                Provider = "daemon",
                Payload = payload.ToJsonString(),
            }
        );

    private static ReviewArtifactBranchReceipt? ReadBranchReceipt(ReviewStore store, long runId)
    {
        var artifact = store.TryGetLatestArtifact(runId, ReviewArtifactKinds.ArtifactBranchKind);
        if (artifact is null)
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<ReviewArtifactBranchReceipt>(
                artifact.Payload,
                ReviewArtifactKinds.PayloadOptions
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static RedoReviewArtifactBranchResult Refused(
        RedoReviewArtifactBranchOutcome outcome,
        RepoIdentity repo,
        string prId,
        string detail,
        long? runId = null,
        ReviewArtifactBranchReceipt? recorded = null
    ) =>
        new()
        {
            Outcome = outcome,
            RepoKey = repo.NormalizedKey,
            PrId = prId,
            ReviewRunId = runId,
            Branch = recorded?.Branch,
            RecordedSha = recorded?.PushedSha,
            Detail = detail,
        };
}

/// <summary>
/// What one redo established. Only <see cref="Deleted"/> and <see cref="AlreadyAbsent"/> reconcile and
/// re-admit; everything else leaves the run exactly as it found it, except <see cref="Quarantined"/>, which
/// additionally stops all future redos of that run.
/// </summary>
internal enum RedoReviewArtifactBranchOutcome
{
    /// <summary>The recorded branch was present at the recorded SHA and is now proven absent.</summary>
    Deleted,

    /// <summary>The recorded branch was already gone; the durable state was reconciled to match.</summary>
    AlreadyAbsent,

    /// <summary>No run, or no durable branch receipt — there is nothing this command owns to delete.</summary>
    NoRecordedBranch,

    /// <summary>The recorded branch/SHA does not describe this run, or the retention receipt disagrees.</summary>
    OwnershipMismatch,

    /// <summary>A workflow workspace assignment for the run is still active.</summary>
    ActiveWorkflow,

    /// <summary>An operation for the run has not resolved, so its side effects are unknown.</summary>
    UnresolvedOperation,

    /// <summary>Origin's branch has moved off the recorded commit; deleting it would discard someone else's work.</summary>
    RemoteDrift,

    /// <summary>
    /// The run's retention receipts do not identify one branch unambiguously — another workflow instance
    /// acknowledged a different commit on the same branch. Nothing was deleted.
    /// </summary>
    AmbiguousRetention,

    /// <summary>
    /// The workflow coordinator lease is held, so a daemon (or another redo) is running against this
    /// database. Nothing was read or deleted.
    /// </summary>
    CoordinatorLeaseUnavailable,

    /// <summary>The outcome could not be established. The run is parked and no redo will retry it.</summary>
    Quarantined,
}

/// <summary>
/// The compact, safe result of one redo — identities, a branch name and SHAs. No credentials, no
/// configuration, no payloads.
/// </summary>
internal sealed record RedoReviewArtifactBranchResult
{
    public required RedoReviewArtifactBranchOutcome Outcome { get; init; }

    public string? RepoKey { get; init; }

    public string? PrId { get; init; }

    public long? ReviewRunId { get; init; }

    public string? Branch { get; init; }

    public string? RecordedSha { get; init; }

    /// <summary>
    /// The one-use rerun authorization a verified deletion issued, or <c>null</c> on every other outcome.
    /// Its presence is the whole signal: only a proven-absent branch with invalidated receipts produces
    /// one, so a consumer never has to reason about which refusal it is looking at.
    /// </summary>
    public long? RerunAuthorizationId { get; init; }

    /// <summary>The <c>trigger_watermark</c> the authorized rerun must be created with.</summary>
    public string? RerunWatermark { get; init; }

    public string? Detail { get; init; }
}
