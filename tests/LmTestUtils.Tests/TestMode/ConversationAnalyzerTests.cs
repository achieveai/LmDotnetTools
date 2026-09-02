using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmTestUtils.Tests.TestMode;

/// <summary>
/// Pins how <see cref="ConversationAnalyzer"/> picks the instruction chain that drives the next mock
/// reply. Regression guard for #711: a sub-agent completion <see cref="NotifyMessage"/> relays the
/// child's raw task prompt — in test mode a full <c>&lt;|instruction_start|&gt;…</c> block — as a user
/// turn, and the analyzer must NOT treat that embedded chain as the parent's script.
/// </summary>
public class ConversationAnalyzerTests
{
    private const string ParentChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"spawn","id_message":"Spawn worker","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"researcher","run_in_background":true,"prompt":"do work"}}]}]},{"id":"parent-done","id_message":"Wrap up","messages":[{"text":"Spawned alpha in the background."}]}]}<|instruction_end|>""";

    private const string ChildChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"a1","id_message":"Child reports","messages":[{"text":"Alpha reporting: I found three fresh AI papers today."}]}]}<|instruction_end|>""";

    private readonly ConversationAnalyzer _analyzer = new(
        NullLogger<ConversationAnalyzer>.Instance,
        new InstructionChainParser(NullLogger<InstructionChainParser>.Instance)
    );

    [Fact]
    public void AnalyzeConversation_ChainEmbeddedInNotificationTextBlock_ResumesParentChain()
    {
        // Anthropic wire: the completion notification is a text block folded into the same user turn
        // as the tool_result (AnthropicRequest.MergeConsecutiveSameRoleMessages).
        var request = AnthropicRequest(
            UserText(ParentChain),
            AssistantToolUse("toolu_1"),
            UserToolResultAndText("toolu_1", CompletionEnvelope(task: ChildChain))
        );

        var (plan, assistantCount) = _analyzer.AnalyzeConversation(request);

        Assert.NotNull(plan);
        Assert.Equal("Wrap up", plan.IdMessage);
        Assert.Equal(1, assistantCount);
    }

    [Fact]
    public void AnalyzeConversation_ChainEmbeddedInNotificationStringContent_ResumesParentChain()
    {
        // OpenAI wire: the notification maps through ICanGetText to a user message with string content.
        var request = OpenAiRequest(
            UserString(ParentChain),
            AssistantToolCall("call_1"),
            ToolResult("call_1"),
            UserString(CompletionEnvelope(task: ChildChain))
        );

        var (plan, assistantCount) = _analyzer.AnalyzeConversation(request);

        Assert.NotNull(plan);
        Assert.Equal("Wrap up", plan.IdMessage);
        Assert.Equal(1, assistantCount);
    }

    [Fact]
    public void AnalyzeConversation_NotificationWithoutChain_ResumesParentChain()
    {
        var request = AnthropicRequest(
            UserText(ParentChain),
            AssistantToolUse("toolu_1"),
            UserToolResultAndText("toolu_1", CompletionEnvelope(task: "summarise the news"))
        );

        var (plan, assistantCount) = _analyzer.AnalyzeConversation(request);

        Assert.NotNull(plan);
        Assert.Equal("Wrap up", plan.IdMessage);
        Assert.Equal(1, assistantCount);
    }

    [Fact]
    public void AnalyzeConversation_NewerUserChain_WinsOverOlderChain()
    {
        const string NewerChain =
            """<|instruction_start|>{"instruction_chain":[{"id":"n1","id_message":"Newer step","messages":[{"text":"newer"}]}]}<|instruction_end|>""";

        var request = AnthropicRequest(
            UserText(ParentChain),
            AssistantText("Spawned alpha in the background."),
            UserText(NewerChain)
        );

        var (plan, assistantCount) = _analyzer.AnalyzeConversation(request);

        Assert.NotNull(plan);
        Assert.Equal("Newer step", plan.IdMessage);
        Assert.Equal(0, assistantCount);
    }

    /// <summary>
    /// The real envelope: <see cref="NotifyMessage.Text"/> built exactly as
    /// <c>SubAgentManager</c> composes the completion relay, so the test pins the wire shape the
    /// analyzer sees rather than a hand-typed approximation.
    /// </summary>
    private static string CompletionEnvelope(string task)
    {
        var relay =
            "<sub-agent name=\"researcher\" id=\"agent-1\">\n"
            + $"[Completed] Task: {task}\n"
            + "Result: Alpha reporting: I found three fresh AI papers today.\n"
            + "</sub-agent>";

        return NotifyMessage
            .Create(
                NotifyKinds.SubAgentCompletion,
                detail: relay,
                sourceToolName: "Agent",
                sourceToolCallId: "agent-1",
                label: "researcher"
            )
            .Text;
    }

    private static JsonElement AnthropicRequest(params object[] messages) =>
        JsonSerializer.SerializeToElement(new { model = "claude-test", messages });

    private static JsonElement OpenAiRequest(params object[] messages) =>
        JsonSerializer.SerializeToElement(new { model = "gpt-test", messages });

    private static object UserText(string text) =>
        new { role = "user", content = new object[] { new { type = "text", text } } };

    private static object AssistantText(string text) =>
        new { role = "assistant", content = new object[] { new { type = "text", text } } };

    private static object AssistantToolUse(string id) =>
        new
        {
            role = "assistant",
            content = new object[]
            {
                new
                {
                    type = "tool_use",
                    id,
                    name = "Agent",
                    input = new
                    {
                        subagent_type = "researcher",
                        run_in_background = true,
                        prompt = "do work",
                    },
                },
            },
        };

    private static object UserToolResultAndText(string toolUseId, string text) =>
        new
        {
            role = "user",
            content = new object[]
            {
                new
                {
                    type = "tool_result",
                    tool_use_id = toolUseId,
                    content = "{\"agent_id\":\"agent-1\",\"status\":\"spawned\"}",
                },
                new { type = "text", text },
            },
        };

    private static object UserString(string content) => new { role = "user", content };

    private static object AssistantToolCall(string id) =>
        new
        {
            role = "assistant",
            content = (string?)null,
            tool_calls = new object[]
            {
                new
                {
                    id,
                    type = "function",
                    function = new { name = "Agent", arguments = "{\"subagent_type\":\"researcher\"}" },
                },
            },
        };

    private static object ToolResult(string toolCallId) =>
        new
        {
            role = "tool",
            tool_call_id = toolCallId,
            content = "{\"agent_id\":\"agent-1\",\"status\":\"spawned\"}",
        };
}
