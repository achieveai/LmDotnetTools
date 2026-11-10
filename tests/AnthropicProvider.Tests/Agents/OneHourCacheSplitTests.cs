using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Core;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Tests.Agents;

/// <summary>
///     The 1-hour share of a cache write, through the agent's real streaming and non-streaming paths. A share
///     the response does not report is 0 only for a 5-minute request. For a 1-hour request it stays unknown, so
///     the cost estimate is Partial instead of a Complete figure at the 5-minute rate.
/// </summary>
public class OneHourCacheSplitTests
{
    private static readonly JsonSerializerOptions s_wire = AnthropicJsonSerializerOptionsFactory.CreateForProduction();

    [Theory]
    // Recorded (sample-response-anthropic.sse.txt): message_delta repeats the count without the split.
    [InlineData(true, 619, 619, null, 619)]
    // message_delta carries its own split, so its value wins over message_start's.
    [InlineData(true, 619, 619, 300, 300)]
    // Recorded (sample-response-anthropic-websearch.sse.txt): the count grows from 517 to 11,769 with no split.
    [InlineData(true, 517, 11769, null, null)]
    [InlineData(true, 517, 11769, 11769, 11769)]
    // No split anywhere: unknown on a 1-hour request, exactly 0 on a 5-minute one.
    [InlineData(true, null, 619, null, null)]
    [InlineData(false, null, 619, null, 0)]
    public async Task AStreamedCacheWrite_TakesItsOneHourShare_FromTheLastEventThatReportsIt(
        bool oneHour,
        int? startSplit,
        int deltaWritten,
        int? deltaSplit,
        int? expected
    )
    {
        var startWritten = startSplit ?? deltaWritten;
        var client = new CannedClient(
            null,
            [
                $$$"""
                {"type": "message_start", "message": {"id": "msg_1", "type": "message", "role": "assistant",
                 "model": "claude-sonnet-5", "content": [], "usage": {{{Usage(1, startWritten, startSplit)}}}}}
                """,
                """{"type": "content_block_start", "index": 0, "content_block": {"type": "text", "text": ""}}""",
                """{"type": "content_block_delta", "index": 0, "delta": {"type": "text_delta", "text": "hi"}}""",
                """{"type": "content_block_stop", "index": 0}""",
                $$$"""
                {"type": "message_delta", "delta": {"stop_reason": "end_turn", "stop_sequence": null},
                 "usage": {{{Usage(5, deltaWritten, deltaSplit)}}}}
                """,
                """{"type": "message_stop"}""",
            ]
        );
        using var agent = new AnthropicAgent("test-agent", client);

        var replies = new List<IMessage>();
        await foreach (var reply in await agent.GenerateReplyStreamingAsync(Prompt, Options(oneHour)))
        {
            replies.Add(reply);
        }

        AssertShare(replies, deltaWritten, expected);
    }

    [Theory]
    [InlineData(true, 619, 619)]
    [InlineData(true, null, null)]
    [InlineData(false, null, 0)]
    public async Task ABufferedCacheWrite_ReportsItsOneHourShare_OnlyWhenTheResponseOrTheLifetimeMakesItKnown(
        bool oneHour,
        int? split,
        int? expected
    )
    {
        const int written = 619;
        var response = JsonSerializer.Deserialize<AnthropicResponse>(
            $$$"""
            {"id": "msg_1", "type": "message", "role": "assistant", "model": "claude-sonnet-5",
             "content": [{"type": "text", "text": "hi"}], "stop_reason": "end_turn",
             "usage": {{{Usage(5, written, split)}}}}
            """,
            s_wire
        )!;
        using var agent = new AnthropicAgent("test-agent", new CannedClient(response, []));

        var replies = await agent.GenerateReplyAsync(Prompt, Options(oneHour));

        AssertShare(replies, written, expected);
    }

    private static IMessage[] Prompt => [new TextMessage { Role = Role.User, Text = "hello" }];

    private static GenerateReplyOptions Options(bool oneHour) =>
        new()
        {
            ModelId = "claude-sonnet-5",
            PromptCaching = oneHour ? PromptCachingMode.AutoOneHour : PromptCachingMode.Auto,
        };

    private static string Usage(int output, int written, int? oneHour) =>
        oneHour is int hour
            ? $$$"""
                {"input_tokens": 9, "output_tokens": {{{output}}}, "cache_creation_input_tokens": {{{written}}},
                 "cache_creation": {"ephemeral_5m_input_tokens": {{{written
                    - hour}}}, "ephemeral_1h_input_tokens": {{{hour}}}}}
                """
            : $$$"""{"input_tokens": 9, "output_tokens": {{{output}}}, "cache_creation_input_tokens": {{{written}}}}""";

    private static void AssertShare(IEnumerable<IMessage> replies, int written, int? expected)
    {
        var usage = replies.OfType<UsageMessage>().Last().Usage;
        Assert.Equal(written, usage.GetExtraProperty<int>("cache_creation_input_tokens"));
        if (expected is null)
        {
            Assert.False(usage.ExtraProperties.ContainsKey("ephemeral_1h_input_tokens"));
        }
        else
        {
            Assert.Equal(expected, usage.GetExtraProperty<int>("ephemeral_1h_input_tokens"));
        }
    }

    private sealed class CannedClient(AnthropicResponse? response, string[] events) : IAnthropicClient
    {
        public Task<AnthropicResponse> CreateChatCompletionsAsync(
            AnthropicRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(response ?? throw new InvalidOperationException("No buffered response."));

        public Task<IAsyncEnumerable<AnthropicStreamEvent>> StreamingChatCompletionsAsync(
            AnthropicRequest request,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(StreamAsync(cancellationToken));

        private async IAsyncEnumerable<AnthropicStreamEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            foreach (var json in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return JsonSerializer.Deserialize<AnthropicStreamEvent>(json, s_wire)!;
                await Task.Yield();
            }
        }

        public void Dispose() { }
    }
}
