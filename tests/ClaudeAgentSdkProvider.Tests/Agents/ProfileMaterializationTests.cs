using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ProfileMaterializationTests
{
    [Fact]
    public void Materialize_NullProfile_ReturnsEmptyHandle()
    {
        using var result = ProfileMaterializer.Materialize(null);

        Assert.Null(result.StagingDirectory);
        Assert.Empty(result.McpServers);
        Assert.Null(result.SystemPrompt);
    }

    [Fact]
    public void Materialize_EmptyProfile_ReturnsEmptyHandle()
    {
        using var result = ProfileMaterializer.Materialize(new AgentRuntimeProfile());

        Assert.Null(result.StagingDirectory);
        Assert.Empty(result.McpServers);
        Assert.Null(result.SystemPrompt);
    }

    [Fact]
    public void Materialize_OnlyMcpAndSystemPrompt_NoStagingDirCreated()
    {
        var profile = new AgentRuntimeProfile
        {
            SystemPrompt = "hello",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["m"] = McpServerConfig.CreateStdio("node", ["a.js"]),
            },
        };

        using var result = ProfileMaterializer.Materialize(profile);

        Assert.Null(result.StagingDirectory);
        Assert.Equal("hello", result.SystemPrompt);
        Assert.True(result.McpServers.ContainsKey("m"));
    }

    [Fact]
    public void Materialize_InlineSkill_WritesSkillMd()
    {
        var profile = new AgentRuntimeProfile
        {
            Skills = [AgentSkill.Inline("my-skill", "# Skill body")],
        };

        using var result = ProfileMaterializer.Materialize(profile);

        Assert.NotNull(result.StagingDirectory);
        var skillFile = Path.Combine(result.StagingDirectory!, "skills", "my-skill", "SKILL.md");
        Assert.True(File.Exists(skillFile));
        Assert.Equal("# Skill body", File.ReadAllText(skillFile));
    }

    [Fact]
    public void Materialize_InlineSubAgent_WritesAgentMd()
    {
        var profile = new AgentRuntimeProfile
        {
            SubAgents = [SubAgentDefinition.Inline("my-agent", "agent body")],
        };

        using var result = ProfileMaterializer.Materialize(profile);

        Assert.NotNull(result.StagingDirectory);
        var agentFile = Path.Combine(result.StagingDirectory!, "agents", "my-agent.md");
        Assert.True(File.Exists(agentFile));
        Assert.Equal("agent body", File.ReadAllText(agentFile));
    }

    [Fact]
    public void Materialize_PathSkillFromFile_CopiedToSkillMd()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"skill-src-{Guid.NewGuid():N}.md");
        File.WriteAllText(tempFile, "file body");
        try
        {
            var profile = new AgentRuntimeProfile
            {
                Skills = [AgentSkill.FromPath("name", tempFile)],
            };

            using var result = ProfileMaterializer.Materialize(profile);
            var skillFile = Path.Combine(result.StagingDirectory!, "skills", "name", "SKILL.md");

            Assert.True(File.Exists(skillFile));
            Assert.Equal("file body", File.ReadAllText(skillFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Materialize_PathSkillFromDirectory_TreeReplicated()
    {
        var srcDir = Path.Combine(Path.GetTempPath(), $"skill-dir-{Guid.NewGuid():N}");
        var subDir = Path.Combine(srcDir, "nested");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(srcDir, "SKILL.md"), "top");
        File.WriteAllText(Path.Combine(subDir, "helper.md"), "nested");
        try
        {
            var profile = new AgentRuntimeProfile
            {
                Skills = [AgentSkill.FromPath("name", srcDir)],
            };

            using var result = ProfileMaterializer.Materialize(profile);
            var destSkillRoot = Path.Combine(result.StagingDirectory!, "skills", "name");

            Assert.Equal("top", File.ReadAllText(Path.Combine(destSkillRoot, "SKILL.md")));
            Assert.Equal("nested", File.ReadAllText(Path.Combine(destSkillRoot, "nested", "helper.md")));
        }
        finally
        {
            Directory.Delete(srcDir, recursive: true);
        }
    }

    [Fact]
    public void Materialize_StagingDir_DeletedOnDispose()
    {
        var profile = new AgentRuntimeProfile
        {
            Skills = [AgentSkill.Inline("x", "y")],
        };
        string? stagingPath;
        using (var result = ProfileMaterializer.Materialize(profile))
        {
            stagingPath = result.StagingDirectory;
            Assert.True(Directory.Exists(stagingPath));
        }

        Assert.False(Directory.Exists(stagingPath));
    }

    [Fact]
    public void Materialize_McpServers_AreCopiedIntoHandle()
    {
        var profile = new AgentRuntimeProfile
        {
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["alpha"] = McpServerConfig.CreateStdio("node", ["a.js"]),
                ["beta"] = McpServerConfig.CreateHttp("https://example.com"),
            },
        };

        using var result = ProfileMaterializer.Materialize(profile);

        Assert.Equal(2, result.McpServers.Count);
        Assert.Equal("node", result.McpServers["alpha"].Command);
        Assert.Equal("https://example.com", result.McpServers["beta"].Url);
    }
}
