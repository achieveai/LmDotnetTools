using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     The cross-product regression matrix for the four translation invariants a second review found
///     were fixed only at the exact spot each reproduction pointed at:
///
///     <list type="number">
///         <item>a streamed tool call may never complete once its arguments are lost;</item>
///         <item>a reply declaring a failed lifecycle may never translate as a successful turn;</item>
///         <item>malformed elements INSIDE <c>output</c> must fail, not be filtered away;</item>
///         <item>an official <c>refusal</c> must reach the client as <c>stop_reason: "refusal"</c>.</item>
///     </list>
///
///     Each invariant is exercised across the axes the first pass missed — malformed vs valid vs
///     absent, open vs absent block, streamed vs buffered, outer container vs inner element, classifier
///     stop vs completed refusal — and each reviewer reproduction is pinned verbatim. Tests proving the
///     translators do NOT over-reject sit alongside them: an invariant that fails a legitimate reply is
///     a different outage, not a fix.
/// </summary>
public class ResponsesTranslationIntegrityTests
{
    private const string Created = """{"type":"response.created","response":{"id":"r","model":"m"}}""";

    /// <summary>A terminal event whose payload asserts nothing beyond "the turn finished".</summary>
    private const string Completed = """{"type":"response.completed","response":{"output":[],"usage":{}}}""";

    /// <summary>A terminal event whose payload says a function call was produced.</summary>
    private const string CompletedWithCall =
        """{"type":"response.completed","response":{"output":[{"type":"function_call"}],"usage":{}}}""";

    private static JsonElement Buffered(string responsesJson) =>
        JsonDocument.Parse(ResponsesToAnthropicJson.Translate(responsesJson, "fallback-model")).RootElement.Clone();

    private static Action Translating(string responsesJson) =>
        () => ResponsesToAnthropicJson.Translate(responsesJson, "fallback-model");

    private static string Streamed(params string[] events)
    {
        var translator = new ResponsesToAnthropicSse("msg_test", "gpt-5.3-codex");
        return string.Concat(events.SelectMany(translator.Next));
    }

    /// <summary>
    ///     The wire form of one <c>input_json_delta</c> carrying <paramref name="partialJson"/>, built
    ///     through the same serializer the frames are, so the assertion is about the fragment rather than
    ///     about how <c>"</c> happens to be escaped inside it.
    /// </summary>
    private static string ArgumentFragment(string partialJson) =>
        new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = partialJson }.ToJsonString();

    // ---------------------------------------------------------------------------------------------
    // 1. A streamed tool call may never complete after its arguments are lost.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reviewers_reproduction_an_announced_call_with_a_malformed_delta_never_completes()
    {
        // Verbatim: an announced `delete_all`, a malformed delta, and an output_item.done carrying the
        // real arguments. This used to emit input:{}, stop_reason:"tool_use" and message_stop — an
        // executable call stripped of the filter that made it safe.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"call_1","name":"delete_all"}}""",
            """{"type":"response.function_call_arguments.delta","delta":{"nested":true}}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"call_1","name":"delete_all","arguments":"{\"path\":\"/\"}"}}""",
            CompletedWithCall
        );

        output.Should().Contain("event: error");
        output.Should().Contain("\"type\":\"api_error\"");
        output.Should().NotContain("\"stop_reason\":\"tool_use\"");
        output.Should().NotContain("message_delta");
        output.Should().NotContain("message_stop", "the turn did not complete; claiming it did is the silent failure");
        output.Should().NotContain("input_json_delta", "no argument fragment was ever proven readable");

        // The announcement was already on the wire when the malformed delta arrived, so the block start
        // cannot be unsent. It is terminated by content_block_stop and then a terminal error with no
        // message_stop — exactly the state Anthropic documents as "discard the partial output".
        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"tool_use\"");
        output.Should().Contain("\"type\":\"content_block_stop\",\"index\":0");
    }

    [Theory]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":123}""")]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":{"nested":true}}""")]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":["a"]}""")]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":null}""")]
    [InlineData("""{"type":"response.function_call_arguments.delta"}""")]
    public void An_unreadable_argument_fragment_fails_the_stream_even_with_a_tool_block_open(string malformed)
    {
        // The earlier fix only covered the no-open-block case. With a block open the fragment became an
        // empty string and was dropped, leaving the client concatenating a hole in the middle of the
        // argument JSON — which either fails to parse or parses into a DIFFERENT call.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"n"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"city\":"}""",
            malformed,
            """{"type":"response.function_call_arguments.done","arguments":"{\"city\":\"Paris\"}"}""",
            """{"type":"response.output_item.done"}""",
            CompletedWithCall
        );

        output.Should().Contain("event: error");
        output.Should().NotContain("message_stop");
    }

    [Fact]
    public void An_argument_fragment_that_is_readable_but_empty_is_not_a_failure()
    {
        // "" is a fragment the upstream genuinely sent and we genuinely read. Only fragments that could
        // not be READ break the reassembly; failing this one would be the mirror-image outage.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"n"}}""",
            """{"type":"response.function_call_arguments.delta","delta":""}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"city\":\"Paris\"}"}""",
            """{"type":"response.output_item.done"}""",
            CompletedWithCall
        );

        output.Should().NotContain("event: error");
        output.Should().Contain("\"stop_reason\":\"tool_use\"");
        output.Should().Contain("event: message_stop");
    }

    [Theory]
    // The COMPLETE argument string, in each of the two events that can carry it.
    [InlineData("""{"type":"response.function_call_arguments.done","name":"n","arguments":"{\"city\":\"Paris\"}"}""")]
    [InlineData(
        """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c","name":"n","arguments":"{\"city\":\"Paris\"}"}}"""
    )]
    public void A_done_event_is_the_only_carrier_when_no_delta_ever_arrived(string done)
    {
        // Both done events used to be ignored outright, so a call whose deltas never arrived reached the
        // client as input:{}. The complete string is authoritative and is forwarded.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"n"}}""",
            done,
            """{"type":"response.output_item.done"}""",
            CompletedWithCall
        );

        output.Should().NotContain("event: error");
        output.Should().Contain(ArgumentFragment("""{"city":"Paris"}"""));
        output.Should().Contain("\"stop_reason\":\"tool_use\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void Completed_arguments_that_contradict_the_streamed_fragments_fail_the_stream()
    {
        // The client has already been streamed "/tmp". No later frame can retract it, so the turn cannot
        // be allowed to complete around a call the model did not make.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"rm"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"path\":\"/tmp\"}"}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c","name":"rm","arguments":"{\"path\":\"/\"}"}}""",
            CompletedWithCall
        );

        output.Should().Contain("event: error");
        output.Should().NotContain("message_stop");
    }

    [Fact]
    public void Completed_arguments_that_match_the_streamed_fragments_are_not_resent()
    {
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"rm"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{\"path\":"}""",
            """{"type":"response.function_call_arguments.delta","delta":"\"/tmp\"}"}""",
            """{"type":"response.function_call_arguments.done","arguments":"{\"path\":\"/tmp\"}"}""",
            """{"type":"response.output_item.done","item":{"type":"function_call","call_id":"c","name":"rm","arguments":"{\"path\":\"/tmp\"}"}}""",
            CompletedWithCall
        );

        output.Should().NotContain("event: error");
        output.Should().Contain(ArgumentFragment("""{"path":"""));
        output.Should().Contain(ArgumentFragment("\"/tmp\"}"));
        output
            .Split("input_json_delta")
            .Should()
            .HaveCount(3, "exactly the two fragments the upstream streamed, with no replay of the whole string");
        output.Should().Contain("event: message_stop");
    }

    [Theory]
    // Truncated JSON, a JSON value that is not an object, and a block closed by an unrelated event
    // before any arguments arrived: each leaves tool_use.input unusable.
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":"{\"city\":"}""")]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":"[1,2]"}""")]
    [InlineData("""{"type":"response.output_text.delta","delta":"meanwhile"}""")]
    public void A_tool_block_never_closes_around_arguments_that_are_not_a_json_object(string middle)
    {
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"n"}}""",
            middle,
            CompletedWithCall
        );

        output.Should().Contain("event: error");
        output.Should().NotContain("\"stop_reason\":\"tool_use\"");
        output.Should().NotContain("message_stop");
    }

    [Fact]
    public void An_explicitly_parameterless_call_still_completes()
    {
        // "{}" reads cleanly and is a real call the model chose to make.
        var output = Streamed(
            Created,
            """{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c","name":"now"}}""",
            """{"type":"response.function_call_arguments.delta","delta":"{}"}""",
            """{"type":"response.output_item.done"}""",
            CompletedWithCall
        );

        output.Should().NotContain("event: error");
        output.Should().Contain("\"stop_reason\":\"tool_use\"");
        output.Should().Contain("event: message_stop");
    }

    [Theory]
    [InlineData("""{"type":"response.function_call_arguments.delta","delta":"{}"}""")]
    [InlineData("""{"type":"response.function_call_arguments.done","arguments":"{}"}""")]
    public void Argument_events_with_no_announced_call_fail_the_stream_in_both_forms(string orphan)
    {
        // The delta form was already covered; the done form landed in the silent default arm and let the
        // turn complete as if no tool call had been attempted at all.
        var output = Streamed(Created, orphan, Completed);

        output.Should().Contain("event: error");
        output.Should().NotContain("message_stop");
    }

    [Theory]
    [InlineData("""{"type":"response.output_item.added","item":{"type":"function_call","name":"n"}}""")]
    [InlineData("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":"c"}}""")]
    [InlineData("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":"","name":"n"}}""")]
    [InlineData("""{"type":"response.output_item.added","item":{"type":"function_call","call_id":123,"name":"n"}}""")]
    public void A_call_announced_without_a_usable_id_and_name_fails_rather_than_opening_a_dead_block(string added)
    {
        // call_id is what the client echoes back in tool_result and name is what it dispatches on.
        // Substituting "" produced a block that could be streamed and closed but never honoured.
        var output = Streamed(
            Created,
            added,
            """{"type":"response.function_call_arguments.delta","delta":"{}"}""",
            CompletedWithCall
        );

        output.Should().Contain("event: error");
        output.Should().NotContain("\"id\":\"\"");
        output.Should().NotContain("message_stop");
    }

    // ---------------------------------------------------------------------------------------------
    // 2. A declared lifecycle failure may never translate as a successful turn.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reviewers_reproduction_a_failed_buffered_reply_is_refused() =>
        // Verbatim. This became a normal Anthropic message with empty content and stop_reason
        // "end_turn": an HTTP-2xx body that says it failed, reported to the client as a silent success.
        Translating("""{"status":"failed","error":{"code":"server_error"},"output":[]}""")
            .Should()
            .Throw<ArgumentException>()
            .WithInnerExceptionExactly<InvalidOperationException>()
            .WithMessage("*server_error*", "the shaped upstream code is what the streaming path relays too");

    [Theory]
    // Carrying real content does not make an unfinished turn finished: reshaping it would report the
    // fragment the upstream had produced so far as the model's complete answer.
    [InlineData(
        """{"id":"r","status":"failed","output":[{"type":"message","content":[{"type":"output_text","text":"half"}]}]}"""
    )]
    [InlineData(
        """{"id":"r","status":"cancelled","output":[{"type":"message","content":[{"type":"output_text","text":"half"}]}]}"""
    )]
    [InlineData(
        """{"id":"r","status":"in_progress","output":[{"type":"message","content":[{"type":"output_text","text":"half"}]}]}"""
    )]
    [InlineData("""{"id":"r","status":"queued","output":[]}""")]
    [InlineData("""{"id":"r","status":"a_status_added_after_this_proxy_shipped","output":[]}""")]
    public void A_buffered_reply_that_is_not_a_finished_turn_is_refused(string body) =>
        Translating(body).Should().Throw<ArgumentException>();

    [Theory]
    // completed and incomplete are both finished turns, an absent status asserts nothing, and an
    // explicitly null error is the shape the spec documents for every response that did not fail.
    [InlineData("""{"id":"r","status":"completed","output":[]}""")]
    [InlineData("""{"id":"r","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[]}""")]
    [InlineData("""{"id":"r","output":[]}""")]
    [InlineData("""{"id":"r","status":"completed","error":null,"output":[]}""")]
    public void A_finished_turn_still_translates(string body) =>
        Buffered(body).GetProperty("type").GetString().Should().Be("message");

    [Fact]
    public void An_error_object_outranks_a_status_claiming_success() =>
        // The spec leaves `error` null unless the response failed, so its presence contradicts the
        // status. Believing the status is how an upstream failure became an empty successful turn.
        Translating(
                """
                {"id":"r","status":"completed","error":{"code":"rate_limit_exceeded","message":"slow down"},
                 "output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}]}
                """
            )
            .Should()
            .Throw<ArgumentException>();

    [Theory]
    [InlineData(
        """{"type":"response.completed","response":{"status":"failed","error":{"code":"server_error"},"output":[]}}"""
    )]
    [InlineData("""{"type":"response.completed","response":{"status":"cancelled","output":[]}}""")]
    [InlineData("""{"type":"response.incomplete","response":{"status":"in_progress","output":[]}}""")]
    [InlineData("""{"type":"response.completed","response":{"error":{"code":"server_error"},"output":[]}}""")]
    public void A_terminal_event_declaring_a_failed_lifecycle_fails_the_stream_too(string terminal)
    {
        // The streamed twin of the buffered check, through the same shared predicate: a terminal event
        // is not permission to ignore what its own payload says about the turn.
        var output = Streamed(Created, terminal);

        output.Should().Contain("event: error");
        output.Should().Contain("\"type\":\"api_error\"");
        output.Should().NotContain("message_stop");
    }

    [Fact]
    public void A_buffered_and_a_streamed_failure_are_described_identically()
    {
        const string failure =
            """{"status":"failed","error":{"code":"server_error","message":"echoing the prompt back"}}""";

        var streamed = Streamed(Created, """{"type":"response.failed","response":""" + failure + "}");

        var buffered = Translating(failure)
            .Should()
            .Throw<ArgumentException>()
            .WithInnerExceptionExactly<InvalidOperationException>()
            .Which.Message;

        streamed.Should().Contain(buffered);
        streamed.Should().NotContain("echoing the prompt back", "an upstream message can echo the prompt back");
        buffered.Should().NotContain("echoing the prompt back");
    }

    // ---------------------------------------------------------------------------------------------
    // 3. Malformed elements INSIDE output must fail, not be filtered away.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reviewers_reproduction_a_corrupt_output_element_is_refused() =>
        // Verbatim: {"output":["corrupt"]} translated to an HTTP-success-shaped content:[] with
        // stop_reason "end_turn". OfType<JsonObject>() had simply dropped the corrupt element.
        Translating("""{"output":["corrupt"]}""").Should().Throw<ArgumentException>();

    [Theory]
    [InlineData("""{"output":["corrupt"]}""")]
    [InlineData("""{"output":[123]}""")]
    [InlineData("""{"output":[null]}""")]
    [InlineData("""{"output":[[]]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"output_text","text":"real"}]},"corrupt"]}""")]
    [InlineData("""{"output":[{}]}""")]
    [InlineData("""{"output":[{"type":123}]}""")]
    public void A_malformed_output_element_is_refused(string body) =>
        Translating(body).Should().Throw<ArgumentException>();

    [Theory]
    // The same defect one level further in: a recognised item whose own payload is corrupt.
    [InlineData("""{"output":[{"type":"message","content":"not an array"}]}""")]
    [InlineData("""{"output":[{"type":"message","content":["corrupt"]}]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"output_text"}]}]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"output_text","text":123}]}]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"refusal"}]}]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":123}]}]}""")]
    [InlineData("""{"output":[{"type":"reasoning","summary":"not an array"}]}""")]
    [InlineData("""{"output":[{"type":"reasoning","summary":[123]}]}""")]
    [InlineData("""{"output":[{"type":"reasoning","summary":[{"type":"summary_text","text":5}]}]}""")]
    [InlineData("""{"output":[{"type":"function_call","name":"n","arguments":"{}"}]}""")]
    [InlineData("""{"output":[{"type":"function_call","call_id":"c","arguments":"{}"}]}""")]
    [InlineData("""{"output":[{"type":"function_call","call_id":123,"name":"n","arguments":"{}"}]}""")]
    public void Malformed_content_inside_a_recognised_item_is_refused(string body) =>
        Translating(body).Should().Throw<ArgumentException>();

    [Theory]
    // An unknown TYPE is an upstream newer than this proxy, not an upstream that is broken. Failing
    // these would break the proxy on the next Responses release.
    [InlineData("""{"output":[{"type":"web_search_call","status":"completed"}]}""")]
    [InlineData("""{"output":[{"type":"mcp_call","name":"x","arguments":"not a json object"}]}""")]
    [InlineData("""{"output":[{"type":"message","content":[{"type":"reasoning_text","text":"x"}]}]}""")]
    [InlineData("""{"output":[{"type":"reasoning","summary":[{"type":"summary_text"}]}]}""")]
    public void An_unrecognised_item_or_part_type_is_still_skipped_rather_than_refused(string body) =>
        Buffered(body).GetProperty("content").GetArrayLength().Should().Be(0);

    [Fact]
    public void A_legitimate_reply_is_untouched_by_the_new_checks()
    {
        var content = Buffered(
                """
                {"id":"r","model":"m","status":"completed",
                 "output":[{"type":"reasoning","summary":[{"type":"summary_text","text":"weighing"}]},
                           {"type":"web_search_call","status":"completed"},
                           {"type":"message","content":[{"type":"output_text","text":"Paris","annotations":[]}]},
                           {"type":"function_call","call_id":"c","name":"n","arguments":"{\"a\":1}"}],
                 "usage":{"input_tokens":4,"output_tokens":9}}
                """
            )
            .GetProperty("content");

        content.GetArrayLength().Should().Be(3);
        content[0].GetProperty("type").GetString().Should().Be("thinking");
        content[1].GetProperty("text").GetString().Should().Be("Paris");
        content[2].GetProperty("input").GetProperty("a").GetInt32().Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------
    // 4. An official refusal must reach the client as stop_reason "refusal".
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_reviewers_reproduction_a_completed_refusal_is_not_a_normal_completion()
    {
        // Verbatim: a valid {"type":"refusal","refusal":"..."} content part on a COMPLETED response. It
        // was ignored outright, so a safety decline reached the client as empty content with stop_reason
        // "end_turn". This is distinct from incomplete_details.reason "content_filter": there the turn
        // was cut short, here the model answered and its answer was a refusal.
        var result = Buffered(
            """
            {"id":"r","model":"m","status":"completed",
             "output":[{"type":"message","role":"assistant",
                        "content":[{"type":"refusal","refusal":"I'm sorry, I cannot assist with that request."}]}],
             "usage":{"input_tokens":12,"output_tokens":9}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("refusal");

        var content = result.GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("I'm sorry, I cannot assist with that request.");
    }

    [Fact]
    public void A_refusal_outranks_a_function_call_in_the_same_completed_output() =>
        // Reporting tool_use would invite the client to execute a call the model produced on its way to
        // declining — the same reasoning Anthropic documents for its own refusal stop reason.
        Buffered(
                """
                {"id":"r","model":"m","status":"completed",
                 "output":[{"type":"function_call","call_id":"c","name":"rm","arguments":"{\"path\":\"/\"}"},
                           {"type":"message","content":[{"type":"refusal","refusal":"No."}]}]}
                """
            )
            .GetProperty("stop_reason")
            .GetString()
            .Should()
            .Be("refusal");

    [Fact]
    public void A_streamed_refusal_reaches_the_client_as_text_and_a_refusal_stop_reason()
    {
        // response.refusal.delta / .done were both ignored, and the terminal payload can be lean, so a
        // streamed decline arrived as an empty turn that looked like a normal answer.
        var output = Streamed(
            Created,
            """{"type":"response.content_part.added","part":{"type":"refusal"}}""",
            """{"type":"response.refusal.delta","delta":"I cannot "}""",
            """{"type":"response.refusal.delta","delta":"help with that."}""",
            """{"type":"response.refusal.done","refusal":"I cannot help with that."}""",
            """{"type":"response.content_part.done","part":{"type":"refusal"}}""",
            Completed
        );

        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"I cannot \"");
        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"help with that.\"");
        output
            .Should()
            .NotContain("\"text\":\"I cannot help with that.\"", "the done event repeats what the deltas carried");
        output.Should().Contain("\"index\":0,\"content_block\":{\"type\":\"text\"");
        output.Should().NotContain("\"index\":1", "one refusal is one block");
        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void A_refusal_done_event_is_the_only_carrier_when_no_refusal_delta_arrived()
    {
        var output = Streamed(Created, """{"type":"response.refusal.done","refusal":"I will not."}""", Completed);

        output.Should().Contain("\"type\":\"text_delta\",\"text\":\"I will not.\"");
        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().Contain("event: message_stop");
    }

    [Fact]
    public void A_streamed_refusal_outranks_a_function_call_in_the_terminal_payload()
    {
        var output = Streamed(Created, """{"type":"response.refusal.delta","delta":"No."}""", CompletedWithCall);

        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().NotContain("\"stop_reason\":\"tool_use\"");
    }

    [Fact]
    public void A_refusal_carried_only_by_the_terminal_payload_is_still_reported()
    {
        // No refusal.* event at all — the whole decline sits in the completed response's output, exactly
        // as the buffered path would see it.
        var output = Streamed(
            Created,
            """{"type":"response.completed","response":{"status":"completed","output":[{"type":"message","content":[{"type":"refusal","refusal":"No."}]}],"usage":{}}}"""
        );

        output.Should().Contain("\"stop_reason\":\"refusal\"");
        output.Should().Contain("event: message_stop");
    }

    // ---------------------------------------------------------------------------------------------
    // Cross-modality parity: the buffered and the streamed path may not disagree about a turn.
    // ---------------------------------------------------------------------------------------------

    public static TheoryData<string, string> StopReasonMatrix =>
        new()
        {
            { """{"output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}]}""", "end_turn" },
            { """{"output":[{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""", "refusal" },
            { """{"output":[{"type":"function_call","call_id":"c","name":"n","arguments":"{}"}]}""", "tool_use" },
            { """{"output":[],"incomplete_details":{"reason":"max_output_tokens"}}""", "max_tokens" },
            { """{"output":[],"incomplete_details":{"reason":"content_filter"}}""", "refusal" },
            {
                """{"output":[{"type":"function_call","call_id":"c","name":"n","arguments":"{}"},{"type":"message","content":[{"type":"refusal","refusal":"no"}]}]}""",
                "refusal"
            },
            {
                """{"output":[{"type":"function_call","call_id":"c","name":"n","arguments":"{}"}],"incomplete_details":{"reason":"max_output_tokens"}}""",
                "tool_use"
            },
        };

    [Theory]
    [MemberData(nameof(StopReasonMatrix))]
    public void The_buffered_and_the_streamed_path_derive_the_same_stop_reason(string body, string expected)
    {
        Buffered(body).GetProperty("stop_reason").GetString().Should().Be(expected);

        Streamed(Created, """{"type":"response.completed","response":""" + body + "}")
            .Should()
            .Contain("\"stop_reason\":\"" + expected + "\"");
    }
}
