using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using LmStreaming.Sample.Browser.E2E.Tests.Infrastructure;

namespace LmStreaming.Sample.Browser.E2E.Tests.Scenarios;

[Collection(PlaywrightCollection.Name)]
public sealed class ViewPreferenceTests
{
    private readonly PlaywrightFixture _fixture;

    public ViewPreferenceTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Consumer_is_default_and_view_switch_preserves_draft_activity_and_preference()
    {
        var responder = ScriptedSseResponder
            .New()
            .ForRole("parent", ctx => ctx.SystemPromptContains("helpful assistant"))
            .Turn(t =>
                t.ToolCall(
                    "calculate",
                    new
                    {
                        a = 2,
                        operation = "add",
                        b = 3,
                    }
                )
            )
            .Turn(t => t.Text("The answer is five.").TextLen(3_000))
            .Build();

        await using var session = await _fixture.OpenAsync("test", responder.HandlerFor("test"));
        var page = session.Page;

        await Assertions.Expect(page.ConsumerViewPreference()).ToBeCheckedAsync();
        await Assertions.Expect(page.ConversationInspectorLauncher()).ToBeVisibleAsync();
        await Assertions.Expect(page.ConversationInspector()).ToHaveCountAsync(0);
        await page.ConversationInspectorLauncher().ClickAsync();
        await Assertions.Expect(page.ConversationInspector()).ToContainTextAsync("Work & agents");
        await session.SaveSuccessScreenshotAsync("ViewPreference.inspector_open");
        await page.ConversationInspector()
            .GetByRole(AriaRole.Button, new() { Name = "Close Work and agents" })
            .ClickAsync();
        await Assertions.Expect(page.ConversationInspector()).ToHaveCountAsync(0);

        await page.Textarea().FillAsync("draft survives view changes");
        await page.SelectDeveloperViewAsync();
        await Assertions.Expect(page.Textarea()).ToHaveValueAsync("draft survives view changes");
        await page.SelectConsumerViewAsync();
        await Assertions.Expect(page.Textarea()).ToHaveValueAsync("draft survives view changes");

        await page.Textarea().FillAsync("calculate two plus three");
        await page.SendButton().ClickAsync();
        await page.WaitForStreamActiveAsync();

        await Assertions.Expect(page.TurnActivity()).ToHaveCountAsync(1);
        await Assertions.Expect(page.TurnActivityToggle()).ToContainTextAsync("Working");
        await Assertions.Expect(page.StopButton()).ToBeVisibleAsync();

        await page.SelectDeveloperViewAsync();
        await Assertions.Expect(page.TurnActivity()).ToHaveCountAsync(0);
        await Assertions.Expect(page.ToolCallPills()).ToHaveCountAsync(1);
        await Assertions.Expect(page.StopButton()).ToBeVisibleAsync();

        await page.SelectConsumerViewAsync();
        await Assertions.Expect(page.TurnActivity()).ToHaveCountAsync(1);
        await Assertions.Expect(page.TurnActivityToggle()).ToContainTextAsync("Working");
        await Assertions.Expect(page.StopButton()).ToBeVisibleAsync();

        await page.WaitForStreamIdleAsync(timeoutMs: 30_000);

        await Assertions.Expect(page.AssistantText()).ToContainTextAsync("The answer is five.");
        await Assertions.Expect(page.TurnActivity()).ToHaveCountAsync(1);
        await Assertions.Expect(page.TurnActivityToggle()).Not.ToContainTextAsync("Working");
        await Assertions.Expect(page.ToolCallPills()).ToHaveCountAsync(0);
        await session.SaveSuccessScreenshotAsync("ViewPreference.consumer_activity");

        await page.TurnActivityToggle().ClickAsync();
        await Assertions.Expect(page.ToolCallPills()).ToHaveCountAsync(1);

        await page.SelectDeveloperViewAsync();
        await Assertions.Expect(page.TurnActivity()).ToHaveCountAsync(0);
        await Assertions.Expect(page.ToolCallPills()).ToHaveCountAsync(1);
        await session.SaveSuccessScreenshotAsync("ViewPreference.developer_detail");

        await page.ReloadAsync();
        await page.Textarea().WaitForAsync();
        await Assertions.Expect(page.DeveloperViewPreference()).ToBeCheckedAsync();
        await Assertions.Expect(page.AssistantText()).ToContainTextAsync("The answer is five.");

        await session.SaveSuccessScreenshotAsync("ViewPreference.consumer_activity_and_persistence");
    }
}
