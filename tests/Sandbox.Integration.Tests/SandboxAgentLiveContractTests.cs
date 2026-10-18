using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for the gateway's per-agent state (ADR 0040, v0.1.16): the <c>X-Sbx-Agent-ID</c>
/// partition seen through the SDK's agents and discovery reads, and the operation
/// <c>executable_not_runnable</c> code. MCP tool calls are raw JSON-RPC because the SDK deliberately has
/// no MCP client; everything asserted is read back through the SDK.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxAgentLiveContractTests
{
    private const string NamedAgent = "agent-1.lane1live";

    private readonly LiveGatewayFixture _fixture;

    public SandboxAgentLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    private SandboxClient Client
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Client;
        }
    }

    [SkippableFact]
    public async Task Agents_NamedAgentIsListed_ThenDeleted_ThenUnknown()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            (await CallToolAsync(sessionId, agentId: null, "Bash", new { command = "true" }))
                .IsError.Should()
                .BeFalse();
            (await CallToolAsync(sessionId, NamedAgent, "Bash", new { command = "true" })).IsError.Should().BeFalse();

            var agents = await Client.ListAgentsAsync(sessionId);
            agents.Select(a => a.AgentId).Should().BeEquivalentTo(["", NamedAgent]);
            agents.Single(a => a.AgentId.Length == 0).IsDefault.Should().BeTrue();
            var named = agents.Single(a => a.AgentId == NamedAgent);
            named.IsDefault.Should().BeFalse();
            named.LastUsedAt.Should().BeOnOrAfter(named.CreatedAt);

            await Client.DeleteAgentAsync(sessionId, NamedAgent);
            (await Client.ListAgentsAsync(sessionId)).Select(a => a.AgentId).Should().BeEquivalentTo([""]);

            var again = await CaptureAsync(() => Client.DeleteAgentAsync(sessionId, NamedAgent));
            again!.Kind.Should().Be(SandboxErrorKind.NotFound);
            again.StatusCode.Should().Be(404);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Freshness_IsPerAgent_ANamedAgentMustReReadAfterTheDefaultAgentEdits()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            const string path = "/workspace/fresh.txt";
            (await CallToolAsync(sessionId, null, "Write", new { file_path = path, content = "v1\n" }))
                .IsError.Should()
                .BeFalse();
            (await CallToolAsync(sessionId, NamedAgent, "Read", new { file_path = path })).IsError.Should().BeFalse();
            (await CallToolAsync(sessionId, null, "Read", new { file_path = path })).IsError.Should().BeFalse();
            (
                await CallToolAsync(
                    sessionId,
                    null,
                    "Edit",
                    new
                    {
                        file_path = path,
                        old_string = "v1",
                        new_string = "v2",
                    }
                )
            ).IsError.Should().BeFalse();

            var (staleIsError, staleText) = await CallToolAsync(
                sessionId,
                NamedAgent,
                "Edit",
                new
                {
                    file_path = path,
                    old_string = "v2",
                    new_string = "v3",
                }
            );
            staleIsError.Should().BeTrue();
            staleText.Should().Contain("modified since you read it");

            (await CallToolAsync(sessionId, NamedAgent, "Read", new { file_path = path })).IsError.Should().BeFalse();
            (
                await CallToolAsync(
                    sessionId,
                    NamedAgent,
                    "Edit",
                    new
                    {
                        file_path = path,
                        old_string = "v2",
                        new_string = "v3",
                    }
                )
            ).IsError.Should().BeFalse();
            (await Client.ReadTextFileAsync(sessionId, "fresh.txt")).Should().Be("v3\n");
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    /// <summary>
    /// Discovery runs only for a sandbox created with a webhook. The webhook deliberately points at a
    /// closed port, so the outcome does not depend on whether the gateway can reach this host: every
    /// delivery fails, the row stays un-notified for every agent, and an agent filter returns nothing.
    /// That pins the wire contract (the query is accepted, the new fields bind) without a webhook sink.
    /// </summary>
    [SkippableFact]
    public async Task Discovered_CarriesDiscoveredAtAndNotified_AndAgentFilterExcludesUndeliveredRows()
    {
        var info = await Client.CreateAsync(
            new SandboxCreateRequest(
                workspace: "lane1-" + Guid.NewGuid().ToString("N"),
                discovery: new SandboxDiscoverySettings("http://127.0.0.1:9/discovery", "lane1-live-webhook-auth")
            )
        );
        var sessionId = info.SessionId;
        try
        {
            await Client.WriteTextFileAsync(sessionId, "sub/CLAUDE.md", "# lane1 live context\n");
            await Client.WriteTextFileAsync(sessionId, "sub/file.txt", "hello\n");
            (await CallToolAsync(sessionId, NamedAgent, "Read", new { file_path = "/workspace/sub/file.txt" }))
                .IsError.Should()
                .BeFalse();

            SandboxDiscoveredItem? row = null;
            for (var attempt = 0; attempt < 50 && row is null; attempt++)
            {
                row = (await Client.ListDiscoveredAsync(sessionId)).FirstOrDefault(i =>
                    i.Kind == "context_file" && i.Path.EndsWith("sub/CLAUDE.md", StringComparison.Ordinal)
                );
                if (row is null)
                {
                    await Task.Delay(200);
                }
            }

            row.Should().NotBeNull("the named agent's Read walks sub/ and persists its CLAUDE.md");
            row!.DiscoveredAt.Should().NotBeNull();
            row.Notified.Should().BeFalse("the webhook delivery failed");

            (await Client.ListDiscoveredAsync(sessionId, NamedAgent)).Should().NotContain(i => i.Path == row.Path);
            (await Client.ListDiscoveredAsync(sessionId, "")).Should().NotContain(i => i.Path == row.Path);
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task Execute_FileWithoutExecuteBit_IsExecutableNotRunnable()
    {
        var sessionId = await CreateSandboxAsync();
        try
        {
            await Client.WriteTextFileAsync(sessionId, "not-executable.sh", "#!/bin/sh\necho hi\n");

            var error = await CaptureAsync(() =>
                Client.ExecuteAsync(sessionId, new SandboxCommand(["./not-executable.sh"]))
            );

            error.Should().NotBeNull();
            error!.Kind.Should().Be(SandboxErrorKind.ExecutableNotRunnable);
            error.ErrorCode.Should().Be("executable_not_runnable");
        }
        finally
        {
            await Client.DeleteAsync(sessionId);
        }
    }

    private async Task<string> CreateSandboxAsync()
    {
        var info = await Client.CreateAsync(
            new SandboxCreateRequest(workspace: "lane1-" + Guid.NewGuid().ToString("N"))
        );
        return info.SessionId;
    }

    /// <summary>One raw MCP <c>tools/call</c>; the agent header is omitted for the default agent.</summary>
    private static async Task<(bool IsError, string Text)> CallToolAsync(
        string sessionId,
        string? agentId,
        string tool,
        object arguments
    )
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(Environment.GetEnvironmentVariable("SANDBOX_BASE_URL")!.Trim()), "mcp")
        )
        {
            Content = JsonContent.Create(
                new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name = tool, arguments },
                }
            ),
        };
        request.Headers.Add("X-Sbx-App-Id", Environment.GetEnvironmentVariable("SANDBOX_APP_ID")!.Trim());
        request.Headers.Add("X-Sbx-App-Key", Environment.GetEnvironmentVariable("SANDBOX_APP_KEY")!.Trim());
        request.Headers.Add("X-Session-ID", sessionId);
        request.Headers.Add("Accept", "application/json, text/event-stream");
        if (agentId is not null)
        {
            request.Headers.Add(SandboxAgentId.HeaderName, agentId);
        }

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (json.RootElement.TryGetProperty("error", out var error))
        {
            throw new InvalidOperationException($"{tool} JSON-RPC error: {error}");
        }

        var result = json.RootElement.GetProperty("result");
        var text = string.Concat(
            result
                .GetProperty("content")
                .EnumerateArray()
                .Select(c => c.TryGetProperty("text", out var t) ? t.GetString() : "")
        );
        return (result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), text);
    }

    private static async Task<SandboxException?> CaptureAsync(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (SandboxException ex)
        {
            return ex;
        }
    }
}
