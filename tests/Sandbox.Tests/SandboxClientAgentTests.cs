using System.Net;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Tests;

/// <summary>Per-agent gateway state (ADR 0040): the agent-id rule, the agents API, and the discovery agent filter.</summary>
public class SandboxClientAgentTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("agent-1.3f9c0a1b2d4e5f60")]
    [InlineData("Az09._-")]
    public void SandboxAgentId_IsValid_AcceptsTheGatewayAlphabet(string agentId) =>
        SandboxAgentId.IsValid(agentId).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("café")]
    public void SandboxAgentId_IsValid_RejectsAnythingElse(string? agentId) =>
        SandboxAgentId.IsValid(agentId).Should().BeFalse();

    [Fact]
    public void SandboxAgentId_IsValid_CapsLengthAt64()
    {
        SandboxAgentId.IsValid(new string('a', 64)).Should().BeTrue();
        SandboxAgentId.IsValid(new string('a', 65)).Should().BeFalse();
    }

    [Fact]
    public async Task ListAgentsAsync_ParsesRows_AndMarksTheDefaultAgent()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/agents",
            """
            {"session_id":"sess-1","agents":[
              {"agent_id":"","created_at":"2026-10-09T10:00:00Z","last_used_at":"2026-10-09T10:05:00Z","open_tasks":0,"loaded_skills":0},
              {"agent_id":"agent-1.abc","created_at":"2026-10-09T10:01:00Z","last_used_at":"2026-10-09T10:02:00Z","open_tasks":2,"loaded_skills":3}
            ]}
            """
        );

        var agents = await client.ListAgentsAsync("sess-1");

        agents.Should().HaveCount(2);
        agents[0].IsDefault.Should().BeTrue();
        var named = agents[1];
        named.AgentId.Should().Be("agent-1.abc");
        named.IsDefault.Should().BeFalse();
        named.CreatedAt.Should().Be(DateTimeOffset.Parse("2026-10-09T10:01:00Z"));
        named.LastUsedAt.Should().Be(DateTimeOffset.Parse("2026-10-09T10:02:00Z"));
        named.OpenTasks.Should().Be(2);
        named.LoadedSkills.Should().Be(3);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"agents":[{"agent_id":"a","created_at":"2026-10-09T10:00:00Z"}]}""")]
    [InlineData(
        """{"agents":[{"agent_id":"bad id","created_at":"2026-10-09T10:00:00Z","last_used_at":"2026-10-09T10:00:00Z","open_tasks":0,"loaded_skills":0}]}"""
    )]
    public async Task ListAgentsAsync_MalformedBody_IsProtocol(string json)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1/agents", json);

        var ex = await Assert.ThrowsAsync<SandboxException>(() => client.ListAgentsAsync("sess-1"));

        ex.Kind.Should().Be(SandboxErrorKind.Protocol);
    }

    [Fact]
    public async Task DeleteAgentAsync_204_SendsDeleteToTheAgentPath()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnStatus(HttpMethod.Delete, "/api/v1/sandboxes/sess-1/agents/agent-1.abc", HttpStatusCode.NoContent);

        await client.DeleteAgentAsync("sess-1", "agent-1.abc");

        var request = handler.Requests.Single();
        request.Method.Should().Be(HttpMethod.Delete);
        request.Uri.AbsolutePath.Should().Be("/api/v1/sandboxes/sess-1/agents/agent-1.abc");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"code":404,"error":"Agent not found: x"}""", SandboxErrorKind.NotFound)]
    [InlineData(
        HttpStatusCode.Conflict,
        """{"code":409,"error":"the default agent cannot be deleted"}""",
        SandboxErrorKind.Conflict
    )]
    [InlineData(
        HttpStatusCode.ServiceUnavailable,
        """{"error":"busy","error_code":"sandbox_busy"}""",
        SandboxErrorKind.Unavailable
    )]
    [InlineData(HttpStatusCode.BadRequest, """{"code":400,"error":"Invalid agent_id: x"}""", SandboxErrorKind.Protocol)]
    public async Task DeleteAgentAsync_ErrorStatus_IsClassified(
        HttpStatusCode status,
        string body,
        SandboxErrorKind kind
    )
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Delete, "/api/v1/sandboxes/sess-1/agents/agent-1", body, status);

        var ex = await Assert.ThrowsAsync<SandboxException>(() => client.DeleteAgentAsync("sess-1", "agent-1"));

        ex.Kind.Should().Be(kind);
        ex.StatusCode.Should().Be((int)status);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bad id")]
    [InlineData(".")] // legal ids, but URI dot-segment normalization would retarget the DELETE
    [InlineData("..")]
    public async Task DeleteAgentAsync_InvalidAgentId_ThrowsBeforeSending(string agentId)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteAgentAsync("sess-1", agentId));

        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "?agent_id=")]
    [InlineData("agent-1.abc", "?agent_id=agent-1.abc")]
    public async Task ListDiscoveredAsync_AgentFilter_IsSentAsQuery(string? agentId, string expectedQuery)
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(HttpMethod.Get, "/api/v1/sandboxes/sess-1/discovered", "{}");

        _ = await client.ListDiscoveredAsync("sess-1", agentId);

        handler.Requests.Single().Uri.Query.Should().Be(expectedQuery);
    }

    [Fact]
    public async Task ListDiscoveredAsync_InvalidAgentId_ThrowsBeforeSending()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();

        await Assert.ThrowsAsync<ArgumentException>(() => client.ListDiscoveredAsync("sess-1", "bad id"));

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task ListDiscoveredAsync_MapsFrontmatterPluginDiscoveredAtAndNotified()
    {
        var (client, handler) = TestSupport.CreateBorrowedClient();
        handler.OnJson(
            HttpMethod.Get,
            "/api/v1/sandboxes/sess-1/discovered",
            """
            {"discovered":[{"kind":"subagent","name":"reviewer","path":"/workspace/.claude/agents/reviewer.md",
              "frontmatter":{"model":"opus","tools":["Read"]},"plugin":"code-reviewer",
              "discovered_at":"2026-10-09T10:00:00Z","notified":true}]}
            """
        );

        var item = (await client.ListDiscoveredAsync("sess-1")).Single();

        item.Frontmatter!.Value.GetProperty("model").GetString().Should().Be("opus");
        item.Plugin.Should().Be("code-reviewer");
        item.DiscoveredAt.Should().Be(DateTimeOffset.Parse("2026-10-09T10:00:00Z"));
        item.Notified.Should().BeTrue();
    }
}
