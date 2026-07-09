using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// #171 — A <see cref="AchieveAi.LmDotnetTools.LmCore.Messages.NotifyMessage"/> renders as a distinct
/// notification pill, NOT a user bubble. The parent (scripted) issues an <c>Agent</c> tool call with
/// <c>run_in_background: true</c>: the spawn returns immediately, the parent finishes its turn and goes
/// idle, and the background researcher runs asynchronously. When the researcher completes,
/// <c>SubAgentManager.SendToParentAsync</c> relays its result as a <c>NotifyMessage</c> that wakes the
/// idle parent and is published live over the persistent WebSocket — surfacing a
/// <c>notification-pill</c> with <c>data-notify-kind="subagent-completion"</c>.
/// </summary>
/// <remarks>
/// Unlike <see cref="SubAgentLifecycleTests"/> (a SYNCHRONOUS Agent call whose sub-agent result becomes
/// the tool result), this exercises the BACKGROUND path — the only producer that emits a
/// <c>NotifyMessage</c> for a sub-agent completion. The decisive assertions: a
/// <c>notification-pill[data-notify-kind="subagent-completion"]</c> appears, and the number of user
/// bubbles stays at one (the message the user typed) — proving the notify (which maps to
/// <c>Role.User</c>) never rendered as a second user bubble.
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class NotificationPillTests
{
    private readonly PlaywrightFixture _fixture;

    public NotificationPillTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Background_sub_agent_completion_renders_notification_pill(string providerMode)
    {
        const string ResearcherMarker = "You are the research sub-agent";

        var responder = ScriptedSseResponder
            .New()
            // Declared first (most specific marker) so the sub-agent's request matches here, not the
            // parent role. The researcher runs one text turn, then completes.
            .ForRole("researcher", ctx => ctx.SystemPromptContains(ResearcherMarker))
            .Turn(t => t.Text("Found three fresh AI papers today."))
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            // Turn 1: spawn the researcher in the BACKGROUND (returns an id immediately, does not block).
            .Turn(t => t.ToolCall(
                "Agent",
                new { subagent_type = "researcher", prompt = "Find AI papers", run_in_background = true }))
            // Turn 2: acknowledge the spawn and end the run, so the parent goes idle while the
            // background researcher is still running.
            .Turn(t => t.Text("Spawned the researcher in the background; I'll report back when it finishes."))
            // Turn 3: the wake-run the injected NotifyMessage triggers once the researcher completes.
            // Consumed only when the notify arrives AFTER the parent went idle (a fast researcher can
            // instead land mid-run-2); either ordering publishes the pill, so this turn is best-effort
            // and RemainingTurns is intentionally not asserted.
            .Turn(t => t.Text("The researcher finished and reported its findings."))
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

        await page.SendMessageAsync("research AI papers in the background");

        // The pill is published out-of-band after the background researcher completes — it can arrive
        // during run 2 or after the parent goes idle (the persistent WebSocket stays open across runs),
        // so poll on the DOM state with a generous timeout rather than on stream-idle.
        await page.NotificationPills().WaitForCountAtLeastAsync(1, timeoutMs: 30_000);

        // It renders as a subagent-completion notification pill.
        var kinds = await page.NotificationPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-notify-kind') ?? '')");
        kinds.Should().Contain("subagent-completion");

        // The background spawn's Agent tool-call pill is present too.
        await page.ToolCallPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var toolNames = await page.ToolCallPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-tool-name') ?? '')");
        toolNames.Should().Contain("Agent");

        // The notify maps to Role.User but must NOT render as a user bubble: exactly one user-message
        // group exists — the message the user typed — proving the notify rendered only as a pill.
        var userGroups = await page.UserMessageGroups().CountAsync();
        userGroups.Should().Be(1, "the out-of-band NotifyMessage must render as a pill, not a second user bubble");

        await session.SaveSuccessScreenshotAsync(
            $"NotificationPill.Background_sub_agent_completion_{providerMode}");
    }
}
