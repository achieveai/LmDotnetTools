using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Locks in the boolean-to-string mapping ClaudeAgentLoop uses for the
/// --setting-sources CLI flag. The three flags (user/project/local) drive a
/// tri-state output:
///   all true  -> null  (omit the flag, CLI uses its own default)
///   all false -> ""    (emit empty value, isolated agent)
///   mixed     -> comma-joined list of the enabled sources
/// </summary>
public class ClaudeAgentLoopSettingSourcesTests
{
    [Fact]
    public void BuildSettingSources_AllTrue_ReturnsNull_SoFlagIsOmitted()
    {
        var options = new ClaudeAgentSdkOptions
        {
            IncludeUserSettings = true,
            IncludeProjectSettings = true,
            IncludeLocalSettings = true,
        };

        var result = ClaudeAgentLoop.BuildSettingSources(options);

        Assert.Null(result);
    }

    [Fact]
    public void BuildSettingSources_AllFalse_ReturnsEmptyString_ForIsolatedAgent()
    {
        var options = new ClaudeAgentSdkOptions
        {
            IncludeUserSettings = false,
            IncludeProjectSettings = false,
            IncludeLocalSettings = false,
        };

        var result = ClaudeAgentLoop.BuildSettingSources(options);

        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData(true, false, false, "user")]
    [InlineData(false, true, false, "project")]
    [InlineData(false, false, true, "local")]
    [InlineData(true, true, false, "user,project")]
    [InlineData(true, false, true, "user,local")]
    [InlineData(false, true, true, "project,local")]
    public void BuildSettingSources_Mixed_ReturnsCommaJoinedSubset(
        bool user, bool project, bool local, string expected)
    {
        var options = new ClaudeAgentSdkOptions
        {
            IncludeUserSettings = user,
            IncludeProjectSettings = project,
            IncludeLocalSettings = local,
        };

        var result = ClaudeAgentLoop.BuildSettingSources(options);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ClaudeAgentSdkOptions_Defaults_IncludeAllSettings()
    {
        // Regression: defaults must keep the host's installed plugins, sub-agents,
        // skills, and worktree .mcp.json visible to the spawned agent.
        var options = new ClaudeAgentSdkOptions();

        Assert.True(options.IncludeUserSettings);
        Assert.True(options.IncludeProjectSettings);
        Assert.True(options.IncludeLocalSettings);
        Assert.Null(ClaudeAgentLoop.BuildSettingSources(options));
    }
}
