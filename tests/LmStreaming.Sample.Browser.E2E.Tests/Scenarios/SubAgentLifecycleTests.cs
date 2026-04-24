using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC3 — Sub-agent spawn / progress / result rendering. Parent emits an <c>Agent</c>
/// tool call; sub-agent returns text; parent summarizes. Verifies the UI renders both
/// a tool-call pill (the <c>Agent</c> relay) and the parent's final text.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class SubAgentLifecycleTests
{
    private readonly PlaywrightFixture _fixture;

    public SubAgentLifecycleTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Parent_spawns_sub_agent_and_renders_result(string providerMode)
    {
        const string ResearcherMarker = "You are the research sub-agent";

        var responder = ScriptedSseResponder
            .New()
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherMarker))
            .Turn(t => t.Text("Found three fresh AI papers today."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { template_name = "researcher", task = "Find AI papers" }))
            .Turn(t => t.Text("Summary: researcher surfaced three AI papers."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["researcher"] = new SubAgentTemplate
                        {
                            Name = "Researcher",
                            SystemPrompt = ResearcherMarker,
                            AgentFactory = providerAgentFactory,
                            MaxTurnsPerRun = 5,
                        },
                    },
                    MaxConcurrentSubAgents = 5,
                }
        );
        var page = session.Page;

        await page.SendMessageAsync("research AI papers for me");
        // Sub-agent flow is slower than plain streams — allow more time for the second
        // parent turn to surface after the Agent relay completes.
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // A tool-call pill for the Agent relay should be visible.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // The parent's final text should be rendered after the relay.
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts).Should().Contain("researcher surfaced three AI papers");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"SubAgent.Parent_spawns_sub_agent_and_renders_result_{providerMode}");
    }
}
