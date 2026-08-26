using CodeReviewDaemon.Sample.Orchestration;

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
    public PrDraftState PrDraftState { get; init; } = PrDraftState.Unknown;

    // ── Confidentiality trust signal (Task 17, design §6 Risk B) ────────────────────────────────
    /// <summary>
    /// True when the PR head comes from a fork, OR when nothing could establish that it doesn't. Defaults
    /// <c>true</c> (fail closed): a run nothing has positively marked as same-trust must be treated as
    /// untrusted, the same as a confirmed fork PR, so
    /// <c>DaemonReviewStageExecutor.AllowsCrossRepoCoLocation</c> never co-locates a sibling private
    /// submodule beside it.
    /// <para>
    /// Populated by <c>PrPollingService</c> from <see cref="Orchestration.PullRequestDescriptor.IsForkPr"/>,
    /// which is nullable there ("the provider could not tell") and collapses to this default. The provider
    /// signal is GitHub <c>head.repo.full_name != base.repo.full_name</c> / ADO's <c>forkSource</c> — NOT
    /// GitHub's <c>head.repo.fork</c> flag, which is true for every PR opened inside a fork including
    /// same-repo ones, and would deny co-location to runs that deserve it.
    /// </para>
    /// <para>
    /// This carry went missing for the daemon's whole life, and the default made the omission invisible:
    /// nothing wrote either field, so the gate read <c>!true &amp;&amp; !true</c> and every configured
    /// cross-repo sibling was refused on every run. If a future change moves the collapse out of the poller,
    /// it must land somewhere equally singular — two places deciding what "unknown" means is how a
    /// fail-closed default quietly becomes fail-open.
    /// </para>
    /// </summary>
    public bool IsForkPr { get; init; } = true;

    /// <summary>
    /// True when the target repo is public, OR when nothing could establish that it isn't. Defaults
    /// <c>true</c> (fail closed) for the same reason as <see cref="IsForkPr"/>, and populated the same way:
    /// <c>PrPollingService</c> collapses the provider's nullable
    /// <see cref="Orchestration.PullRequestDescriptor.IsTargetRepoPublic"/> (GitHub <c>base.repo.private</c>
    /// inverted; ADO <c>repository.project.visibility</c>).
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
    /// The PR's title (GitHub <c>title</c>, ADO <c>title</c>), captured at poll time so the review brief
    /// can state what the change claims to do rather than only which files it touched.
    /// <para>
    /// Null on rows written before this was captured, and on any provider payload that omits it. Renders
    /// as absent — never as a placeholder — because "the author wrote no title" and "we did not capture
    /// one" must stay distinguishable to a reviewer weighing whether the diff matches its stated intent.
    /// </para>
    /// </summary>
    public string? PrTitle { get; init; }

    /// <summary>
    /// The PR's description body (GitHub <c>body</c>, ADO <c>description</c>) as written by its author.
    /// <para>
    /// This is fully author-controlled prose and therefore an untrusted, prompt-injection-bearing input:
    /// every consumer that renders it into an agent prompt must frame it as quoted UNTRUSTED DATA, the
    /// same as a diff or an existing comment. Null when absent; not part of the run's identity tuple.
    /// </para>
    /// </summary>
    public string? PrDescription { get; init; }

    /// <summary>
    /// The branch the PR merges INTO (GitHub <c>base.ref</c>, ADO <c>targetRefName</c>, short form).
    /// Lets the reviewer weigh risk by destination — a change landing on a release branch warrants a
    /// different bar than the same change landing on a feature branch. Null when the provider omits it.
    /// </summary>
    public string? PrTargetBranch { get; init; }
}
