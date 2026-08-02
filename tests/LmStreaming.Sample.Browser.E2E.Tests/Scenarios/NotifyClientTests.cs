using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Issue #246 — cross-boundary coverage for the browser-hosted <c>NotifyClient</c> client tool.
/// Drives the REAL <c>NotifyClientToolProvider</c>: unlike <c>AskUserQuestion</c> it never defers and
/// never triggers a run of its own — it resolves immediately (<c>ToolHandlerResult.FromText</c>) and
/// separately publishes a <see cref="AchieveAi.LmDotnetTools.LmCore.Messages.NotifyMessage"/>
/// (<c>notify_kind: client-notification</c>) that renders as its own <c>notification-pill</c>, distinct
/// from the tool-call pill for the call itself.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class NotifyClientTests
{
    private readonly PlaywrightFixture _fixture;

    public NotifyClientTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    private const string NotifyMessageText = "Heads up: kicking off a long summary now.";

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task NotifyClient_renders_live_without_pausing_or_starting_an_extra_run(string providerMode)
    {
        // Turn 1: NotifyClient (resolves immediately, does NOT pause). Turn 2: a long text block so the
        // run stays visibly active for a few seconds after the notify fires — the window in which we
        // assert the pill is already live, proving it did not merely appear after the fact / on reload.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("NotifyClient", new { message = NotifyMessageText, label = "Progress" }))
            .Turn(t => t.TextLen(3_000))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("go do the long thing");
        await page.WaitForStreamActiveAsync();

        // LIVE: the notification pill appears over the persistent WebSocket while the run is still
        // in flight (turn 2's long text is still streaming) — not only after the run completes.
        await page.NotificationPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        (await page.StopButton().IsVisibleAsync())
            .Should()
            .BeTrue("the NotifyClient pill must render WHILE the run is still active, not only after it completes");

        var kinds = await page.NotificationPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-notify-kind') ?? '')");
        kinds.Should().Contain("client-notification");

        // The NotifyClient tool call itself still renders as an ordinary (non-blocking) tool-call pill —
        // it never defers, so it never shows an awaiting-input form.
        await page.ToolCallPillByName("NotifyClient").WaitForAsync();

        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        // NO EXTRA RUN: NotifyClient bypasses the input queue entirely (see NotifyClientToolProvider's
        // remarks) — it must not inject a second user turn or fork a second assistant reply. Exactly one
        // of each, and the full two-turn scripted plan (not three) ran to completion.
        (await page.UserMessageGroups().CountAsync())
            .Should()
            .Be(1, "NotifyClient must not enqueue an additional user turn");
        (await page.AssistantMessageGroups().CountAsync())
            .Should()
            .Be(1, "NotifyClient must not fork a redundant assistant run");

        responder.RemainingTurns["parent"]
            .Should()
            .Be(0, "the scripted plan is exactly notify -> long text, with no extra turn consumed");

        await session.SaveSuccessScreenshotAsync(
            $"NotifyClient.NotifyClient_renders_live_without_pausing_or_starting_an_extra_run_{providerMode}");
    }

    /// <summary>
    /// A page reload after the notify has already been published must rehydrate the SAME
    /// notification pill from persisted history (the <see cref="AchieveAi.LmDotnetTools.LmCore.Messages.NotifyMessage"/>
    /// round-trips through the REST conversation load, not just the live WebSocket push).
    /// </summary>
    [Fact]
    public async Task NotifyClient_notification_survives_reload()
    {
        const string ProviderMode = "test-anthropic";
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.ToolCall("NotifyClient", new { message = NotifyMessageText, label = "Progress" }))
            .Turn(t => t.Text("All done."))
            .Build();

        await using var session = await _fixture.OpenAsync(ProviderMode, responder.HandlerFor(ProviderMode));
        var page = session.Page;

        await page.NewChatButton().ClickAsync();
        await page.SendMessageAsync("go do the thing");
        await page.WaitForStreamIdleAsync();

        await page.NotificationPills().WaitForCountAtLeastAsync(1);
        await page.ConversationItems().WaitForCountAtLeastAsync(1);
        var threadId = await page.ConversationItems().First.GetAttributeAsync("data-thread-id");
        threadId.Should().NotBeNullOrEmpty();

        var deepLink = $"{session.Factory.ServerAddress.TrimEnd('/')}/?threadId={threadId}";
        await page.GotoAsync(deepLink);
        await page.Textarea().WaitForAsync();

        await page.NotificationPills().WaitForCountAtLeastAsync(1, timeoutMs: 20_000);
        var kinds = await page.NotificationPills()
            .EvaluateAllAsync<string[]>("nodes => nodes.map(n => n.getAttribute('data-notify-kind') ?? '')");
        kinds.Should().Contain("client-notification", "the persisted NotifyMessage must rehydrate from REST history");

        (await page.UserMessageGroups().CountAsync())
            .Should()
            .Be(1, "rehydration must not duplicate the notify as a second user bubble");

        await session.SaveSuccessScreenshotAsync("NotifyClient.NotifyClient_notification_survives_reload");
    }
}
