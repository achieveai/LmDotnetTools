using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Models;

/// <summary>
///     Protocol-divergence probe: verifies that the SSE stream emitted by
///     <see cref="AnthropicSseStreamHttpContent"/> (the wire format mock provider hosts use to
///     stand in for Anthropic's API) round-trips through <see cref="AnthropicStreamParser"/>
///     into a <see cref="TextMessage"/>.
///
///     Issue #29 was a "the mock host completes silently with no rendered content" failure where
///     each individual layer looked correct in isolation. A unit-level probe that asserts on the
///     final parsed <see cref="IMessage"/> shape catches divergence between mock-emitter and
///     production-parser the moment one drifts from the other.
/// </summary>
public sealed class AnthropicSseRoundTripProbeTests
{
    [Fact]
    public async Task Mock_emitter_output_round_trips_through_production_parser_to_TextMessage()
    {
        const string scriptedText = "round-trip-marker round-trip-marker";
        var plan = new InstructionPlan(
            "round-trip",
            reasoningLength: null,
            messages: [InstructionMessage.ForExplicitText(scriptedText)]);

        var content = new AnthropicSseStreamHttpContent(
            plan,
            model: "claude-test",
            wordsPerChunk: 1,
            chunkDelayMs: 0);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var sse = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

        var parser = new AnthropicStreamParser();
        var collected = new List<IMessage>();
        foreach (var (eventType, data) in ParseSse(sse))
        {
            collected.AddRange(parser.ProcessEvent(eventType, data));
        }

        var rendered = string.Concat(
            collected.OfType<TextMessage>().Select(m => m.Text));
        Assert.Contains(scriptedText, rendered);
    }

    [Fact]
    public async Task Mock_emitter_streams_text_deltas_in_protocol_order()
    {
        var plan = new InstructionPlan(
            "ordering",
            reasoningLength: null,
            messages: [InstructionMessage.ForExplicitText("alpha beta gamma")]);

        var content = new AnthropicSseStreamHttpContent(plan, model: "claude-test", wordsPerChunk: 1, chunkDelayMs: 0);

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        var sse = System.Text.Encoding.UTF8.GetString(buffer.ToArray());

        // Spec-shape pin: the parser depends on this sequence — message_start, then per-block
        // start/delta/stop, then message_delta, then message_stop. If anyone re-orders these,
        // every consumer of the mock emitter (production parser + Claude Agent SDK CLI) breaks.
        var iStart = sse.IndexOf("event: message_start", StringComparison.Ordinal);
        var iBlockStart = sse.IndexOf("event: content_block_start", StringComparison.Ordinal);
        var iDelta = sse.IndexOf("event: content_block_delta", StringComparison.Ordinal);
        var iBlockStop = sse.IndexOf("event: content_block_stop", StringComparison.Ordinal);
        var iMessageDelta = sse.IndexOf("event: message_delta", StringComparison.Ordinal);
        var iStop = sse.IndexOf("event: message_stop", StringComparison.Ordinal);

        Assert.True(iStart >= 0, "message_start missing");
        Assert.True(iBlockStart > iStart, "content_block_start must follow message_start");
        Assert.True(iDelta > iBlockStart, "content_block_delta must follow content_block_start");
        Assert.True(iBlockStop > iDelta, "content_block_stop must follow content_block_delta");
        Assert.True(iMessageDelta > iBlockStop, "message_delta must follow content_block_stop");
        Assert.True(iStop > iMessageDelta, "message_stop must come last");
    }

    private static IEnumerable<(string EventType, string Data)> ParseSse(string sse)
    {
        // SSE frames are delimited by a blank line; each frame has at least one event: and one
        // data: line. The mock emitter uses a single data: line per frame, which keeps this
        // helper simple.
        foreach (var frame in sse.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? eventType = null;
            string? data = null;
            foreach (var line in frame.Split('\n'))
            {
                if (line.StartsWith("event:", StringComparison.Ordinal))
                {
                    eventType = line["event:".Length..].Trim();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    data = line["data:".Length..].Trim();
                }
            }

            if (eventType is not null && data is not null)
            {
                yield return (eventType, data);
            }
        }
    }
}
