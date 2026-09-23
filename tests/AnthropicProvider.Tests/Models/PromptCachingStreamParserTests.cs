using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

public class PromptCachingStreamParserTests
{
    [Fact]
    public void ProcessEvent_CacheMetrics_PreservedAcrossMessageDelta()
    {
        var parser = new AnthropicStreamParser();

        // message_start with cache metrics
        parser.ProcessEvent(
            "message_start",
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_test",
                    "type": "message",
                    "role": "assistant",
                    "model": "claude-sonnet-4-20250514",
                    "content": [],
                    "stop_reason": null,
                    "stop_sequence": null,
                    "usage": {
                        "input_tokens": 200,
                        "output_tokens": 0,
                        "cache_creation_input_tokens": 1500,
                        "cache_read_input_tokens": 3000
                    }
                }
            }
            """
        );

        // content_block_start (text)
        parser.ProcessEvent(
            "content_block_start",
            """{"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}"""
        );

        // content_block_delta (text)
        parser.ProcessEvent(
            "content_block_delta",
            """{"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "Hello world"}}"""
        );

        // content_block_stop
        parser.ProcessEvent("content_block_stop", """{"type": "content_block_stop", "index": 0}""");

        // message_delta — this overwrites _usage but cache metrics should be preserved
        var results = parser.ProcessEvent(
            "message_delta",
            """
            {
                "type": "message_delta",
                "delta": {"stop_reason": "end_turn", "stop_sequence": null},
                "usage": {"output_tokens": 50}
            }
            """
        );

        // Find UsageMessage
        var usageMessage = results.OfType<UsageMessage>().FirstOrDefault();
        Assert.NotNull(usageMessage);

        // Cache read tokens should be preserved from message_start
        Assert.Equal(3000, usageMessage.Usage.TotalCachedTokens);

        // Cache creation tokens should be in extra properties
        Assert.Equal(1500, usageMessage.Usage.GetExtraProperty<int>("cache_creation_input_tokens"));

        // Standard usage fields should still be correct
        Assert.Equal(50, usageMessage.Usage.CompletionTokens);

        // Input tokens from message_start should be preserved even after message_delta overwrites _usage
        Assert.Equal(200, usageMessage.Usage.PromptTokens);
        Assert.Equal(250, usageMessage.Usage.TotalTokens);
    }

    [Fact]
    public void ProcessEvent_NoCacheMetrics_UsageMessageStillWorks()
    {
        var parser = new AnthropicStreamParser();

        // message_start without cache metrics (cache fields = 0)
        parser.ProcessEvent(
            "message_start",
            """
            {
                "type": "message_start",
                "message": {
                    "id": "msg_test2",
                    "type": "message",
                    "role": "assistant",
                    "model": "claude-sonnet-4-20250514",
                    "content": [],
                    "stop_reason": null,
                    "stop_sequence": null,
                    "usage": {
                        "input_tokens": 100,
                        "output_tokens": 0,
                        "cache_creation_input_tokens": 0,
                        "cache_read_input_tokens": 0
                    }
                }
            }
            """
        );

        // content_block_start + delta + stop
        parser.ProcessEvent(
            "content_block_start",
            """{"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}"""
        );
        parser.ProcessEvent(
            "content_block_delta",
            """{"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "Hi"}}"""
        );
        parser.ProcessEvent("content_block_stop", """{"type": "content_block_stop", "index": 0}""");

        // message_delta
        var results = parser.ProcessEvent(
            "message_delta",
            """
            {
                "type": "message_delta",
                "delta": {"stop_reason": "end_turn", "stop_sequence": null},
                "usage": {"output_tokens": 25}
            }
            """
        );

        var usageMessage = results.OfType<UsageMessage>().FirstOrDefault();
        Assert.NotNull(usageMessage);

        // No cached tokens when cache_read = 0
        Assert.Equal(0, usageMessage.Usage.TotalCachedTokens);
        Assert.Null(usageMessage.Usage.InputTokenDetails);

        // Standard fields correct
        Assert.Equal(25, usageMessage.Usage.CompletionTokens);

        // Input tokens from message_start should be preserved
        Assert.Equal(100, usageMessage.Usage.PromptTokens);
        Assert.Equal(125, usageMessage.Usage.TotalTokens);
    }

    [Fact]
    public void ProcessEvent_CacheWriteWithoutTtlSplit_ReportsZeroOneHourTokens()
    {
        // This provider only sends default-TTL cache_control, so every write is a 5-minute write. Saying so
        // explicitly (0 one-hour tokens) is what lets the cost estimate be complete instead of a lower bound.
        var usage = StreamUsage("""{"input_tokens": 200, "cache_creation_input_tokens": 1500}""");

        Assert.Equal(1500, usage.GetExtraProperty<int>("cache_creation_input_tokens"));
        Assert.True(usage.ExtraProperties.ContainsKey("ephemeral_1h_input_tokens"));
        Assert.Equal(0, usage.GetExtraProperty<int>("ephemeral_1h_input_tokens"));
    }

    [Fact]
    public void ProcessEvent_CacheWriteWithTtlSplit_ReportsTheOneHourTokens()
    {
        var usage = StreamUsage(
            """
            {
                "input_tokens": 200,
                "cache_creation_input_tokens": 1500,
                "cache_creation": {"ephemeral_5m_input_tokens": 1000, "ephemeral_1h_input_tokens": 500}
            }
            """
        );

        Assert.Equal(500, usage.GetExtraProperty<int>("ephemeral_1h_input_tokens"));
    }

    [Fact]
    public void ProcessEvent_NoCacheWrite_OmitsTheOneHourSplit()
    {
        var usage = StreamUsage("""{"input_tokens": 200, "cache_creation_input_tokens": 0}""");

        Assert.False(usage.ExtraProperties.ContainsKey("ephemeral_1h_input_tokens"));
    }

    [Theory]
    [InlineData("""{"input_tokens": 200, "cache_creation_input_tokens": 0, "cache_read_input_tokens": 6400}""")]
    [InlineData("""{"input_tokens": 200, "cache_read_input_tokens": 6400}""")]
    public void ProcessEvent_ACacheHitWithNoWrite_StillStampsCacheCreationTokens_SoTheAccountingReadsAdditive(
        string messageStartUsage
    )
    {
        // input_tokens EXCLUDES the cache read on every Anthropic-shaped response, not only on the ones that
        // also wrote to the cache. Downstream (MultiTurnAgentLoop.MeasuredInputTokens) reads the presence of
        // cache_creation_input_tokens as "this provider counts additively", so a full cache hit — and a
        // DeepSeek response, which never carries the field at all — must stamp it too, at 0.
        var usage = StreamUsage(messageStartUsage);

        Assert.Equal(200, usage.PromptTokens);
        Assert.Equal(6400, usage.TotalCachedTokens);
        Assert.True(usage.ExtraProperties.ContainsKey("cache_creation_input_tokens"));
        Assert.Equal(0, usage.GetExtraProperty<int>("cache_creation_input_tokens"));
    }

    private static Usage StreamUsage(string messageStartUsage)
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent(
            "message_start",
            $$"""
            {
                "type": "message_start",
                "message": {"id": "msg_ttl", "type": "message", "role": "assistant", "model": "claude-sonnet-5",
                    "content": [], "usage": {{messageStartUsage}}}
            }
            """
        );
        var results = parser.ProcessEvent(
            "message_delta",
            """
            {
                "type": "message_delta",
                "delta": {"stop_reason": "end_turn", "stop_sequence": null},
                "usage": {"output_tokens": 10}
            }
            """
        );

        return Assert.Single(results.OfType<UsageMessage>()).Usage;
    }
}
