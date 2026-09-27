using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;

namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>
/// Task #81, Command B — <c>--run-pr &lt;repoKey&gt; &lt;prId&gt; &lt;approvedHead&gt; &lt;approvedBase&gt;</c>. An
/// operator has already looked at a <see cref="CandidatePr"/> from <see cref="ListCandidatePrsCommand"/>
/// and approved a specific head/base pair; this re-reads the PR fresh and rejects on every drift BEFORE
/// any durable admission or slot allocation, then reuses <see cref="PrOrchestrator.RunAsync"/> exactly as
/// the polling flow does. Never enables polling, never saves or reads the poll cursor.
/// </summary>
internal static class RunSinglePrCommand
{
    /// <summary>
    /// Every check that rejects a run BEFORE any durable admission or slot allocation — repo-key
    /// resolution, provider registration, a fresh re-read, lifecycle, and identity drift. Deliberately
    /// free of <see cref="ReviewStore"/>/<see cref="PrOrchestrator"/>/<see cref="IReviewCommentReader"/>:
    /// none of these checks need the callback host serving routes, so <c>Program.cs</c> calls this BEFORE
    /// <c>app.StartAsync()</c> and only starts the host at all once <see cref="RunSinglePrPrepareResult.Accepted"/>
    /// is true (security review round 1: minimize how long/whether the host is exposed for a rejected run).
    /// </summary>
    public static async Task<RunSinglePrPrepareResult> PrepareAsync(
        CodeReviewDaemonOptions options,
        IReadOnlyList<IPrProvider> providers,
        ILogger logger,
        string repoKey,
        string prId,
        string approvedHeadSha,
        string approvedBaseSha,
        CancellationToken cancellationToken
    )
    {
        var targets = PrPollTargetBuilder.Build(options, logger);
        var matches = targets
            .Where(target => string.Equals(target.Repo.DisplayName, repoKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.OutsideAllowList);
        }

        if (matches.Count > 1)
        {
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.Ambiguous);
        }

        var target = matches[0];
        var provider = PrPollTargetBuilder.ResolveProvider(providers, target);
        if (provider is null)
        {
            logger.LogWarning(
                "No IPrProvider registered for '{Provider}'; refusing to run PR '{PrId}' in '{RepoKey}'.",
                target.Provider,
                prId,
                repoKey
            );
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.OutsideAllowList);
        }

        // Fresh re-read: the caller-supplied head/base are an operator's approval of what they SAW, not an
        // authority for what admission runs against. Only a live look at the host is trusted here.
        var descriptor = await provider.GetPullRequestAsync(target.Repo, prId, cancellationToken).ConfigureAwait(false);
        if (descriptor is null)
        {
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.NotFound);
        }

        if (descriptor.LifecycleState != PrLifecycleState.Open)
        {
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.NotOpen);
        }

        // Strict string equality — no short-SHA tolerance. Any drift means the operator approved something
        // that is no longer true.
        if (
            !string.Equals(descriptor.HeadSha, approvedHeadSha, StringComparison.Ordinal)
            || !string.Equals(descriptor.BaseSha, approvedBaseSha, StringComparison.Ordinal)
        )
        {
            return RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.IdentityDrift);
        }

        return RunSinglePrPrepareResult.Ok(target, provider, descriptor);
    }

    /// <summary>
    /// The admission + orchestrator-run half of a <see cref="RunSinglePrPrepareResult.Accepted"/> outcome.
    /// Only this half needs the callback host serving routes — <c>Program.cs</c> calls it only after
    /// <c>app.StartAsync()</c> has succeeded, and always inside a <c>finally</c> that calls
    /// <c>app.StopAsync()</c>.
    /// </summary>
    /// <param name="prepared">The accepted <see cref="PrepareAsync"/> outcome to admit.</param>
    /// <param name="store">The orchestration store the admission is recorded in.</param>
    /// <param name="orchestrator">The one existing admission/slot-allocation path, reused unchanged.</param>
    /// <param name="cancellationToken">The run's own token (application-stopping / Ctrl+C).</param>
    /// <param name="commentReaders">Comment readers used to freeze the PR discussion context, if any.</param>
    /// <param name="onIdentityRevalidated">
    /// Task #82, requirement 1 — the ONE seam from which a
    /// <see cref="Workspace.Git.ReviewArtifactBranchCapability"/> grant may be minted. Invoked exactly once,
    /// and only after the SECOND fresh re-read below has matched the operator-approved head/base, carrying
    /// the target and the descriptor that check just validated. There is no flag and no operator-typed
    /// repo/PR/head anywhere near it: the grant can only ever name what this command itself re-read, so the
    /// two cannot disagree. Never invoked on any rejection, so a drifted/closed/missing PR mints nothing.
    /// </param>
    /// <param name="fresh">Explicitly supersede a settled failed run instead of resuming it.</param>
    /// <param name="canStartFresh">Validates durable failure and absence of unresolved or retained writes.</param>
    public static async Task<RunSinglePrResult> AdmitAndRunAsync(
        RunSinglePrPrepareResult prepared,
        ReviewStore store,
        PrOrchestrator orchestrator,
        CancellationToken cancellationToken,
        IReadOnlyList<IReviewCommentReader>? commentReaders = null,
        Action<PrPollTarget, PullRequestDescriptor>? onIdentityRevalidated = null,
        bool fresh = false,
        Func<ReviewRun, CancellationToken, Task<bool>>? canStartFresh = null
    )
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (!prepared.Accepted)
        {
            throw new InvalidOperationException(
                "AdmitAndRunAsync requires a prepared result accepted by PrepareAsync."
            );
        }

        var target = prepared.Target!;
        var provider = prepared.Provider!;
        var approved = prepared.Descriptor!;

        // Round 4, item 1 — close the TOCTOU window: PrepareAsync's fresh read happened before the
        // coordinator lease was even attempted and before the host started; an operator-approved PR can
        // merge, close, or force-push in that window. Re-read AGAIN, right before any durable admission,
        // reusing the exact target/provider PrepareAsync already resolved (no re-resolution, no new
        // target lookup) — and reject exactly the same way PrepareAsync would, before touching the store.
        var descriptor = await provider
            .GetPullRequestAsync(target.Repo, approved.PrId, cancellationToken)
            .ConfigureAwait(false);
        if (descriptor is null)
        {
            return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.NotFound);
        }

        if (descriptor.LifecycleState != PrLifecycleState.Open)
        {
            return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.NotOpen);
        }

        if (
            !string.Equals(descriptor.HeadSha, approved.HeadSha, StringComparison.Ordinal)
            || !string.Equals(descriptor.BaseSha, approved.BaseSha, StringComparison.Ordinal)
        )
        {
            return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.IdentityDrift);
        }

        // Every identity gate has now passed twice, the second time moments ago and against the live host.
        // This is the only point in the daemon at which an artifact-branch grant may be minted, and it is
        // handed the very descriptor that check validated — see onIdentityRevalidated's remarks.
        onIdentityRevalidated?.Invoke(target, descriptor);

        var repoId = store.EnsureRepo(target.Repo);
        var seed = new ReviewRun
        {
            RepoId = repoId,
            PrId = descriptor.PrId,
            HeadSha = descriptor.HeadSha,
            BaseSha = descriptor.BaseSha,
            TriggerWatermark = descriptor.TriggerWatermark,
            ReviewKind = target.ReviewKind,
            VariantId = target.VariantId,
            Mode = target.Mode,
            ModelId = target.ModelId,
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = descriptor.LifecycleState,
            PrAuthor = descriptor.Author,
            PrTitle = descriptor.Title,
            PrDescription = descriptor.Description,
        };

        // Round 4, item 4 — an ordinary duplicate exact-run (same commit identity already Completed) must
        // be reported explicitly rather than silently re-invoked. PrOrchestrator.RunAsync already no-ops a
        // Completed run internally, but that no-op still reads as Admitted=true to a caller that only
        // checks the boolean; peeking the store first lets this command say AlreadyCompleted instead, with
        // zero orchestrator/workflow-runner invocations.
        //
        // Round 5 — a durable, explicit redo is now supported: task #82's artifact-branch retention
        // records a single-use ReviewRerunAuthorization ONLY after it verifies a successful, non-quarantined
        // redo (never on an unknown/quarantined outcome). If one is outstanding for this exact PR and still
        // describes the freshly re-read head/base, this command supersedes the completed row with a brand
        // new one (never resetting or erasing the completed row) and runs a full review through it — the
        // ordinary AlreadyCompleted path only fires when no such authorization exists or consuming it fails.
        var existing = store.FindReviewRunByIdentity(seed);
        if (fresh)
        {
            if (
                existing is null
                || canStartFresh is null
                || !await canStartFresh(existing, cancellationToken).ConfigureAwait(false)
            )
                return RunSinglePrResult.Rejected(RunSinglePrRejectionReason.FreshStartUnsafe);

            // A new ID gives this explicit attempt its own immutable workflow scope and snapshot.
            // Keep the failed attempt intact; ordinary restart finds the higher generation and resumes it.
            _ = store.InsertRerunReviewRun(
                seed with
                {
                    TriggerWatermark =
                        $"fresh:{existing.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                    Generation = checked(existing.Generation + 1),
                }
            );
        }
        else if (existing is { WorkflowStatus: WorkflowStatus.Completed })
        {
            if (TryConsumeRerunAuthorization(store, repoId, seed, existing) is null)
            {
                return new RunSinglePrResult
                {
                    Admitted = false,
                    RejectionReason = RunSinglePrRejectionReason.AlreadyCompleted,
                    RepoKey = target.Repo.DisplayName,
                    Provider = target.Provider,
                    PrId = existing.PrId,
                    HeadSha = existing.HeadSha,
                    BaseSha = existing.BaseSha,
                    Stage = existing.Stage,
                    WorkflowStatus = existing.WorkflowStatus,
                };
            }

            // A fresh, higher-generation row now exists for this identity. Fall through to the same
            // orchestrator path below exactly as an ordinary run: PrOrchestrator.RunAsync's own internal
            // CreateOrGetReviewRun(seed) call finds THIS row (generation now outranks the completed one),
            // not the completed row, so the full review actually runs instead of no-opping again.
        }

        var frozenContext =
            seed.Mode == "collect-only"
                ? WorkflowMarkdown.InitialContext(descriptor)
                : await PrCommentContextReader
                    .ReadAsync(
                        commentReaders ?? [],
                        store,
                        target.Repo,
                        target.Provider,
                        repoId,
                        descriptor.PrId,
                        descriptor,
                        new Dictionary<string, string>(StringComparer.Ordinal),
                        cancellationToken
                    )
                    .ConfigureAwait(false);

        // Reuse the ONE existing admission/slot-allocation path — never reimplemented, never bypassed.
        var run = await orchestrator.RunAsync(seed, frozenContext, cancellationToken).ConfigureAwait(false);

        return new RunSinglePrResult
        {
            Admitted = true,
            RejectionReason = null,
            RepoKey = target.Repo.DisplayName,
            Provider = target.Provider,
            PrId = run.PrId,
            HeadSha = run.HeadSha,
            BaseSha = run.BaseSha,
            Stage = run.Stage,
            WorkflowStatus = run.WorkflowStatus,
        };
    }

    /// <summary>
    /// Round 5 — consumes an outstanding <see cref="Persistence.Models.ReviewRerunAuthorization"/> for
    /// <paramref name="seed"/>'s PR, if one exists and still describes <paramref name="seed"/>'s freshly
    /// re-read head/base, by inserting a fresh <c>review_run</c> row one generation past
    /// <paramref name="completed"/> and atomically claiming the authorization for it. Returns <c>null</c>
    /// (no authorization, a stale one, or a losing race on the claim) when the ordinary AlreadyCompleted
    /// path must stand instead — never partially: a losing claim deletes the row it just inserted so no
    /// orphaned, never-authorized row is left behind.
    /// </summary>
    private static ReviewRun? TryConsumeRerunAuthorization(
        ReviewStore store,
        long repoId,
        ReviewRun seed,
        ReviewRun completed
    )
    {
        var authorization = store.GetOutstandingRerunAuthorization(repoId, seed.PrId);
        if (authorization is null || !authorization.Describes(seed.HeadSha, seed.BaseSha))
        {
            return null;
        }

        var created = store.InsertRerunReviewRun(
            seed with
            {
                TriggerWatermark = authorization.RerunWatermark,
                Generation = completed.Generation + 1,
            }
        );

        if (store.TryConsumeRerunAuthorization(authorization.Id, seed.HeadSha, seed.BaseSha, created.Id))
        {
            return created;
        }

        store.DeleteReviewRun(created.Id);
        return null;
    }

    /// <summary>
    /// Convenience composition of <see cref="PrepareAsync"/> + <see cref="AdmitAndRunAsync"/> for callers
    /// (tests, and any future caller that already owns a live host) that don't need to split the two
    /// phases across a host-startup boundary.
    /// </summary>
    public static async Task<RunSinglePrResult> RunAsync(
        CodeReviewDaemonOptions options,
        IReadOnlyList<IPrProvider> providers,
        ReviewStore store,
        PrOrchestrator orchestrator,
        ILogger logger,
        string repoKey,
        string prId,
        string approvedHeadSha,
        string approvedBaseSha,
        CancellationToken cancellationToken,
        IReadOnlyList<IReviewCommentReader>? commentReaders = null,
        Action<PrPollTarget, PullRequestDescriptor>? onIdentityRevalidated = null
    )
    {
        var prepared = await PrepareAsync(
                options,
                providers,
                logger,
                repoKey,
                prId,
                approvedHeadSha,
                approvedBaseSha,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (!prepared.Accepted)
        {
            return RunSinglePrResult.Rejected(prepared.RejectionReason!.Value);
        }

        return await AdmitAndRunAsync(
                prepared,
                store,
                orchestrator,
                cancellationToken,
                commentReaders,
                onIdentityRevalidated
            )
            .ConfigureAwait(false);
    }
}

/// <summary>
/// One <see cref="RunSinglePrCommand.PrepareAsync"/> outcome: either every pre-admission check passed
/// (<see cref="Accepted"/>, carrying the resolved <see cref="Target"/>/<see cref="Descriptor"/> for
/// <see cref="RunSinglePrCommand.AdmitAndRunAsync"/>) or the first check that failed.
/// </summary>
internal sealed record RunSinglePrPrepareResult
{
    public required bool Accepted { get; init; }

    public RunSinglePrRejectionReason? RejectionReason { get; init; }

    public PrPollTarget? Target { get; init; }

    /// <summary>
    /// The <see cref="IPrProvider"/> <see cref="RunSinglePrCommand.PrepareAsync"/> resolved for
    /// <see cref="Target"/>. Carried forward so <see cref="RunSinglePrCommand.AdmitAndRunAsync"/> can
    /// re-read the PR (round 4, item 1's TOCTOU close) without re-resolving the provider from a target it
    /// already settled on.
    /// </summary>
    public IPrProvider? Provider { get; init; }

    public PullRequestDescriptor? Descriptor { get; init; }

    public static RunSinglePrPrepareResult Ok(
        PrPollTarget target,
        IPrProvider provider,
        PullRequestDescriptor descriptor
    ) =>
        new()
        {
            Accepted = true,
            Target = target,
            Provider = provider,
            Descriptor = descriptor,
        };

    public static RunSinglePrPrepareResult Rejected(RunSinglePrRejectionReason reason) =>
        new() { Accepted = false, RejectionReason = reason };
}

/// <summary>Why <see cref="RunSinglePrCommand.RunAsync"/> refused to admit a run, checked in this order —
/// every one of them fires before any durable admission or slot allocation.</summary>
internal enum RunSinglePrRejectionReason
{
    /// <summary>The repo key does not match any configured <c>EnabledRepos</c> entry (or no
    /// <see cref="IPrProvider"/> is registered for the resolved target).</summary>
    OutsideAllowList,

    /// <summary>The repo key matches more than one configured entry (e.g. duplicate-casing
    /// <c>EnabledRepos</c> entries) and cannot be resolved unambiguously.</summary>
    Ambiguous,

    /// <summary>The fresh re-read found no such PR.</summary>
    NotFound,

    /// <summary>The fresh re-read shows the PR is no longer open.</summary>
    NotOpen,

    /// <summary>The fresh re-read's head or base SHA disagrees with the operator-approved values.</summary>
    IdentityDrift,

    /// <summary>
    /// <c>Program.cs</c> could not acquire the same OS-level <c>WorkflowCoordinatorLease</c> the daemon
    /// holds — most likely because the daemon is still running. This value is never set by
    /// <see cref="RunSinglePrCommand.RunAsync"/> itself (which has no knowledge of process-level
    /// coordination); it exists purely so <c>Program.cs</c> can shape a fail-closed lease rejection as the
    /// same <see cref="RunSinglePrResult"/> JSON as every other rejection.
    /// </summary>
    CoordinatorLeaseUnavailable,

    /// <summary>
    /// Round 4, item 4 — the fresh re-read's exact commit identity (repo, PR, head, base, review kind,
    /// variant) already has a <see cref="Persistence.Models.WorkflowStatus.Completed"/> row. Reported
    /// explicitly, with zero orchestrator/workflow-runner invocations, rather than letting
    /// <see cref="Orchestration.PrOrchestrator.RunAsync"/>'s own no-op silently read as "admitted".
    /// <para>
    /// Round 5 — this command still never resets or erases a completed run's persisted state on its own
    /// initiative. It DOES supersede one with a brand new row, but only by consuming a durable, single-use
    /// <see cref="Persistence.Models.ReviewRerunAuthorization"/> that task #82's artifact-branch retention
    /// recorded after independently verifying a successful, non-quarantined redo — see
    /// <see cref="RunSinglePrCommand.AdmitAndRunAsync"/>'s remarks. No such authorization means this
    /// rejection still fires exactly as before.
    /// </para>
    /// </summary>
    AlreadyCompleted,

    /// <summary>A fresh attempt requires a settled failed workflow without retained or unresolved writes.</summary>
    FreshStartUnsafe,

    /// <summary>
    /// Round 4, item 2 — the run's own cancellation token (application-stopping / Ctrl+C) fired while
    /// <see cref="RunSinglePrCommand.AdmitAndRunAsync"/> was in flight. <c>Program.cs</c> still runs
    /// <c>app.StopAsync()</c> in a <c>finally</c> after this; the run is safely abandoned, not admitted.
    /// </summary>
    Cancelled,
}

/// <summary>
/// Compact, safe result of one <see cref="RunSinglePrCommand.RunAsync"/> call — no secrets, no
/// model/feature-flag configuration, no large payloads.
/// </summary>
internal sealed record RunSinglePrResult
{
    public required bool Admitted { get; init; }

    public RunSinglePrRejectionReason? RejectionReason { get; init; }

    public string? RepoKey { get; init; }

    public string? Provider { get; init; }

    public string? PrId { get; init; }

    public string? HeadSha { get; init; }

    public string? BaseSha { get; init; }

    public ReviewStage? Stage { get; init; }

    public WorkflowStatus? WorkflowStatus { get; init; }

    public static RunSinglePrResult Rejected(RunSinglePrRejectionReason reason) =>
        new() { Admitted = false, RejectionReason = reason };
}
