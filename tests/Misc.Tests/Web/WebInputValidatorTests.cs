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
    [InlineData("http://0.0.0.0/")] // IPv4 unspecified / "any" address
    [InlineData("http://[::]/")] // IPv6 unspecified / "any" address
    [InlineData("http://[::1]/")] // IPv6 loopback
    [InlineData("http://[fd00::1]/")] // IPv6 unique-local (ULA)
    [InlineData("http://[fe80::1]/")] // IPv6 link-local
    [InlineData("http://[::ffff:127.0.0.1]/")] // IPv4-mapped IPv6 loopback
    [InlineData("http://[::ffff:10.0.0.1]/")] // IPv4-mapped IPv6 private 10/8
    [InlineData("http://[::ffff:192.168.1.1]/")] // IPv4-mapped IPv6 private 192.168/16
    [InlineData("http://[::ffff:169.254.169.254]/")] // IPv4-mapped IPv6 link-local (cloud metadata)
    [InlineData("http://172.16.0.1/")] // private 172.16/12 (lower boundary)
    [InlineData("http://172.31.255.255/")] // private 172.16/12 (upper boundary)
    [InlineData("http://224.0.0.1/")] // multicast 224/4
    [InlineData("http://foo.internal/")] // internal suffix
    [InlineData("http://foo.localhost/")] // localhost suffix
    [InlineData("http://foo.test/")] // RFC 6761 reserved TLD
    [InlineData("http://foo.example/")] // RFC 6761 reserved TLD
    [InlineData("http://foo.invalid/")] // RFC 6761 reserved TLD
    public void ValidateUrl_WithDisallowedTargets_Rejects(string url)
    {
        // Act
        var result = WebInputValidator.ValidateUrl(url);

        // Assert
        result.IsValid.Should().BeFalse();
        result.Value.Should().BeNull();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("http://172.15.0.1/")] // just below the 172.16/12 private block
    [InlineData("http://172.32.0.1/")] // just above the 172.16/12 private block
    [InlineData("http://93.184.216.34/")] // ordinary public IPv4 host
    [InlineData("https://example.com/")] // ordinary public hostname
    public void ValidateUrl_WithPublicTargets_Accepts(string url)
    {
        // Act
        var result = WebInputValidator.ValidateUrl(url);

        // Assert
        result.IsValid.Should().BeTrue();
        result.Value.Should().NotBeNullOrEmpty();
        result.Error.Should().BeNull();
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
