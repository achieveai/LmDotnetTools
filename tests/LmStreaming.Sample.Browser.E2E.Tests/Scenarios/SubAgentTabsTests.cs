using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Center-pane sub-agent TABS (#tabbed-subagents). A parent spawns a background sub-agent; once the
/// 3s poll surfaces it, a colored tab appears in the center tab strip, and selecting it switches the
/// center pane to that sub-agent's persisted transcript (the main view stays mounted underneath).
/// </summary>
/// <remarks>
/// This deliberately exercises only the DETERMINISTIC path the focus feature relies on — viewing a
/// COMPLETED child: the tab is driven by the <c>ListSubAgents</c> poll, and its transcript loads from
/// the persisted child thread over REST (both proven deterministic by the API-level
/// <c>SubAgentFocusFlowTests</c>). It does NOT race a live focus against a still-running child; that
/// raciness is why the composable/router logic is otherwise covered by vitest.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class SubAgentTabsTests
{
    private readonly PlaywrightFixture _fixture;

    public SubAgentTabsTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Sub_agent_gets_a_center_tab_that_shows_its_transcript(string providerMode)
    {
        const string ResearcherMarker = "You are the research sub-agent";
        const string ResearcherAnswer = "Found three fresh AI papers today.";

        var responder = ScriptedSseResponder
            .New()
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherMarker))
            .Turn(t => t.Text(ResearcherAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            // Background spawn: the Agent call returns a receipt immediately and the child runs
            // out-of-band, persisting its transcript under subagent-{agentId} (the tab replay source).
            .Turn(t => t.ToolCall(
                "Agent",
                new { subagent_type = "researcher", prompt = "Find AI papers", run_in_background = true }))
            .Turn(t => t.Text("Kicked off the researcher in the background."))
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
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // The poll (every 3s) surfaces the spawned child as a sub-agent tab alongside the `main` tab.
        await page.SubAgentTabs().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        (await page.ConversationTabs().IsVisibleAsync()).Should().BeTrue("the tab strip appears once a sub-agent exists");
        await page.ConversationTab("main").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.SubAgentTabs().First.WaitForTextContainsAsync("research", timeoutMs: 20_000);

        // Selecting the sub-agent tab switches the center pane to that child's persisted transcript.
        await page.SubAgentTabs().First.ClickAsync();
        await page.SubAgentView().WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("subagent-transcript").WaitForTextContainsAsync(ResearcherAnswer, timeoutMs: 20_000);

        // Selecting `main` returns to the parent conversation; the sub-agent view unmounts.
        await page.ConversationTab("main").ClickAsync();
        await page.GetByTestId("main-view").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        (await page.SubAgentView().CountAsync()).Should().Be(0, "the sub-agent view unmounts when main is active");

        await session.SaveSuccessScreenshotAsync($"SubAgentTabs.Sub_agent_gets_a_center_tab_{providerMode}");
    }

    /// <summary>
    /// Two sub-agents each get their OWN center tab with a DISTINCT assigned color, and that color also
    /// tints the sub-agent's inline <c>Agent</c> call pill in the parent conversation. Switching tabs
    /// swaps the center pane to the matching child's transcript. Mirrors the validated manual run
    /// <c>playwright-scripts/subagent-tabs.mjs</c>.
    /// </summary>
    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Two_sub_agents_get_distinct_colored_tabs(string providerMode)
    {
        const string AlphaMarker = "You are the ALPHA research sub-agent";
        const string BetaMarker = "You are the BETA math sub-agent";
        const string AlphaAnswer = "Alpha reporting: I found three fresh AI papers today.";
        const string BetaAnswer = "Beta reporting: the answer is 42.";

        // Two background spawns in successive parent turns (each returns a receipt immediately, so the
        // parent loop advances), then a wrap-up. Each child is scripted by its distinct system-prompt role.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("alpha", ctx => ctx.SystemPromptContains(AlphaMarker))
            .Turn(t => t.Text(AlphaAnswer))
            .ForRole("beta", ctx => ctx.SystemPromptContains(BetaMarker))
            .Turn(t => t.Text(BetaAnswer))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "alpha", name = "alpha", run_in_background = true, prompt = "go alpha" }))
            .Turn(t => t.ToolCall("Agent", new { subagent_type = "beta", name = "beta", run_in_background = true, prompt = "go beta" }))
            .Turn(t => t.Text("Spawned alpha and beta in the background."))
            .Build();

        await using var session = await _fixture.OpenAsync(
            providerMode,
            responder.HandlerFor(providerMode),
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["alpha"] = new SubAgentTemplate { Name = "alpha", SystemPrompt = AlphaMarker, AgentFactory = providerAgentFactory, MaxTurnsPerRun = 5 },
                        ["beta"] = new SubAgentTemplate { Name = "beta", SystemPrompt = BetaMarker, AgentFactory = providerAgentFactory, MaxTurnsPerRun = 5 },
                    },
                    MaxConcurrentSubAgents = 5,
                }
        );
        var page = session.Page;

        await page.SendMessageAsync("spawn two background workers");
        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // Two sub-agent tabs (alpha, beta) appear alongside main.
        await page.SubAgentTabs().WaitForCountAtLeastAsync(2, timeoutMs: 20_000);
        var labels = (await page.SubAgentTabs().AllInnerTextsAsync()).Select(l => l.Trim()).ToList();
        labels.Should().HaveCount(2);
        labels.Should().Contain(l => l.Contains("alpha"));
        labels.Should().Contain(l => l.Contains("beta"));

        // Each tab dot gets a DISTINCT assigned color.
        var dotColors = await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll(\"[data-testid='conversation-tab']:not([data-tab-id='main']) .conversation-tab__dot\")).map(d => getComputedStyle(d).backgroundColor)");
        dotColors.Should().HaveCount(2);
        dotColors.Distinct().Should().HaveCount(2, "each sub-agent tab gets a distinct color");

        // Each tab color also tints its inline Agent call pill in the parent conversation.
        var pillBorders = await page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelectorAll(\"[data-testid='main-view'] [data-testid='tool-call-pill'][data-tool-name='Agent']\")).map(p => getComputedStyle(p).borderLeftColor)");
        foreach (var color in dotColors)
        {
            pillBorders.Should().Contain(color, "the sub-agent's inline Agent pill is tinted to match its tab");
        }

        // Selecting each tab swaps the center pane to that child's transcript.
        await page.SubAgentTabs().Filter(new LocatorFilterOptions { HasText = "alpha" }).First.ClickAsync();
        await page.SubAgentView().WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("subagent-transcript").GetByText(AlphaAnswer, new LocatorGetByTextOptions { Exact = true })
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });

        await page.SubAgentTabs().Filter(new LocatorFilterOptions { HasText = "beta" }).First.ClickAsync();
        await page.GetByTestId("subagent-transcript").GetByText(BetaAnswer, new LocatorGetByTextOptions { Exact = true })
            .First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });

        await session.SaveSuccessScreenshotAsync($"SubAgentTabs.Two_sub_agents_distinct_colors_{providerMode}");
    }
}
