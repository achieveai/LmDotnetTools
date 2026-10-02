using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using FluentAssertions;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

/// <summary>
/// Conversation fork, the whole user path: fork after the first reply, land in the fork with its
/// banner, the shared history only, the fork nested in the sidebar and a 2/2 switcher; continue the
/// fork; switch back to the original, which still has its own second turn.
/// </summary>
[Collection(PlaywrightCollection.Name)]
public sealed class ConversationForkTests
{
    private readonly PlaywrightFixture _fixture;

    public ConversationForkTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Fork_from_here_continues_separately_and_the_switcher_returns_to_the_original()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t => t.Text("ALPHA answer."))
            .Turn(t => t.Text("BRAVO answer."))
            .Turn(t => t.Text("DELTA answer in the fork."))
            .Build();

        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;

        await page.SendMessageAsync("ALPHA question");
        await page.WaitForStreamIdleAsync();
        await page.SendMessageAsync("BRAVO question");
        await page.WaitForStreamIdleAsync();
        await page.UserMessageGroups().WaitForCountAtLeastAsync(2);

        var firstReply = page.AssistantMessageGroups().First;
        await firstReply.HoverAsync();
        await firstReply.GetByTestId("fork-from-here-button").ClickAsync();

        await page.GetByTestId("fork-banner").WaitForAsync();
        await page.GetByTestId("sidebar-fork-row").WaitForAsync();
        await page.GetByTestId("branch-switcher-label").WaitForTextContainsAsync("2 / 2");
        (await page.UserMessageGroups().CountAsync()).Should().Be(1, "the fork shares only the first turn");
        (await page.MessageList().InnerTextAsync()).Should().NotContain("BRAVO");

        await page.SendMessageAsync("DELTA question");
        await page.WaitForStreamIdleAsync();
        await page.AssistantText().Last.WaitForTextContainsAsync("DELTA answer");

        await page.GetByTestId("branch-switcher-prev").ClickAsync();
        await page.GetByTestId("fork-banner")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached });
        await page.MessageList().WaitForTextContainsAsync("BRAVO answer");
        (await page.MessageList().InnerTextAsync()).Should().NotContain("DELTA");
        await page.GetByTestId("branch-switcher-label").WaitForTextContainsAsync("1 / 2");

        responder.RemainingTurns["parent"].Should().Be(0);
        await session.SaveSuccessScreenshotAsync("ConversationFork.Fork_from_here_and_switch_back");
    }
}
