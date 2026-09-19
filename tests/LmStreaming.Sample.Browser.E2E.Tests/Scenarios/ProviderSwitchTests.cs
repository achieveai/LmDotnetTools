using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Runtime PROVIDER switch between the two scripted mock providers — <c>test</c> (OpenAI wire) and
/// <c>test-anthropic</c> (Anthropic wire) — inside a single page session. The provider selector lives
/// in the root composer, opens upward without escaping the viewport, and never appears in the
/// chat-context header or a sub-agent composer. Turn 1 streams on the boot provider; the composer
/// dropdown switches the provider while idle (POST
/// <c>/api/conversations/{threadId}/provider</c> → the pool recreates the agent on the OTHER wire);
/// turn 2 must then stream on the recreated agent and spawn a background child whose focused composer
/// has no provider control. Both parent turns come from ONE <see cref="ScriptedSseResponder"/> whose
/// two wire handlers share a plan queue, so this proves the switch rebuilds the agent against a
/// different provider and the next turn is served correctly.
///
/// The exact HTTP codes (409 while streaming, 503 unavailable) are covered deterministically by
/// <c>ConversationsControllerTests</c>; this browser test asserts UI + streamed content only.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ProviderSwitchTests
{
    private readonly PlaywrightFixture _fixture;

    public ProviderSwitchTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test", "test-anthropic", "Test (Anthropic)")]
    [InlineData("test-anthropic", "test", "Test (Mock)")]
    public async Task Provider_dropdown_switches_between_scripted_mocks_and_next_turn_streams(
        string bootProvider,
        string targetProvider,
        string targetLabel
    )
    {
        const int ViewportHeight = 800;
        const string ChildMarker = "You are the provider-selector regression child";

        // ONE responder, two turns. The role matches on the system prompt, which both the OpenAI and
        // Anthropic extractors surface, so either wire pops the next plan.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("child", ctx => ctx.SystemPromptContains(ChildMarker))
            .Turn(t => t.Text("Child ready for a focused reply."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("First turn answer."))
            .Turn(t =>
                t.ToolCall(
                    "Agent",
                    new
                    {
                        subagent_type = "provider-child",
                        name = "provider-child",
                        prompt = "Open a focused child composer.",
                        run_in_background = true,
                    }
                )
            )
            .Turn(t => t.Text("Second turn answer."))
            .Build();

        // Boot on bootProvider (its own wire). The provider-aware ScriptedBuilder overload lets the
        // agent be recreated on EITHER wire when the provider is switched.
        await using var session = await _fixture.OpenAsync(
            bootProvider,
            responder,
            subAgentFactory: (_, providerAgentFactory) =>
                new SubAgentOptions
                {
                    Templates = new Dictionary<string, SubAgentTemplate>
                    {
                        ["provider-child"] = new SubAgentTemplate
                        {
                            Name = "provider-child",
                            SystemPrompt = ChildMarker,
                            AgentFactory = providerAgentFactory,
                            MaxTurnsPerRun = 5,
                        },
                    },
                    MaxConcurrentSubAgents = 1,
                }
        );
        var page = session.Page;
        await page.SetViewportSizeAsync(1280, ViewportHeight);

        // Turn 1 on the boot provider — establishes the (started) conversation.
        await page.SendMessageAsync("first");
        await page.WaitForStreamIdleAsync();
        string.Join(" ", await page.AssistantText().AllInnerTextsAsync()).Should().Contain("First turn answer");

        var rootComposer = page.GetByTestId("main-view").GetByTestId("chat-input");
        var rootProviderButton = rootComposer.GetByTestId("provider-selector-button");
        await Assertions.Expect(rootProviderButton).ToHaveCountAsync(1);
        await Assertions
            .Expect(page.Locator(".chat-context-header").GetByTestId("provider-selector-button"))
            .ToHaveCountAsync(0);

        await AssertComposerStaysInsideViewportAsync(page, rootComposer, 1280, ViewportHeight);
        await AssertComposerStaysInsideViewportAsync(page, rootComposer, 390, ViewportHeight);
        await page.SetViewportSizeAsync(1280, ViewportHeight);

        // Switch the provider while idle: the root composer dropdown opens UPWARD, then selecting an
        // option fires the backend switch (recreate on the other wire). Bounding boxes are relative
        // to the viewport, so the menu's bottom edge must stay at or above the trigger's top edge.
        await rootProviderButton.ClickAsync();
        var menu = page.ProviderSelectorMenu();
        await menu.WaitForAsync();
        var triggerBox = await rootProviderButton.BoundingBoxAsync();
        var menuBox = await menu.BoundingBoxAsync();
        triggerBox.Should().NotBeNull("the root-composer provider trigger must be visible and measurable");
        menuBox.Should().NotBeNull("the opened provider menu must be visible and measurable");
        (menuBox!.Y + menuBox.Height)
            .Should()
            .BeLessThanOrEqualTo(
                triggerBox!.Y + 1,
                "the root composer is pinned to the viewport bottom, so its provider menu must open upward"
            );

        await page.ProviderOption(targetProvider).ClickAsync();
        await rootProviderButton.WaitForTextContainsAsync(targetLabel);

        // Turn 2 must stream on the RECREATED agent (the other wire). If the switch didn't rebuild the
        // agent against the target provider, the scripted handler would 404 the mismatched wire and
        // this would never render / go idle.
        await page.SendMessageAsync("second");
        await page.WaitForStreamIdleAsync();
        string.Join(" ", await page.AssistantText().AllInnerTextsAsync()).Should().Contain("Second turn answer");

        // The second turn spawns a real background child. Selecting its tab mounts the child's own
        // reply composer; the provider selector remains owned by the hidden root composer only.
        await page.AgentPickerTrigger().WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        await page.OpenAgentPickerAsync();
        await page.AgentPickerOptions().First.ClickAsync();
        var subAgentView = page.SubAgentView();
        await subAgentView.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await Assertions.Expect(subAgentView.GetByTestId("chat-input")).ToHaveCountAsync(1);
        await Assertions.Expect(subAgentView.GetByTestId("provider-selector-button")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("provider-selector-button")).ToHaveCountAsync(1);

        responder.RemainingTurns["parent"].Should().Be(0, "both turns ran to completion across the switch");
        responder.RemainingTurns["child"].Should().Be(0, "the focused sub-agent is the real spawned child");
        await session.SaveSuccessScreenshotAsync($"ProviderSwitch.root_composer_{bootProvider}_to_{targetProvider}");
    }

    private static async Task AssertComposerStaysInsideViewportAsync(
        IPage page,
        ILocator rootComposer,
        int width,
        int height
    )
    {
        await page.SetViewportSizeAsync(width, height);

        foreach (
            var (surface, name) in new[]
            {
                (rootComposer, "root composer"),
                (rootComposer.GetByTestId("send-button"), "root send button"),
            }
        )
        {
            var box = await surface.BoundingBoxAsync();
            box.Should().NotBeNull($"the {name} must remain visible at {width}px");
            box!.X.Should().BeGreaterThanOrEqualTo(0, $"the {name} must stay inside the left viewport edge");
            box.Y.Should().BeGreaterThanOrEqualTo(0, $"the {name} must stay inside the top viewport edge");
            (box.X + box.Width)
                .Should()
                .BeLessThanOrEqualTo(width + 1, $"the {name} must stay inside the right viewport edge");
            (box.Y + box.Height)
                .Should()
                .BeLessThanOrEqualTo(height + 1, $"the {name} must stay inside the bottom viewport edge");
        }
    }
}
