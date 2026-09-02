namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Pins how the stream parser turns an adaptive-thinking model's thinking block into messages,
///     for both values of <c>thinking.display</c> (#709).
/// </summary>
/// <remarks>
///     With <c>display: "summarized"</c> the block carries real <c>thinking_delta</c> text and must
///     produce one Plain <see cref="ReasoningMessage" /> alongside the Encrypted signature. With
///     Anthropic's default <c>display: "omitted"</c> the Copilot backend still emits the block, but
///     with a single empty delta and only a signature — that must NOT produce a Plain message, or the
///     client renders an empty thinking pill. These are event fixtures, not live calls: adaptive
///     thinking is free to skip thinking entirely on a simple prompt, so no test may assert that a
///     thinking block arrives.
/// </remarks>
public class AdaptiveThinkingStreamParserTests
{
    [Fact]
    public void ProcessEvent_SummarizedDisplay_ProducesPlainReasoningWithJoinedText()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());
        parser.ProcessEvent("event", BuildThinkingBlockStart());

        // The three deltas an adaptive model streams when a summary is requested.
        var updates = new[] { "Bre", "aking it down: 17 x 23 = 340 + 51 = 391.", "\n\n" }
            .SelectMany(text => parser.ProcessEvent("event", BuildThinkingDelta(text)))
            .ToList();

        Assert.All(updates, message => Assert.IsType<ReasoningUpdateMessage>(message));

        var signatureMessages = parser.ProcessEvent("event", BuildSignatureDelta("ErUBCkYIBxgCKkD3"));
        var encrypted = Assert.IsType<ReasoningMessage>(Assert.Single(signatureMessages));
        Assert.Equal(ReasoningVisibility.Encrypted, encrypted.Visibility);
        Assert.Equal("ErUBCkYIBxgCKkD3", encrypted.Reasoning);

        var stopMessages = parser.ProcessEvent("event", BuildContentBlockStop());
        var plain = Assert.IsType<ReasoningMessage>(Assert.Single(stopMessages));
        Assert.Equal(ReasoningVisibility.Plain, plain.Visibility);
        Assert.Equal("Breaking it down: 17 x 23 = 340 + 51 = 391.\n\n", plain.Reasoning);
    }

    [Fact]
    public void ProcessEvent_OmittedDisplay_ProducesNoPlainReasoning()
    {
        var parser = new AnthropicStreamParser();
        parser.ProcessEvent("event", BuildMessageStart());
        parser.ProcessEvent("event", BuildThinkingBlockStart());

        // What the backend sends when `display` is left at its "omitted" default: one empty delta.
        var emitted = parser
            .ProcessEvent("event", BuildThinkingDelta(string.Empty))
            .Concat(parser.ProcessEvent("event", BuildSignatureDelta("ErUBCkYIBxgCKkD3")))
            .Concat(parser.ProcessEvent("event", BuildContentBlockStop()))
            .ToList();

        // The signature still arrives — as Encrypted, which the client hides.
        var reasoning = emitted.OfType<ReasoningMessage>().ToList();
        Assert.Equal(ReasoningVisibility.Encrypted, Assert.Single(reasoning).Visibility);
        Assert.DoesNotContain(reasoning, message => message.Visibility == ReasoningVisibility.Plain);
    }

    private static string BuildMessageStart() =>
        JsonSerializer.Serialize(
            new
            {
                type = "message_start",
                message = new
                {
                    id = "msg_thinking_01",
                    type = "message",
                    role = "assistant",
                    model = "claude-sonnet-5",
                    content = Array.Empty<object>(),
                    usage = new { input_tokens = 18, output_tokens = 0 },
                },
            }
        );

    private static string BuildThinkingBlockStart() =>
        JsonSerializer.Serialize(
            new
            {
                type = "content_block_start",
                index = 0,
                content_block = new
                {
                    type = "thinking",
                    thinking = string.Empty,
                    signature = string.Empty,
                },
            }
        );

    private static string BuildThinkingDelta(string thinking) =>
        JsonSerializer.Serialize(
            new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "thinking_delta", thinking },
            }
        );

    private static string BuildSignatureDelta(string signature) =>
        JsonSerializer.Serialize(
            new
            {
                type = "content_block_delta",
                index = 0,
                delta = new { type = "signature_delta", signature },
            }
        );

    private static string BuildContentBlockStop() =>
        JsonSerializer.Serialize(new { type = "content_block_stop", index = 0 });
}
