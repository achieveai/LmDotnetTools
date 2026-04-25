using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC1 — Provider-mode coverage restricted to the mock-only lanes per issue #8 reviewer
/// answer Q1: OpenAI (<c>test</c>) and Anthropic (<c>test-anthropic</c>) scripted-SSE.
/// Codex and Copilot are deliberately out of scope (they drive real subprocess stdio so
/// they are not runnable on test machines without external binaries).
/// </summary>
/// <remarks>
/// <para>
/// The sample reads <c>LM_PROVIDER_MODE</c> once at startup, so a single page session
/// can't swap providers at runtime. AC1 is expressed here as a parametrized theory: each
/// mode boots its own factory + page and exercises the full UI → WebSocket → scripted
/// responder round-trip independently.
/// </para>
/// <para>
/// Mode switching inside a page session (switching between chat-modes such as default ↔
/// math-helper) is covered by <see cref="ModeDropdown_switches_server_side_mode"/>.
/// That test keeps one factory and flips the <c>ChatMode</c> via the UI selector,
/// which causes the client to reconnect the WebSocket with a new <c>modeId</c>.
/// </para>
/// </remarks>
[Collection(PlaywrightCollection.Name)]
public sealed class ModeSwitchingTests
{
    private readonly PlaywrightFixture _fixture;

    public ModeSwitchingTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Scripted_provider_serves_response_for(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text($"Served via {providerMode}."))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("hello");
        await page.WaitForStreamIdleAsync();

        var texts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", texts).Should().Contain($"Served via {providerMode}");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"ModeSwitch.Scripted_provider_serves_response_for_{providerMode}");
    }

    /// <summary>
    /// Flips the chat-mode selector (default → math-helper) mid-session. The client
    /// should disconnect the current WebSocket and reconnect with the new <c>modeId</c>.
    /// Verifies the UI reflects the selected mode after the swap.
    /// </summary>
    [Fact]
    public async Task ModeDropdown_switches_server_side_mode()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("Default mode ack."))
            .ForRole("math", ctx => ctx.SystemPromptContains("math assistant"))
            .Turn(t => t.Text("Math mode ack."))
            .Build();

        await using var session = await _fixture.OpenAsync("test", responder.AsOpenAiHandler());
        var page = session.Page;

        // Open dropdown and pick math-helper.
        await page.ModeSelectorButton().ClickAsync();
        await page.ModeOption("math-helper").ClickAsync();

        // The UI debounces mode changes via the WebSocket reconnect path — wait for the
        // button label to reflect the new mode before sending a prompt.
        await page.ModeSelectorButton().WaitForTextContainsAsync("Math Helper");

        await page.SendMessageAsync("2+2?");
        await page.WaitForStreamIdleAsync();

        var texts = await page.AssistantText().AllInnerTextsAsync();
        string.Join(" ", texts).Should().Contain("Math mode ack");

        responder.RemainingTurns["math"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync("ModeSwitch.ModeDropdown_switches_server_side_mode");
    }
}
