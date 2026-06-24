using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Replaying thinking blocks in multi-turn history. Real Anthropic emits thinking with an
///     encrypted <c>signature</c>; Anthropic-compatible backends (notably Kimi) emit thinking TEXT
///     but NO signature. A <c>thinking</c> content block without a signature is rejected on replay
///     with <c>400 invalid_request_error</c>. Such reasoning must be demoted to a <c>text</c> block
///     (content preserved) rather than sent as an unsigned thinking block.
/// </summary>
public class ThinkingReplayTests
{
    private static readonly JsonSerializerOptions _jsonOptions =
        AnthropicJsonSerializerOptionsFactory.CreateUniversal();

    private static GenerateReplyOptions ThinkingOptions() =>
        new()
        {
            ModelId = "kimi-for-coding",
            ExtraProperties = ImmutableDictionary.Create<string, object?>().Add("Thinking", new AnthropicThinking(2048)),
        };

    /// <summary>Every <c>thinking</c> block in the rebuilt request must carry a non-empty signature.</summary>
    private static void AssertNoUnsignedThinkingBlocks(JsonDocument doc)
    {
        foreach (var msg in doc.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var t) || t.GetString() != "thinking")
                {
                    continue;
                }

                var hasSignature =
                    block.TryGetProperty("signature", out var sig) && !string.IsNullOrEmpty(sig.GetString());
                Assert.True(
                    hasSignature,
                    "an unsigned thinking block was sent on replay; Anthropic/Kimi reject thinking-in-history without a signature"
                );
            }
        }
    }

    [Fact]
    public void FromMessages_PlainReasoningWithoutSignature_IsNotSentAsUnsignedThinkingBlock()
    {
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "What is 2+2?" },
            // Kimi-style: thinking captured as plain reasoning with NO following encrypted signature.
            new ReasoningMessage
            {
                Role = Role.Assistant,
                Reasoning = "Let me add 2 and 2.",
                Visibility = ReasoningVisibility.Plain,
            },
            new TextMessage { Role = Role.Assistant, Text = "4" },
            new TextMessage { Role = Role.User, Text = "And 3+3?" },
        };

        var request = AnthropicRequest.FromMessages(messages, ThinkingOptions());
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var doc = JsonDocument.Parse(json);

        AssertNoUnsignedThinkingBlocks(doc);
        // The reasoning content is preserved (demoted to text), not dropped.
        Assert.Contains("Let me add 2 and 2.", json);
    }

    [Fact]
    public void FromMessages_SignedThinkingBlock_IsPreserved()
    {
        // A plain thinking block immediately followed by its encrypted signature must still merge
        // into one signed thinking block (the real-Anthropic happy path must keep working).
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "What is 2+2?" },
            new ReasoningMessage
            {
                Role = Role.Assistant,
                Reasoning = "Let me add 2 and 2.",
                Visibility = ReasoningVisibility.Plain,
            },
            new ReasoningMessage
            {
                Role = Role.Assistant,
                Reasoning = "sig-abc123",
                Visibility = ReasoningVisibility.Encrypted,
            },
            new TextMessage { Role = Role.Assistant, Text = "4" },
            new TextMessage { Role = Role.User, Text = "And 3+3?" },
        };

        var request = AnthropicRequest.FromMessages(messages, ThinkingOptions());
        var json = JsonSerializer.Serialize(request, _jsonOptions);
        using var doc = JsonDocument.Parse(json);

        AssertNoUnsignedThinkingBlocks(doc);
        Assert.Contains("sig-abc123", json);
        Assert.Contains("Let me add 2 and 2.", json);
    }
}
