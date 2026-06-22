using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Services.Discovery;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Regression proof for the "Workspace Agent mode omits marketplace agents" bug: an agent that the
/// UI's marketplace browser lists (a <see cref="CatalogAgent"/>) must be a spawnable
/// <c>subagent_type</c> in the <c>Agent</c> tool, end to end through the real Vue renderer.
/// </summary>
/// <remarks>
/// The sub-agent templates here are produced by the SAME production mapping the app uses —
/// <see cref="MarketplaceSubAgentLoader.MapCatalog"/> over a representative catalog — rather than
/// hand-built, so this exercises the actual catalog→template bridge. The parent (scripted) emits an
/// <c>Agent</c> tool call with <c>subagent_type = "orleans-reviewer"</c>, a name that exists ONLY
/// because the catalog agent was mapped into a spawnable template. The decisive assertion is the
/// sub-agent's own phrase ("grain reentrancy looks correct") appearing in the expanded Agent pill:
/// it is absent from the parent script, so it can only have come from the catalog-derived sub-agent
/// actually running — if the mapping had dropped the agent, the spawn would have failed instead.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class MarketplaceSubAgentTests
{
    private readonly PlaywrightFixture _fixture;

    public MarketplaceSubAgentTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    // A representative marketplace catalog (one marketplace → one plugin → one agent), mirroring the
    // shape the gateway's /marketplaces/preview returns and the UI browser shows.
    private static MarketplaceCatalog SampleCatalog() => new(
        Selected: ["ClaudePlugins"],
        Marketplaces:
        [
            new CatalogMarketplace(
                Alias: "ClaudePlugins",
                Error: null,
                Plugins:
                [
                    new CatalogPlugin(
                        Name: "orleans-dev",
                        Version: "1.0.2",
                        Description: "Orleans patterns, best practices, and code review.",
                        Skills: [],
                        Agents:
                        [
                            new CatalogAgent(
                                "orleans-reviewer", "Senior Orleans code reviewer", "orleans-dev",
                                "ClaudePlugins", "/marketplaces/ClaudePlugins/orleans-dev/agents/orleans-reviewer.md"),
                        ]),
                ]),
        ]);

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Marketplace_catalog_agent_is_spawnable_via_Agent_tool(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            // The sub-agent's system prompt is the best-effort persona MapToTemplate builds; it
            // contains the agent's name, so this predicate dispatches the spawned catalog agent.
            .ForRole("reviewer", ctx => ctx.SystemPromptContains("orleans-reviewer"))
            .Turn(t => t.Text("Reviewed: grain reentrancy looks correct."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            // subagent_type "orleans-reviewer" only resolves because the catalog agent was mapped
            // into a spawnable template — that is exactly the bug fix under test.
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "orleans-reviewer", prompt = "Review my grain" }))
            // Deliberately free of the reviewer's phrase — proof of execution must come from the sub-agent.
            .Turn(t => t.Text("Parent summary: review delegated."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (_, providerAgentFactory) => new SubAgentOptions
            {
                // Real production mapping: catalog → spawnable templates.
                Templates = MarketplaceSubAgentLoader.MapCatalog(SampleCatalog(), providerAgentFactory),
                MaxConcurrentSubAgents = 5,
            });
        var page = session.Page;

        await page.SendMessageAsync("ask the orleans reviewer to look at my grain");
        // The synchronous Agent call blocks until the sub-agent returns, so allow extra time.
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The parent delegated via the Agent tool.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // Parent's final summary rendered (proves the run completed through turn 2).
        await page.AssistantText().WaitForCountAtLeastAsync(1);
        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", assistantTexts).Should().Contain("review delegated");

        // Decisive check: expand the Agent pill; the sub-agent's own output is the tool result.
        // "grain reentrancy looks correct" appears nowhere in the parent script, so its presence
        // proves the catalog-derived sub-agent actually spawned and ran.
        await page.ToolCallPills().First.ClickAsync();
        var pillContent = page.Locator(".tool-call-result");
        await pillContent.First.WaitForAsync();
        var expanded = string.Join(" ", await pillContent.AllInnerTextsAsync());
        expanded.Should().Contain("grain reentrancy looks correct",
            "the Agent tool result is the marketplace catalog agent's own reply");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"MarketplaceSubAgent.{providerMode}");
    }
}
