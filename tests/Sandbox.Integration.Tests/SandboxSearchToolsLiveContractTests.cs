using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// Live contract for the gateway's search tools (ADR 0047, v0.1.21): the create flags and their echo, and whether
/// <c>tools/list</c> offers the tools. MCP calls are raw JSON-RPC because the SDK deliberately has no MCP client. The
/// skill comes from whatever marketplace the gateway serves. Every sandbox it creates is deleted.
/// </summary>
[Collection(LiveGatewayCollection.Name)]
public sealed class SandboxSearchToolsLiveContractTests
{
    private const string SearchSkills = "SearchSkills";
    private const string SearchAgents = "SearchAgents";

    private readonly LiveGatewayFixture _fixture;

    public SandboxSearchToolsLiveContractTests(LiveGatewayFixture fixture) => _fixture = fixture;

    private SandboxClient Client
    {
        get
        {
            Skip.If(_fixture.SkipReason is not null, _fixture.SkipReason);
            return _fixture.Client;
        }
    }

    [SkippableFact]
    public async Task CreateFlags_ListBothTools_AndSearchSkillsFindsAServedSkill()
    {
        var client = Client;
        SandboxClient
            .SupportsSearchTools(await client.GetGatewayVersionAsync())
            .Should()
            .BeTrue("the gateway this contract pins must accept the flags");
        var (alias, plugin) = (await client.PreviewMarketplacesAsync())
            .Marketplaces.SelectMany(m => m.Plugins.Select(p => (m.Alias, Plugin: p)))
            .First(x => x.Plugin.Skills.Count >= 1);
        var skill = plugin.Skills[0].Name;

        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                workspace: "searchtools-" + Guid.NewGuid().ToString("N"),
                marketplaces: [alias],
                skills: new SandboxSkillsPolicy(searchTool: true, agentsSearchTool: true)
            )
        );
        try
        {
            info.SkillsPolicy!.SearchTool.Should().BeTrue();
            info.SkillsPolicy.AgentsSearchTool.Should().BeTrue();
            (await ListToolsAsync(info.SessionId)).Should().Contain([SearchSkills, SearchAgents, "Skill"]);

            var (isError, text) = await SandboxMcpRpc.CallToolAsync(
                info.SessionId,
                null,
                SearchSkills,
                new { query = skill }
            );
            isError.Should().BeFalse(text);
            text.Should().Contain($"{plugin.Name}:{skill}", "a hit's name is what the Skill tool loads");

            var (agentsIsError, agentsText) = await SandboxMcpRpc.CallToolAsync(
                info.SessionId,
                null,
                SearchAgents,
                new { query = "review" }
            );
            agentsIsError.Should().BeFalse(agentsText);
        }
        finally
        {
            await client.DeleteAsync(info.SessionId);
        }
    }

    /// <summary>
    /// What LMDT sends: <c>SearchSkills</c> on, <c>SearchAgents</c> explicitly off. An explicit value has to hold on a
    /// gateway whose env default turns both on, or a named agent is offered a tool that finds nothing.
    /// </summary>
    [SkippableFact]
    public async Task ExplicitAgentsSearchOff_ListsSearchSkillsAndNotSearchAgents()
    {
        var client = Client;
        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                workspace: "searchtools-" + Guid.NewGuid().ToString("N"),
                skills: new SandboxSkillsPolicy(searchTool: true, agentsSearchTool: false)
            )
        );
        try
        {
            info.SkillsPolicy!.AgentsSearchTool.Should().BeFalse("the gateway omits false, and the SDK restores it");
            var tools = await ListToolsAsync(info.SessionId);
            tools.Should().Contain(SearchSkills);
            tools.Should().NotContain(SearchAgents);
        }
        finally
        {
            await client.DeleteAsync(info.SessionId);
        }
    }

    [SkippableFact]
    public async Task NoFlags_ListNeitherTool_AndTheCreateStillAcceptsAnEmptyPolicy()
    {
        var client = Client;
        var info = await client.CreateAsync(
            new SandboxCreateRequest(
                workspace: "searchtools-" + Guid.NewGuid().ToString("N"),
                skills: new SandboxSkillsPolicy()
            )
        );
        try
        {
            info.SkillsPolicy!.SearchTool.Should().NotBe(true);
            (await ListToolsAsync(info.SessionId)).Should().NotContain([SearchSkills, SearchAgents]);
        }
        finally
        {
            await client.DeleteAsync(info.SessionId);
        }
    }

    private static async Task<string[]> ListToolsAsync(string sessionId)
    {
        using var json = await SandboxMcpRpc.SendAsync(sessionId, "tools/list", new { });
        return
        [
            .. json
                .RootElement.GetProperty("result")
                .GetProperty("tools")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!),
        ];
    }
}
