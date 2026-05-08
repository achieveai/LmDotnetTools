using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Agents;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.OpenAiResponsesProvider.Tests;

/// <summary>
/// End-to-end agent tests: drive <see cref="OpenAiResponsesAgent"/> through an in-process
/// <see cref="OpenAiResponsesTestSseMessageHandler"/> and assert that the streaming-event
/// grammar is mapped to the expected sequence of <see cref="IMessage"/> values.
/// </summary>
public sealed class OpenAiResponsesAgentTests
{
    [Fact]
    public async Task Agent_streams_text_updates_and_finalized_text_message()
    {
        await using var rig = TestRig.Create();
        const string prompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"messages":[{"text_message":{"length":4}}]}
            ]}
            <|instruction_end|>
            """;
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = prompt },
        };

        var stream = await rig.Agent.GenerateReplyStreamingAsync(messages);

        var collected = new List<IMessage>();
        await foreach (var m in stream)
        {
            collected.Add(m);
        }

        collected.OfType<TextUpdateMessage>().Should().NotBeEmpty();
        var finalText = collected.OfType<TextMessage>().Single();
        finalText.Role.Should().Be(Role.Assistant);
        finalText.Text.Trim().Split(' ').Should().HaveCount(4);

        // Concatenated deltas should equal the finalized text.
        var concatenated = string.Concat(collected.OfType<TextUpdateMessage>().Select(t => t.Text));
        concatenated.Should().Be(finalText.Text);
    }

    [Fact]
    public async Task Agent_emits_tools_call_message_for_function_call_event_stream()
    {
        await using var rig = TestRig.Create();
        const string prompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"messages":[{"tool_call":[{"name":"search","args":{"q":"x"}}]}]}
            ]}
            <|instruction_end|>
            """;
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = prompt },
        };

        var stream = await rig.Agent.GenerateReplyStreamingAsync(messages);

        var calls = new List<ToolsCallMessage>();
        await foreach (var m in stream)
        {
            if (m is ToolsCallMessage tc)
            {
                calls.Add(tc);
            }
        }

        calls.Should().ContainSingle();
        var single = calls[0].ToolCalls.Should().ContainSingle().Subject;
        single.FunctionName.Should().Be("search");
        single.FunctionArgs.Should().Contain("\"q\":\"x\"");
    }

    [Fact]
    public async Task Agent_emits_usage_message_after_completed_lifecycle()
    {
        await using var rig = TestRig.Create();
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.User, Text = "say something short" },
        };

        var stream = await rig.Agent.GenerateReplyStreamingAsync(messages);

        UsageMessage? usage = null;
        await foreach (var m in stream)
        {
            if (m is UsageMessage u)
            {
                usage = u;
            }
        }

        usage.Should().NotBeNull();
        usage!.Usage.PromptTokens.Should().BeGreaterThan(0);
        usage.Usage.CompletionTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Agent_GenerateReplyAsync_aggregates_to_final_messages()
    {
        await using var rig = TestRig.Create();
        var messages = new IMessage[]
        {
            new TextMessage { Role = Role.System, Text = "be brief" },
            new TextMessage { Role = Role.User, Text = "ping" },
        };

        var result = await rig.Agent.GenerateReplyAsync(messages);

        result.OfType<TextMessage>().Should().NotBeEmpty();
    }

    private sealed class TestRig : IAsyncDisposable
    {
        public required OpenAiResponsesAgent Agent { get; init; }
        public required HttpClient Http { get; init; }

        public static TestRig Create()
        {
            var handler = new OpenAiResponsesTestSseMessageHandler { ChunkDelayMs = 0 };
            var http = new HttpClient(handler) { BaseAddress = new Uri("http://mock.local/") };
            var client = new OpenAiResponsesClient(http);
            var agent = new OpenAiResponsesAgent("test-agent", client);
            return new TestRig { Agent = agent, Http = http };
        }

        public ValueTask DisposeAsync()
        {
            Agent.Dispose();
            Http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
