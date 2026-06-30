using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotAnthropicProxy.Tests;

/// <summary>Endpoint-level behavior: count_tokens, malformed body, model injection, /v1/models, routing, auth.</summary>
public sealed class EndpointBehaviorTests
{
    private static StringContent ValidBody() =>
        new("{\"model\":\"x\",\"max_tokens\":5}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task CountTokens_unsupported_upstream_404_returns_anthropic_not_found_error()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.Json("nope", HttpStatusCode.NotFound)));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages/count_tokens", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["type"]!.GetValue<string>().Should().Be("error");
        node["error"]!["type"]!.GetValue<string>().Should().Be("not_found_error");
    }

    [Fact]
    public async Task CountTokens_supported_upstream_response_passes_through()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) =>
            Task.FromResult(TestUpstream.Json("{\"input_tokens\":123}")));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages/count_tokens", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["input_tokens"]!.GetValue<int>().Should().Be(123);
    }

    [Fact]
    public async Task Malformed_request_body_returns_400_without_calling_upstream()
    {
        var upstreamCalled = false;
        await using var factory = new ProxyWebAppFactory((req, ct) =>
        {
            upstreamCalled = true;
            return Task.FromResult(TestUpstream.Json("{}"));
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages", new StringContent("this is not json", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!
            .GetValue<string>().Should().Be("invalid_request_error");
        upstreamCalled.Should().BeFalse("a malformed body must never be forwarded upstream");
    }

    [Fact]
    public async Task Missing_model_is_injected_into_the_forwarded_body()
    {
        string? forwardedBody = null;
        await using var factory = new ProxyWebAppFactory(async (req, ct) =>
        {
            forwardedBody = await req.Content!.ReadAsStringAsync(ct);
            return TestUpstream.Json("{\"type\":\"message\"}");
        });
        using var client = factory.CreateClient();

        using var response = await client.PostAsync(
            "/v1/messages", new StringContent("{\"max_tokens\":5}", Encoding.UTF8, "application/json"));

        response.IsSuccessStatusCode.Should().BeTrue();
        JsonNode.Parse(forwardedBody!)!["model"]!.GetValue<string>().Should().Be(ProxyWebAppFactory.ConfiguredModel);
    }

    [Fact]
    public async Task Models_stub_advertises_the_resolved_model()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/v1/models");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        node["has_more"]!.GetValue<bool>().Should().BeFalse();
        var first = node["data"]!.AsArray()[0]!;
        first["type"]!.GetValue<string>().Should().Be("model");
        first["id"]!.GetValue<string>().Should().Be(ProxyWebAppFactory.ConfiguredModel);
    }

    [Fact]
    public async Task Unknown_route_returns_404_not_found_error()
    {
        await using var factory = new ProxyWebAppFactory((req, ct) => Task.FromResult(TestUpstream.Json("{}")));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!
            .GetValue<string>().Should().Be("not_found_error");
    }

    [Fact]
    public async Task Request_path_token_failure_returns_401_authentication_error()
    {
        // Succeeds for the eager startup check (call #1), then fails on the request path (call #2),
        // simulating a credential revoked after startup.
        await using var factory = new ProxyWebAppFactory(
            (req, ct) => Task.FromResult(TestUpstream.Json("{\"type\":\"message\"}")),
            tokenProvider: new FlakyCopilotTokenProvider("fake-token"));
        using var client = factory.CreateClient();

        using var response = await client.PostAsync("/v1/messages", ValidBody());

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["type"]!
            .GetValue<string>().Should().Be("authentication_error");
    }
}

/// <summary>Returns a token on the first call (startup check) and throws afterwards (request path).</summary>
internal sealed class FlakyCopilotTokenProvider : ICopilotTokenProvider
{
    private readonly string _token;
    private int _calls;

    public FlakyCopilotTokenProvider(string token) => _token = token;

    public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) =>
        Interlocked.Increment(ref _calls) == 1
            ? Task.FromResult(_token)
            : throw new InvalidOperationException("Copilot token expired (test).");
}
