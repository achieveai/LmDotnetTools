using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ResponsesToAnthropicSseTests
{
    /// <summary>Feeds a scripted Responses stream and returns the Anthropic SSE frames it produced.</summary>
    private static IReadOnlyList<string> Frames(params string[] events)
    {
        var translator = new ResponsesToAnthropicSse("msg_test", "gpt-5.3-codex");
        return [.. events.SelectMany(translator.Next)];
    }

    /// <summary>Feeds a scripted Responses stream and returns the concatenated Anthropic SSE output.</summary>
    private static string Run(params string[] events) => string.Concat(Frames(events));

    /// <summary>
    ///     The event name of each frame, in order. Substring assertions over the concatenated output
    ///     are blind to both duplication and ordering — a second message_start, or a content block
    ///     opening before message_start, still satisfies every <c>Contain</c>.
    /// </summary>
    private static IReadOnlyList<string> EventNames(params string[] events) =>
        [.. Frames(events).Select(frame => frame.Split('\n')[0]["event: ".Length..])];

    [Fact]
    public void Emits_a_well_formed_text_stream()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5.3-codex"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text","text":""}}""",
            """{"type":"response.output_text.delta","delta":"Hel"}""",
            """{"type":"response.output_text.delta","delta":"lo"}""",
            """{"type":"response.content_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":9,"output_tokens":2}}}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().Contain("\"id\":\"resp_1\"");
        output.Should().Contain("event: content_block_start");
        output.Should().Contain("\"index\":0");
        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"Hel\"");
        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"lo\"");
        output.Should().Contain("event: content_block_stop");
        output.Should().Contain("\"stop_reason\":\"end_turn\"");
        output.Should().Contain("\"input_tokens\":9");
        output.Should().Contain("\"output_tokens\":2");
        output.Should().EndWith("\n\n");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Emits_the_happy_path_frames_in_exactly_this_order()
    {
        var events = EventNames(
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5.3-codex"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text","text":""}}""",
            """{"type":"response.output_text.delta","delta":"Hel"}""",
            """{"type":"response.output_text.delta","delta":"lo"}""",
            """{"type":"response.content_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":9,"output_tokens":2}}}"""
        );

        events
            .Should()
            .Equal(
                "message_start",
                "content_block_start",
                "content_block_delta",
                "content_block_delta",
                "content_block_stop",
                "message_delta",
                "message_stop"
            );
    }

    [Fact]
    public void Streams_a_tool_call_as_an_input_json_delta_block()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call_1","name":"get_weather"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"city\":"}""",
            """{"type":"response.function_call_arguments.delta","delta":"\"Paris\"}"}""",
            """{"type":"response.output_item.done"}""",
            """{"type":"response.completed","response":{"output":[{"type":"function_call"}],"usage":{"input_tokens":1,"output_tokens":5}}}"""
        );

        output.Should().Contain("\"type\":\"tool_use\"");
        output.Should().Contain("\"id\":\"call_1\"");
        output.Should().Contain("\"name\":\"get_weather\"");
        output.Should().Contain("\"type\":\"input_json_delta\"");
        output.Should().Contain("\"stop_reason\":\"tool_use\"");
    }

    [Fact]
    public void Streams_a_reasoning_summary_as_a_thinking_block()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.reasoning_summary_part.added"}""",
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking..."}""",
            """{"type":"response.reasoning_summary_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"type\":\"thinking\"");
        output.Should().Contain("\"type\":\"thinking_delta\"");
    }

    [Fact]
    public void Closes_an_open_block_before_terminating()
    {
        // Upstream ends the response without closing its content part — the block must still be closed
        // before message_delta, or the Anthropic stream is malformed.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"hi"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output
            .IndexOf("content_block_stop", StringComparison.Ordinal)
            .Should()
            .BeLessThan(output.IndexOf("message_delta", StringComparison.Ordinal));
    }

    [Fact]
    public void A_response_with_no_content_still_produces_a_well_formed_envelope()
    {
        // Claude Code's `max_tokens: 1` validation probe. Zero content blocks, but message_start and
        // message_stop MUST both be present or the model is judged unusable.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":3,"output_tokens":0}}}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().Contain("event: message_delta");
        output.Should().Contain("event: message_stop");
        output.Should().NotContain("content_block_start", "no content is honest; a fabricated block is not");
    }

    [Fact]
    public void Reports_max_tokens_for_an_incomplete_response()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.incomplete","response":{"output":[],"incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":1,"output_tokens":16}}}"""
        );

        output.Should().Contain("\"stop_reason\":\"max_tokens\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void A_truncated_stream_is_not_capped_with_a_fabricated_terminator()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"partial"}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().NotContain("message_stop", "the upstream never terminated; inventing one hides the failure");
    }

    [Fact]
    public void Ignores_unknown_and_malformed_events()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.some_future_event","data":{}}""",
            "not json at all",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Numbers_successive_content_blocks_from_zero_upwards()
    {
        // A reasoning model answers with a thinking block and then a text block. Anthropic requires a
        // monotonically increasing index per block; reusing index 0 would make the client overwrite
        // the thinking block with the answer.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.reasoning_summary_part.added"}""",
            """{"type":"response.reasoning_summary_text.delta","delta":"weighing"}""",
            """{"type":"response.reasoning_summary_part.done"}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"answer"}""",
            """{"type":"response.content_part.done"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"thinking\"");
        output.Should().Contain("\"index\":1,\"content_block\":{\"type\":\"text\"");
        output.Should().Contain("\"type\":\"thinking_delta\",\"thinking\":\"weighing\"");
        output.Should().Contain("\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"answer\"}");
        output.Should().Contain("\"type\":\"content_block_stop\",\"index\":0");
        output.Should().Contain("\"type\":\"content_block_stop\",\"index\":1");
    }

    [Fact]
    public void Emits_nothing_once_the_stream_has_terminated()
    {
        // Copilot sometimes trails a terminal event with further frames. A second message_stop would
        // break any client that treats the first one as the end of the message.
        var translator = new ResponsesToAnthropicSse("msg_test", "m");
        translator.Next("""{"type":"response.created","response":{"id":"r","model":"m"}}""");
        translator.Next("""{"type":"response.completed","response":{"output":[],"usage":{}}}""");

        var afterTermination = translator.Next("""{"type":"response.output_text.delta","delta":"late"}""");

        afterTermination.Should().BeEmpty();
    }

    [Fact]
    public void Fails_the_stream_when_tool_arguments_arrive_with_no_tool_block_to_hold_them()
    {
        // The output_item.added that would have opened the tool_use block is unreadable, so the text
        // delta owns index 0. The arguments that follow cannot be written anywhere: splicing tool-call
        // JSON into rendered assistant text is malformed, and dropping them hands the client a tool
        // call with no arguments and no way to know any existed. So the stream fails visibly.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.output_item.added","item":["not-an-object"]}""",
            """{"type":"response.output_text.delta","delta":"hi"}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"city\":\"Paris\"}"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}");
        output.Should().Contain("\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}");
        output.Should().NotContain("input_json_delta");
        output.Should().NotContain("Paris");
        output.Should().NotContain("\"index\":1", "no second block was ever opened");

        output.Should().Contain("event: error");
        output.Should().Contain("\"type\":\"api_error\"");
        output
            .Should()
            .NotContain("message_stop", "the turn did not complete; claiming it did is the silent failure");
    }

    [Fact]
    public void The_open_block_is_closed_before_the_terminal_error_frame()
    {
        var events = EventNames(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.content_part.added","part":{"type":"output_text"}}""",
            """{"type":"response.output_text.delta","delta":"partial"}""",
            """{"type":"error","code":"server_error","message":"boom"}"""
        );

        events
            .Should()
            .Equal("message_start", "content_block_start", "content_block_delta", "content_block_stop", "error");
    }

    [Fact]
    public void Translates_the_top_level_error_event_into_an_anthropic_error_frame()
    {
        // ResponseErrorEvent: {"type":"error","code":...,"message":...,"param":...}. It used to land in
        // the silent default arm, so the client saw an unexplained truncation.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"error","code":"rate_limit_exceeded","message":"You asked about Paris too often","param":null}"""
        );

        output.Should().Contain("event: error");
        output.Should().Contain("\"type\":\"api_error\"");
        output.Should().Contain("rate_limit_exceeded", "a token-shaped code is machine-readable, not prose");
        output.Should().NotContain("Paris", "the upstream's free text can echo the prompt back");
        output.Should().NotContain("message_stop");
    }

    [Fact]
    public void Translates_response_failed_into_an_anthropic_error_frame()
    {
        // ResponseFailedEvent carries the same error object one level down, under response.error.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.failed","response":{"status":"failed","error":{"code":"server_error","message":"upstream exploded"}}}"""
        );

        output.Should().Contain("event: error");
        output.Should().Contain("server_error");
        output.Should().NotContain("upstream exploded");
        output.Should().NotContain("message_stop");
    }

    [Theory]
    [InlineData("""{"type":"error","message":"no code at all"}""")]
    [InlineData("""{"type":"error","code":"a sentence, with punctuation, that is really prose"}""")]
    [InlineData("""{"type":"error","code":123}""")]
    [InlineData("""{"type":"response.failed","response":{"error":null}}""")]
    [InlineData("""{"type":"response.failed"}""")]
    public void An_upstream_failure_with_no_usable_code_still_ends_the_stream(string failure)
    {
        var output = Run("""{"type":"response.created","response":{"id":"r","model":"m"}}""", failure);

        output.Should().Contain("event: error");
        output.Should().Contain("The upstream Copilot stream failed.");
        output.Should().NotContain("Upstream code:");
    }

    [Fact]
    public void Emits_nothing_after_a_terminal_error_frame()
    {
        var translator = new ResponsesToAnthropicSse("msg_test", "m");
        _ = translator.Next("""{"type":"response.created","response":{"id":"r","model":"m"}}""");
        _ = translator.Next("""{"type":"error","code":"server_error"}""");

        var afterFailure = translator.Next(
            """{"type":"response.completed","response":{"output":[],"usage":{}}}"""
        );

        afterFailure.Should().BeEmpty();
    }

    [Fact]
    public void Reports_refusal_when_a_classifier_stopped_a_streamed_turn()
    {
        // The buffered and the streamed path share DeriveStopReason precisely so this cannot differ
        // between them.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.incomplete","response":{"output":[],"incomplete_details":{"reason":"content_filter"},"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void A_streamed_classifier_stop_outranks_a_half_formed_function_call()
    {
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.incomplete","response":{"output":[{"type":"function_call"}],"incomplete_details":{"reason":"content_filter"},"usage":{}}}"""
        );

        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().NotContain("\"stop_reason\":\"tool_use\"");
    }

    [Fact]
    public void Switches_block_when_a_text_delta_interrupts_an_open_thinking_block()
    {
        // The upstream went straight from its reasoning summary to the answer without sending
        // reasoning_summary_part.done. The thinking block must be closed and a text block opened at
        // the next index — writing text_delta at the thinking block's index would be malformed.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.reasoning_summary_part.added"}""",
            """{"type":"response.reasoning_summary_text.delta","delta":"weighing"}""",
            """{"type":"response.output_text.delta","delta":"answer"}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\"}");
        output.Should().Contain("\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"weighing\"}");
        output.Should().Contain("\"type\":\"content_block_stop\",\"index\":0");
        output.Should().Contain("\"index\":1,\"content_block\":{\"type\":\"text\",\"text\":\"\"}");
        output.Should().Contain("\"index\":1,\"delta\":{\"type\":\"text_delta\",\"text\":\"answer\"}");
        output.Should().NotContain("\"index\":0,\"delta\":{\"type\":\"text_delta\"");
    }

    [Fact]
    public void Reports_a_token_count_the_upstream_sent_in_a_shape_int_cannot_hold()
    {
        // 9.0 is a whole number written in floating-point form, and the output figure exceeds int.
        // Reporting 0 for either would silently zero the client's cost accounting.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":9.0,"output_tokens":3000000000}}}"""
        );

        output.Should().Contain("\"input_tokens\":9,\"output_tokens\":3000000000");
    }

    [Fact]
    public void Terminates_cleanly_when_the_terminal_event_carries_no_response()
    {
        // response.completed with no "response" body: there is nothing to derive a stop reason from,
        // so end_turn stands in. The envelope still has to close.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":"response.completed"}"""
        );

        output.Should().Contain("\"stop_reason\":\"end_turn\"");
        output.Should().Contain("\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}\n\n");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Tolerates_event_fields_of_an_unexpected_json_kind()
    {
        // Every read here would throw InvalidOperationException under a bare GetValue<string>(), and a
        // throw mid-stream is an unexplained truncation the client cannot diagnose. Each frame must
        // instead be skipped or degrade, and the stream must still terminate cleanly.
        var output = Run(
            """{"type":"response.created","response":{"id":"r","model":"m"}}""",
            """{"type":123}""",
            """{"type":"response.content_part.added","part":"not-an-object"}""",
            """{"type":"response.output_item.added","item":["not-an-object"]}""",
            """{"type":"response.output_text.delta","delta":{"nested":true}}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":1,"output_tokens":1}}}"""
        );

        output.Should().Contain("event: message_start");
        output.Should().Contain("event: message_stop");
        output.Should().NotContain("\"type\":\"tool_use\"", "the item that would have named the call was unreadable");
        output.Should().NotContain("text_delta", "the delta itself was unreadable, so there is no text to report");

        // The unreadable output_text.delta still opened a text block: the upstream did assert that it
        // was emitting text, and an empty text block is the honest degradation. It is the ONLY block —
        // neither the malformed part nor the malformed item opened one.
        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}");
        output.Should().Contain("\"type\":\"content_block_stop\",\"index\":0");
        output.Should().NotContain("\"index\":1");
    }

    [Fact]
    public void Falls_back_to_the_supplied_id_and_model_and_to_zero_usage()
    {
        // The upstream announced neither an id nor a model, and reported usage in a shape we cannot
        // read. message_start still has to be well-formed.
        var output = Run(
            """{"type":"response.created","response":{"id":123,"model":null}}""",
            """{"type":"response.completed","response":{"output":[],"usage":{"input_tokens":"9"}}}"""
        );

        output.Should().Contain("\"id\":\"msg_test\"");
        output.Should().Contain("\"model\":\"gpt-5.3-codex\"");
        output.Should().Contain("\"usage\":{\"input_tokens\":0,\"output_tokens\":0}}\n\n");
        output.Should().Contain("event: message_stop");
    }
}
