using System.Text.Json;
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
}
