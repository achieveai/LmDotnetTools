using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

public class SystemPromptAugmenterTests
{
    [Fact]
    public void PrependCurrentDate_PrependsUtcDateLine_BeforeExistingPrompt()
    {
        var now = new DateTimeOffset(2026, 6, 23, 22, 30, 0, TimeSpan.Zero);

        var result = SystemPromptAugmenter.PrependCurrentDate("You are a helpful assistant.", now);

        result.Should().StartWith("The current date is 2026-06-23 (UTC).");
        result.Should().EndWith("You are a helpful assistant.");
        result.Should().Contain("\n\nYou are a helpful assistant.");
    }

    [Fact]
    public void PrependCurrentDate_UsesUtcDate_NotLocalOffset()
    {
        // 2026-06-23 23:30 at -07:00 is 2026-06-24 06:30 UTC — the date line must be the UTC date.
        var now = new DateTimeOffset(2026, 6, 23, 23, 30, 0, TimeSpan.FromHours(-7));

        var result = SystemPromptAugmenter.PrependCurrentDate("x", now);

        result.Should().StartWith("The current date is 2026-06-24 (UTC).");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void PrependCurrentDate_ReturnsDateLineOnly_WhenPromptNullOrEmpty(string? prompt)
    {
        var now = new DateTimeOffset(2026, 6, 23, 0, 0, 0, TimeSpan.Zero);

        var result = SystemPromptAugmenter.PrependCurrentDate(prompt, now);

        result.Should().Be("The current date is 2026-06-23 (UTC).");
    }

    [Fact]
    public void AppendCallerInstructions_AppendsAfterTheExistingPrompt()
    {
        // The caller's instructions go LAST, not first: the mode prompt, the workspace-path suffix and any
        // discovered CLAUDE.md block are all still in force, and recency gives the caller's task the strongest
        // pull. A headless caller that replaced the prompt would lose the workspace wiring it depends on.
        var result = SystemPromptAugmenter.AppendCallerInstructions(
            "You are a workspace agent.\n\nYour workspace directory is: /workspace",
            "Review the PR and dispatch the code-reviewer:* sub-agents."
        );

        result.Should().StartWith("You are a workspace agent.");
        result.Should().EndWith("\n\nReview the PR and dispatch the code-reviewer:* sub-agents.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppendCallerInstructions_ReturnsThePromptUnchanged_WhenThereIsNothingToAppend(string? appendix)
    {
        var result = SystemPromptAugmenter.AppendCallerInstructions("You are a workspace agent.", appendix);

        result.Should().Be("You are a workspace agent.");
    }

    [Fact]
    public void AppendCallerInstructions_ReturnsTheAppendixAlone_WhenThereIsNoPrompt()
    {
        var result = SystemPromptAugmenter.AppendCallerInstructions(null, "Review the PR.");

        result.Should().Be("Review the PR.");
    }
}
