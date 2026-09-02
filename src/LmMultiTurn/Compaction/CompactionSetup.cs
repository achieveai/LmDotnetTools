namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
/// Everything a <see cref="MultiTurnAgentLoop"/> needs to run the just-in-time compaction policy (spec
/// 679 §5), handed to the loop's tool-control constructor. Null on the loop means the feature is
/// absent: the request is built exactly as before and no recall tool is registered. A hierarchy shares
/// one setup through <see cref="SubAgents.SubAgentOptions.Compaction"/>; every loop builds its own
/// summarizer over its own provider unless <see cref="Summarizer"/> is supplied.
/// </summary>
public sealed record CompactionSetup
{
    /// <summary>Policy flags and thresholds.</summary>
    public CompactionOptions Options { get; init; } = new();

    /// <summary>
    ///     The summary pass. Null builds a <see cref="ProviderCheckpointSummarizer"/> over the loop's own
    ///     provider agent with <see cref="CompactionOptions.SummaryModelId"/> (or the loop's model).
    /// </summary>
    public ICheckpointSummarizer? Summarizer { get; init; }

    /// <summary>
    ///     Context window in tokens for a model id, or null when unknown (§5.3 row 2). The capacity
    ///     resolver of #681 adapts into this delegate; a host without one leaves it null and the policy
    ///     answers <c>capacity_unknown</c>.
    /// </summary>
    public Func<string?, long?>? ResolveWindowTokens { get; init; }

    /// <summary>Provider id used with the model id to look up <see cref="CompactionOptions.ModeByRoute"/>.</summary>
    public string? ProviderId { get; init; }

    /// <summary>
    ///     Decides whether a provider failure is a context-window overflow (the reactive path). Null uses
    ///     the built-in verdict: an <see cref="HttpRequestException"/> with status 400 or 413 while the
    ///     request was already at or above the usable window. A transport abort never qualifies (spec Q1).
    /// </summary>
    public Func<Exception, bool>? IsContextOverflow { get; init; }

    /// <summary>Clock for cache temperature, cooldown timestamps and checkpoint times.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>Environment reader for the kill switch variable; null reads the process environment.</summary>
    public Func<string, string?>? ReadEnvironment { get; init; }
}

/// <summary>
/// Raised when the harness would otherwise send a request it knows exceeds the usable window: after the
/// reactive compaction and its one retry both overflowed, or when a pre-emptive compaction failed and
/// the request is still beyond <c>window − reserve</c> (spec 679 §5.6, #678 AC 7). The run completes with
/// <c>isError</c> and the message starts with <see cref="Reason"/>.
/// </summary>
public sealed class ContextOverflowException(string reason, string message, Exception? inner = null)
    : InvalidOperationException($"{reason}: {message}", inner)
{
    /// <summary>The typed reason, normally <see cref="CompactionFailureReasons.OverflowAfterCompaction"/>.</summary>
    public string Reason { get; } = reason;
}
