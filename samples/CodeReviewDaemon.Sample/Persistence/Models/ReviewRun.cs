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
}
