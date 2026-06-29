using AchieveAi.LmDotnetTools.Misc.Web;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Misc.Tests.Web;

public class WebInputValidatorTests
{
    [Fact]
    public void ValidateUrl_WithValidUrl_StripsFragmentAndAccepts()
    {
        // Act
        var result = WebInputValidator.ValidateUrl("https://example.com/p?a=1#frag");

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().Be("https://example.com/p?a=1");
        result.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateUrl_WithEmptyInput_Rejects(string? url)
    {
        // Act
        var result = WebInputValidator.ValidateUrl(url);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("ftp://example.com/file")] // non-http(s) scheme
    [InlineData("http://user:pass@example.com/")] // userinfo
    [InlineData("http://localhost/")] // localhost
    [InlineData("http://127.0.0.1/")] // loopback
    [InlineData("http://10.0.0.1/")] // private 10/8
    [InlineData("http://192.168.1.1/")] // private 192.168/16
    [InlineData("http://169.254.1.1/")] // link-local
    [InlineData("http://foo.local/")] // internal suffix
    public void ValidateUrl_WithDisallowedTargets_Rejects(string url)
    {
        // Act
        var result = WebInputValidator.ValidateUrl(url);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ValidateUrl_WithOverlongUrl_Rejects()
    {
        // Arrange
        var longUrl = "https://example.com/" + new string('a', 2100);

        // Act
        var result = WebInputValidator.ValidateUrl(longUrl);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("length");
    }

    [Fact]
    public void ValidateQuery_TrimsAndAccepts()
    {
        // Act
        var result = WebInputValidator.ValidateQuery("  hello world  ", 2048);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().Be("hello world");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateQuery_WithEmptyOrWhitespace_Rejects(string? query)
    {
        // Act
        var result = WebInputValidator.ValidateQuery(query, 2048);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Value.Should().BeNull();
    }

    [Fact]
    public void ValidateQuery_WithOverlongQuery_Rejects()
    {
        // Act
        var result = WebInputValidator.ValidateQuery(new string('x', 50), 10);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("length");
    }

    [Fact]
    public void ValidateQuery_WithControlCharacters_Rejects()
    {
        // Arrange - (char)1 is SOH, a control character embedded mid-query.
        var query = "bad" + (char)1 + "query";

        // Act
        var result = WebInputValidator.ValidateQuery(query, 2048);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("control");
    }
}
