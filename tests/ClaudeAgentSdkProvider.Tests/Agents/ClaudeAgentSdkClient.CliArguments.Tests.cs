using System.Reflection;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

public class ClaudeAgentSdkClientCliArgumentsTests
{
    [Fact]
    public void BuildCliArguments_IncludesPartialMessagesForToolCallVisibility()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var method = typeof(ClaudeAgentSdkClient).GetMethod(
            "BuildCliArguments",
            BindingFlags.Instance | BindingFlags.NonPublic
        );
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", AllowedTools = "Read" };

        var args = Assert.IsType<string>(method?.Invoke(client, [request]));

        Assert.Contains("--include-partial-messages", args);
    }

    [Fact]
    public void BuildCliArguments_OmitsModelFlag_WhenModelIdIsEmpty()
    {
        // Regression for: --max-turns being parsed as the model name when ModelId
        // is empty. The old builder always emitted "--model {value}", producing
        // "--model --max-turns 50 ..." which the CLI parsed as Model="--max-turns".
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = string.Empty, MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--model", args);
        Assert.Contains("--max-turns 50", args);
    }

    [Fact]
    public void BuildCliArguments_OmitsModelFlag_WhenModelIdIsWhitespace()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "   ", MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--model", args);
        Assert.Contains("--max-turns 50", args);
    }

    [Fact]
    public void BuildCliArguments_EmitsModelFlag_WhenModelIdIsProvided()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", MaxTurns = 50 };

        var args = client.BuildCliArguments(request);

        Assert.Contains("--model claude-sonnet-4-6", args);
        Assert.Contains("--max-turns 50", args);
    }

    [Fact]
    public void BuildCliArguments_OmitsSettingSourcesFlag_WhenUnset()
    {
        // Regression for: --setting-sources "" was always emitted, causing the
        // CLI to load no settings at all (no plugins, no skills, no sub-agents,
        // no worktree .mcp.json). Omitting the flag lets the CLI fall back to
        // its own default of user,project,local.
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6" };

        var args = client.BuildCliArguments(request);

        Assert.DoesNotContain("--setting-sources", args);
    }

    [Fact]
    public void BuildCliArguments_EmitsSettingSourcesFlag_WhenSet()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", SettingSources = "user" };

        var args = client.BuildCliArguments(request);

        Assert.Contains("--setting-sources \"user\"", args);
    }

    [Fact]
    public void BuildCliArguments_EmitsEmptySettingSources_WhenIsolated()
    {
        // Explicit empty value tells the CLI to load no settings sources at all
        // (no user, no project, no local). Distinct from null / omitted, which
        // lets the CLI fall back to its own default of user,project,local.
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", SettingSources = string.Empty };

        var args = client.BuildCliArguments(request);

        Assert.Contains("--setting-sources \"\"", args);
    }

    [Fact]
    public void ApplyStagingDirectoryEnv_SetsClaudeConfigDir_WhenStagingDirectoryProvided()
    {
        var env = new Dictionary<string, string?>();

        ClaudeAgentSdkClient.ApplyStagingDirectoryEnv(env, "/tmp/lm-claude-abc");

        Assert.True(env.ContainsKey("CLAUDE_CONFIG_DIR"));
        Assert.Equal("/tmp/lm-claude-abc", env["CLAUDE_CONFIG_DIR"]);
    }

    [Fact]
    public void ApplyStagingDirectoryEnv_DoesNotSet_WhenStagingDirectoryNull()
    {
        var env = new Dictionary<string, string?>();

        ClaudeAgentSdkClient.ApplyStagingDirectoryEnv(env, null);

        Assert.False(env.ContainsKey("CLAUDE_CONFIG_DIR"));
    }

    [Fact]
    public void ApplyStagingDirectoryEnv_DoesNotSet_WhenStagingDirectoryEmpty()
    {
        var env = new Dictionary<string, string?>();

        ClaudeAgentSdkClient.ApplyStagingDirectoryEnv(env, string.Empty);

        Assert.False(env.ContainsKey("CLAUDE_CONFIG_DIR"));
    }
}
