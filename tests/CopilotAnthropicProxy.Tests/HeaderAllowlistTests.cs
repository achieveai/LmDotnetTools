using System.Text;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Verifies the positive request-header allowlist (anthropic-version only; anthropic-beta is dropped).</summary>
public sealed class HeaderAllowlistTests
{
    private static StringContent Body() =>
        new("{\"model\":\"x\",\"max_tokens\":5}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Forwards_anthropic_version_but_not_beta_or_other_inbound_headers()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory((req, ct) =>
        {
            forwarded = req;
            return Task.FromResult(TestUpstream.Json("{\"type\":\"message\"}"));
        });
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = Body() };
        request.Headers.TryAddWithoutValidation("anthropic-version", "2099-01-01");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "feature-a");
        request.Headers.TryAddWithoutValidation("anthropic-beta", "feature-b");
        request.Headers.TryAddWithoutValidation("x-api-key", "secret-key");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer inbound-token");
        request.Headers.TryAddWithoutValidation("User-Agent", "inbound-agent/1.0");

        using var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        forwarded.Should().NotBeNull();

        forwarded!.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2099-01-01");
        forwarded.Headers.Contains("anthropic-beta")
            .Should()
            .BeFalse("Copilot rejects the whole request if any beta value is unrecognized");

        forwarded.Headers.Contains("x-api-key").Should().BeFalse("inbound x-api-key must not be forwarded");
        // Authorization is force-set by CopilotHeadersHandler to the Copilot bearer, never the inbound value.
        forwarded.Headers.Authorization!.Parameter.Should().NotBe("inbound-token");
        // User-Agent is owned by CopilotHeadersHandler, not the inbound request.
        forwarded.Headers.UserAgent.ToString().Should().NotContain("inbound-agent");
    }

    [Fact]
    public async Task Injects_default_anthropic_version_when_absent()
    {
        HttpRequestMessage? forwarded = null;
        await using var factory = new ProxyWebAppFactory((req, ct) =>
        {
            forwarded = req;
            return Task.FromResult(TestUpstream.Json("{\"type\":\"message\"}"));
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages", Body());

        response.IsSuccessStatusCode.Should().BeTrue();
        forwarded!.Headers.GetValues("anthropic-version").Should().ContainSingle().Which.Should().Be("2023-06-01");
    }
}
