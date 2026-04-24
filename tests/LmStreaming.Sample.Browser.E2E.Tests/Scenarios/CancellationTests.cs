using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC6 — Mid-stream cancellation via the UI stop button. Starts a long-running scripted
/// stream, waits for the stop button to appear, clicks it, and verifies the UI returns
/// to the idle state (stop button hidden, send button visible). The scripted responder
/// must NOT have consumed its full plan, because cancellation breaks the loop before the
/// final turn completes.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class CancellationTests
{
    private readonly PlaywrightFixture _fixture;

    public CancellationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Stop_button_terminates_stream_and_restores_idle(string providerMode)
    {
        // Long streamed turn: many small text fragments so cancellation races correctly —
        // the stream must be in-flight when the user clicks stop.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.TextLen(2000))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("please produce a very long response");

        // Wait until the stream is active (stop button visible), then cancel.
        await page.WaitForStreamActiveAsync();
        await page.StopButton().ClickAsync();

        // After cancellation the UI must return to idle: stop button hidden, send button
        // visible (v-else branch swaps them based on `streaming` state).
        await page.WaitForStreamIdleAsync();

        var stopVisible = await page.StopButton().IsVisibleAsync();
        stopVisible.Should().BeFalse("stop button should disappear once streaming ends");

        var sendVisible = await page.SendButton().IsVisibleAsync();
        sendVisible.Should().BeTrue("send button should return once streaming ends");

        // Typing reactivates the send button — proves the idle state allows a new turn.
        // (An empty textarea disables send by design; that is unrelated to cancellation.)
        await page.Textarea().FillAsync("follow up");
        var sendDisabledWithText = await page.SendButton().IsDisabledAsync();
        sendDisabledWithText.Should().BeFalse("send button should enable after cancel when input has text");
    }
}
