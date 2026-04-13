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
        parser.ProcessEvent(
            "content_block_stop",
            """{"type": "content_block_stop", "index": 0}"""
        );

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
        parser.ProcessEvent(
            "content_block_stop",
            """{"type": "content_block_stop", "index": 0}"""
        );

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
}
