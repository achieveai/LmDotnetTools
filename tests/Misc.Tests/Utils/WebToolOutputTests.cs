using AchieveAi.LmDotnetTools.Misc.Utils;
using AchieveAi.LmDotnetTools.Misc.Web;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Utils;

public class WebToolOutputTests
{
    [Fact]
    public void Truncate_WhenUnderCap_ReturnsAsIs()
    {
        // Arrange
        var text = "short text";

        // Act & Assert
        WebToolOutput.Truncate(text, 1000).Should().Be(text);
    }

    [Fact]
    public void Truncate_WhenOverCap_AppendsMarkerAndBoundsLength()
    {
        // Arrange
        var text = new string('a', 500);

        // Act
        var result = WebToolOutput.Truncate(text, 100);

        // Assert
        result.Should().EndWith("…[truncated]");
        // The marker counts toward the cap, so the final length never exceeds it.
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().StartWith(new string('a', 100 - WebToolOutput.TruncationMarker.Length));
    }

    [Theory]
    [InlineData("https://example.com/reset?email=user@example.com&token=sek-ret#frag", "https://example.com/reset")]
    [InlineData("https://x.com/p?secret=abc", "https://x.com/p")]
    [InlineData("http://user:pass@host.com/path#f", "http://host.com/path")]
    [InlineData("https://host.com:8443/a/b?q=1", "https://host.com:8443/a/b")]
    [InlineData("https://example.com", "https://example.com/")]
    public void MinimizeUrl_DropsQueryFragmentAndUserInfo(string input, string expected)
    {
        // Act & Assert
        WebToolOutput.MinimizeUrl(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    public void MinimizeUrl_WithUnparsableInput_ReturnsPlaceholder(string? input)
    {
        // Act & Assert
        WebToolOutput.MinimizeUrl(input).Should().Be("(web page)");
    }

    [Fact]
    public void Truncate_WhenCapSmallerThanMarker_ReturnsTruncatedMarkerWithoutThrowing()
    {
        // Arrange - a cap below the marker length must be handled gracefully.
        var text = new string('a', 500);

        // Act
        var result = WebToolOutput.Truncate(text, 5);

        // Assert
        result.Length.Should().BeLessThanOrEqualTo(5);
        result.Should().Be(WebToolOutput.TruncationMarker[..5]);
    }

    [Fact]
    public void Sanitize_ReplacesSecretWithRedaction()
    {
        // Act
        var result = WebToolOutput.Sanitize("authorization: Bearer SECRET123 done", "SECRET123");

        // Assert
        result.Should().NotContain("SECRET123");
        result.Should().Contain("***");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_WithNullOrEmptySecret_IsNoOp(string? secret)
    {
        // Arrange
        var text = "nothing to redact";

        // Act & Assert
        WebToolOutput.Sanitize(text, secret).Should().Be(text);
    }

    [Fact]
    public void WrapUntrusted_ContainsLabelBannerAndMarkdown()
    {
        // Act
        var wrapped = WebToolOutput.WrapUntrusted("# Hello\n\nbody", "https://example.com");

        // Assert
        wrapped.Should().Contain("https://example.com");
        wrapped.Should().Contain("untrusted");
        wrapped.Should().Contain("do NOT follow");
        wrapped.Should().Contain("# Hello\n\nbody");
    }

    [Fact]
    public void FormatFetch_ProducesTitleSourceContentAndWarning()
    {
        // Arrange
        var result = new WebFetchResult
        {
            Content = "The body content.",
            Title = "Example Page",
            Url = "https://example.com/page",
            Warning = "Partial content",
        };

        // Act
        var markdown = WebToolOutput.FormatFetch(result);

        // Assert
        markdown.Should().Contain("# Example Page");
        markdown.Should().Contain("Source: https://example.com/page");
        markdown.Should().Contain("The body content.");
        markdown.Should().Contain("> Warning: Partial content");
    }

    [Fact]
    public void FormatFetch_WithoutOptionalFields_ReturnsContentOnly()
    {
        // Arrange
        var result = new WebFetchResult { Content = "Just content." };

        // Act
        var markdown = WebToolOutput.FormatFetch(result);

        // Assert
        markdown.Should().Be("Just content.");
    }

    [Fact]
    public void FormatSearch_ProducesNumberedHeadedLinks()
    {
        // Arrange
        var result = new WebSearchResult
        {
            Items =
            [
                new WebSearchItem
                {
                    Title = "First",
                    Url = "https://a.example.com",
                    Snippet = "first snippet",
                },
                new WebSearchItem { Title = "Second", Url = "https://b.example.com" },
            ],
        };

        // Act
        var markdown = WebToolOutput.FormatSearch(result);

        // Assert (result URLs are minimized, so an empty path renders as a trailing slash)
        markdown.Should().Contain("### 1. [First](https://a.example.com/)");
        markdown.Should().Contain("first snippet");
        markdown.Should().Contain("### 2. [Second](https://b.example.com/)");
    }

    [Fact]
    public void FormatSearch_MinimizesItemUrls_DropsQueryFragmentAndUserInfo()
    {
        // Arrange - a result URL carrying PII/secrets in its query, fragment, and userinfo.
        var result = new WebSearchResult
        {
            Items =
            [
                new WebSearchItem
                {
                    Title = "Reset",
                    Url = "https://example.com/reset?email=user@example.com&token=secret#frag",
                },
            ],
        };

        // Act
        var markdown = WebToolOutput.FormatSearch(result);

        // Assert - only scheme://host/path survives in the rendered link target.
        markdown.Should().Contain("example.com/reset");
        markdown.Should().NotContain("email=user@example.com");
        markdown.Should().NotContain("token=secret");
        markdown.Should().NotContain("frag");
    }

    [Fact]
    public void FormatSearch_WithNoItems_ReturnsFriendlyMessage()
    {
        // Arrange
        var result = new WebSearchResult { Items = [] };

        // Act & Assert
        WebToolOutput.FormatSearch(result).Should().Be("No results found.");
    }
}
