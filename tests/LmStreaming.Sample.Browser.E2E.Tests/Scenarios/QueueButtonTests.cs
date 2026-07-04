using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Bug 4 — the composer's primary button has THREE mutually-exclusive states. While a run is
/// streaming, an empty input box shows the red <c>Stop</c> button (cancels the run), while a
/// non-empty box shows the blue <c>Queue</c> button (queues the typed message through the existing
/// full-duplex path). When idle it shows <c>Send</c>. These tests drive a long scripted stream so
/// the "streaming" window stays open long enough to observe the swap and to queue a follow-up.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class QueueButtonTests
{
    private readonly PlaywrightFixture _fixture;

    public QueueButtonTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Queue_button_swaps_with_stop_based_on_input_text_while_streaming(string providerMode)
    {
        // Long streamed turn so the stream stays in-flight while we type/clear the input.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.TextLen(2000))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("please produce a very long response");
        await page.WaitForStreamActiveAsync();

        // Empty box while streaming → red Stop, no Queue.
        await Assertions.Expect(page.StopButton()).ToBeVisibleAsync();
        await Assertions.Expect(page.QueueButton()).ToBeHiddenAsync();

        // Type a draft while streaming → blue Queue replaces Stop.
        await page.Textarea().FillAsync("a follow-up typed mid-stream");
        await Assertions.Expect(page.QueueButton()).ToBeVisibleAsync();
        await Assertions.Expect(page.StopButton()).ToBeHiddenAsync();

        // Clear the draft → reverts to Stop (so an empty box can still cancel the run).
        await page.Textarea().FillAsync(string.Empty);
        await Assertions.Expect(page.StopButton()).ToBeVisibleAsync();
        await Assertions.Expect(page.QueueButton()).ToBeHiddenAsync();

        // Clean up: cancel the still-active run and confirm idle.
        await page.StopButton().ClickAsync();
        await page.WaitForStreamIdleAsync();
        await session.SaveSuccessScreenshotAsync($"QueueButton.Swaps_with_stop_{providerMode}");
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Queue_button_routes_typed_message_into_the_pending_send_queue(string providerMode)
    {
        // Turn 1 streams long so it is still active while we queue a follow-up; Turn 2 is a
        // safety net for the queued message if the backend processes it before teardown.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.TextLen(2000))
            .Turn(t => t.Text("Queued answer: acknowledged."))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("please produce a very long response");
        await page.WaitForStreamActiveAsync();

        // Wait for the assistant response to actually begin streaming. This guarantees the first
        // send has settled (isSending has flipped back to false) so the queued follow-up is
        // accepted rather than dropped by sendMessage's re-entrancy guard — matching how a real
        // user types a follow-up seconds into a stream, not in the sub-second send-setup window.
        await page.AssistantText().WaitForCountAtLeastAsync(1);

        // Queue a follow-up while the first run is still streaming.
        await page.Textarea().FillAsync("and what is two plus two?");
        await Assertions.Expect(page.QueueButton()).ToBeVisibleAsync();
        await page.QueueButton().ClickAsync();

        // The click routes through the send path: the box clears immediately and the message
        // enters the "Waiting to send…" pending queue (it stays pending until the active run
        // finishes). The full "answered as its own turn after the run" flow is verified live.
        await Assertions.Expect(page.Textarea()).ToHaveValueAsync(string.Empty);
        await Assertions.Expect(page.Locator(".pending-queue")).ToContainTextAsync("two plus two");
        await session.SaveSuccessScreenshotAsync($"QueueButton.Queues_message_{providerMode}");
    }
}
