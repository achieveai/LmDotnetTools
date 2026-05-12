using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Models;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
/// Verifies the InstructionPlan → ResponseEvent[] writer produces the OpenAI Responses
/// wire-grammar order: lifecycle → per-item bracket → completed. The writer is the only
/// place this ordering is encoded, so these tests guard the contract both transports rely on.
/// </summary>
public sealed class OpenAiResponsesEventStreamWriterTests
{
    [Fact]
    public void Plain_text_plan_emits_canonical_event_sequence()
    {
        var plan = new InstructionPlan("single-text", null, [InstructionMessage.ForExplicitText("hello world")]);

        var events = OpenAiResponsesEventStreamWriter.Write(plan, model: "gpt-test");

        var types = events.Select(e => e.Type).ToList();
        types
            .Should()
            .Equal(
                ResponseEventTypes.ResponseCreated,
                ResponseEventTypes.ResponseInProgress,
                ResponseEventTypes.OutputItemAdded,
                ResponseEventTypes.ContentPartAdded,
                ResponseEventTypes.OutputTextDelta,
                ResponseEventTypes.OutputTextDone,
                ResponseEventTypes.ContentPartDone,
                ResponseEventTypes.OutputItemDone,
                ResponseEventTypes.ResponseCompleted
            );

        // Sequence numbers are dense and monotonic.
        events.Select(e => e.SequenceNumber).Should().Equal(Enumerable.Range(0, events.Count).Cast<int?>());
    }

    [Fact]
    public void Function_call_plan_emits_argument_delta_and_done_within_item_bracket()
    {
        var plan = new InstructionPlan(
            "tool-call",
            null,
            [InstructionMessage.ForToolCalls([new InstructionToolCall("get_weather", """{"city":"Paris"}""")])]
        );

        var events = OpenAiResponsesEventStreamWriter.Write(plan);

        var fnAdded = events.OfType<ResponseOutputItemEvent>().First(e => e.Type == ResponseEventTypes.OutputItemAdded);
        fnAdded.Item.GetProperty("type").GetString().Should().Be("function_call");
        fnAdded.Item.GetProperty("name").GetString().Should().Be("get_weather");

        var argsDone = events.OfType<ResponseFunctionCallArgumentsDoneEvent>().Single();
        argsDone.Arguments.Should().Be("""{"city":"Paris"}""");

        events.Last().Type.Should().Be(ResponseEventTypes.ResponseCompleted);
    }

    [Fact]
    public void Reasoning_plan_emits_reasoning_item_before_text_items()
    {
        var plan = new InstructionPlan("thinking-then-text", 4, [InstructionMessage.ForExplicitText("final answer")]);

        var events = OpenAiResponsesEventStreamWriter.Write(plan);

        var itemEvents = events
            .OfType<ResponseOutputItemEvent>()
            .Where(e => e.Type == ResponseEventTypes.OutputItemAdded)
            .ToList();

        itemEvents.Should().HaveCount(2);
        itemEvents[0].OutputIndex.Should().Be(0);
        itemEvents[0].Item.GetProperty("type").GetString().Should().Be("reasoning");
        itemEvents[0]
            .Item.GetProperty("summary")
            .EnumerateArray()
            .Should()
            .ContainSingle()
            .Which.GetProperty("text")
            .GetString()
            .Should()
            .NotBeNullOrWhiteSpace();

        itemEvents[1].OutputIndex.Should().Be(1);
        itemEvents[1].Item.GetProperty("type").GetString().Should().Be("message");
    }

    [Fact]
    public void Concatenated_text_deltas_equal_final_text()
    {
        var plan = new InstructionPlan(
            "delta-cat",
            null,
            [InstructionMessage.ForExplicitText("the quick brown fox jumps over the lazy dog")]
        );

        var events = OpenAiResponsesEventStreamWriter.Write(plan, wordsPerChunk: 3);

        var concatenated = string.Concat(events.OfType<ResponseOutputTextDeltaEvent>().Select(d => d.Delta));
        var done = events.OfType<ResponseOutputTextDoneEvent>().Single();
        concatenated.Should().Be(done.Text);
    }

    [Fact]
    public void Completed_lifecycle_carries_usage_block()
    {
        var plan = new InstructionPlan("with-usage", null, [InstructionMessage.ForExplicitText("ok")]);

        var events = OpenAiResponsesEventStreamWriter.Write(plan);

        var completed = events
            .OfType<ResponseLifecycleEvent>()
            .Single(e => e.Type == ResponseEventTypes.ResponseCompleted);
        completed.Response.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public void Multiple_messages_get_distinct_output_indexes()
    {
        var plan = new InstructionPlan(
            "multi",
            null,
            [InstructionMessage.ForExplicitText("first"), InstructionMessage.ForExplicitText("second")]
        );

        var events = OpenAiResponsesEventStreamWriter.Write(plan);

        var indexes = events
            .OfType<ResponseOutputItemEvent>()
            .Where(e => e.Type == ResponseEventTypes.OutputItemAdded)
            .Select(e => e.OutputIndex)
            .ToList();
        indexes.Should().Equal(0, 1);
    }

    [Fact]
    public void Empty_plan_still_emits_lifecycle_pair()
    {
        var plan = new InstructionPlan("empty", null, []);

        var events = OpenAiResponsesEventStreamWriter.Write(plan);

        events.Should().HaveCount(3);
        events[0].Type.Should().Be(ResponseEventTypes.ResponseCreated);
        events[1].Type.Should().Be(ResponseEventTypes.ResponseInProgress);
        events[2].Type.Should().Be(ResponseEventTypes.ResponseCompleted);
    }

    [Fact]
    public void Fixed_response_id_threads_through_lifecycle_events()
    {
        var plan = new InstructionPlan("ids", null, [InstructionMessage.ForExplicitText("hi")]);

        var events = OpenAiResponsesEventStreamWriter.Write(plan, responseId: "resp_fixed");

        var lifecycleIds = events
            .OfType<ResponseLifecycleEvent>()
            .Select(e => e.Response.GetProperty("id").GetString())
            .Distinct();
        lifecycleIds.Should().ContainSingle().Which.Should().Be("resp_fixed");
    }
}
