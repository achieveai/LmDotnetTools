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
        result.Should().StartWith(new string('a', 100));
        result.Should().EndWith("…[truncated]");
        result.Length.Should().Be(100 + WebToolOutput.TruncationMarker.Length);
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

        // Assert
        markdown.Should().Contain("### 1. [First](https://a.example.com)");
        markdown.Should().Contain("first snippet");
        markdown.Should().Contain("### 2. [Second](https://b.example.com)");
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
