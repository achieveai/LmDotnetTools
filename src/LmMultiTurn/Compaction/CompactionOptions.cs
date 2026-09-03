namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>How far the just-in-time compaction policy is allowed to go (spec §8.1).</summary>
public enum CompactionMode
{
    /// <summary>Nothing is evaluated; the recall tool is not registered.</summary>
    Off = 0,

    /// <summary>Every decision is recorded; the provider input never changes.</summary>
    Warn = 1,

    /// <summary>A compact-worthy pass builds and validates a checkpoint without appending or activating it.</summary>
    Shadow = 2,

    /// <summary>Checkpoints are appended and activated; the execution view hides the compacted rows.</summary>
    Compact = 3,
}

/// <summary>Bounds on what one <c>RecallConversation</c> call may return (spec §6.1).</summary>
public sealed record RecallLimits
{
    /// <summary>Rows returned when the model gives no <c>limit</c>.</summary>
    public int DefaultLimit { get; init; } = 10;

    /// <summary>Largest <c>limit</c> honoured.</summary>
    public int MaxLimit { get; init; } = 40;

    /// <summary>Total characters returned when the model gives no <c>max_chars</c>.</summary>
    public int DefaultMaxChars { get; init; } = 8_000;

    /// <summary>Largest <c>max_chars</c> honoured.</summary>
    public int MaxMaxChars { get; init; } = 32_000;

    /// <summary>Per-row text cap; longer rows end with a truncation marker.</summary>
    public int RowCharCap { get; init; } = 1_500;
}

/// <summary>
/// Per-host configuration of the just-in-time compaction policy (spec §8.1). One instance is handed to
/// the primary <see cref="MultiTurnAgentLoop"/> and travels unchanged to every owned child loop through
/// <see cref="SubAgents.SubAgentOptions.ForChildLoop"/>, so a hierarchy runs one policy.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where the defaults come from.</b> Every threshold below started as a hypothesis anchored on
/// something this repository already does rather than on another product's example. #686 ran the §12.4
/// corpus (<c>tests/LmMultiTurn.Tests/Compaction/Corpus</c>, mock providers, 2,400-token window) in
/// Off, Shadow and Compact; <c>docs/features/compaction-rollout-proof/compaction-rollout-proof.md</c>
/// holds the numbers. What they said about the defaults:
/// </para>
/// <list type="bullet">
///     <item>The ratio ladder behaved as designed: warn one turn before the compact band, hard band one
///     turn before the provider's overflow, a 14–19 % peak-request reduction after a cut, and the only
///     item that overflows without compaction (a 40-turn tool run) finishes with it. Nothing observed
///     argues for moving <see cref="WarnRatio"/>, <see cref="CompactRatio"/>, <see cref="HardRatio"/>
///     or <see cref="TargetRatio"/>.</item>
///     <item>Every corpus compaction came from the hard band. The economic band (policy row 6) prices the
///     summary at <see cref="CheckpointTokenCap"/> output tokens, which no 2,400-token window can repay;
///     the same formula is positive at a 200k window, so <see cref="ExpectedFutureGenerations"/> and
///     <see cref="MinPredictedSavingsMicros"/> remain unmeasured hypotheses.</item>
///     <item>Residual cost risk: on conversations that fit the window anyway, Compact cost 22–35 % more
///     than Off, because the envelope rewrites the cached prefix (cache-hit ratio fell 12–29 points) and
///     each summary is billed. Compaction earns its cost only where the alternative is failure, which is
///     why <see cref="Mode"/> stays <see cref="CompactionMode.Off"/> and the rollout goes per route
///     through Warn and Shadow (Shadow's cost and cache profile are identical to Off's).</item>
///     <item><see cref="MaxCompactionsPerRun"/> = 2 was raised to 20 for the corpus, where one run spans
///     twenty windows; at production windows a run compacts far less often, but a tool-heavy run several
///     windows long would hit the cap and then overflow. Raise it only with
///     <see cref="CooldownNewTokens"/> in place.</item>
/// </list>
/// <para>The original anchors, still the reason each number is what it is:</para>
/// <list type="bullet">
///     <item><see cref="WarnAbsoluteTokens"/> = 100,000 is the inline estimate <c>MultiTurnAgentLoop</c>
///     already uses to label a failed run "likely exceeded the model context window"
///     (<c>largeConversationTokenEstimate</c> in <c>ExecuteAssignedRunAsync</c>).</item>
///     <item><see cref="ReserveMarginTokens"/> = 2,048 sits on top of <c>DefaultOptions.MaxToken</c>, whose
///     floor is <c>MultiTurnAgentBase.DefaultMaxTokenFloor</c> (8,192): the reserve is the output budget
///     the loop already asks for plus one quarter of it for tool-result padding.</item>
///     <item><see cref="MinTailTokens"/> / <see cref="MaxTailTokens"/> = 8k / 24k and
///     <see cref="CorrectionLookbackRuns"/> = 3 repeat <c>CutSelectorOptions</c> (#683), so the policy and
///     the cut selector agree on what a protected tail is.</item>
///     <item><see cref="NarrativeTokenCap"/> / <see cref="CheckpointTokenCap"/> = 2k / 6k repeat
///     <c>CheckpointValidationOptions</c> (#683).</item>
///     <item><see cref="WarnRatio"/> / <see cref="CompactRatio"/> / <see cref="HardRatio"/> = 0.70 / 0.80 /
///     0.90 and <see cref="TargetRatio"/> = 0.45 are chosen so that, at the sample host's
///     <c>maxTurnsPerRun: 150</c> with tool turns of a few thousand tokens each, a run crosses the compact
///     band with several tool turns of room left before the hard band; the hard band leaves exactly one
///     reserve of headroom.</item>
///     <item><see cref="CooldownGenerations"/> / <see cref="CooldownNewTokens"/> = 3 / 10k and
///     <see cref="MaxCompactionsPerRun"/> = 2 (one pre-emptive, one reactive) bound the number of summary
///     calls a single run can make, the same shape of guard the review daemon's retry budget uses (#616,
///     #470) — a precedent, not shared code.</item>
///     <item><see cref="CacheTtl"/> = 5 minutes matches the 5-minute cache-write rate <c>ModelPricing</c>
///     prices (#682) and the sample host's <c>PromptCachingMode.Auto</c>.</item>
///     <item><see cref="ExpectedFutureGenerations"/> = 3 is the economic guess the spec names; #686 could
///     not reach the band it governs (see above), so it is still the first knob to measure on a
///     production window.</item>
/// </list>
/// </remarks>
public sealed record CompactionOptions
{
    /// <summary>Environment variable that, when set to <c>1</c> or <c>true</c>, kills compaction in every loop.</summary>
    public const string KillSwitchEnvironmentVariable = "LMMULTITURN_COMPACTION_DISABLED";

    /// <summary>Default mode for every route without an override.</summary>
    public CompactionMode Mode { get; init; } = CompactionMode.Off;

    /// <summary>
    ///     Mode overrides keyed by <c>"{providerId}/{modelId}"</c>; a key without a slash matches the model id
    ///     alone. See <see cref="ResolveMode"/>.
    /// </summary>
    public IReadOnlyDictionary<string, CompactionMode>? ModeByRoute { get; init; }

    /// <summary>Configured kill switch; the environment variable is the other half (spec §8.4).</summary>
    public bool KillSwitch { get; init; }

    /// <summary>Model the summary pass runs on; null means the loop's own model (spec Q2).</summary>
    public string? SummaryModelId { get; init; }

    /// <summary>Utilization at which a <c>Warn</c> is recorded.</summary>
    public double WarnRatio { get; init; } = 0.70;

    /// <summary>Utilization at which an economic compaction is considered.</summary>
    public double CompactRatio { get; init; } = 0.80;

    /// <summary>Fraction of the whole window at which compaction is forced regardless of economics or cooldown.</summary>
    public double HardRatio { get; init; } = 0.90;

    /// <summary>Fraction of the usable window the view should occupy after a cut.</summary>
    public double TargetRatio { get; init; } = 0.45;

    /// <summary>Tokens added to <c>DefaultOptions.MaxToken</c> to form the reserve.</summary>
    public long ReserveMarginTokens { get; init; } = 2048;

    /// <summary>Smallest protected tail (R3).</summary>
    public long MinTailTokens { get; init; } = 8_000;

    /// <summary>Tail size above which the cut is flagged as leaving too much (R3).</summary>
    public long MaxTailTokens { get; init; } = 24_000;

    /// <summary>Narrative cap the validator enforces (V6).</summary>
    public long NarrativeTokenCap { get; init; } = 2_000;

    /// <summary>Whole-envelope cap the validator enforces (V6).</summary>
    public long CheckpointTokenCap { get; init; } = 6_000;

    /// <summary>Generations after a checkpoint during which no economic compaction runs.</summary>
    public int CooldownGenerations { get; init; } = 3;

    /// <summary>New tail tokens required after a checkpoint before another economic compaction.</summary>
    public long CooldownNewTokens { get; init; } = 10_000;

    /// <summary>Upper bound on compactions (pre-emptive plus reactive) in one run.</summary>
    public int MaxCompactionsPerRun { get; init; } = 2;

    /// <summary>Generations the predicted-savings formula assumes will reuse the compacted view.</summary>
    public int ExpectedFutureGenerations { get; init; } = 3;

    /// <summary>Runs the cut selector looks back for a correction (R4).</summary>
    public int CorrectionLookbackRuns { get; init; } = 3;

    /// <summary>Economic floor: an economic compaction needs at least this many predicted micro-dollars saved.</summary>
    public long MinPredictedSavingsMicros { get; init; }

    /// <summary>How long a prompt cache stays hot after the last provider call.</summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Ring length of <c>context.observations</c> kept in thread metadata.</summary>
    public int ObservationHistoryLength { get; init; } = 50;

    /// <summary>Tokens above which an unknown-window request is still worth a <c>Warn</c>.</summary>
    public long WarnAbsoluteTokens { get; init; } = 100_000;

    /// <summary>Bounds for the recall tool.</summary>
    public RecallLimits Recall { get; init; } = new();

    /// <summary>Mode for one route: the exact <c>"{providerId}/{modelId}"</c> key, then the model id alone, then <see cref="Mode"/>.</summary>
    public CompactionMode ResolveMode(string? providerId, string? modelId)
    {
        if (ModeByRoute is null || ModeByRoute.Count == 0 || string.IsNullOrEmpty(modelId))
        {
            return Mode;
        }

        if (!string.IsNullOrEmpty(providerId) && ModeByRoute.TryGetValue($"{providerId}/{modelId}", out var byRoute))
        {
            return byRoute;
        }

        return ModeByRoute.TryGetValue(modelId, out var byModel) ? byModel : Mode;
    }

    /// <summary>A template may lower the mode, never raise it (spec §8.1).</summary>
    public CompactionOptions WithModeCeiling(CompactionMode ceiling) =>
        ceiling < Mode ? this with { Mode = ceiling } : this;

    /// <summary>True when the config flag or the environment variable kills compaction.</summary>
    public bool IsKilled(Func<string, string?>? readEnvironment = null)
    {
        if (KillSwitch)
        {
            return true;
        }

        var value = (readEnvironment ?? Environment.GetEnvironmentVariable)(KillSwitchEnvironmentVariable);
        return value is not null
            && (
                value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            );
    }
}
