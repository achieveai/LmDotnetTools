using System.Text.Json;
using AchieveAi.LmDotnetTools.AnthropicProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.OpenAIProvider.Agents;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Browser-level proof of the sub-agent RECURSION GUARD: a sub-agent cannot spawn a sub-agent. A
/// parent spawns a sub-agent whose embedded instruction chain itself tries to spawn a SECOND-level
/// sub-agent via the <c>Agent</c> tool. Because <c>MultiTurnAgentLoop</c> registers the
/// <c>Agent</c>/<c>CheckAgent</c>/<c>SendMessage</c> tools AFTER snapshotting the parent's tools, and
/// <c>SubAgentManager.CreateSubAgentAsync</c> builds each sub-agent loop with NO
/// <c>SubAgentManager</c>, a sub-agent never inherits the <c>Agent</c> tool — the level-2 call resolves
/// to an unknown tool and the delegation tree stops one level deep.
/// </summary>
/// <remarks>
/// This is the deliberate "prevent unbounded recursive delegation" invariant, and it is exactly why
/// the multi-level nested-chain prompt in <c>PromptExamples.md</c> only ever executes its first level.
/// Genuine multi-level orchestration is the <c>StartWorkflowAgent</c> workflow feature (a controller
/// loop is a nested-root that re-registers <c>Agent</c>), not the plain <c>Agent</c> tool.
///
/// Decisive assertion: the level-2 marker text is unique to the L2 chain, so if it never appears in
/// the DOM, the second-level sub-agent never ran. 1-level embedded delegation working end to end is
/// covered by <see cref="SubAgentEmbeddedChainTests"/>.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SubAgentRecursionGuardTests
{
    private const string L2Marker = "L2-SHOULD-NEVER-RUN";

    private readonly PlaywrightFixture _fixture;

    public SubAgentRecursionGuardTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Wrap(object chain) =>
        $"<|instruction_start|>{JsonSerializer.Serialize(chain)}<|instruction_end|>";

    // Level-2 chain — a single text turn. It must NEVER execute (a sub-agent can't spawn a sub-agent).
    private static string L2Chain() =>
        Wrap(new { instruction_chain = new object[] { new { id = "l2", messages = new object[] { new { text = L2Marker } } } } });

    // Level-1 chain (the sub-agent the PARENT spawns): turn 1 attempts to spawn a level-2 sub-agent via
    // the Agent tool (which the sub-agent does NOT have), turn 2 ends the run with a leaf text.
    private static string L1Chain() =>
        Wrap(new
        {
            instruction_chain = new object[]
            {
                new
                {
                    id = "l1-spawn",
                    messages = new object[]
                    {
                        new
                        {
                            tool_call = new object[]
                            {
                                new { name = "Agent", args = new { subagent_type = "general-purpose", prompt = L2Chain() } },
                            },
                        },
                    },
                },
                new { id = "l1-text", messages = new object[] { new { text = "L1 leaf reached." } } },
            },
        });

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Sub_agent_cannot_spawn_a_sub_agent_level_two_never_runs(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "general-purpose", prompt = L1Chain() }))
            .Turn(t => t.Text("Parent done: delegation finished."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (loggerFactory, _) => BuildSubAgentOptions(providerMode, loggerFactory)
        );
        var page = session.Page;

        await page.SendMessageAsync("delegate to a sub-agent that tries to sub-delegate");
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The parent delegated via the Agent tool.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // Parent's run completed (the sub-agent's failed sub-delegation must not hang or crash the run).
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = string.Join(" ", await page.AssistantText().AllInnerTextsAsync());
        assistantTexts.Should().Contain("delegation finished");

        // DECISIVE: expand the parent's Agent pill. Its RESULT is the sub-agent's (L1) own output — L1
        // attempted a level-2 spawn (a no-op: a sub-agent has no Agent tool) then continued to its leaf.
        // So the RESULT carries L1's leaf text and NEVER the level-2 marker. The marker DOES appear in
        // the call's ARGUMENTS (as input text) — which is why we check the RESULT section only, not the
        // whole page. The precise "sub-agent has no Agent tool" invariant is locked deterministically by
        // SubAgentToolInheritanceExclusionTests.SpawnedSubAgents_DoNotInheritTheAgentFamilyTools_RecursionGuard.
        await page.ToolCallPills().First.ClickAsync();
        var result = page.Locator(".tool-call-result");
        await result.First.WaitForAsync();
        var resultText = string.Join(" ", await result.AllInnerTextsAsync());
        resultText.Should().Contain("L1 leaf reached", "the sub-agent ran past its no-op level-2 spawn to its own leaf");
        resultText.Should().NotContain(L2Marker, "the level-2 chain never executes, so no RESULT carries its marker");

        await session.SaveSuccessScreenshotAsync($"SubAgentRecursionGuard.{providerMode}");
    }

    private static SubAgentOptions BuildSubAgentOptions(string providerMode, ILoggerFactory loggerFactory)
    {
        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "RecursionGuardSub",
                    SystemPrompt = "You are an embedded-chain sub-agent.",
                    AgentFactory = () => BuildEmbeddedChainAgent(providerMode, loggerFactory),
                    MaxTurnsPerRun = 5,
                },
            },
            MaxConcurrentSubAgents = 5,
        };
    }

    // Sub-agent backed by the embedded-chain test handler (parses <|instruction_start|>…<|instruction_end|>
    // from the request), mirroring SubAgentEmbeddedChainTests / the sample's test-mode agent construction.
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
