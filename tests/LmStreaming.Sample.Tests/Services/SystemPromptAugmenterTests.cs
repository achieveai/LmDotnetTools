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
}
