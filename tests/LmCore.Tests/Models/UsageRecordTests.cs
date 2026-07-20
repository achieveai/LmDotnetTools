using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Models;

public class UsageRecordTests
{
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
        };

        var json = JsonSerializer.Serialize(record);
        var back = JsonSerializer.Deserialize<UsageRecord>(json)!;

        back.ProviderAttemptId.Should().Be("attempt-1");
        back.Revision.Should().Be(7);
        back.TotalTokens.Should().Be(170);
        back.EstimatedPublicCostMicros.Should().Be(6000);
        back.ExecutionKind.Should().Be(UsageExecutionKind.SubAgent);
    }
}
