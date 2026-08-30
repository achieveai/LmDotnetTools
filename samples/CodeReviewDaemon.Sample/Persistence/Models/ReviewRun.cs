namespace CodeReviewDaemon.Sample.Persistence.Models;

/// <summary>
/// One review attempt for one (PR, head/base, trigger, kind, variant, mode) tuple (plan §6). The
/// identity columns below — together with the repo (which encodes provider + org + project? + repo
/// stable id) — form the uniqueness tuple; <c>head_sha</c> alone is insufficient because same-SHA
/// comment/thread updates re-trigger a review, hence <see cref="TriggerWatermark"/>. The remaining
/// columns persist the exact inputs needed to reproduce an A/B run.
/// </summary>
internal sealed record ReviewRun
{
    /// <summary>Surrogate row id (0 until persisted).</summary>
    public long Id { get; init; }

    /// <summary>FK to the normalized <c>repo</c> row (plan §7).</summary>
    public required long RepoId { get; init; }

    // ── Identity tuple (§6) ──────────────────────────────────────────────────────────────────────
    public required string PrId { get; init; }
    public required string HeadSha { get; init; }
    public required string BaseSha { get; init; }
    public required string TriggerWatermark { get; init; }
    public required string ReviewKind { get; init; }
    public required string VariantId { get; init; }
    public required string Mode { get; init; }

    // ── Reproducibility inputs (§6) ──────────────────────────────────────────────────────────────
    public string? MergeSha { get; init; }
    public string? ModelProvider { get; init; }
    public string? ModelId { get; init; }
    public string? PromptTemplateHash { get; init; }
    public string? PolicyBundleVersion { get; init; }
    public string? FeatureFlagSnapshot { get; init; }

    // ── Three axes ───────────────────────────────────────────────────────────────────────────────
    public required ReviewStage Stage { get; init; }
    public required WorkflowStatus WorkflowStatus { get; init; }
    public required PrLifecycleState PrLifecycleState { get; init; }

    // ── Confidentiality trust signal (Task 17, design §6 Risk B) ────────────────────────────────
    /// <summary>
    /// True when the PR head comes from a fork (or this is unknown). Defaults <c>true</c> (fail closed):
    /// a run nothing has positively marked as same-org must be treated as untrusted, the same as a
    /// confirmed fork PR, so <c>DaemonReviewStageExecutor.AllowsCrossRepoCoLocation</c> never co-locates
    /// the sibling private submodule beside it. Intended to be populated from the PR provider's fork
    /// indicator (GitHub <c>head.repo.fork</c>) once the poller plumbs it through — not yet wired (gap).
    /// </summary>
    public bool IsForkPr { get; init; } = true;

    /// <summary>
    /// True when the target repo is public (or this is unknown). Defaults <c>true</c> (fail closed) for
    /// the same reason as <see cref="IsForkPr"/>. Intended to be populated from the PR provider's
    /// visibility field (GitHub <c>base.repo.private</c>, inverted) once the poller plumbs it through —
    /// not yet wired (gap).
    /// </summary>
    public bool IsTargetRepoPublic { get; init; } = true;

    // ── Per-developer review feedback ────────────────────────────────────────────────────────────
    /// <summary>
    /// The identity that OPENED the PR (<see cref="Orchestration.PullRequestDescriptor.Author"/>),
    /// captured on the run so the at-close feedback extraction can address the developer's record
    /// without re-polling a PR that is by then already closed.
    /// <para>
    /// Null means the author is unknown — an ordinary outcome, not an error, and the value pre-existing
    /// rows carry. Consumers must write no feedback record at all rather than substituting a placeholder
    /// identity. It is not part of the run's identity tuple: a PR's author never changes, so it can never
    /// distinguish two runs.
    /// </para>
    /// </summary>
    public string? PrAuthor { get; init; }

    // ── What the PR says it does ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// The PR's title (GitHub <c>title</c>, ADO <c>title</c>), captured at poll time so retrieval can rank
    /// prior knowledge on what the change CLAIMS to do rather than only on which files it touched.
    /// <para>
    /// Null on rows written before this was captured, and on any provider payload that omits it. A null
    /// title is not an error — the ranking simply falls back to changed paths alone, which is exactly how it
    /// behaved before this field existed. It is not part of the run's identity tuple.
    /// </para>
    /// </summary>
    public string? PrTitle { get; init; }

    /// <summary>
    /// The PR's description body (GitHub <c>body</c>, ADO <c>description</c>) as written by its author.
    /// <para>
    /// This is fully author-controlled prose and therefore an untrusted, prompt-injection-bearing input:
    /// every consumer that renders it into an agent prompt must frame it as quoted UNTRUSTED DATA, the same
    /// as a diff or an existing comment. Ranking only TOKENIZES it, which is why the ranking may read it
    /// directly. Null when absent; not part of the run's identity tuple.
    /// </para>
    /// </summary>
    public string? PrDescription { get; init; }

    // ── Durable retry budget and permanent park (migration v8) ───────────────────────────────────
    /// <summary>
    /// How many times a failure this daemon judges STUCK rather than transient
    /// (<c>PrOrchestrator.IsGovernedFailure</c>) has been charged against this run. Cleared by a governed
    /// stage succeeding, so a run that recovers does not carry old failures toward a park it no longer
    /// deserves.
    /// <para>
    /// Durable precisely because the in-memory equivalent is not: <see cref="Orchestration.RetryGovernor"/>
    /// keeps the same count in a dictionary that <c>PrOrchestrator.ReconcileAsync</c> resets every time the
    /// stranded-run reconciler resumes the run, which is roughly every 45 minutes — so the in-memory bound
    /// was never reached and a stuck run retried forever. Nothing on the resume path may clear this one.
    /// </para>
    /// </summary>
    public int GovernedFailureCount { get; init; }

    /// <summary>
    /// When this run was PERMANENTLY parked — it spent its durable budget and nothing will attempt it
    /// again. Null (the ordinary case) means the run is live work.
    /// <para>
    /// This is the authoritative re-pick exclusion, and it is a column rather than a
    /// <see cref="WorkflowStatus"/> value because status cannot carry it: <c>ListStrandedRuns</c> selects
    /// <c>workflow_status &lt;&gt; 'Completed'</c>, so a run marked <see cref="WorkflowStatus.Failed"/>
    /// still matches it. A park is per run, and a run's identity includes <see cref="HeadSha"/>, so a new
    /// commit opens a new row with a full budget — the escape hatch needs no operator action.
    /// </para>
    /// </summary>
    public DateTimeOffset? ParkedAt { get; init; }

    /// <summary>
    /// Why this run was parked — the stage and the last error, for the operator reading the row long after
    /// the log line scrolled past. Null exactly when <see cref="ParkedAt"/> is.
    /// </summary>
    public string? ParkReason { get; init; }
}
