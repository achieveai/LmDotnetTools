using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace LmTestUtils.Tests.TestMode;

public class AnthropicSseStreamHttpContentTests
{
    [Fact]
    public async Task ReasoningStream_EmitsThinkingAndSignatureDeltas()
    {
        var plan = new InstructionPlan(
            "reasoning-signature-test",
            reasoningLength: 16,
            messages: [InstructionMessage.ForText(5)]
        );
        var content = new AnthropicSseStreamHttpContent(
            plan,
            model: "claude-sonnet-4-5-20250929",
            wordsPerChunk: 4,
            chunkDelayMs: 0
        );

        using var stream = new MemoryStream();
        await content.CopyToAsync(stream);
        var sse = Encoding.UTF8.GetString(stream.ToArray());

        Assert.Contains("\"type\":\"thinking_delta\"", sse);
        Assert.Contains("\"type\":\"signature_delta\"", sse);

        var firstThinkingDelta = sse.IndexOf("\"type\":\"thinking_delta\"", StringComparison.Ordinal);
        var firstSignatureDelta = sse.IndexOf("\"type\":\"signature_delta\"", StringComparison.Ordinal);
        var firstBlockStop = sse.IndexOf("event: content_block_stop", StringComparison.Ordinal);

        Assert.True(firstThinkingDelta >= 0, "thinking_delta event not found");
        Assert.True(firstSignatureDelta > firstThinkingDelta, "signature_delta should follow thinking deltas");
        Assert.True(firstBlockStop > firstSignatureDelta, "content_block_stop should come after signature_delta");
    }
}
