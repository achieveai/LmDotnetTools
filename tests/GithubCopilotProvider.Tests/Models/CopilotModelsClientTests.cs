using System.Net;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Models;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.GithubCopilotProvider.Tests.Models;

public sealed class CopilotModelsClientTests
{
    private static HttpClient ClientOver(FakeHttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.enterprise.githubcopilot.com") };

    [Fact]
    public async Task GetModelsAsync_requests_the_models_endpoint()
    {
        string? requestedPath = null;
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            requestedPath = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{ "data": [] }"""),
            });
        });

        var client = new CopilotModelsClient(ClientOver(handler));
        _ = await client.GetModelsAsync();

        requestedPath.Should().Be("/models");
    }

    [Fact]
    public async Task GetModelsAsync_parses_successful_response()
    {
        const string json = """
            { "data": [
              { "id": "claude-sonnet-5", "name": "Claude Sonnet 5", "vendor": "Anthropic", "supported_endpoints": ["/v1/messages"] },
              { "id": "gpt-5.5", "name": "GPT-5.5", "vendor": "OpenAI", "supported_endpoints": ["/responses"] },
              { "id": "gemini-3.5-flash", "name": "Gemini", "vendor": "Google", "supported_endpoints": ["/chat/completions"] }
            ] }
            """;
        var client = new CopilotModelsClient(ClientOver(FakeHttpMessageHandler.CreateSimpleJsonHandler(json)));

        var models = await client.GetModelsAsync();

        models.Select(m => m.Id).Should().BeEquivalentTo("claude-sonnet-5", "gpt-5.5");
    }

    [Fact]
    public async Task GetModelsAsync_returns_empty_on_non_success_status()
    {
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler("unauthorized", HttpStatusCode.Unauthorized);
        var client = new CopilotModelsClient(ClientOver(handler));

        var models = await client.GetModelsAsync();

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModelsAsync_returns_empty_on_transport_failure()
    {
        var handler = new FakeHttpMessageHandler((_, _) => throw new HttpRequestException("boom"));
        var client = new CopilotModelsClient(ClientOver(handler));

        var models = await client.GetModelsAsync();

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModelsAsync_returns_empty_on_malformed_json()
    {
        var handler = FakeHttpMessageHandler.CreateSimpleJsonHandler("{ not json ");
        var client = new CopilotModelsClient(ClientOver(handler));

        var models = await client.GetModelsAsync();

        models.Should().BeEmpty();
    }

    [Fact]
    public async Task GetModelsAsync_propagates_caller_requested_cancellation()
    {
        // Caller cancellation is cooperative and must NOT be masked as an empty catalog — the
        // caller (e.g. a bounded startup timeout) decides how to degrade.
        var handler = new FakeHttpMessageHandler((_, ct) => throw new OperationCanceledException(ct));
        var client = new CopilotModelsClient(ClientOver(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetModelsAsync(cts.Token));
    }
}
