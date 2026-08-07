using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using LmStreaming.Sample.Configuration;
using Microsoft.Extensions.Configuration;

namespace LmStreaming.Sample.Tests.Configuration;

/// <summary>
/// Covers the host-side opt-in gate for hierarchy-wide collaboration (#244): the section must bind,
/// stay off by default, and reject unusable values at startup rather than at the first spawn.
/// </summary>
public class AgentCollaborationHostOptionsTests
{
    private static AgentCollaborationHostOptions Bind(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return configuration
                .GetSection(AgentCollaborationHostOptions.SectionName)
                .Get<AgentCollaborationHostOptions>() ?? new AgentCollaborationHostOptions();
    }

    [Fact]
    public void MissingSection_LeavesCollaborationOff()
    {
        var options = Bind([]);

        options.Enabled.Should().BeNull("an unconfigured flag means 'let the mode decide', not 'off'");
        options.ToCollaborationOptions().Should().BeNull(
            "absence of the library options object is the feature gate");
    }

    /// <summary>
    /// The mode-default matrix: an UNSPECIFIED <c>Enabled</c> defers to the caller's per-mode default
    /// (on for the Workspace Agent, off elsewhere), while an explicit value always wins over it. This is
    /// what lets the Workspace Agent ship collaboration on without switching it on for every other mode.
    /// </summary>
    [Theory]
    [InlineData(null, true, true)] // unspecified + Workspace Agent  -> on
    [InlineData(null, false, false)] // unspecified + ordinary mode   -> off
    [InlineData("false", true, false)] // explicit off beats the mode default
    [InlineData("false", false, false)]
    [InlineData("true", true, true)] // explicit on beats the mode default
    [InlineData("true", false, true)]
    public void ResolveForMode_HonoursTheExplicitFlagAndFallsBackToTheModeDefault(
        string? configured, bool defaultEnabled, bool expectEnabled)
    {
        var values = new Dictionary<string, string?>
        {
            ["AgentCollaboration:MaxTotalAgents"] = "64",
        };
        if (configured is not null)
        {
            values["AgentCollaboration:Enabled"] = configured;
        }

        var resolved = Bind(values).ResolveForMode(defaultEnabled);

        if (expectEnabled)
        {
            resolved.Should().NotBeNull();
            resolved!.MaxTotalAgents.Should().Be(64, "the configured limits apply however the flag was resolved");
        }
        else
        {
            resolved.Should().BeNull();
        }
    }

    [Fact]
    public void ToCollaborationOptions_KeepsTheOffByDefaultContractForCallersThatHaveNoMode()
    {
        // The parameterless overload is the backward-compatible seam: no mode, no default-on.
        Bind([]).ToCollaborationOptions().Should().BeNull();
        Bind(new Dictionary<string, string?> { ["AgentCollaboration:Enabled"] = "true" })
            .ToCollaborationOptions().Should().NotBeNull();
    }

    [Fact]
    public void EnabledFalse_LeavesCollaborationOff()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AgentCollaboration:Enabled"] = "false",
            ["AgentCollaboration:MaxDelegationDepth"] = "3",
        });

        options.ToCollaborationOptions().Should().BeNull(
            "a configured-but-disabled section must not switch the feature on");
    }

    [Fact]
    public void EnabledSection_ProjectsEveryBoundValue()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AgentCollaboration:Enabled"] = "true",
            ["AgentCollaboration:MaxDelegationDepth"] = "3",
            ["AgentCollaboration:MaxTotalAgents"] = "64",
            ["AgentCollaboration:MaxInboxMessages"] = "8",
            ["AgentCollaboration:ClosedEntryRetentionMinutes"] = "5",
            ["AgentCollaboration:MaxClosedEntries"] = "16",
            ["AgentCollaboration:TranscriptVisibility"] = "open",
        });

        var collaboration = options.ToCollaborationOptions();

        collaboration.Should().NotBeNull();
        collaboration!.MaxDelegationDepth.Should().Be(3);
        collaboration.MaxTotalAgents.Should().Be(64);
        collaboration.MaxInboxMessages.Should().Be(8);
        collaboration.ClosedEntryRetention.Should().Be(TimeSpan.FromMinutes(5));
        collaboration.MaxClosedEntries.Should().Be(16);
        collaboration.TranscriptVisibility.Should().Be(TranscriptVisibilityMode.Open,
            "the mode is parsed case-insensitively so config files need not match enum casing");
    }

    [Fact]
    public void EnabledSection_DefaultsToTheNarrowestVisibility()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AgentCollaboration:Enabled"] = "true",
        });

        options.ToCollaborationOptions()!.TranscriptVisibility
            .Should().Be(TranscriptVisibilityMode.Ancestors);
    }

    [Fact]
    public void UnknownTranscriptVisibility_FailsWithTheAllowedValues()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AgentCollaboration:Enabled"] = "true",
            ["AgentCollaboration:TranscriptVisibility"] = "everyone",
        });

        var act = () => options.ToCollaborationOptions();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*TranscriptVisibility*")
            .WithMessage("*Ancestors*")
            .WithMessage("*everyone*");
    }

    [Theory]
    [InlineData("MaxDelegationDepth", "-1")]
    [InlineData("MaxTotalAgents", "0")]
    [InlineData("MaxInboxMessages", "0")]
    [InlineData("ClosedEntryRetentionMinutes", "0")]
    [InlineData("MaxClosedEntries", "0")]
    public void UnusableLimit_IsRejectedAtStartup(string key, string value)
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["AgentCollaboration:Enabled"] = "true",
            [$"AgentCollaboration:{key}"] = value,
        });

        var act = () => options.ToCollaborationOptions();

        act.Should().Throw<ArgumentOutOfRangeException>(
            "the library's own guard should surface the bad bound while the host is still booting");
    }
}
