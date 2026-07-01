using System.Net;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>
///     Tests the pure host/loopback/cross-site predicate directly (TestServer does not populate
///     RemoteIpAddress) plus the middleware wiring for foreign Host and cross-site rejection.
/// </summary>
public sealed class HostGuardTests
{
    private const int Port = 8787;

    [Fact]
    public void Loopback_ipv4_with_matching_host_is_allowed()
    {
        ProxyGuard.IsAllowed(IPAddress.Loopback, "127.0.0.1:8787", null, null, Port).Should().BeTrue();
    }

    [Fact]
    public void Loopback_ipv6_with_bracketed_host_is_allowed()
    {
        ProxyGuard.IsAllowed(IPAddress.IPv6Loopback, "[::1]:8787", null, null, Port).Should().BeTrue();
    }

    [Fact]
    public void Ipv4_mapped_ipv6_loopback_is_normalized_and_allowed()
    {
        var mapped = IPAddress.Parse("::ffff:127.0.0.1");
        mapped.IsIPv4MappedToIPv6.Should().BeTrue();

        ProxyGuard.IsAllowed(mapped, "localhost", null, null, Port).Should().BeTrue();
    }

    [Fact]
    public void Null_remote_in_process_is_allowed_when_host_is_loopback()
    {
        ProxyGuard.IsAllowed(null, "localhost", null, null, Port).Should().BeTrue();
    }

    [Fact]
    public void Non_loopback_remote_is_rejected()
    {
        ProxyGuard.IsAllowed(IPAddress.Parse("8.8.8.8"), "127.0.0.1:8787", null, null, Port).Should().BeFalse();
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("127.0.0.1.evil.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Foreign_or_missing_host_is_rejected(string? host)
    {
        ProxyGuard.IsAllowed(IPAddress.Loopback, host, null, null, Port).Should().BeFalse();
    }

    [Fact]
    public void Cross_site_sec_fetch_site_is_rejected()
    {
        ProxyGuard.IsAllowed(IPAddress.Loopback, "127.0.0.1:8787", null, "cross-site", Port).Should().BeFalse();
    }

    [Fact]
    public void Non_loopback_origin_is_rejected()
    {
        ProxyGuard.IsAllowed(IPAddress.Loopback, "127.0.0.1:8787", "http://evil.com", null, Port).Should().BeFalse();
    }

    [Fact]
    public void Loopback_origin_and_same_origin_fetch_is_allowed()
    {
        ProxyGuard.IsAllowed(
            IPAddress.Loopback, "localhost:8787", "http://localhost:8787", "same-origin", Port).Should().BeTrue();
    }

    [Fact]
    public void Loopback_origin_on_a_different_port_is_rejected()
    {
        // A page on another local port (another dev server, or something malicious) is still "loopback"
        // but must not be treated as same-origin with this proxy.
        ProxyGuard.IsAllowed(
            IPAddress.Loopback, "127.0.0.1:8787", "http://127.0.0.1:3000", null, Port).Should().BeFalse();
    }

    [Fact]
    public async Task Foreign_host_header_is_rejected_with_403_permission_error()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Host = "evil.com";

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["error"]!["type"]!.GetValue<string>().Should().Be("permission_error");
    }

    [Fact]
    public async Task Cross_site_request_is_rejected_with_403()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
