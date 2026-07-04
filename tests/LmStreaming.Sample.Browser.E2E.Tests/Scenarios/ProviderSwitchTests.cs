using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Runtime PROVIDER switch between the two scripted mock providers — <c>test</c> (OpenAI wire) and
/// <c>test-anthropic</c> (Anthropic wire) — inside a single page session. Turn 1 streams on the boot
/// provider; the header dropdown switches the provider while idle (POST
/// <c>/api/conversations/{threadId}/provider</c> → the pool recreates the agent on the OTHER wire);
/// turn 2 must then stream on the recreated agent. Both turns come from ONE
/// <see cref="ScriptedSseResponder"/> whose two wire handlers share a plan queue, so this proves the
/// switch rebuilds the agent against a different provider and the next turn is served correctly.
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
        string targetLabel)
    {
        // ONE responder, two turns. The role matches on the system prompt, which both the OpenAI and
        // Anthropic extractors surface, so either wire pops the next plan.
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("First turn answer."))
            .Turn(t => t.Text("Second turn answer."))
            .Build();

        // Boot on bootProvider (its own wire). The provider-aware ScriptedBuilder overload lets the
        // agent be recreated on EITHER wire when the provider is switched.
        await using var session = await _fixture.OpenAsync(bootProvider, responder);
        var page = session.Page;

        // Turn 1 on the boot provider — establishes the (started) conversation.
        await page.SendMessageAsync("first");
        await page.WaitForStreamIdleAsync();
        string.Join(" ", await page.AssistantText().AllInnerTextsAsync())
            .Should().Contain("First turn answer");

        // Switch the provider while idle: header dropdown → target option. This fires the backend
        // switch (recreate on the other wire). Wait for the button label to reflect the new provider.
        await page.ProviderSelectorButton().ClickAsync();
        await page.ProviderOption(targetProvider).ClickAsync();
        await page.ProviderSelectorButton().WaitForTextContainsAsync(targetLabel);

        // Turn 2 must stream on the RECREATED agent (the other wire). If the switch didn't rebuild the
        // agent against the target provider, the scripted handler would 404 the mismatched wire and
        // this would never render / go idle.
        await page.SendMessageAsync("second");
        await page.WaitForStreamIdleAsync();
        string.Join(" ", await page.AssistantText().AllInnerTextsAsync())
            .Should().Contain("Second turn answer");

        responder.RemainingTurns["parent"].Should().Be(0, "both turns ran to completion across the switch");
        await session.SaveSuccessScreenshotAsync(
            $"ProviderSwitch.{bootProvider}_to_{targetProvider}");
    }
}
