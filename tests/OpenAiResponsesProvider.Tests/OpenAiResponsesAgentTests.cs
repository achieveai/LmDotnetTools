using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
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
    public async Task Agent_emits_reasoning_message_for_response_reasoning_item()
    {
        await using var rig = TestRig.Create();
        const string prompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"reasoning":{"length":4},"messages":[{"text":"final answer"}]}
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

        var reasoning = collected.OfType<ReasoningMessage>().Single();
        reasoning.Role.Should().Be(Role.Assistant);
        // The reasoning here comes from the provider's reasoning_summary_text stream — a SUMMARY,
        // not full chain-of-thought — so it must carry Summary visibility (otherwise an Anthropic
        // replay serializes it as an unsigned thinking block and is rejected with 400).
        reasoning.Visibility.Should().Be(ReasoningVisibility.Summary);
        reasoning.Reasoning.Should().NotBeNullOrWhiteSpace();
        reasoning.MessageOrderIdx.Should().Be(0);

        // Whole-flow coverage: the summary streamed as reasoning_summary_text.delta events (surfaced
        // as ReasoningUpdateMessage) through the mock SSE handler before the terminal reasoning
        // message; the deltas must concatenate to the final reasoning text and share its visibility.
        var reasoningUpdates = collected.OfType<ReasoningUpdateMessage>().ToList();
        reasoningUpdates.Should().NotBeEmpty("the reasoning summary must stream incrementally through the SSE pipe");
        reasoningUpdates.Should().OnlyContain(u => u.Visibility == ReasoningVisibility.Summary);
        string.Concat(reasoningUpdates.Select(u => u.Reasoning)).Should().Be(reasoning.Reasoning);

        var finalText = collected.OfType<TextMessage>().Single();
        finalText.Text.Should().Be("final answer");
        finalText.MessageOrderIdx.Should().Be(1);
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

    // ---------------------------------------------------------------------------------------------
    // Regression: GPT-5.5 / Copilot Responses "duplicate assistant bubble" bug.
    // The provider emits text deltas followed by its OWN finalizing TextMessage. Two layers must
    // treat that as ONE logical message:
    //   1) MessageTransformationMiddleware must give the finalizing TextMessage the SAME
    //      messageOrderIdx as the deltas (so the live UI merges them by generationId+messageOrderIdx).
    //   2) MessageUpdateJoinerMiddleware must not ALSO emit a synthesized "built" copy alongside the
    //      provider's finalizing message (so history/persistence/reload store it once).
    // Both are driven here through the real mock OpenAI /responses SSE stream.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Agent_throughMessageTransformation_finalizedText_sharesOrderIdx_withTextDeltas()
    {
        await using var rig = TestRig.Create();
        const string prompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"messages":[{"text_message":{"length":5}}]}
            ]}
            <|instruction_end|>
            """;
        var messages = new IMessage[] { new TextMessage { Role = Role.User, Text = prompt } };

        var pipeline = rig.Agent.WithMessageTransformation();

        var collected = new List<IMessage>();
        await foreach (var m in await pipeline.GenerateReplyStreamingAsync(messages))
        {
            collected.Add(m);
        }

        var deltas = collected.OfType<TextUpdateMessage>().ToList();
        var finalText = collected.OfType<TextMessage>().Single();
        deltas.Should().NotBeEmpty();
        var deltaOrderIdxs = deltas.Select(d => d.MessageOrderIdx).Distinct().ToList();
        deltaOrderIdxs.Should().ContainSingle("all text deltas of one answer share one messageOrderIdx");
        finalText.MessageOrderIdx.Should().Be(
            deltaOrderIdxs[0],
            "the finalizing TextMessage must reuse the delta stream's messageOrderIdx so the client merges them into one bubble"
        );
    }

    [Fact]
    public async Task Agent_throughTransformationAndJoiner_yields_single_finalized_text_message()
    {
        await using var rig = TestRig.Create();
        const string prompt = """
            <|instruction_start|>
            {"instruction_chain":[
                {"messages":[{"text_message":{"length":6}}]}
            ]}
            <|instruction_end|>
            """;
        var messages = new IMessage[] { new TextMessage { Role = Role.User, Text = prompt } };

        // The same downstream "for history" pipeline the MultiTurnAgentLoop assembles:
        //   provider -> MessageTransformation (assigns messageOrderIdx) -> MessageUpdateJoiner.
        var pipeline = rig.Agent
            .WithMessageTransformation()
            .WithMiddleware(new MessageUpdateJoinerMiddleware());

        var collected = new List<IMessage>();
        await foreach (var m in await pipeline.GenerateReplyStreamingAsync(messages))
        {
            collected.Add(m);
        }

        var textMessages = collected.OfType<TextMessage>().ToList();
        textMessages.Should().ContainSingle(
            "the joiner must not emit both a synthesized built copy and the provider's finalizing TextMessage"
        );
        textMessages[0].Role.Should().Be(Role.Assistant);
        textMessages[0].Text.Trim().Split(' ').Should().HaveCount(6);
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
