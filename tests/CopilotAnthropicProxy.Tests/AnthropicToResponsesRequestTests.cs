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
