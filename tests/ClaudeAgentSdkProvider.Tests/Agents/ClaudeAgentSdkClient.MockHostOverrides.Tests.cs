using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;

namespace AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Tests.Agents;

/// <summary>
/// Verifies that <see cref="ClaudeAgentSdkClient.ApplyMockHostOverrides"/> maps the additive
/// E2E options onto the well-known CLI env vars (ANTHROPIC_BASE_URL, ANTHROPIC_AUTH_TOKEN,
/// CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS) without disturbing unrelated entries.
/// </summary>
public class ClaudeAgentSdkClientMockHostOverridesTests
{
    [Fact]
    public void Sets_all_three_env_vars_when_options_are_populated()
    {
        var env = new Dictionary<string, string?>
        {
            ["UNRELATED"] = "preserved",
        };
        var options = new ClaudeAgentSdkOptions
        {
            BaseUrl = "http://127.0.0.1:5099",
            AuthToken = "mock-token",
            DisableExperimentalBetas = true,
        };

        ClaudeAgentSdkClient.ApplyMockHostOverrides(env, options);

        Assert.Equal("http://127.0.0.1:5099", env["ANTHROPIC_BASE_URL"]);
        Assert.Equal("mock-token", env["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("1", env["CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"]);
        Assert.Equal("preserved", env["UNRELATED"]);
    }

    [Fact]
    public void Defensively_strips_trailing_v1_from_BaseUrl()
    {
        // Issue #29: the Anthropic SDK CLI re-appends '/v1/messages' to ANTHROPIC_BASE_URL,
        // so a configured '/v1' suffix produces '/v1/v1/messages' and 404s silently.
        // ApplyMockHostOverrides must strip it defensively at the provider boundary.
        var env = new Dictionary<string, string?>();
        var options = new ClaudeAgentSdkOptions
        {
            BaseUrl = "http://127.0.0.1:5099/v1",
        };

        ClaudeAgentSdkClient.ApplyMockHostOverrides(env, options);

        Assert.Equal("http://127.0.0.1:5099", env["ANTHROPIC_BASE_URL"]);
    }

    [Fact]
    public void Leaves_env_unchanged_when_options_are_default()
    {
        var env = new Dictionary<string, string?>();
        var options = new ClaudeAgentSdkOptions();

        ClaudeAgentSdkClient.ApplyMockHostOverrides(env, options);

        Assert.Empty(env);
    }

    [Fact]
    public void Skips_blank_BaseUrl_and_AuthToken()
    {
        var env = new Dictionary<string, string?>();
        var options = new ClaudeAgentSdkOptions
        {
            BaseUrl = string.Empty,
            AuthToken = string.Empty,
            DisableExperimentalBetas = false,
        };

        ClaudeAgentSdkClient.ApplyMockHostOverrides(env, options);

        Assert.False(env.ContainsKey("ANTHROPIC_BASE_URL"));
        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_DISABLE_EXPERIMENTAL_BETAS"));
    }
}
