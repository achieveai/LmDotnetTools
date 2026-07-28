using System.Text.Json;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

public class AnthropicToResponsesRequestTests
{
    private static JsonElement TranslateToElement(string anthropicJson) =>
        JsonDocument.Parse(AnthropicToResponsesRequest.Translate(anthropicJson)).RootElement.Clone();

    [Fact]
    public void Translates_a_plain_text_turn()
    {
        var result = TranslateToElement(
            """
            {"model":"gpt-5.3-codex","max_tokens":1024,"stream":true,
             "messages":[{"role":"user","content":"Hello"}]}
            """
        );

        result.GetProperty("model").GetString().Should().Be("gpt-5.3-codex");
        result.GetProperty("max_output_tokens").GetInt32().Should().Be(1024);
        result.GetProperty("stream").GetBoolean().Should().BeTrue();
        result.GetProperty("store").GetBoolean().Should().BeFalse();

        var item = result.GetProperty("input")[0];
        item.GetProperty("type").GetString().Should().Be("message");
        item.GetProperty("role").GetString().Should().Be("user");
        item.GetProperty("content")[0].GetProperty("type").GetString().Should().Be("input_text");
        item.GetProperty("content")[0].GetProperty("text").GetString().Should().Be("Hello");
    }

    [Fact]
    public void Clamps_the_model_validation_probe_to_the_minimum_output_tokens()
    {
        // Claude Code's first request against any new model is `max_tokens: 1`. The Responses API
        // rejects max_output_tokens < 16, so without this clamp EVERY GPT model is rejected on sight.
        var result = TranslateToElement(
            """
            {"model":"gpt-5.3-codex","max_tokens":1,
             "messages":[{"role":"user","content":[{"type":"text","text":"Hi"}]}]}
            """
        );

        result.GetProperty("max_output_tokens").GetInt32().Should().Be(AnthropicToResponsesRequest.MinimumOutputTokens);
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(15, 16)]
    [InlineData(16, 16)]
    [InlineData(17, 17)]
    public void Clamps_max_tokens_at_the_minimum_output_tokens_boundary(int maxTokens, int expected)
    {
        var result = TranslateToElement(
            $$"""{"model":"m","max_tokens":{{maxTokens}},"messages":[{"role":"user","content":"Hi"}]}"""
        );

        result.GetProperty("max_output_tokens").GetInt32().Should().Be(expected);
    }

    [Fact]
    public void Defaults_stream_to_false_when_absent()
    {
        var result = TranslateToElement(
            """{"model":"m","max_tokens":100,"messages":[{"role":"user","content":"Hi"}]}"""
        );

        result.GetProperty("stream").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Flattens_a_system_block_array_into_instructions()
    {
        // Claude Code ALWAYS sends `system` as an array of text blocks, never a bare string.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,
             "system":[{"type":"text","text":"You are terse.","cache_control":{"type":"ephemeral","ttl":"1h"}},
                       {"type":"text","text":"Answer in English."}],
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        result.GetProperty("instructions").GetString().Should().Be("You are terse.\n\nAnswer in English.");
    }

    [Fact]
    public void Accepts_the_legacy_bare_string_system_prompt()
    {
        var result = TranslateToElement(
            """{"model":"m","max_tokens":100,"system":"Be brief.","messages":[{"role":"user","content":"Hi"}]}"""
        );

        result.GetProperty("instructions").GetString().Should().Be("Be brief.");
    }

    [Fact]
    public void Turns_tool_use_and_tool_result_into_top_level_items()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[
              {"role":"user","content":"weather?"},
              {"role":"assistant","content":[
                 {"type":"text","text":"checking"},
                 {"type":"tool_use","id":"call_1","name":"get_weather","input":{"city":"Paris"}}]},
              {"role":"user","content":[
                 {"type":"tool_result","tool_use_id":"call_1","content":[{"type":"text","text":"18C"}]}]}
            ]}
            """
        );

        var input = result.GetProperty("input");
        input.GetArrayLength().Should().Be(4);

        input[1].GetProperty("type").GetString().Should().Be("message");
        input[1].GetProperty("content")[0].GetProperty("type").GetString().Should().Be("output_text");

        input[2].GetProperty("type").GetString().Should().Be("function_call");
        input[2].GetProperty("call_id").GetString().Should().Be("call_1");
        input[2].GetProperty("name").GetString().Should().Be("get_weather");
        input[2].GetProperty("arguments").GetString().Should().Be("""{"city":"Paris"}""");

        input[3].GetProperty("type").GetString().Should().Be("function_call_output");
        input[3].GetProperty("call_id").GetString().Should().Be("call_1");
        input[3].GetProperty("output").GetString().Should().Be("18C");
    }

    [Fact]
    public void Maps_function_tools_and_drops_server_tools()
    {
        // web_search_20250305 has no input_schema. Copilot answers
        // 400 "The use of the web search tool is not supported." if it is forwarded.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[
               {"name":"get_weather","description":"Weather.","input_schema":{"type":"object","properties":{}}},
               {"type":"web_search_20250305","name":"web_search","max_uses":8}],
             "tool_choice":{"type":"any"}}
            """
        );

        var tools = result.GetProperty("tools");
        tools.GetArrayLength().Should().Be(1);
        tools[0].GetProperty("type").GetString().Should().Be("function");
        tools[0].GetProperty("name").GetString().Should().Be("get_weather");
        tools[0].GetProperty("parameters").GetProperty("type").GetString().Should().Be("object");

        result.GetProperty("tool_choice").GetString().Should().Be("required");
    }

    [Theory]
    [InlineData("auto", "auto")]
    [InlineData("none", "none")]
    public void Maps_tool_choice_auto_and_none(string anthropicType, string expected)
    {
        var result = TranslateToElement(
            $$$"""
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[{"name":"get_weather","input_schema":{"type":"object"}}],
             "tool_choice":{"type":"{{{anthropicType}}}"}}
            """
        );

        result.GetProperty("tool_choice").GetString().Should().Be(expected);
    }

    [Fact]
    public void Maps_tool_choice_naming_a_specific_tool()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[{"name":"get_weather","input_schema":{"type":"object"}}],
             "tool_choice":{"type":"tool","name":"get_weather"}}
            """
        );

        var toolChoice = result.GetProperty("tool_choice");
        toolChoice.GetProperty("type").GetString().Should().Be("function");
        toolChoice.GetProperty("name").GetString().Should().Be("get_weather");
    }

    [Fact]
    public void Omits_tool_choice_when_no_function_tools_survive_filtering()
    {
        // Every tool here is a server tool (no input_schema), so BuildTools produces an empty array.
        // Emitting `tool_choice` alongside an absent `tools` is a Responses 400 ("'tool_choice' is
        // only allowed when 'tools' are specified") — a legal Anthropic request must not become that.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"web_search_20250305","name":"web_search","max_uses":8}],
             "tool_choice":{"type":"any"}}
            """
        );

        result.TryGetProperty("tools", out _).Should().BeFalse();
        result.TryGetProperty("tool_choice", out _).Should().BeFalse();
    }

    [Fact]
    public void Omits_tool_choice_naming_a_tool_that_filtering_dropped()
    {
        // tool_choice names the server tool that BuildTools' input_schema filter removed from `tools`.
        // Forwarding {"type":"function","name":"web_search"} would point Responses at a tool that no
        // longer exists in the request, so the safer behavior is to omit tool_choice entirely.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":"hi"}],
             "tools":[
               {"name":"get_weather","input_schema":{"type":"object"}},
               {"type":"web_search_20250305","name":"web_search","max_uses":8}],
             "tool_choice":{"type":"tool","name":"web_search"}}
            """
        );

        result.GetProperty("tools").GetArrayLength().Should().Be(1, "only get_weather survives the filter");
        result.TryGetProperty("tool_choice", out _).Should().BeFalse();
    }

    [Fact]
    public void Drops_fields_the_responses_api_would_reject()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"betas":["context-management-2025-06-27"],
             "stop_sequences":["</severity>"],"metadata":{"user_id":"abc"},
             "messages":[{"role":"user","content":[
               {"type":"thinking","thinking":"hmm","signature":"sig"},
               {"type":"text","text":"Hi","cache_control":{"type":"ephemeral","scope":"global"}}]}]}
            """
        );

        result.TryGetProperty("betas", out _).Should().BeFalse();
        result.TryGetProperty("stop_sequences", out _).Should().BeFalse();
        result.TryGetProperty("metadata", out _).Should().BeFalse();

        var content = result.GetProperty("input")[0].GetProperty("content");
        content.GetArrayLength().Should().Be(1, "thinking blocks are not replayed");
        content[0].GetProperty("text").GetString().Should().Be("Hi");
        content[0].TryGetProperty("cache_control", out _).Should().BeFalse();
    }

    [Fact]
    public void Always_asks_for_reasoning_summaries()
    {
        // Without this field the Responses stream carries no reasoning_summary events, so the
        // translated route can never produce a thinking block. It is sent on every request — no
        // opt-in, no model sniffing — which is why it is asserted on the plainest possible body.
        var result = TranslateToElement(
            """{"model":"m","max_tokens":100,"messages":[{"role":"user","content":"Hi"}]}"""
        );

        var reasoning = result.GetProperty("reasoning");
        reasoning.GetProperty("summary").GetString().Should().Be("auto");
        reasoning
            .TryGetProperty("effort", out _)
            .Should()
            .BeFalse("a request that never enabled thinking must cost exactly what it costs today");
    }

    [Theory]
    [InlineData(1024, "low")]
    [InlineData(4096, "low")] // Claude Code's lowest tier
    [InlineData(8191, "low")]
    [InlineData(8192, "medium")] // boundary: the low/medium threshold is exclusive below
    [InlineData(10240, "medium")] // Claude Code's middle tier
    [InlineData(24575, "medium")]
    [InlineData(24576, "high")] // boundary: the medium/high threshold is inclusive above
    [InlineData(32768, "high")] // Claude Code's top tier
    public void Maps_the_thinking_budget_onto_a_reasoning_effort(int budgetTokens, string expected)
    {
        var result = TranslateToElement(
            $$"""
            {"model":"m","max_tokens":100,"thinking":{"type":"enabled","budget_tokens":{{budgetTokens}}},
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        var reasoning = result.GetProperty("reasoning");
        reasoning.GetProperty("effort").GetString().Should().Be(expected);
        reasoning.GetProperty("summary").GetString().Should().Be("auto", "summaries are asked for either way");
    }

    [Fact]
    public void Treats_enabled_thinking_with_no_budget_as_medium_effort()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"thinking":{"type":"enabled"},
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        result.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be("medium");
    }

    [Theory]
    [InlineData("""{"type":"disabled"}""")]
    [InlineData("""{"type":"enabled_but_not_really"}""")]
    [InlineData("""{"budget_tokens":32768}""")] // a budget without type:enabled is not a request to think
    [InlineData("""{"type":null}""")]
    [InlineData("\"enabled\"")] // a bare string where an object belongs must not throw
    [InlineData("null")]
    public void Sends_no_effort_unless_thinking_is_explicitly_enabled(string thinking)
    {
        var result = TranslateToElement(
            $$"""
            {"model":"m","max_tokens":100,"thinking":{{thinking}},
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        var reasoning = result.GetProperty("reasoning");
        reasoning.TryGetProperty("effort", out _).Should().BeFalse();
        reasoning.GetProperty("summary").GetString().Should().Be("auto");
    }

    [Fact]
    public void Does_not_forward_the_thinking_field_itself()
    {
        // Responses has no top-level `thinking`; it is consumed into `reasoning` and must not leak.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"thinking":{"type":"enabled","budget_tokens":10240},
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        result.TryGetProperty("thinking", out _).Should().BeFalse();
    }

    [Fact]
    public void Passes_temperature_and_top_p_through()
    {
        // Probed live before being allowed through: OpenAI documents its own reasoning models as
        // rejecting a non-default temperature, which would have made this a 400 on every such request.
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"temperature":0.7,"top_p":0.5,
             "messages":[{"role":"user","content":"Hi"}]}
            """
        );

        result.GetProperty("temperature").GetDouble().Should().Be(0.7);
        result.GetProperty("top_p").GetDouble().Should().Be(0.5);
    }

    [Fact]
    public void Omits_temperature_and_top_p_when_the_client_sent_neither()
    {
        var result = TranslateToElement(
            """{"model":"m","max_tokens":100,"messages":[{"role":"user","content":"Hi"}]}"""
        );

        result.TryGetProperty("temperature", out _).Should().BeFalse();
        result.TryGetProperty("top_p", out _).Should().BeFalse();
    }

    [Fact]
    public void Maps_a_base64_image_block_to_a_data_url()
    {
        var result = TranslateToElement(
            """
            {"model":"m","max_tokens":100,"messages":[{"role":"user","content":[
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]}]}
            """
        );

        var part = result.GetProperty("input")[0].GetProperty("content")[0];
        part.GetProperty("type").GetString().Should().Be("input_image");
        part.GetProperty("image_url").GetString().Should().Be("data:image/png;base64,AAAA");
    }
}
