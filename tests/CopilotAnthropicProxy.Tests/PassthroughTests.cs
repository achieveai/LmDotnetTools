using System.Net;
using System.Text;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Verifies upstream status + headers pass through verbatim minus hop-by-hop/framing headers.</summary>
public sealed class PassthroughTests
{
    [Fact]
    public async Task Status_body_and_rate_limit_headers_pass_through_while_hop_by_hop_are_stripped()
    {
        const string upstreamBody = "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"slow down\"}}";
        var headers = new Dictionary<string, string>
        {
            ["Retry-After"] = "120",
            ["anthropic-ratelimit-requests-remaining"] = "0",
            ["anthropic-ratelimit-tokens-remaining"] = "42",
            ["request-id"] = "req_abc123",
            ["Keep-Alive"] = "timeout=5",       // hop-by-hop -> must be stripped
            ["Connection"] = "keep-alive",       // hop-by-hop -> must be stripped
        };

        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.Json(upstreamBody, HttpStatusCode.TooManyRequests, headers)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages", new StringContent("{\"model\":\"x\",\"max_tokens\":5}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await response.Content.ReadAsStringAsync()).Should().Be(upstreamBody);

        response.Headers.GetValues("Retry-After").Should().ContainSingle().Which.Should().Be("120");
        response.Headers.GetValues("anthropic-ratelimit-requests-remaining").Should().ContainSingle().Which.Should().Be("0");
        response.Headers.GetValues("anthropic-ratelimit-tokens-remaining").Should().ContainSingle().Which.Should().Be("42");
        response.Headers.GetValues("request-id").Should().ContainSingle().Which.Should().Be("req_abc123");

        response.Headers.Contains("Keep-Alive").Should().BeFalse("Keep-Alive is hop-by-hop");
    }

    [Fact]
    public async Task Non_streaming_success_body_is_copied_verbatim()
    {
        const string upstreamBody = "{\"type\":\"message\",\"unknown_future_field\":{\"a\":1},\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}";
        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Json(upstreamBody)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages", new StringContent("{\"model\":\"x\",\"max_tokens\":5}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(upstreamBody);
    }
}
