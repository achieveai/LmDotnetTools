using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using FluentAssertions;
using LmStreaming.Sample.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.E2E.Tests.Scenarios;

/// <summary>
/// Exercises a <em>nested-prompt</em> sub-agent fan-out: the parent's scripted turn emits an
/// <c>Agent</c> tool call whose <c>prompt</c> argument carries an embedded
/// <c>&lt;|instruction_start|&gt;…&lt;|instruction_end|&gt;</c> instruction chain. The sub-agent is
/// backed by the embedded-chain handler (<see cref="TestSseMessageHandler"/> /
/// <see cref="AnthropicTestSseMessageHandler"/>), so it consumes that nested chain to call exactly
/// one tool (<c>calculate</c>) and then reply "hi from agent". Under the synchronous Agent model,
/// the sub-agent's final text comes back as the <c>Agent</c> tool result.
/// </summary>
/// <remarks>
/// The parent is scripted via <see cref="ScriptedSseResponder"/> (role dispatch, no embedded tags)
/// rather than its own embedded chain: nesting one chain inside another collides on the first
/// <c>&lt;|instruction_end|&gt;</c> tag (see <c>InstructionChainParser.ExtractInstructionChain</c>).
/// Keeping the parent scripted and only the sub-agent embedded-chain-driven sidesteps that.
/// </remarks>
public sealed class SubAgentEmbeddedChainTests
{
    // The nested prompt handed to the sub-agent via the Agent tool's `prompt` argument.
    // Turn 1: call `calculate` (a tool inherited from the parent). Turn 2: reply with text, which
    // ends the synchronous run and becomes the Agent tool result.
    private const string InnerChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"sub-tool","id_message":"Sub-agent uses calculate","messages":[{"tool_call":[{"name":"calculate","args":{"a":2,"operation":"add","b":3}}]}]},{"id":"sub-text","id_message":"Sub-agent replies","messages":[{"text":"hi from agent"}]}]}<|instruction_end|>""";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_passes_nested_chain_and_subagent_uses_tool_then_replies(string providerMode)
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
                .Turn(t => t.ToolCall(
                    "Agent",
                    new { subagent_type = "general-purpose", prompt = InnerChain }))
                .Turn(t => t.Text("Summary: the sub-agent replied 'hi from agent'."))
            .Build();

        var handler = providerMode == "test-anthropic"
            ? responder.AsAnthropicHandler()
            : responder.AsOpenAiHandler();

        var builder = new ScriptedBuilder(
            handler,
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(providerMode, loggerFactory));

        using var factory = new E2EWebAppFactory(providerMode, builder);

        var threadId = $"subagent-embedded-{providerMode}-{Guid.NewGuid():N}";
        var socket = await factory.ConnectWebSocketAsync(threadId);
        await using var client = new WebSocketTestClient(socket);

        await client.SendUserMessageAsync("delegate to the embedded-chain sub-agent");
        using var frames = await client.CollectUntilDoneAsync(TimeSpan.FromSeconds(30));

        // Parent delegated via the Agent tool.
        frames.ToolCallNames().Should().Contain("Agent");

        // Synchronous Agent: the sub-agent ran its nested chain (calculate -> text) and its final
        // text comes back as the Agent tool result — proving the embedded chain executed end to end.
        frames.ToolCallResults().Should().Contain(
            r => r.Contains("hi from agent", StringComparison.Ordinal),
            "the Agent tool result is the sub-agent's final text from the nested instruction chain");

        frames.ConcatText().Should().Contain("the sub-agent replied");

        responder.RemainingTurns["parent"].Should().Be(0);
    }

    private static SubAgentOptions BuildSubAgentOptions(string providerMode, ILoggerFactory loggerFactory)
    {
        var templates = new Dictionary<string, SubAgentTemplate>
        {
            ["general-purpose"] = new SubAgentTemplate
            {
                Name = "EmbeddedChainSub",
                // Marker only — the sub-agent's behavior comes from the embedded chain in its
                // prompt, not from role dispatch.
                SystemPrompt = "You are an embedded-chain sub-agent.",
                AgentFactory = () => BuildEmbeddedChainAgent(providerMode, loggerFactory),
                MaxTurnsPerRun = 5,
            },
        };

        return new SubAgentOptions
        {
            Templates = templates,
            MaxConcurrentSubAgents = 5,
        };
    }

    // Builds a sub-agent backed by the embedded-chain test handler (parses
    // <|instruction_start|>…<|instruction_end|> from the request), mirroring the sample's
    // test-mode agent construction (Program.CreateTestAgent / CreateAnthropicTestAgent).
    private static IStreamingAgent BuildEmbeddedChainAgent(string providerMode, ILoggerFactory loggerFactory)
    {
        if (providerMode == "test-anthropic")
        {
            var handler = new AnthropicTestSseMessageHandler(
                loggerFactory.CreateLogger<AnthropicTestSseMessageHandler>());
            var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://test-mode/v1") };
            var anthropicClient = new AnthropicClient(
                httpClient,
                baseUrl: "http://test-mode/v1",
                logger: loggerFactory.CreateLogger<AnthropicClient>());
            return new AnthropicAgent(
                "MockAnthropicSub",
                anthropicClient,
                loggerFactory.CreateLogger<AnthropicAgent>());
        }

        var openHandler = new TestSseMessageHandler(loggerFactory.CreateLogger<TestSseMessageHandler>());
        var openHttpClient = new HttpClient(openHandler) { BaseAddress = new Uri("http://test-mode/v1") };
        var openClient = new OpenClient(
            openHttpClient,
            "http://test-mode/v1",
            logger: loggerFactory.CreateLogger<OpenClient>());
        return new OpenClientAgent("MockSub", openClient, loggerFactory.CreateLogger<OpenClientAgent>());
    }
}
