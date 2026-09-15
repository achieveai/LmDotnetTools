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
///     <see cref="MaxCompactionsPerRun"/> = 2 (the policy's own attempts; fit and reactive re-cuts are bounded by
///     progress instead) bound the number of summary calls a single run's policy can make, the same shape of
///     guard the review daemon's retry budget uses (#616, #470) — a precedent, not shared code.</item>
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

    /// <summary>
    ///     Upper bound on the policy's compaction attempts (pre-emptive and economic) in one run. Fit-check and reactive
    ///     re-cuts count toward it but are not gated by it (spec 679 §5.1).
    /// </summary>
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

    /// <summary>
    ///     Fraction of the usable window one tool result may occupy in the execution view (Compact only). A
    ///     longer result is shown as head + marker + tail; the persisted row stays whole and the marker
    ///     tells the model how to read the rest with <c>RecallConversation</c>.
    /// </summary>
    public double ToolResultViewCapRatio { get; init; } = 0.20;

    /// <summary>Smallest per-result view cap, in characters, however small the window.</summary>
    public int ToolResultViewCapMinChars { get; init; } = 4_000;

    /// <summary>Largest per-result view cap, in characters; also the cap when the window is unknown.</summary>
    public int ToolResultViewCapMaxChars { get; init; } = 100_000;

    /// <summary>
    ///     Tool turns whose results stay whole when compaction clears older results from the view before
    ///     it summarizes (Compact only). Null turns clearing off.
    /// </summary>
    public int? ClearToolResultsKeepTurns { get; init; } = 3;

    /// <summary><see cref="MinTailTokens"/> never exceeds this fraction of the usable window.</summary>
    public double MinTailRatio { get; init; } = 0.15;

    /// <summary><see cref="MaxTailTokens"/> never exceeds this fraction of the usable window.</summary>
    public double MaxTailRatio { get; init; } = 0.60;

    /// <summary>
    ///     <see cref="CheckpointTokenCap"/> never exceeds this fraction of the usable window, nor falls below
    ///     <see cref="ScaledCheckpointTokenCapFloor"/> through scaling.
    /// </summary>
    public double CheckpointTokenCapRatio { get; init; } = 0.15;

    /// <summary>The least envelope cap window scaling produces; a smaller envelope cannot hold a manifest.</summary>
    public const long ScaledCheckpointTokenCapFloor = 1_000;

    /// <summary>
    ///     An automatic cut must newly cover at least this fraction of the usable window, or it is skipped with
    ///     <c>insufficient_gain</c> before any summary call. 0 turns the gate off. The fit check's re-cut, the
    ///     reactive path and a manual request are exempt.
    /// </summary>
    public double MinCompactionGainRatio { get; init; } = 0.10;

    /// <summary>
    ///     Most automatic compaction attempts (activated or failed) one thread may make within
    ///     <see cref="ThreadCompactionWindow"/>, across runs; further ones skip with <c>rate_limited</c>. 0 turns
    ///     the limit off.
    /// </summary>
    public int MaxCompactionsPerThreadWindow { get; init; } = 6;

    /// <summary>The sliding window <see cref="MaxCompactionsPerThreadWindow"/> counts over.</summary>
    public TimeSpan ThreadCompactionWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    ///     Generations skipped with <c>failure_backoff</c> after a failed compaction; doubles with each consecutive
    ///     failure up to <see cref="MaxFailureBackoffGenerations"/> and resets when a checkpoint activates. 0 turns
    ///     backoff off.
    /// </summary>
    public int FailureBackoffGenerations { get; init; } = 2;

    /// <summary>The longest failure backoff, in generations.</summary>
    public const int MaxFailureBackoffGenerations = 32;

    /// <summary>How long one summary call may take before it counts as failed.</summary>
    public TimeSpan SummaryTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Summary calls per checkpoint: a failed or timed-out call is retried until this many were made.</summary>
    public int SummaryAttempts { get; init; } = 2;

    /// <summary>The output-token limit the summary call is sent with.</summary>
    public int SummaryMaxOutputTokens { get; init; } = 8_000;

    /// <summary>Longest text one non-human row contributes to the summary prompt; longer rows are truncated.</summary>
    public int SummaryRowCharCap { get; init; } = 4_000;

    /// <summary>Upper bound on the summary prompt's row text, however large the summary model's window.</summary>
    public int SummaryPromptMaxChars { get; init; } = 400_000;

    /// <summary>The least summary prompt budget window scaling produces.</summary>
    public const int SummaryPromptMinChars = 20_000;

    /// <summary>
    ///     Generations to back off after <paramref name="consecutiveFailures"/> failures in a row:
    ///     <c>FailureBackoffGenerations × 2^(failures − 1)</c>, capped at <see cref="MaxFailureBackoffGenerations"/>.
    /// </summary>
    public int FailureBackoff(int consecutiveFailures) =>
        FailureBackoffGenerations <= 0 || consecutiveFailures <= 0
            ? 0
            : (int)
                Math.Min(
                    MaxFailureBackoffGenerations,
                    (long)FailureBackoffGenerations << Math.Min(consecutiveFailures - 1, 20)
                );

    /// <summary>
    ///     Characters of row text the summary prompt may carry for a summary model window of
    ///     <paramref name="summaryWindowTokens"/>: three quarters of what is left after the output limit, within
    ///     <see cref="SummaryPromptMinChars"/> and <see cref="SummaryPromptMaxChars"/>.
    /// </summary>
    public int SummaryPromptCharBudget(long? summaryWindowTokens)
    {
        if (summaryWindowTokens is not { } window)
        {
            return SummaryPromptMaxChars;
        }

        var chars = (long)((window - SummaryMaxOutputTokens) * DefaultCharsPerToken * 0.75);
        return (int)Math.Clamp(chars, Math.Min(SummaryPromptMinChars, SummaryPromptMaxChars), SummaryPromptMaxChars);
    }

    /// <summary>The per-result view cap in characters for a usable window of <paramref name="usableTokens"/>.</summary>
    public int ToolResultViewCapChars(long? usableTokens)
    {
        if (usableTokens is not { } usable)
        {
            return ToolResultViewCapMaxChars;
        }

        var chars = (long)(ToolResultViewCapRatio * usable * DefaultCharsPerToken);
        return (int)Math.Clamp(chars, ToolResultViewCapMinChars, ToolResultViewCapMaxChars);
    }

    /// <summary>R3's floor for a usable window: <c>min(MinTailTokens, MinTailRatio × usable)</c>.</summary>
    public long EffectiveMinTailTokens(long? usableTokens) => Scaled(MinTailTokens, MinTailRatio, usableTokens);

    /// <summary>R7's preference for a usable window: <c>min(MaxTailTokens, MaxTailRatio × usable)</c>.</summary>
    public long EffectiveMaxTailTokens(long? usableTokens) => Scaled(MaxTailTokens, MaxTailRatio, usableTokens);

    /// <summary>V9's envelope cap for a usable window, never scaled below <see cref="ScaledCheckpointTokenCapFloor"/>.</summary>
    public long EffectiveCheckpointTokenCap(long? usableTokens) =>
        usableTokens is null
            ? CheckpointTokenCap
            : Math.Min(
                CheckpointTokenCap,
                Math.Max(ScaledCheckpointTokenCapFloor, (long)(CheckpointTokenCapRatio * usableTokens.Value))
            );

    /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> naming the first out-of-range fit knob.</summary>
    public void Validate()
    {
        RequireRatio(ToolResultViewCapRatio, nameof(ToolResultViewCapRatio));
        if (ToolResultViewCapMaxChars < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToolResultViewCapMaxChars),
                ToolResultViewCapMaxChars,
                "must be positive"
            );
        }

        if (ToolResultViewCapMinChars < 1 || ToolResultViewCapMinChars > ToolResultViewCapMaxChars)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ToolResultViewCapMinChars),
                ToolResultViewCapMinChars,
                $"must be between 1 and {nameof(ToolResultViewCapMaxChars)}"
            );
        }

        if (ClearToolResultsKeepTurns is < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ClearToolResultsKeepTurns),
                ClearToolResultsKeepTurns,
                "must be at least 1, or null to turn clearing off"
            );
        }

        RequireRatio(MinTailRatio, nameof(MinTailRatio));
        RequireRatio(MaxTailRatio, nameof(MaxTailRatio));
        RequireRatio(CheckpointTokenCapRatio, nameof(CheckpointTokenCapRatio));
        Require(
            MinCompactionGainRatio is >= 0 and < 1,
            MinCompactionGainRatio,
            nameof(MinCompactionGainRatio),
            "must be in [0, 1)"
        );
        Require(
            MaxCompactionsPerThreadWindow >= 0,
            MaxCompactionsPerThreadWindow,
            nameof(MaxCompactionsPerThreadWindow),
            "must not be negative"
        );
        Require(
            MaxCompactionsPerThreadWindow == 0 || ThreadCompactionWindow > TimeSpan.Zero,
            ThreadCompactionWindow,
            nameof(ThreadCompactionWindow),
            "must be positive while the thread limit is on"
        );
        Require(
            FailureBackoffGenerations >= 0,
            FailureBackoffGenerations,
            nameof(FailureBackoffGenerations),
            "must not be negative"
        );
        Require(SummaryTimeout > TimeSpan.Zero, SummaryTimeout, nameof(SummaryTimeout), "must be positive");
        Require(SummaryAttempts is >= 1 and <= 5, SummaryAttempts, nameof(SummaryAttempts), "must be between 1 and 5");
        Require(
            SummaryMaxOutputTokens >= 1,
            SummaryMaxOutputTokens,
            nameof(SummaryMaxOutputTokens),
            "must be positive"
        );
        Require(SummaryRowCharCap >= 200, SummaryRowCharCap, nameof(SummaryRowCharCap), "must be at least 200");
        Require(
            SummaryPromptMaxChars >= SummaryRowCharCap,
            SummaryPromptMaxChars,
            nameof(SummaryPromptMaxChars),
            $"must be at least {nameof(SummaryRowCharCap)}"
        );
    }

    private static void Require(bool valid, object value, string name, string message)
    {
        if (!valid)
        {
            throw new ArgumentOutOfRangeException(name, value, message);
        }
    }

    private const int DefaultCharsPerToken = 4;

    private static long Scaled(long absolute, double ratio, long? usableTokens) =>
        usableTokens is { } usable ? Math.Min(absolute, (long)(ratio * usable)) : absolute;

    private static void RequireRatio(double value, string name)
    {
        if (value is not (> 0 and <= 1))
        {
            throw new ArgumentOutOfRangeException(name, value, "must be in (0, 1]");
        }
    }

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
