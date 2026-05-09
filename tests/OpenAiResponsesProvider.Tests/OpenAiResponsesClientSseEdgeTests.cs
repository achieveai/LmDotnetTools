using System.Text;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
///     Edge-case coverage for <see cref="OpenAiResponsesClient.ReadSseAsync"/>. The mock SSE
///     handler always emits well-formed <c>data: {json}\n\n</c> frames, but real OpenAI servers
///     interleave <c>: keep-alive</c> comments, terminate with <c>data: [DONE]</c>, or close
///     without a trailing blank line — these tests pin the parser's tolerance to those shapes.
/// </summary>
public sealed class OpenAiResponsesClientSseEdgeTests
{
    [Fact]
    public async Task ReadSseAsync_done_sentinel_terminates_stream()
    {
        var sse = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}\n\n"
            + "data: [DONE]\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{}}\n\n";

        var events = await ReadAllAsync(sse);

        events.Should().ContainSingle();
        events[0].Should().BeOfType<ResponseLifecycleEvent>();
        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
    }

    [Fact]
    public async Task ReadSseAsync_skips_comment_lines()
    {
        var sse = ": keep-alive\n\n"
            + "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}\n\n"
            + ": another comment\n"
            + "data: {\"type\":\"response.completed\",\"response\":{}}\n\n";

        var events = await ReadAllAsync(sse);

        events.Should().HaveCount(2);
        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
        events[1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
    }

    [Fact]
    public async Task ReadSseAsync_dispatches_final_data_block_without_trailing_blank_line()
    {
        // No "\n\n" terminator on the last event — server closed early.
        var sse = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}\n\n"
            + "data: {\"type\":\"response.completed\",\"response\":{}}";

        var events = await ReadAllAsync(sse);

        events.Should().HaveCount(2);
        events[1].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
    }

    [Fact]
    public async Task ReadSseAsync_done_sentinel_at_eof_without_blank_line_does_not_dispatch()
    {
        var sse = "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\"}}\n\n"
            + "data: [DONE]";

        var events = await ReadAllAsync(sse);

        events.Should().ContainSingle();
        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
    }

    private static async Task<List<ResponseEvent>> ReadAllAsync(string sse)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        var collected = new List<ResponseEvent>();
        await foreach (var ev in OpenAiResponsesClient.ReadSseAsync(stream, CancellationToken.None))
        {
            collected.Add(ev);
        }

        return collected;
    }
}
