using System.Net;
using System.Text;
using AchieveAi.LmDotnetTools.LmTestUtils.TestMode;
using AchieveAi.LmDotnetTools.MockProviderHost.Tests.Infrastructure;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.MockProviderHost.Tests;

/// <summary>
/// Tests for routing, the readiness probe, and convention headers — anything not covered by
/// the byte-identity invariant.
/// </summary>
public sealed class HealthAndRoutingTests
{
    [Fact]
    public async Task Healthz_returns_ok()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("any", _ => true).Turn(t => t.Text("unused"))
            .Build();
        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("ok");
    }

    [Fact]
    public async Task Unknown_path_returns_404()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("any", _ => true).Turn(t => t.Text("unused"))
            .Build();
        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var response = await client.GetAsync("/v1/embeddings");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OpenAi_endpoint_echoes_convention_headers()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true).Turn(t => t.Text("hi"))
            .Build();
        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var body = """{"model":"gpt-test","stream":true,"messages":[{"role":"user","content":"hi"}]}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/chat/completions", content);

        response.EnsureSuccessStatusCode();
        response.Headers.Should().Contain(h => h.Key.Equals("openai-version", StringComparison.OrdinalIgnoreCase));
        response.Headers.Should().Contain(h => h.Key.Equals("x-request-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Anthropic_endpoint_echoes_convention_headers()
    {
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true).Turn(t => t.Text("hi"))
            .Build();
        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var body = """{"model":"claude-test","stream":true,"max_tokens":256,"messages":[{"role":"user","content":"hi"}]}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/v1/messages", content);

        response.EnsureSuccessStatusCode();
        response.Headers.Should().Contain(h => h.Key.Equals("anthropic-version", StringComparison.OrdinalIgnoreCase));
        response.Headers.Should().Contain(h => h.Key.Equals("anthropic-request-id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Endpoint_accepts_authorization_and_x_api_key_headers_simultaneously()
    {
        // Claude Agent SDK CLI sends both Authorization: Bearer ... and x-api-key: ... — the
        // forwarder must accept both and not reject the request on either header.
        var responder = ScriptedSseResponder.New()
            .ForRole("parent", _ => true).Turn(t => t.Text("hi"))
            .Build();
        await using var fixture = await MockProviderHostFixture.StartAsync(responder);
        using var client = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl) };

        var body = """{"model":"claude-test","stream":true,"max_tokens":256,"messages":[{"role":"user","content":"hi"}]}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages") { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer mock-token");
        request.Headers.TryAddWithoutValidation("x-api-key", "mock-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
