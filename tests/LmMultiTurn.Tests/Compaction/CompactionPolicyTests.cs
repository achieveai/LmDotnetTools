using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// One case per row of the spec §5.3 decision table, plus the two cross-row cases the spec names
/// (hard threshold overrides cooldown; a hot cache defers an economic compaction). Every input is
/// explicit so each row is pinned by the condition that distinguishes it from its neighbours.
/// </summary>
public class CompactionPolicyTests
{
    private const long Window = 100_000;
    private const long Reserve = 10_000;

    // Usable window = 90k. Utilization = tokens / usable.
    private const long BelowWarn = 60_000; // 0.67
    private const long AtWarn = 63_000; // 0.70
    private const long AtCompact = 72_000; // 0.80
    private const long AtHard = 80_000; // tokens + reserve = 90k = window x 0.90

    private static readonly CompactionOptions Options = new() { Mode = CompactionMode.Compact };

    private static CompactionPolicyInput Input(
        long tokens,
        CompactionMode mode = CompactionMode.Compact,
        long? window = Window,
        long? cooldownUntil = null,
        long? newTokens = null,
        CacheTemperature cache = CacheTemperature.Cold,
        CompactionEconomics? economics = null
    ) =>
        new()
        {
            Mode = mode,
            EstimatedInputTokens = tokens,
            WindowTokens = window,
            ReserveTokens = Reserve,
            GenerationOrdinal = 10,
            CooldownUntilGenerationOrdinal = cooldownUntil,
            NewTokensSinceCheckpoint = newTokens,
            CacheTemperature = cache,
            Economics = economics,
        };

    private static CompactionDecision Evaluate(CompactionPolicyInput input, CompactionOptions? options = null) =>
        new CompactionPolicy(options ?? Options).Evaluate(input);

    [Fact]
    public void Row1_ModeOff_IsSkippedDisabled()
    {
        var decision = Evaluate(Input(AtHard, mode: CompactionMode.Off));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.Disabled);
    }

    [Fact]
    public void Row1_KillSwitch_IsSkippedDisabled_EvenAboveHardThreshold()
    {
        var decision = Evaluate(Input(AtHard) with { Killed = true });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.Disabled);
    }

    [Fact]
    public void Row1_ProviderOwnedSession_IsSkippedProviderOwned()
    {
        var decision = Evaluate(Input(AtHard) with { ProviderOwnedSession = true });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.ProviderOwnedSession);
    }

    [Fact]
    public void Row2_UnknownWindow_IsSkippedCapacityUnknown()
    {
        var decision = Evaluate(Input(tokens: 90_000, window: null));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.CapacityUnknown);
        decision.Summary.Utilization.Should().BeNull();
    }

    [Fact]
    public void Row2_UnknownWindow_AboveAbsoluteWarn_Warns()
    {
        var decision = Evaluate(Input(tokens: Options.WarnAbsoluteTokens + 1, window: null));

        decision.Decision.Should().Be(CompactionDecisionKinds.Warn);
        decision.Reason.Should().Be(CompactionSkipReasons.CapacityUnknown);
    }

    [Fact]
    public void Row3_OwedContinuation_IsSkippedUnsafeState()
    {
        var decision = Evaluate(Input(AtHard) with { LoopState = new CutBlockingState(OwedContinuations: 1) });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.UnsafeState);
    }

    [Fact]
    public void Row3_InterruptedTurn_IsSkippedUnsafeState()
    {
        var decision = Evaluate(Input(AtHard) with { LoopState = new CutBlockingState(InterruptedTurns: 1) });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.UnsafeState);
    }

    [Fact]
    public void Row3_LiveDeferredToolCall_IsSkippedUnsafeState()
    {
        // The live-coordinator guard: a deferred AskUserQuestion or a parked Wait that the coordinator
        // still tracks blocks the cut before any row is read (corpus (g)/(h)).
        var decision = Evaluate(Input(AtHard) with { LiveDeferredCount = 1 });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.UnsafeState);
    }

    [Fact]
    public void Row4_HardThreshold_Compacts()
    {
        var decision = Evaluate(Input(AtHard));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
        decision.Reason.Should().Be(CompactionPolicy.HardReason);
        decision.TargetTokens.Should().Be((long)(Options.TargetRatio * (Window - Reserve)));
    }

    [Fact]
    public void Row4_HardThreshold_OverridesCooldown()
    {
        var decision = Evaluate(Input(AtHard, cooldownUntil: 20, newTokens: 0));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
        decision.Reason.Should().Be(CompactionPolicy.HardReason);
    }

    [Fact]
    public void Row4_HardThreshold_IgnoresHotCacheAndEconomics()
    {
        var unprofitable = new CompactionEconomics
        {
            InputRatePerMillion = 1m,
            OutputRatePerMillion = 1m,
            CacheWriteRatePerMillion = 1000m,
            CachingEnabled = true,
        };

        var decision = Evaluate(Input(AtHard, cache: CacheTemperature.Hot, economics: unprofitable));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
        decision.Reason.Should().Be(CompactionPolicy.HardReason);
    }

    [Fact]
    public void Row5_CooldownByGenerations_IsSkippedCooldown()
    {
        var decision = Evaluate(Input(AtCompact, cooldownUntil: 12, newTokens: 50_000));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.Cooldown);
        decision.Summary.CooldownRemaining.Should().Be(2);
    }

    [Fact]
    public void Row5_CooldownByNewTokenFloor_IsSkippedCooldown()
    {
        var decision = Evaluate(Input(AtCompact, cooldownUntil: 5, newTokens: Options.CooldownNewTokens - 1));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.Cooldown);
    }

    [Fact]
    public void Row5_CooldownElapsed_DoesNotSkip()
    {
        var decision = Evaluate(Input(AtCompact, cooldownUntil: 10, newTokens: Options.CooldownNewTokens));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
    }

    [Fact]
    public void Row6_CompactRatio_ColdCache_CompactsEconomically()
    {
        var decision = Evaluate(Input(AtCompact));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
        decision.Reason.Should().Be(CompactionPolicy.EconomicReason);
        decision.Summary.Utilization.Should().BeApproximately(0.80, 0.001);
    }

    [Fact]
    public void Row6_CompactRatio_HotCache_IsSkippedCacheHot()
    {
        var decision = Evaluate(Input(AtCompact, cache: CacheTemperature.Hot));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.CacheHot);
        decision.Summary.CacheTemperature.Should().Be(CacheTemperature.Hot);
    }

    [Fact]
    public void Row6_PredictedSavings_FollowSpecFormula()
    {
        // (tokens - target) x generations x inputRate - summaryCost - target x cacheWriteRate; with caching
        // off the rewrite term is zero and the compaction pays for itself.
        var economics = new CompactionEconomics
        {
            InputRatePerMillion = 3m,
            OutputRatePerMillion = 15m,
            CacheWriteRatePerMillion = 3.75m,
            CachingEnabled = false,
        };
        var target = (long)(Options.TargetRatio * (Window - Reserve)); // 40,500
        var reuse = (AtCompact - target) * Options.ExpectedFutureGenerations * 3m;
        var summary = ((AtCompact - target) * 3m) + (Options.CheckpointTokenCap * 15m);

        var decision = Evaluate(Input(AtCompact, economics: economics));

        decision.Decision.Should().Be(CompactionDecisionKinds.Compact);
        decision.Summary.PredictedSavingsMicros.Should().Be((long)(reuse - summary));
    }

    [Fact]
    public void Row6_CacheRewriteCost_CanMakeTheCompactionUnprofitable()
    {
        // Same rates with caching on: re-caching the 40.5k-token target prefix at the cache-write rate
        // outweighs three generations of reuse, so the economic row declines under the default floor.
        var economics = new CompactionEconomics
        {
            InputRatePerMillion = 3m,
            OutputRatePerMillion = 15m,
            CacheWriteRatePerMillion = 3.75m,
            CachingEnabled = true,
        };
        var target = (long)(Options.TargetRatio * (Window - Reserve));
        var reuse = (AtCompact - target) * Options.ExpectedFutureGenerations * 3m;
        var summary = ((AtCompact - target) * 3m) + (Options.CheckpointTokenCap * 15m);
        var rewrite = target * 3.75m;

        var decision = Evaluate(Input(AtCompact, economics: economics));

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.BelowThreshold);
        decision.Summary.PredictedSavingsMicros.Should().Be((long)(reuse - summary - rewrite)).And.BeNegative();
    }

    [Fact]
    public void Row6_SavingsBelowMinimum_IsSkippedBelowThreshold()
    {
        var economics = new CompactionEconomics
        {
            InputRatePerMillion = 3m,
            OutputRatePerMillion = 15m,
            CachingEnabled = false,
        };
        var options = Options with { MinPredictedSavingsMicros = long.MaxValue };

        var decision = Evaluate(Input(AtCompact, economics: economics), options);

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.BelowThreshold);
        decision.Summary.PredictedSavingsMicros.Should().BePositive();
    }

    [Fact]
    public void Row6_UnpricedModel_CannotClearAPositiveFloor_ButPassesTheDefaultFloor()
    {
        var floor = Options with { MinPredictedSavingsMicros = 1 };

        Evaluate(Input(AtCompact), floor).Reason.Should().Be(CompactionSkipReasons.BelowThreshold);
        Evaluate(Input(AtCompact)).Decision.Should().Be(CompactionDecisionKinds.Compact);
    }

    [Fact]
    public void Row7_WarnRatio_Warns()
    {
        var decision = Evaluate(Input(AtWarn));

        decision.Decision.Should().Be(CompactionDecisionKinds.Warn);
        decision.Summary.Utilization.Should().BeApproximately(0.70, 0.001);
    }

    [Fact]
    public void Row8_BelowWarn_IsNoAction()
    {
        var decision = Evaluate(Input(BelowWarn));

        decision.Decision.Should().Be(CompactionDecisionKinds.NoAction);
        decision.Reason.Should().BeNull();
    }

    [Fact]
    public void MaxCompactionsPerRun_IsSkippedMaxPerRun_EvenAtHardThreshold()
    {
        var decision = Evaluate(Input(AtHard) with { CompactionsThisRun = Options.MaxCompactionsPerRun });

        decision.Decision.Should().Be(CompactionDecisionKinds.Skipped);
        decision.Reason.Should().Be(CompactionSkipReasons.MaxPerRun);
    }

    [Fact]
    public void WarnMode_DowngradesCompactToWarn_KeepingTheReason()
    {
        var decision = Evaluate(Input(AtHard, mode: CompactionMode.Warn));

        decision.Decision.Should().Be(CompactionDecisionKinds.Warn);
        decision.Reason.Should().Be(CompactionPolicy.HardReason);
    }

    [Fact]
    public void ShadowMode_ReportsShadow_ForACompactRow()
    {
        var decision = Evaluate(Input(AtCompact, mode: CompactionMode.Shadow));

        decision.Decision.Should().Be(CompactionDecisionKinds.Shadow);
        decision.Reason.Should().Be(CompactionPolicy.EconomicReason);
        decision.IsCompact.Should().BeTrue();
    }

    [Fact]
    public void Summary_CarriesTheInputsEveryDecisionIsJudgedOn()
    {
        var decision = Evaluate(Input(AtCompact, cache: CacheTemperature.Cold));

        decision.Summary.Tokens.Should().Be(AtCompact);
        decision.Summary.Window.Should().Be(Window);
        decision.Summary.Reserve.Should().Be(Reserve);
        decision.Summary.CacheTemperature.Should().Be(CacheTemperature.Cold);
        decision.Summary.Decision.Should().Be(decision.Decision);
        decision.Summary.Reason.Should().Be(decision.Reason);
    }
}
