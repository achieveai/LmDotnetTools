using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

public class UsageRecordTests
{
    private static readonly DateTimeOffset OccurredAt = new(2026, 8, 22, 23, 45, 0, TimeSpan.Zero);

    private static UsageRecord Sample() =>
        new()
        {
            LogicalCallId = "call-1",
            ProviderAttemptId = "attempt-1",
            RootConversationId = "conv-1",
            RequestedModel = "gpt-5",
        };

    [Fact]
    public void TotalTokens_IsInputPlusCacheWritePlusOutput_ExcludingSubsets()
    {
        // cache-read is a subset of input and reasoning a subset of output — neither is re-added.
        var record = Sample() with
        {
            InputTokens = 100,
            CacheReadTokens = 30,
            CacheWriteTokens = 20,
            OutputTokens = 50,
            ReasoningTokens = 10,
        };

        record.TotalTokens.Should().Be(170); // 100 + 20 + 50
    }

    [Fact]
    public void EffectiveModelId_FallsBackToRequestedModel_WhenEffectiveMissing()
    {
        Sample().EffectiveModelId.Should().Be("gpt-5");
        (Sample() with { EffectiveModel = "gpt-5-2026" }).EffectiveModelId.Should().Be("gpt-5-2026");
    }

    [Fact]
    public void RoundTrips_ThroughJson_PreservingCoreFields()
    {
        var record = Sample() with
        {
            Revision = 7,
            InputTokens = 100,
            OutputTokens = 50,
            CacheWriteTokens = 20,
            EstimatedPublicCostMicros = 6000,
            ExecutionKind = UsageExecutionKind.SubAgent,
            OccurredAtUtc = OccurredAt,
        };

        var json = JsonSerializer.Serialize(record);
        var back = JsonSerializer.Deserialize<UsageRecord>(json)!;

        back.ProviderAttemptId.Should().Be("attempt-1");
        back.Revision.Should().Be(7);
        back.TotalTokens.Should().Be(170);
        back.EstimatedPublicCostMicros.Should().Be(6000);
        back.ExecutionKind.Should().Be(UsageExecutionKind.SubAgent);

        // The attempt's wall-clock time is what a per-day tenant rollup buckets on (#307), so it has to
        // survive the property-bag JSON round trip like every other accounting field.
        back.OccurredAtUtc.Should().Be(OccurredAt);
    }

    [Fact]
    public void Deserializing_RecordPersistedBeforeTheField_YieldsNull_NotYearOne()
    {
        // Usage is persisted as reflection-serialized JSON in a metadata property bag, so records written
        // before OccurredAtUtc existed simply lack the property. A non-nullable DateTimeOffset would
        // deserialize those to 0001-01-01 and silently drop them into a rollup bucket; null keeps
        // "unknown" honest and forces the rollup to handle it.
        const string legacyJson =
            @"{
              ""LogicalCallId"": ""call-1"",
              ""ProviderAttemptId"": ""attempt-1"",
              ""Revision"": 7,
              ""RootConversationId"": ""conv-1"",
              ""RequestedModel"": ""gpt-5"",
              ""InputTokens"": 100,
              ""OutputTokens"": 50,
              ""Currency"": ""USD"",
              ""Finalized"": true
            }";

        var back = JsonSerializer.Deserialize<UsageRecord>(legacyJson)!;

        back.ProviderAttemptId.Should().Be("attempt-1");
        back.OccurredAtUtc.Should().BeNull();
        back.OccurredAtUtc.Should().NotBe(DateTimeOffset.MinValue); // i.e. not 0001-01-01
    }

    [Fact]
    public void EarliestOccurredAt_IsFirstWins_AndNullMeansUnknown()
    {
        var early = OccurredAt;
        var late = OccurredAt.AddHours(3);

        UsageRecord.EarliestOccurredAt(early, late).Should().Be(early);
        UsageRecord.EarliestOccurredAt(late, early).Should().Be(early);
        UsageRecord.EarliestOccurredAt(null, late).Should().Be(late);
        UsageRecord.EarliestOccurredAt(early, null).Should().Be(early);
        UsageRecord.EarliestOccurredAt(null, null).Should().BeNull();
    }
}
