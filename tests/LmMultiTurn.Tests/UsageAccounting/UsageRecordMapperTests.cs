using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
///     Verifies <see cref="UsageRecordMapper" /> (#196): cache-creation tokens map into the additive
///     cache-write dimension (both CLR-int and provider JsonElement shapes), and id-less usage messages get
///     a distinct-per-call attempt identity rather than a shared constant that silently undercounts.
/// </summary>
public class UsageRecordMapperTests
{
    [Fact]
    public void FromUsageMessage_MapsCacheCreationTokens_FromClrInt()
    {
        var usage = new Usage { PromptTokens = 100, CompletionTokens = 40 }
            .SetExtraProperty("cache_creation_input_tokens", 25);
        var message = new UsageMessage { Usage = usage, GenerationId = "gen-1" };

        var record = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "model-A");

        record.CacheWriteTokens.Should().Be(25);
        record.TotalTokens.Should().Be(165); // 100 input + 25 cache-write + 40 output
    }

    [Fact]
    public void FromUsageMessage_MapsCacheCreationTokens_FromJsonElement()
    {
        // Providers/persistence re-hydrate ExtraProperties values as JsonElement — the mapper must read the
        // cache-creation count in that shape too.
        var usage = new Usage { PromptTokens = 100, CompletionTokens = 40 }
            .SetExtraProperty("cache_creation_input_tokens", JsonSerializer.SerializeToElement(25));
        var message = new UsageMessage { Usage = usage, GenerationId = "gen-1" };

        var record = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "model-A");

        record.CacheWriteTokens.Should().Be(25);
    }

    [Fact]
    public void FromUsageMessage_StampsProviderReportedProvenance_WhenProviderCostPresent()
    {
        var usage = new Usage { PromptTokens = 100, CompletionTokens = 40, TotalCost = 0.01 };
        var message = new UsageMessage { Usage = usage, GenerationId = "gen-1" };

        var record = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "model-A");

        record.ProviderReportedCostMicros.Should().Be(10_000);
        record.CostProvenance.Should().Be(CostProvenance.ProviderReported);
    }

    [Fact]
    public void FromUsageMessage_StampsUnavailableProvenance_WhenNoProviderCost()
    {
        var usage = new Usage { PromptTokens = 100, CompletionTokens = 40 };
        var message = new UsageMessage { Usage = usage, GenerationId = "gen-1" };

        var record = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "model-A");

        record.ProviderReportedCostMicros.Should().BeNull();
        record.CostProvenance.Should().Be(CostProvenance.Unavailable);
    }

    [Fact]
    public void CacheCreationTokens_FoldAdditively_IntoAggregateTotal()
    {
        var ledger = new UsageLedger("root");
        ledger.RecordUsage(UsageRecordMapper.FromUsageMessage(
            new UsageMessage
            {
                Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 }
                    .SetExtraProperty("cache_creation_input_tokens", 25),
                GenerationId = "gen-1",
            },
            "root",
            UsageExecutionKind.Primary,
            "model-A"));

        var snapshot = ledger.Snapshot();
        snapshot.PerModel.Should().ContainSingle();
        snapshot.PerModel[0].CacheWriteTokens.Should().Be(25);
        snapshot.TotalTokens.Should().Be(165);
    }

    [Fact]
    public void FromUsageMessage_WithoutIds_DoesNotCollapseDistinctCalls()
    {
        // Two separate provider calls arriving without a generation/run id but with different usage must NOT
        // be merged into one attempt by a shared constant key — that would MAX-collapse and undercount (#196).
        var first = new UsageMessage { Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 } };
        var second = new UsageMessage { Usage = new Usage { PromptTokens = 30, CompletionTokens = 10 } };

        var ledger = new UsageLedger("root");
        ledger.RecordUsage(UsageRecordMapper.FromUsageMessage(first, "root", UsageExecutionKind.Primary, "m"));
        ledger.RecordUsage(UsageRecordMapper.FromUsageMessage(second, "root", UsageExecutionKind.Primary, "m"));

        var snapshot = ledger.Snapshot();
        snapshot.PerModel.Should().ContainSingle();
        snapshot.PerModel[0].AttemptCount.Should().Be(2);
        snapshot.TotalTokens.Should().Be(180); // 140 + 40, not MAX-collapsed to 140
    }

    [Fact]
    public void FromUsageMessage_WithoutIds_SameObservationDedups()
    {
        // An exact replay of the same id-less observation must still resolve to one attempt.
        var message = new UsageMessage { Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 } };

        var first = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "m");
        var second = UsageRecordMapper.FromUsageMessage(message, "root", UsageExecutionKind.Primary, "m");

        first.ProviderAttemptId.Should().Be(second.ProviderAttemptId);
    }

    [Fact]
    public void FromUsageMessage_StampsOccurredAtUtc_FromTheInjectedClock()
    {
        // The only production factory for a UsageRecord is where the attempt's wall-clock time is stamped
        // (#307). It reads an injected TimeProvider — the codebase's established clock seam — rather than
        // DateTimeOffset.UtcNow, so the stamp is asserted exactly instead of merely "not null".
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 22, 23, 50, 0, TimeSpan.Zero));
        var message = new UsageMessage
        {
            Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
            GenerationId = "gen-1",
        };

        var record = UsageRecordMapper.FromUsageMessage(
            message, "root", UsageExecutionKind.Primary, "model-A", clock);

        record.OccurredAtUtc.Should().Be(clock.Now);
    }

    [Fact]
    public void FromUsageMessage_WithoutAClock_StampsSystemTime()
    {
        // Production call sites pass no clock, so the default must still produce a usable timestamp — an
        // unstamped record is indistinguishable from a legacy one and drops out of the rollup entirely.
        var before = DateTimeOffset.UtcNow;
        var record = UsageRecordMapper.FromUsageMessage(
            new UsageMessage { Usage = new Usage { PromptTokens = 1 }, GenerationId = "gen-1" },
            "root",
            UsageExecutionKind.Primary,
            "model-A");
        var after = DateTimeOffset.UtcNow;

        record.OccurredAtUtc.Should().NotBeNull();
        record.OccurredAtUtc!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    /// <summary>A clock frozen at a known instant, so the stamped time is asserted exactly.</summary>
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }
}
