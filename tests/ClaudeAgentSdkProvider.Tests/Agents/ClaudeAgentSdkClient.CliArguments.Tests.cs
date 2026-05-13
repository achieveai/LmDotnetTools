using System.Reflection;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.AgentRuntime;

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

    // BuildCliArgumentTokens is the actual runtime path (BuildCliArguments
    // is retained only for diagnostic logging). These tests mirror the
    // BuildCliArguments coverage above so the tokenized path keeps the same
    // semantics — emit/omit flags, tri-state SettingSources, boolean flags.

    [Fact]
    public void BuildCliArgumentTokens_OmitsModelToken_WhenModelIdIsEmpty()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = string.Empty, MaxTurns = 50 };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.DoesNotContain("--model", tokens);
        AssertContainsPair(tokens, "--max-turns", "50");
    }

    [Fact]
    public void BuildCliArgumentTokens_OmitsModelToken_WhenModelIdIsWhitespace()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "   ", MaxTurns = 50 };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.DoesNotContain("--model", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsModelToken_WhenModelIdIsProvided()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", MaxTurns = 50 };

        var tokens = client.BuildCliArgumentTokens(request);

        AssertContainsPair(tokens, "--model", "claude-sonnet-4-6");
    }

    [Fact]
    public void BuildCliArgumentTokens_OmitsSettingSourcesToken_WhenNull()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", SettingSources = null };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.DoesNotContain("--setting-sources", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsExplicitSettingSources_WhenSet()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", SettingSources = "user" };

        var tokens = client.BuildCliArgumentTokens(request);

        AssertContainsPair(tokens, "--setting-sources", "user");
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsEmptySettingSources_WhenIsolated()
    {
        // Tokenized form: the empty string is a distinct value, not quoted.
        // The argv-style hand-off needs no shell-quoting around it.
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", SettingSources = string.Empty };

        var tokens = client.BuildCliArgumentTokens(request);

        AssertContainsPair(tokens, "--setting-sources", string.Empty);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsIncludePartialMessages_WhenStreamJsonOutputFormat()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6", AllowedTools = "Read" };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.Contains("--include-partial-messages", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsNoCheckpoints_WhenDisabled()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions { DisableCheckpoints = true });
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6" };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.Contains("--no-checkpoints", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsNoSessionPersistence_WhenDisabled()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions { DisableSessionPersistence = true });
        var request = new ClaudeAgentSdkRequest { ModelId = "claude-sonnet-4-6" };

        var tokens = client.BuildCliArgumentTokens(request);

        Assert.Contains("--no-session-persistence", tokens);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsMcpConfigJsonWithoutShellEscaping()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            McpServers = new Dictionary<string, McpServerConfig>
            {
                ["fs"] = new McpServerConfig { Type = "stdio", Command = "fs-mcp" },
            },
        };

        var tokens = client.BuildCliArgumentTokens(request);

        var idx = tokens.ToList().IndexOf("--mcp-config");
        Assert.True(idx >= 0, "--mcp-config token not emitted");
        Assert.True(idx + 1 < tokens.Count, "--mcp-config has no value token");
        var json = tokens[idx + 1];
        // No surrounding shell quotes; raw JSON only.
        Assert.StartsWith("{", json);
        Assert.Contains("\"mcpServers\"", json);
        Assert.Contains("\"fs\"", json);
    }

    [Fact]
    public void BuildCliArgumentTokens_EmitsResumeToken_WhenSessionIdSet()
    {
        var client = new ClaudeAgentSdkClient(new ClaudeAgentSdkOptions());
        var request = new ClaudeAgentSdkRequest
        {
            ModelId = "claude-sonnet-4-6",
            SessionId = "abc-123",
        };

        var tokens = client.BuildCliArgumentTokens(request);

        AssertContainsPair(tokens, "--resume", "abc-123");
    }

    private static void AssertContainsPair(IReadOnlyList<string> tokens, string flag, string value)
    {
        var idx = tokens.ToList().IndexOf(flag);
        Assert.True(idx >= 0, $"Expected flag '{flag}' not found in {string.Join(' ', tokens)}");
        Assert.True(idx + 1 < tokens.Count, $"Flag '{flag}' has no value token");
        Assert.Equal(value, tokens[idx + 1]);
    }
}
