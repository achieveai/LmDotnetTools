using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// AC2 — Multi-turn conversation with streamed text and thinking blocks. The user submits
/// two consecutive prompts; the parent's scripted plan serves each turn with a thinking
/// block followed by plain-text output. Verifies both user bubbles, both assistant text
/// bubbles, and at least one thinking pill render in the UI.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class MultiTurnConversationTests
{
    private readonly PlaywrightFixture _fixture;

    public MultiTurnConversationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData("test")]
    [InlineData("test-anthropic")]
    public async Task Two_user_turns_render_text_and_thinking(string providerMode)
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Thinking(32).Text("First answer: forty-two."))
            .Turn(t => t.Thinking(32).Text("Second answer: still forty-two."))
            .Build();

        await using var session = await _fixture.OpenAsync(providerMode, responder.HandlerFor(providerMode));
        var page = session.Page;

        await page.SendMessageAsync("what is the meaning of life?");
        await page.WaitForStreamIdleAsync();

        await page.SendMessageAsync("are you sure?");
        await page.WaitForStreamIdleAsync();

        await page.UserMessageGroups().WaitForCountAtLeastAsync(2);
        await page.AssistantText().WaitForCountAtLeastAsync(2);

        var assistantTexts = await page.AssistantText().AllInnerTextsAsync();
        var combined = string.Join(" ", assistantTexts);
        combined.Should().Contain("First answer");
        combined.Should().Contain("Second answer");

        var thinkingCount = await page.ThinkingPills().CountAsync();
        thinkingCount.Should().BeGreaterThan(0, "both scripted turns emitted a thinking block");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync($"MultiTurn.Two_user_turns_render_text_and_thinking_{providerMode}");
    }
}
