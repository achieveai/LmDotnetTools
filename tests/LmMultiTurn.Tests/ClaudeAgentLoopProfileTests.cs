using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests;

public class ClaudeAgentLoopProfileTests
{
    [Fact]
    public async Task BuildRequest_ProfileMcp_OverridesHostMcp_OnKeyCollision()
    {
        var hostMcp = new Dictionary<string, McpServerConfig>
        {
            ["alpha"] = McpServerConfig.CreateStdio("host-alpha", ["x"]),
            ["shared"] = McpServerConfig.CreateStdio("host-shared", ["x"]),
        };
        var profileMcp = new Dictionary<string, McpServerConfig>
        {
            ["beta"] = McpServerConfig.CreateStdio("profile-beta", ["y"]),
            ["shared"] = McpServerConfig.CreateStdio("profile-shared", ["y"]),
        };

        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions
            {
                Profile = new AgentRuntimeProfile { McpServers = profileMcp },
            },
            mcpServers: hostMcp,
            threadId: "thread-claude-profile-1");

        var request = loop.BuildClaudeAgentSdkRequest();

        request.McpServers.Should().NotBeNull();
        request.McpServers!["alpha"].Command.Should().Be("host-alpha");
        request.McpServers["beta"].Command.Should().Be("profile-beta");
        request.McpServers["shared"].Command.Should().Be("profile-shared");
    }

    [Fact]
    public async Task BuildRequest_ProfileNull_PreservesHostMcp()
    {
        var hostMcp = new Dictionary<string, McpServerConfig>
        {
            ["alpha"] = McpServerConfig.CreateStdio("host-alpha", ["x"]),
        };

        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions(),
            mcpServers: hostMcp,
            threadId: "thread-claude-profile-2");

        var request = loop.BuildClaudeAgentSdkRequest();

        request.McpServers.Should().ContainKey("alpha");
        request.McpServers!["alpha"].Command.Should().Be("host-alpha");
        request.StagingDirectory.Should().BeNull();
    }

    [Fact]
    public async Task BuildRequest_ProfileSystemPrompt_OverridesOptionsSystemPrompt()
    {
        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions
            {
                Profile = new AgentRuntimeProfile { SystemPrompt = "profile-wins" },
            },
            mcpServers: null,
            threadId: "thread-claude-profile-3",
            systemPrompt: "ctor-prompt");

        var request = loop.BuildClaudeAgentSdkRequest();

        request.SystemPrompt.Should().Be("profile-wins");
    }

    [Fact]
    public async Task BuildRequest_ProfileWithInlineSkill_SetsStagingDirectory()
    {
        var profile = new AgentRuntimeProfile
        {
            Skills = [AgentSkill.Inline("skill-1", "# body")],
        };

        await using var loop = new ClaudeAgentLoop(
            new ClaudeAgentSdkOptions { Profile = profile },
            mcpServers: null,
            threadId: "thread-claude-profile-4");

        var request = loop.BuildClaudeAgentSdkRequest();

        request.StagingDirectory.Should().NotBeNullOrEmpty();
        Directory.Exists(request.StagingDirectory).Should().BeTrue();
        var skillFile = Path.Combine(request.StagingDirectory!, "skills", "skill-1", "SKILL.md");
        File.Exists(skillFile).Should().BeTrue();
    }
}
