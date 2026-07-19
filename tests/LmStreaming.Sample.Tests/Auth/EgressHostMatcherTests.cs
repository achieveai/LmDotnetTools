namespace LmStreaming.Sample.Tests.Auth;

/// <summary>
/// Unit tests for <see cref="EgressHostMatcher"/> — the shared host-pattern match + SSRF host-pattern
/// validation + header name/value validation used by both the OAuth and predefined-key egress paths,
/// plus <see cref="OAuthProviderHosts.CollidesWithManagedHost"/>. Pure, ungated (always run in CI).
/// </summary>
public sealed class EgressHostMatcherTests
{
    [Theory]
    [InlineData("api.example.com", "api.example.com", true)]      // exact
    [InlineData("api.example.com", "API.EXAMPLE.COM", true)]      // case-insensitive
    [InlineData("*.example.com", "sub.example.com", true)]        // wildcard suffix
    [InlineData("*.example.com", "a.b.example.com", true)]        // multi-label wildcard suffix
    [InlineData("*.example.com", "example.com", false)]           // wildcard does not match apex
    [InlineData("*.example.com", "evil-example.com", false)]      // wildcard requires the dot separator
    [InlineData("api.example.com", "other.example.com", false)]   // exact non-match
    public void IsAllowed_matches_exact_and_wildcard(string pattern, string host, bool expected) =>
        EgressHostMatcher.IsAllowed([pattern], host).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsAllowed_fails_closed_on_empty_destination(string? host) =>
        EgressHostMatcher.IsAllowed(["api.example.com"], host).Should().BeFalse();

    [Fact]
    public void IsAllowed_fails_closed_on_empty_host_list() =>
        EgressHostMatcher.IsAllowed([], "api.example.com").Should().BeFalse();

    [Theory]
    [InlineData("api.example.com")]
    [InlineData("*.example.com")]
    [InlineData("example.com")]
    [InlineData("a-b.example.co.uk")]
    public void ValidateHostPattern_accepts_valid_hosts(string pattern) =>
        EgressHostMatcher.ValidateHostPattern(pattern).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("*")]                        // bare wildcard
    [InlineData("*.com")]                     // wildcard covering a bare TLD
    [InlineData("*.")]                        // empty wildcard suffix
    [InlineData("https://example.com")]       // scheme
    [InlineData("example.com/path")]          // path
    [InlineData("example.com:443")]           // port
    [InlineData("example.com.")]              // trailing dot
    [InlineData(" example.com")]              // leading whitespace
    [InlineData("exa mple.com")]              // inner space
    [InlineData("-bad.example.com")]          // label starts with hyphen
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.9.9.9")]
    [InlineData("0.0.0.0")]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("169.254.169.254")]           // cloud metadata
    [InlineData("metadata.google.internal")]
    public void ValidateHostPattern_rejects_invalid_or_dangerous_hosts(string? pattern) =>
        EgressHostMatcher.ValidateHostPattern(pattern).Should().NotBeNull();

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Cookie")]
    [InlineData("X-API-Key")]
    [InlineData("x-custom_header")]
    public void ValidateHeaderName_accepts_valid_names(string name) =>
        EgressHostMatcher.ValidateHeaderName(name).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Has Space")]
    [InlineData("Bad:Colon")]
    [InlineData("Bad\nName")]
    [InlineData("Host")]
    [InlineData("Content-Length")]
    [InlineData("Connection")]
    [InlineData("Transfer-Encoding")]
    [InlineData("Content-Type")]
    public void ValidateHeaderName_rejects_invalid_or_forbidden_names(string? name) =>
        EgressHostMatcher.ValidateHeaderName(name).Should().NotBeNull();

    [Theory]
    [InlineData("Bearer abc.def")]
    [InlineData("sessionid=deadbeef; other=1")]
    public void ValidateHeaderValue_accepts_valid_values(string value) =>
        EgressHostMatcher.ValidateHeaderValue(value).Should().BeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has\r\ninjection")]
    [InlineData("has\nnewline")]
    public void ValidateHeaderValue_rejects_empty_or_control(string? value) =>
        EgressHostMatcher.ValidateHeaderValue(value).Should().NotBeNull();

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("api.github.com", true)]
    [InlineData("*.github.com", true)]           // wildcard entry covering a managed host
    [InlineData("dev.azure.com", true)]
    [InlineData("sub.dev.azure.com", true)]      // managed *.dev.azure.com covers it
    [InlineData("myorg.visualstudio.com", true)] // managed *.visualstudio.com covers it
    [InlineData("graph.microsoft.com", true)]
    [InlineData("api.example.com", false)]
    [InlineData("*.example.com", false)]
    public void CollidesWithManagedHost_flags_managed_overlaps(string pattern, bool expected) =>
        OAuthProviderHosts.CollidesWithManagedHost(pattern).Should().Be(expected);
}
