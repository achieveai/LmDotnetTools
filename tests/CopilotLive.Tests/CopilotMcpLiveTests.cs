using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AchieveAi.LmDotnetTools.GithubCopilotProvider.Auth;
using FluentAssertions;

namespace AchieveAi.LmDotnetTools.CopilotLive.Tests;

[Collection(CopilotLiveCollection.Name)]
public sealed class CopilotMcpLiveTests(CopilotLiveFixture fixture)
{
    [SkippableFact]
    public async Task Readonly_catalog_can_select_only_hosted_web_search()
    {
        Skip.IfNot(fixture.Available, fixture.SkipReason);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var http = CopilotHttpClientFactory.Create(
            fixture.Options.BaseUrl,
            fixture.TokenProvider,
            fixture.Session,
            fixture.Options,
            timeout: TimeSpan.FromSeconds(30)
        );
        const string endpoint = "/mcp/readonly";

        using var initializeResponse = await SendAsync(
            http,
            endpoint,
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"lmdotnettools-live-test\",\"version\":\"1\"}}}",
            sessionId: null,
            cts.Token
        );
        initializeResponse.EnsureSuccessStatusCode();
        var sessionId = initializeResponse.Headers.GetValues("Mcp-Session-Id").Single();

        using var initializedResponse = await SendAsync(
            http,
            endpoint,
            "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
            sessionId,
            cts.Token
        );
        initializedResponse.IsSuccessStatusCode.Should().BeTrue();

        using var listResponse = await SendAsync(
            http,
            endpoint,
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\",\"params\":{}}",
            sessionId,
            cts.Token
        );
        listResponse.EnsureSuccessStatusCode();
        var body = await listResponse.Content.ReadAsStringAsync(cts.Token);
        using var payload = JsonDocument.Parse(ReadSseData(body));
        var tools = payload.RootElement.GetProperty("result").GetProperty("tools");

        tools.GetArrayLength().Should().Be(1);
        var tool = tools[0];
        tool.GetProperty("name").GetString().Should().Be("web_search");
        tool
            .GetProperty("inputSchema")
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be("query");
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient http,
        string endpoint,
        string body,
        string? sessionId,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.TryAddWithoutValidation("Mcp-Protocol-Version", "2025-06-18");
        request.Headers.TryAddWithoutValidation("X-MCP-Tools", "web_search");
        if (sessionId is not null)
        {
            request.Headers.TryAddWithoutValidation("Mcp-Session-Id", sessionId);
        }

        return await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
    }

    private static string ReadSseData(string body)
    {
        return body
                .Split('\n')
                .First(line => line.StartsWith("data: ", StringComparison.Ordinal))
                ["data: ".Length..];
    }
}
