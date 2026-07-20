namespace AchieveAi.LmDotnetTools.LmCore.Models;

/// <summary>
///     Provenance of a resolved cost figure.
/// </summary>
public enum CostProvenance
{
    /// <summary>No cost could be resolved (unknown / retired / free / reseller-priced model).</summary>
    Unavailable,

    /// <summary>Cost estimated from the public pricing catalog.</summary>
    PublicEstimate,

    /// <summary>Cost reported directly by the provider.</summary>
    ProviderReported,
}

/// <summary>
///     The execution context that produced a usage record, for attribution within a conversation tree.
/// </summary>
public enum UsageExecutionKind
{
    /// <summary>The primary (root) agent turn.</summary>
    Primary,

    /// <summary>A directly spawned sub-agent.</summary>
    SubAgent,

    /// <summary>An LmWorkflow controller loop.</summary>
    WorkflowController,

    /// <summary>An LmWorkflow task agent.</summary>
    WorkflowTask,

    /// <summary>A continuation / restart of an existing execution.</summary>
    Continuation,
}

/// <summary>
///     A durable, idempotent record of the token usage and cost for a single billable provider attempt.
///     This is the canonical accounting fact for conversation-wide usage (issue #196): the parent
///     conversation aggregate is a pure fold of these records.
/// </summary>
/// <remarks>
///     <para>
///         Records are deduplicated by <see cref="ProviderAttemptId" />. Cumulative streaming updates,
///         retries, reconnects, and replays for the same attempt <b>replace</b> (never add to) the prior
///         record for that key — the highest <see cref="Revision" /> wins.
///     </para>
///     <para>
///         Money is stored in integer micro-units (1e-6 of <see cref="Currency" />) for deterministic
///         arithmetic — never cumulative binary floating point.
///     </para>
///     <para>
///         Token semantics (normative): <see cref="CacheReadTokens" /> ⊆ <see cref="InputTokens" />,
///         <see cref="ReasoningTokens" /> ⊆ <see cref="OutputTokens" />, and
///         <see cref="CacheWriteTokens" /> is additive, so
///         <see cref="TotalTokens" /> = input + cache-write + output.
///     </para>
/// </remarks>
public sealed record UsageRecord
{
    // --- Identity ---

    /// <summary>Identifier of the intended model call — stable across retries/fallbacks/replays.</summary>
    public required string LogicalCallId { get; init; }

    /// <summary>Identifier of a single separately-billable provider attempt. The dedup key.</summary>
    public required string ProviderAttemptId { get; init; }

    /// <summary>Per-conversation monotonic revision assigned when this record is (re)written.</summary>
    public long Revision { get; init; }

    // --- Lineage ---

    /// <summary>The root conversation this attempt is attributed to.</summary>
    public required string RootConversationId { get; init; }

    /// <summary>The parent execution id (null for the root).</summary>
    public string? ParentExecutionId { get; init; }

    /// <summary>How this attempt was produced (primary / sub-agent / workflow / continuation).</summary>
    public UsageExecutionKind ExecutionKind { get; init; } = UsageExecutionKind.Primary;

    // --- Model ---

    /// <summary>The model the caller requested.</summary>
    public required string RequestedModel { get; init; }

    /// <summary>The effective / actual provider model, when it differs from the requested one.</summary>
    public string? EffectiveModel { get; init; }

    /// <summary>The model id used for grouping — the effective model when present, else requested.</summary>
    public string EffectiveModelId => string.IsNullOrEmpty(EffectiveModel) ? RequestedModel : EffectiveModel;

    // --- Token counts (64-bit) ---

    /// <summary>Billed input tokens (includes <see cref="CacheReadTokens" />).</summary>
    public long InputTokens { get; init; }

    /// <summary>Billed output tokens (includes <see cref="ReasoningTokens" />).</summary>
    public long OutputTokens { get; init; }

    /// <summary>Cached-read tokens — a subset of <see cref="InputTokens" />.</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Cache-creation tokens — billed separately, additive to the total.</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Reasoning tokens — a subset of <see cref="OutputTokens" />.</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Total billable tokens: input + cache-write + output.</summary>
    public long TotalTokens => InputTokens + CacheWriteTokens + OutputTokens;

    // --- Cost (micro-units) ---

    /// <summary>Public-pricing cost estimate in micro-units, or null when unavailable.</summary>
    public long? EstimatedPublicCostMicros { get; init; }

    /// <summary>Provider-reported cost in micro-units, or null when the provider reports none.</summary>
    public long? ProviderReportedCostMicros { get; init; }

    /// <summary>ISO currency code for the cost figures.</summary>
    public string Currency { get; init; } = "USD";

    // --- Finalization ---

    /// <summary>True once the terminal (final accumulated) usage for this attempt has been observed.</summary>
    public bool Finalized { get; init; }
}
