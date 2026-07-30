using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class ResponsesToAnthropicJsonTests
{
    private static JsonElement Translate(string responsesJson) =>
        JsonDocument.Parse(ResponsesToAnthropicJson.Translate(responsesJson, "fallback-model")).RootElement.Clone();

    [Fact]
    public void Translates_a_text_response()
    {
        var result = Translate(
            """
            {"id":"resp_1","model":"gpt-5.3-codex",
             "output":[{"type":"message","role":"assistant",
                        "content":[{"type":"output_text","text":"Hello there"}]}],
             "usage":{"input_tokens":12,"output_tokens":3}}
            """
        );

        result.GetProperty("id").GetString().Should().Be("resp_1");
        result.GetProperty("type").GetString().Should().Be("message");
        result.GetProperty("role").GetString().Should().Be("assistant");
        result.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        result.GetProperty("stop_sequence").ValueKind.Should().Be(JsonValueKind.Null);
        result.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(12);
        result.GetProperty("usage").GetProperty("output_tokens").GetInt32().Should().Be(3);

        var content = result.GetProperty("content");
        content.GetArrayLength().Should().Be(1);
        content[0].GetProperty("type").GetString().Should().Be("text");
        content[0].GetProperty("text").GetString().Should().Be("Hello there");
    }

    [Fact]
    public void Translates_a_function_call_into_a_tool_use_block()
    {
        var result = Translate(
            """
            {"id":"resp_2","model":"m",
             "output":[{"type":"function_call","call_id":"call_9","name":"get_weather",
                        "arguments":"{\"city\":\"Paris\"}"}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("tool_use");

        var block = result.GetProperty("content")[0];
        block.GetProperty("type").GetString().Should().Be("tool_use");
        block.GetProperty("id").GetString().Should().Be("call_9");
        block.GetProperty("name").GetString().Should().Be("get_weather");
        block.GetProperty("input").GetProperty("city").GetString().Should().Be("Paris");
    }

    [Fact]
    public void Surfaces_a_reasoning_summary_as_a_thinking_block()
    {
        var result = Translate(
            """
            {"id":"r","model":"m",
             "output":[{"type":"reasoning","summary":[{"type":"summary_text","text":"weighing options"}]},
                       {"type":"message","content":[{"type":"output_text","text":"Done"}]}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        var content = result.GetProperty("content");
        content[0].GetProperty("type").GetString().Should().Be("thinking");
        content[0].GetProperty("thinking").GetString().Should().Be("weighing options");
        content[1].GetProperty("type").GetString().Should().Be("text");
    }

    [Fact]
    public void Reports_max_tokens_when_the_response_was_truncated()
    {
        var result = Translate(
            """
            {"id":"r","model":"m","incomplete_details":{"reason":"max_output_tokens"},
             "output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}],
             "usage":{"input_tokens":1,"output_tokens":16}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("max_tokens");
    }

    [Fact]
    public void An_empty_output_yields_an_empty_content_array_not_a_placeholder_block()
    {
        // This is what Claude Code's `max_tokens: 1` model-validation probe can produce against a
        // reasoning model. The envelope must be well-formed; the content must NOT be invented.
        var result = Translate("""{"id":"r","model":"m","output":[],"usage":{"input_tokens":1,"output_tokens":0}}""");

        result.GetProperty("content").GetArrayLength().Should().Be(0);
        result.GetProperty("stop_reason").GetString().Should().Be("end_turn");
        result.GetProperty("type").GetString().Should().Be("message");
    }

    [Fact]
    public void Falls_back_to_the_supplied_model_when_the_response_omits_one()
    {
        var result = Translate("""{"id":"r","output":[]}""");

        result.GetProperty("model").GetString().Should().Be("fallback-model");
        result.GetProperty("usage").GetProperty("input_tokens").GetInt32().Should().Be(0);
    }

    [Fact]
    public void Throws_ArgumentException_for_malformed_json_rather_than_a_raw_JsonException()
    {
        var act = () => ResponsesToAnthropicJson.Translate("{not valid json", "fallback-model");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_ArgumentException_when_a_top_level_field_has_an_unexpected_type()
    {
        // "id" is a number here instead of a string: GetValue<string>() would otherwise leak an
        // InvalidOperationException past the documented contract.
        var act = () => ResponsesToAnthropicJson.Translate("""{"id":123,"output":[]}""", "fallback-model");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_ArgumentException_when_the_top_level_value_is_not_an_object()
    {
        var act = () => ResponsesToAnthropicJson.Translate("[]", "fallback-model");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void DeriveStopReason_tolerates_a_non_string_output_item_type()
    {
        // Called directly (not via Translate), which is how ResponsesToAnthropicSse (Task 8) uses it,
        // outside Translate's own try/catch. A non-string "type" must not throw.
        var response = (JsonObject)JsonNode.Parse("""{"output":[{"type":123}]}""")!;

        ResponsesToAnthropicJson.DeriveStopReason(response).Should().Be("end_turn");
    }

    [Fact]
    public void DeriveStopReason_tolerates_a_non_string_incomplete_details_reason()
    {
        var response = (JsonObject)JsonNode.Parse("""{"output":[],"incomplete_details":{"reason":123}}""")!;

        ResponsesToAnthropicJson.DeriveStopReason(response).Should().Be("end_turn");
    }

    [Fact]
    public void Malformed_function_call_arguments_fail_the_reply_rather_than_degrading_to_an_empty_object()
    {
        // Degrading to {} used to produce a well-formed tool_use whose input had silently lost every
        // argument the model chose, which the client cannot distinguish from a parameterless call.
        var act = () =>
            ResponsesToAnthropicJson.Translate(
                """
                {"id":"r","model":"m",
                 "output":[{"type":"function_call","call_id":"call_1","name":"noop","arguments":"not json"}],
                 "usage":{"input_tokens":1,"output_tokens":1}}
                """,
                "fallback-model"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Missing_function_call_arguments_fail_the_reply()
    {
        var act = () =>
            ResponsesToAnthropicJson.Translate(
                """
                {"id":"r","model":"m",
                 "output":[{"type":"function_call","call_id":"call_1","name":"noop"}],
                 "usage":{"input_tokens":1,"output_tokens":1}}
                """,
                "fallback-model"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Function_call_arguments_that_are_not_a_json_object_fail_the_reply()
    {
        var act = () =>
            ResponsesToAnthropicJson.Translate(
                """
                {"id":"r","model":"m",
                 "output":[{"type":"function_call","call_id":"call_1","name":"noop","arguments":"[1,2]"}],
                 "usage":{"input_tokens":1,"output_tokens":1}}
                """,
                "fallback-model"
            );

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_explicit_empty_arguments_object_is_still_honoured()
    {
        // A genuinely parameterless call is not the failure case above: "{}" reads cleanly.
        var result = Translate(
            """
            {"id":"r","model":"m",
             "output":[{"type":"function_call","call_id":"call_1","name":"noop","arguments":"{}"}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        var block = result.GetProperty("content")[0];
        block.GetProperty("type").GetString().Should().Be("tool_use");
        block.GetProperty("input").EnumerateObject().Should().BeEmpty();
    }

    [Fact]
    public void An_output_field_that_is_not_an_array_fails_the_reply()
    {
        // Reading a present-but-wrong-kind output as "absent" turned an invalid upstream reply into a
        // successful, empty Anthropic message.
        var act = () => ResponsesToAnthropicJson.Translate("""{"id":"r","output":{}}""", "fallback-model");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reports_refusal_when_a_classifier_stopped_the_turn()
    {
        var result = Translate(
            """
            {"id":"r","model":"m","incomplete_details":{"reason":"content_filter"},
             "output":[{"type":"message","content":[{"type":"output_text","text":"partial"}]}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("refusal");
    }

    [Fact]
    public void A_classifier_stop_outranks_a_half_formed_function_call()
    {
        // A filtered turn can still carry the function call the model had begun. Reporting that as
        // tool_use invites the client to execute it.
        var result = Translate(
            """
            {"id":"r","model":"m","incomplete_details":{"reason":"content_filter"},
             "output":[{"type":"function_call","call_id":"c","name":"rm","arguments":"{\"path\":\"/\"}"}],
             "usage":{"input_tokens":1,"output_tokens":1}}
            """
        );

        result.GetProperty("stop_reason").GetString().Should().Be("refusal");
    }

    [Theory]
    // Copilot has been observed reporting counts that do not fit int, and whole numbers written in
    // floating-point form. Both must read the same here as they do on the streaming path.
    [InlineData("3000000000", 3000000000L)]
    [InlineData("12.0", 12L)]
    [InlineData("\"9\"", 0L)]
    [InlineData("null", 0L)]
    public void Token_counts_are_read_through_one_shared_policy(string literal, long expected)
    {
        var result = Translate(
            $$"""{"id":"r","model":"m","output":[],"usage":{"input_tokens":{{literal}},"output_tokens":1} }"""
        );

        result.GetProperty("usage").GetProperty("input_tokens").GetInt64().Should().Be(expected);
    }
}
