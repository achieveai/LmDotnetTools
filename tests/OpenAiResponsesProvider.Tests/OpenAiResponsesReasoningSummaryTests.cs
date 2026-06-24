using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Reasoning-summary streaming. The OpenAI Responses API delivers a reasoning-capable model's
///     human-readable summary via <c>response.reasoning_summary_text.delta</c>/<c>.done</c> events
///     (NOT inside the reasoning output_item's <c>summary</c> array, which stays empty while
///     streaming). The parser previously decoded these to <see cref="GenericResponseEvent"/> and the
///     agent dropped them, so requested reasoning summaries never surfaced as thinking blocks.
/// </summary>
public sealed class OpenAiResponsesReasoningSummaryTests
{
    private sealed class ScriptedClient(IReadOnlyList<ResponseEvent> events) : IOpenAiResponsesClient
    {
        public async IAsyncEnumerable<ResponseEvent> StreamResponseAsync(
            ResponseCreateRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (var ev in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }

    [Fact]
    public void Parser_decodes_reasoning_summary_text_delta_and_done()
    {
        var delta = ResponseEventParser.Parse(
            "{\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":0,\"delta\":\"Let me \"}"
        );
        var done = ResponseEventParser.Parse(
            "{\"type\":\"response.reasoning_summary_text.done\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":0,\"text\":\"Let me think.\"}"
        );

        delta.Should().BeOfType<ResponseReasoningSummaryTextDeltaEvent>().Which.Delta.Should().Be("Let me ");
        done.Should().BeOfType<ResponseReasoningSummaryTextDoneEvent>().Which.Text.Should().Be("Let me think.");
    }

    [Fact]
    public async Task Agent_emits_reasoning_from_summary_stream()
    {
        ResponseEvent[] events =
        [
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCreated },
            new ResponseReasoningSummaryTextDeltaEvent
            {
                Type = ResponseEventTypes.ReasoningSummaryTextDelta,
                ItemId = "rs_1",
                OutputIndex = 0,
                SummaryIndex = 0,
                Delta = "Let me ",
            },
            new ResponseReasoningSummaryTextDeltaEvent
            {
                Type = ResponseEventTypes.ReasoningSummaryTextDelta,
                ItemId = "rs_1",
                OutputIndex = 0,
                SummaryIndex = 0,
                Delta = "think.",
            },
            new ResponseReasoningSummaryTextDoneEvent
            {
                Type = ResponseEventTypes.ReasoningSummaryTextDone,
                ItemId = "rs_1",
                OutputIndex = 0,
                SummaryIndex = 0,
                Text = "Let me think.",
            },
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCompleted },
        ];

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(events));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions { GenerationId = "run-1" }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        // Streaming deltas surface as reasoning updates...
        var updates = messages.OfType<ReasoningUpdateMessage>().ToList();
        updates.Should().NotBeEmpty("reasoning summary deltas must stream as updates");
        // ...and the completed summary as a final reasoning message carrying the full text.
        var finalReasoning = messages.OfType<ReasoningMessage>().ToList();
        finalReasoning.Should().ContainSingle("the summary stream completes with one reasoning message");
        finalReasoning[0].Reasoning.Should().Be("Let me think.");
        finalReasoning[0].GenerationId.Should().Be("run-1");

        // These are provider SUMMARIES, not full chain-of-thought. Persisting them as Plain makes an
        // Anthropic-format replay serialize them as UNSIGNED thinking blocks (rejected with 400). They
        // must carry ReasoningVisibility.Summary so AnthropicRequest emits them as text instead.
        updates.Should().OnlyContain(
            u => u.Visibility == ReasoningVisibility.Summary,
            "reasoning summary deltas are provider summaries, not unsigned thinking"
        );
        finalReasoning[0].Visibility.Should().Be(ReasoningVisibility.Summary);
    }

    // The reasoning output_item's summary[].text array is the same provider-summary content as the
    // streaming summary events; TryExtractReasoning must classify it as Summary (encrypted_content
    // stays Encrypted) so it never replays as an unsigned Anthropic thinking block.
    [Fact]
    public async Task Agent_emits_summary_visibility_from_reasoning_item_summary_array()
    {
        var reasoningItem = System.Text.Json.JsonDocument.Parse(
            "{\"type\":\"reasoning\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"step one\"}]}"
        ).RootElement;

        ResponseEvent[] events =
        [
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCreated },
            new ResponseOutputItemEvent
            {
                Type = ResponseEventTypes.OutputItemDone,
                OutputIndex = 0,
                Item = reasoningItem,
            },
            new ResponseLifecycleEvent { Type = ResponseEventTypes.ResponseCompleted },
        ];

        using var agent = new OpenAiResponsesAgent("test", new ScriptedClient(events));
        var stream = await agent.GenerateReplyStreamingAsync(
            [new TextMessage { Role = Role.User, Text = "go" }],
            new GenerateReplyOptions { GenerationId = "run-1" }
        );

        var messages = new List<IMessage>();
        await foreach (var message in stream)
        {
            messages.Add(message);
        }

        var reasoning = messages.OfType<ReasoningMessage>().ToList();
        reasoning.Should().ContainSingle();
        reasoning[0].Reasoning.Should().Be("step one");
        reasoning[0].Visibility.Should().Be(ReasoningVisibility.Summary);
    }
}
