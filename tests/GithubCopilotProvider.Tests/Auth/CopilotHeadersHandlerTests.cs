using System.Net;
using System.Net.Http.Headers;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Http;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Auth;

public sealed class CopilotHeadersHandlerTests
{
    private sealed class StubTokenProvider(string token) : ICopilotTokenProvider
    {
        public int CallCount { get; private set; }

        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(token);
        }
    }

    private static (HttpClient client, List<HttpRequestMessage> captured, StubTokenProvider tokens) BuildClient(
        CopilotOptions? options = null,
        CopilotSessionContext? session = null
    )
    {
        var captured = new List<HttpRequestMessage>();
        var inner = new FakeHttpMessageHandler(
            (request, _) =>
            {
                captured.Add(request);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        );

        var tokens = new StubTokenProvider("gho_test_token");
        var handler = new CopilotHeadersHandler(
            tokens,
            session ?? new CopilotSessionContext("machine-1", "session-1"),
            options,
            inner
        );

        return (
            new HttpClient(handler) { BaseAddress = new Uri("https://api.enterprise.githubcopilot.com") },
            captured,
            tokens
        );
    }

    [Fact]
    public async Task Adds_auth_and_copilot_headers()
    {
        var (client, captured, tokens) = BuildClient(
            new CopilotOptions
            {
                ExtraHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" },
            }
        );

        _ = await client.PostAsync("/v1/messages", new StringContent("{}"));

        var req = captured.Single();
        req.Headers.Authorization.Should().BeEquivalentTo(new AuthenticationHeaderValue("Bearer", "gho_test_token"));
        HeaderValue(req, "copilot-integration-id").Should().Be("copilot-developer-cli");
        HeaderValue(req, "editor-version").Should().StartWith("copilot/");
        HeaderValue(req, "x-github-api-version").Should().Be("2026-06-01");
        HeaderValue(req, "x-client-machine-id").Should().Be("machine-1");
        HeaderValue(req, "x-client-session-id").Should().Be("session-1");
        HeaderValue(req, "x-interaction-id").Should().NotBeNullOrWhiteSpace();
        HeaderValue(req, "anthropic-version").Should().Be("2023-06-01");
        tokens.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Interaction_id_is_fresh_per_request_but_session_ids_are_stable()
    {
        var (client, captured, _) = BuildClient();

        _ = await client.PostAsync("/responses", new StringContent("{}"));
        _ = await client.PostAsync("/responses", new StringContent("{}"));

        var first = captured[0];
        var second = captured[1];

        HeaderValue(first, "x-interaction-id").Should().NotBe(HeaderValue(second, "x-interaction-id"));
        HeaderValue(first, "x-client-session-id").Should().Be(HeaderValue(second, "x-client-session-id"));
        HeaderValue(first, "x-client-machine-id").Should().Be(HeaderValue(second, "x-client-machine-id"));
    }

    [Fact]
    public async Task Does_not_overwrite_a_header_the_caller_already_set()
    {
        var (client, captured, _) = BuildClient(
            new CopilotOptions
            {
                ExtraHeaders = new Dictionary<string, string> { ["anthropic-version"] = "2023-06-01" },
            }
        );

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = new StringContent("{}"),
        };
        _ = request.Headers.TryAddWithoutValidation("anthropic-version", "custom-version");

        _ = await client.SendAsync(request);

        HeaderValue(captured.Single(), "anthropic-version").Should().Be("custom-version");
    }

    private static string? HeaderValue(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
    }
}
