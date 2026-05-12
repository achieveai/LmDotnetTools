using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.LmCore.Tests.AgentRuntime;

public class AgentRuntimeProfileTests
{
    [Fact]
    public void Defaults_AreEmpty()
    {
        var profile = new AgentRuntimeProfile();

        Assert.Null(profile.SystemPrompt);
        Assert.Empty(profile.Skills);
        Assert.Empty(profile.SubAgents);
        Assert.Empty(profile.McpServers);
    }

    [Fact]
    public void InitOnly_PreservesAllFields()
    {
        var skills = new[] { AgentSkill.Inline("s1", "body") };
        var subs = new[] { SubAgentDefinition.Inline("a1", "body") };
        var mcp = new Dictionary<string, McpServerConfig>
        {
            ["m1"] = McpServerConfig.CreateStdio("node", ["a.js"]),
        };

        var profile = new AgentRuntimeProfile
        {
            SystemPrompt = "sp",
            Skills = skills,
            SubAgents = subs,
            McpServers = mcp,
        };

        Assert.Equal("sp", profile.SystemPrompt);
        Assert.Single(profile.Skills);
        Assert.Equal("s1", profile.Skills[0].Name);
        Assert.Single(profile.SubAgents);
        Assert.Equal("a1", profile.SubAgents[0].Name);
        Assert.True(profile.McpServers.ContainsKey("m1"));
    }
}
