using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.Messages;

/// <summary>
///     Coverage for the live conversation-usage frame (#196, BUG 1a): the aggregate → banner-tuple
///     projection (including the per-model uncached-input rule) and its stable camelCase wire shape with the
///     <c>conversation_usage</c> discriminator, serialized through the production message converter.
/// </summary>
public class ConversationUsageMessageTests
{
    private static ConversationUsageAggregate BuildAggregate(UsageCompleteness completeness)
    {
        // m1: cache read is a subset of input (OpenAI family). m2: cache read exceeds input (Anthropic
        // additive family). Two models so Fold produces two rows and the per-row uncached rule is exercised.
        var records = new[]
        {
            new UsageRecord
            {
                LogicalCallId = "m1:g1",
                ProviderAttemptId = "m1:g1",
                RootConversationId = "conv",
                RequestedModel = "m1",
                InputTokens = 100,
                CacheReadTokens = 30,
                OutputTokens = 40,
                EstimatedPublicCostMicros = 1234,
            },
            new UsageRecord
            {
                LogicalCallId = "m2:g1",
                ProviderAttemptId = "m2:g1",
                RootConversationId = "conv",
                RequestedModel = "m2",
                InputTokens = 50,
                CacheReadTokens = 70,
                OutputTokens = 10,
            },
        };

        return ConversationUsageAggregate.Fold("conv", records, foldedRevision: 5, completeness);
    }

    [Fact]
    public void FromAggregate_MapsTotals_AndAppliesPerRowUncachedInputRule()
    {
        var aggregate = BuildAggregate(UsageCompleteness.Complete);

        var frame = ConversationUsageMessage.FromAggregate(aggregate, "conv");

        Assert.Equal("conv", frame.ThreadId);
        Assert.Equal(200, frame.TotalTokens);
        Assert.Equal(150, frame.PromptTokens);
        Assert.Equal(50, frame.CompletionTokens);
        Assert.Equal(100, frame.CachedTokens);
        Assert.Equal(0, frame.CacheCreationTokens);
        // 70 (m1: 100-30) + 50 (m2: cacheRead 70 > input 50 -> full input) = 120
        Assert.Equal(120, frame.UncachedInputTokens);
        Assert.Equal("Complete", frame.Completeness);
        Assert.Equal(1234, frame.EstimatedCostMicros);
        Assert.Null(frame.ProviderReportedCostMicros);
    }

    [Fact]
    public void Serialize_AsIMessage_UsesConversationUsageDiscriminator_AndCamelCaseFields()
    {
        IMessage frame = ConversationUsageMessage.FromAggregate(BuildAggregate(UsageCompleteness.InProgress), "conv");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(frame, options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("conversation_usage", root.GetProperty("$type").GetString());
        Assert.Equal(200, root.GetProperty("totalTokens").GetInt64());
        Assert.Equal(150, root.GetProperty("promptTokens").GetInt64());
        Assert.Equal(120, root.GetProperty("uncachedInputTokens").GetInt64());
        Assert.Equal(50, root.GetProperty("completionTokens").GetInt64());
        Assert.Equal(100, root.GetProperty("cachedTokens").GetInt64());
        Assert.Equal("InProgress", root.GetProperty("completeness").GetString());
        Assert.Equal("conv", root.GetProperty("threadId").GetString());
    }

    [Fact]
    public void RoundTrips_ThroughIMessageConverter()
    {
        IMessage original = ConversationUsageMessage.FromAggregate(BuildAggregate(UsageCompleteness.Complete), "conv");
        var options = JsonSerializerOptionsFactory.CreateForProduction();

        var json = JsonSerializer.Serialize(original, options);
        var restored = JsonSerializer.Deserialize<IMessage>(json, options);

        var frame = Assert.IsType<ConversationUsageMessage>(restored);
        Assert.Equal(200, frame.TotalTokens);
        Assert.Equal(120, frame.UncachedInputTokens);
        Assert.Equal(1234, frame.EstimatedCostMicros);
        Assert.Equal("Complete", frame.Completeness);
    }
}
