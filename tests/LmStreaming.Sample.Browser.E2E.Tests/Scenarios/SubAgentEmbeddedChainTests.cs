using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level proof that a <em>nested-prompt</em> sub-agent works end to end through the real
/// Vue renderer. The parent (scripted) emits an <c>Agent</c> tool call whose <c>prompt</c> argument
/// carries an embedded <c>&lt;|instruction_start|&gt;…&lt;|instruction_end|&gt;</c> instruction
/// chain. The sub-agent is backed by the embedded-chain handler, so it consumes that nested chain
/// to call <c>calculate</c> and then reply "hi from agent" — which returns as the synchronous
/// <c>Agent</c> tool result.
/// </summary>
/// <remarks>
/// The decisive assertion: the parent's own summary deliberately does NOT contain the phrase
/// "hi from agent", so when that phrase appears in the expanded <c>Agent</c> tool-call pill it can
/// only have come from the sub-agent executing its embedded chain. The pill's full content
/// (including the tool result) is rendered into the DOM only once the pill is expanded
/// (EventPill: <c>v-if="isExpanded &amp;&amp; fullContent"</c>).
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SubAgentEmbeddedChainTests
{
    private readonly PlaywrightFixture _fixture;

    public SubAgentEmbeddedChainTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // The nested prompt handed to the sub-agent via the Agent tool's `prompt` argument:
    // turn 1 calls `calculate` (inherited from the parent), turn 2 replies with text that ends the
    // synchronous run and becomes the Agent tool result.
    private const string InnerChain =
        """<|instruction_start|>{"instruction_chain":[{"id":"sub-tool","id_message":"Sub-agent uses calculate","messages":[{"tool_call":[{"name":"calculate","args":{"a":2,"operation":"add","b":3}}]}]},{"id":"sub-text","id_message":"Sub-agent replies","messages":[{"text":"hi from agent"}]}]}<|instruction_end|>""";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Nested_chain_drives_sub_agent_and_result_renders_in_ui(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "general-purpose", prompt = InnerChain }))
            // Deliberately free of "hi from agent" — that phrase can only reach the UI from the
            // sub-agent's embedded chain, not from this scripted parent summary.
            .Turn(t => t.Text("Parent summary: delegation finished."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(providerMode, loggerFactory)
        );
        var page = session.Page;

        await page.SendMessageAsync("delegate to the embedded-chain sub-agent");
        // The synchronous Agent call blocks until the sub-agent runs its full nested chain
        // (calculate -> text), so allow extra time for the parent's second turn to surface.
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The parent delegated via the Agent tool.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // Parent's final summary rendered (proves the run completed through turn 2).
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts).Should().Contain("delegation finished");

        // Decisive check: expand the Agent pill and confirm the sub-agent's own output is the tool
        // result. "hi from agent" appears nowhere in the parent script, so its presence proves the
        // embedded nested chain executed (tool turn -> text turn) and flowed back through the UI.
        await page.ToolCallPills().First.ClickAsync();
        var pillContent = page.Locator(".tool-call-result");
        await pillContent.First.WaitForAsync();
        var expanded = string.Join(" ", await pillContent.AllInnerTextsAsync());
        expanded.Should().Contain("hi from agent",
            "the expanded Agent tool result is the sub-agent's final text from the nested chain");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"SubAgentEmbeddedChain.{providerMode}");
    }

    private static SubAgentOptions BuildSubAgentOptions(string providerMode, ILoggerFactory loggerFactory)
    {
        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "EmbeddedChainSub",
                    // Marker only — behavior comes from the embedded chain in the prompt, not role dispatch.
                    SystemPrompt = "You are an embedded-chain sub-agent.",
                    AgentFactory = () => BuildEmbeddedChainAgent(providerMode, loggerFactory),
                    MaxTurnsPerRun = 5,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
    }

    // Sub-agent backed by the embedded-chain test handler (parses
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
