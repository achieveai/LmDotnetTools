namespace AchieveAi.LmDotnetTools.LmCore.Tests.Utils;

/// <summary>
///     Pins the two BaseUrl conventions in this codebase: the in-house client expects a trailing
///     <c>/v1</c>, and the Anthropic SDK / Claude Agent SDK CLI expect no <c>/v1</c>. Issue #29
///     surfaced when these were silently confused.
/// </summary>
public sealed class BaseUrlNormalizerTests
{
    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1")]
    [InlineData("http://127.0.0.1:5099", "http://127.0.0.1:5099/v1")]
    public void EnsureV1Suffix_appends_or_preserves_v1(string input, string expected)
    {
        Assert.Equal(expected, BaseUrlNormalizer.EnsureV1Suffix(input));
    }

    [Theory]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com")]
    [InlineData("http://127.0.0.1:5099/v1", "http://127.0.0.1:5099")]
    public void StripV1Suffix_removes_or_leaves_off_v1(string input, string expected)
    {
        Assert.Equal(expected, BaseUrlNormalizer.StripV1Suffix(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Both_helpers_pass_through_null_or_whitespace(string? input)
    {
        Assert.Equal(input, BaseUrlNormalizer.EnsureV1Suffix(input));
        Assert.Equal(input, BaseUrlNormalizer.StripV1Suffix(input));
    }

    [Fact]
    public void StripV1Suffix_only_strips_a_trailing_v1_segment_not_substrings()
    {
        // A path like /api/v1service must not be confused with the literal /v1 segment.
        Assert.Equal("https://example.com/api/v1service",
            BaseUrlNormalizer.StripV1Suffix("https://example.com/api/v1service"));
    }
}
