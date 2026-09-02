using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>Values of <see cref="CompactionDecisionSummary.Decision"/>.</summary>
public static class CompactionDecisionKinds
{
    public const string NoAction = "no_action";
    public const string Warn = "warn";
    public const string Shadow = "shadow";
    public const string Compact = "compact";
    public const string Skipped = "skipped";
    public const string Failed = "failed";
}

/// <summary>Skip vocabulary of spec §5.6 that #683's <see cref="CompactionReasons"/> does not already name.</summary>
public static class CompactionSkipReasons
{
    public const string Disabled = "disabled";
    public const string ProviderOwnedSession = "provider_owned_session";
    public const string CapacityUnknown = "capacity_unknown";
    public const string UnsafeState = CompactionReasons.UnsafeState;
    public const string Cooldown = "cooldown";
    public const string CacheHot = "cache_hot";
    public const string NoSafeBoundary = CompactionReasons.NoSafeBoundary;
    public const string BelowThreshold = "below_threshold";
    public const string MaxPerRun = "max_per_run";
}

/// <summary>Failure vocabulary of spec §5.6 that #683's reason classes do not already name.</summary>
public static class CompactionFailureReasons
{
    public const string EstimatorFailed = "estimator_failed";
    public const string OverflowAfterCompaction = "overflow_after_compaction";
    public const string Killed = "killed";
}

/// <summary>Rates the economic row prices a compaction with; null when the model is unpriced.</summary>
internal sealed record CompactionEconomics
{
    public required decimal InputRatePerMillion { get; init; }

    public required decimal OutputRatePerMillion { get; init; }

    /// <summary>Cache-write rate for the configured TTL; null when the model has no cache-write price.</summary>
    public decimal? CacheWriteRatePerMillion { get; init; }

    /// <summary>Whether the loop sends requests with prompt caching on.</summary>
    public bool CachingEnabled { get; init; }
}

/// <summary>Everything one policy pass is judged on (spec §5.2). Pure data; the runtime assembles it.</summary>
internal sealed record CompactionPolicyInput
{
    public required CompactionMode Mode { get; init; }

    /// <summary>Config kill switch or the environment variable.</summary>
    public bool Killed { get; init; }

    /// <summary>The thread's metadata carries provider session mappings (spec §9).</summary>
    public bool ProviderOwnedSession { get; init; }

    public required long EstimatedInputTokens { get; init; }

    public long? WindowTokens { get; init; }

    public required long ReserveTokens { get; init; }

    /// <summary>Loop-only state the cut selector cannot see in rows.</summary>
    public CutBlockingState LoopState { get; init; } = CutBlockingState.Clean;

    /// <summary>Deferred tool calls the live coordinator still tracks (deferred questions, parked waits).</summary>
    public int LiveDeferredCount { get; init; }

    public required long GenerationOrdinal { get; init; }

    public long? CooldownUntilGenerationOrdinal { get; init; }

    /// <summary>Tail tokens added since the active checkpoint; null when no checkpoint is active.</summary>
    public long? NewTokensSinceCheckpoint { get; init; }

    public int CompactionsThisRun { get; init; }

    public CacheTemperature CacheTemperature { get; init; } = CacheTemperature.Unknown;

    public CompactionEconomics? Economics { get; init; }
}

/// <summary>The typed outcome of one policy pass.</summary>
internal sealed record CompactionDecision
{
    /// <summary>One of <see cref="CompactionDecisionKinds"/>.</summary>
    public required string Decision { get; init; }

    public string? Reason { get; init; }

    /// <summary>Tokens the view should occupy after the cut; set only for a compact-worthy decision.</summary>
    public long? TargetTokens { get; init; }

    public required CompactionDecisionSummary Summary { get; init; }

    /// <summary>True for a real or shadow compaction.</summary>
    public bool IsCompact => Decision is CompactionDecisionKinds.Compact or CompactionDecisionKinds.Shadow;
}

/// <summary>
/// The decision table of spec §5.3, first match wins. Pure: no store, no clock, no rows — the runtime
/// feeds it an <see cref="CompactionPolicyInput"/> built from the in-memory estimate and the loop's own
/// state, and only a compact-worthy answer makes the runtime read the store.
/// </summary>
internal sealed class CompactionPolicy(CompactionOptions options)
{
    public const string HardReason = "hard";
    public const string EconomicReason = "economic";

    private readonly CompactionOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public CompactionOptions Options => _options;

    public CompactionDecision Evaluate(CompactionPolicyInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tokens = input.EstimatedInputTokens;
        var window = input.WindowTokens;
        var reserve = input.ReserveTokens;
        var usable = window is { } w && w - reserve > 0 ? w - reserve : (long?)null;
        var utilization = usable is { } u ? (double)tokens / u : (double?)null;
        long? cooldownRemaining =
            input.CooldownUntilGenerationOrdinal is { } until && until > input.GenerationOrdinal
                ? until - input.GenerationOrdinal
                : null;

        CompactionDecision Decide(string decision, string? reason, long? target = null, long? savings = null) =>
            new()
            {
                Decision = decision,
                Reason = reason,
                TargetTokens = target,
                Summary = new CompactionDecisionSummary
                {
                    Decision = decision,
                    Reason = reason,
                    Utilization = utilization,
                    Tokens = tokens,
                    Window = window,
                    Reserve = reserve,
                    CacheTemperature = input.CacheTemperature,
                    CooldownRemaining = cooldownRemaining,
                    PredictedSavingsMicros = savings,
                },
            };

        // Row 1: off, killed, or a loop whose provider owns the session.
        if (input.Mode == CompactionMode.Off || input.Killed)
        {
            return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.Disabled);
        }

        if (input.ProviderOwnedSession)
        {
            return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.ProviderOwnedSession);
        }

        // Row 2: no capacity figure — nothing to compare against, but a huge request is still worth a warning.
        if (usable is null)
        {
            return tokens > _options.WarnAbsoluteTokens
                ? Decide(CompactionDecisionKinds.Warn, CompactionSkipReasons.CapacityUnknown)
                : Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.CapacityUnknown);
        }

        // Row 3: cut-blocking state the loop knows about before any row is read.
        if (
            input.LoopState.OwedContinuations > 0
            || input.LoopState.InterruptedTurns > 0
            || input.LiveDeferredCount > 0
        )
        {
            return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.UnsafeState);
        }

        // Death-spiral guard: the per-run cap holds even for the hard row, because a run that has
        // already compacted twice and is still over the line will not be saved by a third summary.
        if (input.CompactionsThisRun >= _options.MaxCompactionsPerRun)
        {
            return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.MaxPerRun);
        }

        var target = (long)(_options.TargetRatio * usable.Value);
        var compactKind = input.Mode switch
        {
            CompactionMode.Warn => CompactionDecisionKinds.Warn,
            CompactionMode.Shadow => CompactionDecisionKinds.Shadow,
            _ => CompactionDecisionKinds.Compact,
        };

        // Row 4: hard threshold — economics and cooldown ignored.
        if (tokens + reserve >= window!.Value * _options.HardRatio)
        {
            return Decide(compactKind, HardReason, target);
        }

        // Row 5: cooldown by generations or by the new-token floor (only meaningful after a checkpoint).
        if (
            cooldownRemaining is not null
            || (input.NewTokensSinceCheckpoint is { } fresh && fresh < _options.CooldownNewTokens)
        )
        {
            return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.Cooldown);
        }

        // Row 6: economic compaction.
        if (utilization >= _options.CompactRatio)
        {
            var savings = PredictSavings(tokens, target, input.Economics);
            if (savings is { } predicted && predicted < _options.MinPredictedSavingsMicros)
            {
                return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.BelowThreshold, savings: savings);
            }

            if (savings is null && _options.MinPredictedSavingsMicros > 0)
            {
                // An unpriced model cannot clear a positive economic floor.
                return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.BelowThreshold);
            }

            if (input.CacheTemperature == CacheTemperature.Hot && utilization < _options.HardRatio)
            {
                return Decide(CompactionDecisionKinds.Skipped, CompactionSkipReasons.CacheHot, savings: savings);
            }

            return Decide(compactKind, EconomicReason, target, savings);
        }

        // Row 7: warn band.
        if (utilization >= _options.WarnRatio)
        {
            return Decide(CompactionDecisionKinds.Warn, null);
        }

        // Row 8.
        return Decide(CompactionDecisionKinds.NoAction, null);
    }

    /// <summary>
    ///     Spec §5.3: <c>(tokens − target) × ExpectedFutureGenerations × inputRate − summaryCost −
    ///     cacheRewriteCost</c>, in micro-dollars (tokens × rate-per-million is already micros). The summary
    ///     call reads roughly the rows it replaces and writes at most the checkpoint cap; the rewrite cost is
    ///     the target-sized prefix being cached again.
    /// </summary>
    private long? PredictSavings(long tokens, long target, CompactionEconomics? economics)
    {
        if (economics is null)
        {
            return null;
        }

        var removed = Math.Max(0, tokens - target);
        var reuse = removed * _options.ExpectedFutureGenerations * economics.InputRatePerMillion;
        var summaryCost =
            (removed * economics.InputRatePerMillion) + (_options.CheckpointTokenCap * economics.OutputRatePerMillion);
        var rewriteCost =
            economics.CachingEnabled && economics.CacheWriteRatePerMillion is { } writeRate ? target * writeRate : 0m;
        return (long)(reuse - summaryCost - rewriteCost);
    }
}
