using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// Instruction prompts on the chat-completions and Anthropic-messages routes. Request bodies are
/// trimmed from real CLI traffic: Claude Code puts <c>&lt;system-reminder&gt;</c> parts around the
/// human text, Copilot CLI prefixes it with <c>&lt;current_datetime&gt;</c> in one string.
/// </summary>
public sealed class InstructionChainRoutingTests
{
    private const string ScenarioReply = "SCENARIO_REPLY";

    private const string ToolsListChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"tools","messages":[{"tools_list":{}}]}]}<|instruction_end|>""";

    private const string ReadThenTextChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"call","messages":[{"tool_call":[{"name":"Read","args":{"file_path":"note.txt"}}]}]},{"id":"done","messages":[{"text":"Step two ran."}]}]}<|instruction_end|>""";

    private const string ClaudeTools = """
        [{"name":"Read","description":"r","input_schema":{"type":"object"}},
         {"name":"WebFetch","description":"f","input_schema":{"type":"object"}},
         {"name":"WebSearch","description":"s","input_schema":{"type":"object"}}]
        """;

    private const string CopilotTools = """
        [{"type":"function","function":{"name":"powershell","parameters":{"type":"object"}}},
         {"type":"function","function":{"name":"view","parameters":{"type":"object"}}},
         {"type":"function","function":{"name":"sample_tools-echo","parameters":{"type":"object"}}}]
        """;

    [Fact]
    public async Task Claude_tools_list_after_system_reminders_lists_the_tools()
    {
        var messages = $"[{ClaudeUser(ToolsListChain)}]";

        var stream = await PostAsync("/v1/messages", ClaudeBody(messages));

        stream.Should().Contain("Read, WebFetch, WebSearch").And.NotContain(ScenarioReply);
    }

    [Fact]
    public async Task Claude_plain_message_after_a_chain_gets_the_scenario()
    {
        var messages = $$"""
            [{{ClaudeUser(ToolsListChain)}},
             {"role":"assistant","content":[{"type":"text","text":"Read, WebFetch, WebSearch"}]},
             {{ClaudeUser("Hello again, a plain message.")}}]
            """;

        var stream = await PostAsync("/v1/messages", ClaudeBody(messages));

        stream.Should().Contain(ScenarioReply).And.NotContain("WebFetch");
    }

    [Fact]
    public async Task Claude_tool_result_keeps_the_chain_running()
    {
        var messages = $$"""
            [{{ClaudeUser(ReadThenTextChain)}},
             {"role":"assistant","content":[{"type":"tool_use","id":"toolu_1","name":"Read","input":{"file_path":"note.txt"} }]},
             {"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1",
               "content":"1\thello\n\n<system-reminder>\n<total_tokens>1</total_tokens>\n</system-reminder>"}]}]
            """;

        var stream = await PostAsync("/v1/messages", ClaudeBody(messages));

        stream.Should().Contain("Step two ran.").And.NotContain(ScenarioReply);
    }

    [Fact]
    public async Task Copilot_tools_list_behind_a_datetime_prefix_lists_the_tools()
    {
        var user = JsonSerializer.Serialize(
            $"<current_datetime>2026-11-01T18:00:00Z</current_datetime>\n\n{ToolsListChain}"
        );
        var body = $$"""
            {"model":"claude-sonnet-4.5","stream":true,"tools":{{CopilotTools}},
             "messages":[{"role":"system","content":"You are Copilot."},{"role":"user","content":{{user}} }]}
            """;

        var stream = await PostAsync("/v1/chat/completions", body);

        stream.Should().Contain("powershell, view, sample_tools-echo").And.NotContain(ScenarioReply);
    }

    [Fact]
    public async Task Chat_plain_message_gets_the_scenario()
    {
        var body = """
            {"model":"claude-sonnet-4.5","stream":true,
             "messages":[{"role":"user","content":"<current_datetime>now</current_datetime>\n\nhi"}]}
            """;

        var stream = await PostAsync("/v1/chat/completions", body);

        stream.Should().Contain(ScenarioReply);
    }

    [Theory]
    [InlineData(
        """{"messages":[{"role":"user","content":"CHAIN"},{"role":"user","content":"<notification kind=\"x\">done</notification>"}]}""",
        true
    )]
    [InlineData(
        """{"messages":[{"role":"user","content":"CHAIN"},{"role":"user","content":"<trigger>wait</trigger>"}]}""",
        true
    )]
    [InlineData(
        """{"messages":[{"role":"user","content":"CHAIN"},{"role":"user","content":"<agent-message from=\"a\">hi</agent-message>"}]}""",
        true
    )]
    [InlineData(
        """{"messages":[{"role":"user","content":"CHAIN"},{"role":"user","content":"<elapsed-time-notice>5m</elapsed-time-notice>"}]}""",
        true
    )]
    [InlineData(
        """{"messages":[{"role":"user","content":"CHAIN"},{"role":"tool","tool_call_id":"c1","content":"out"}]}""",
        true
    )]
    [InlineData(
        """{"messages":[{"role":"user","content":[{"type":"text","text":"CHAIN"},{"type":"text","text":"<system-reminder>late</system-reminder>"}]}]}""",
        true
    )]
    [InlineData("""{"messages":[{"role":"user","content":"CHAIN"},{"role":"user","content":"plain"}]}""", false)]
    [InlineData("""{"input":[]}""", false)]
    [InlineData("not json", false)]
    public void Routes_on_the_latest_human_text(string body, bool expected)
    {
        var withChain = body.Replace("CHAIN", "go " + InstructionChainRouter.ChainStart, StringComparison.Ordinal);

        InstructionChainRouter.CarriesInstructionChain(withChain).Should().Be(expected);
    }

    private static string ClaudeUser(string humanText)
    {
        return $$"""
            {"role":"user","content":[
              {"type":"text","text":"<system-reminder>\nAs you answer the user's questions...\n</system-reminder>"},
              {"type":"text","text":"<system-reminder>\nToday's date is 2026-11-01.\n</system-reminder>"},
              {"type":"text","text":{{JsonSerializer.Serialize(humanText)}},"cache_control":{"type":"ephemeral"} }]}
            """;
    }

    private static string ClaudeBody(string messages)
    {
        return $$"""
            {"model":"claude-sonnet-4-6","stream":true,"max_tokens":32000,
             "system":[{"type":"text","text":"You are a Claude agent."}],
             "tools":{{ClaudeTools}},"messages":{{messages}} }
            """;
    }

    private static async Task<string> PostAsync(string path, string body)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("scenario", _ => true)
            .Turn(t => t.Text(ScenarioReply))
            .Build();
        await using var fixture = await EphemeralHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(path, content);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
