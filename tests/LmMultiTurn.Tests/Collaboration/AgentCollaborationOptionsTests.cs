using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers what <see cref="AgentCollaborationOptions"/> promises: its defaults reproduce today's
/// behaviour, and every bound it carries is rejected at construction rather than at first send.
/// </summary>
/// <remarks>
/// The bounds here become refusals returned to a model. A host that configures an impossible bound
/// must therefore fail the run that configured it — surfacing much later as an inexplicable refusal
/// mid-conversation is the failure mode these tests exist to prevent.
/// </remarks>
public class AgentCollaborationOptionsTests
{
    [Fact]
    public void Defaults_ReproduceTodaysBehaviour()
    {
        var options = new AgentCollaborationOptions();

        // One ordinary hop is exactly what the runtime permits today, and the narrowest transcript
        // mode is the only one that may ever be reached without an explicit choice.
        options.MaxDelegationDepth.Should().Be(1);
        options.TranscriptVisibility.Should().Be(TranscriptVisibilityMode.Ancestors);
        options.MaxTotalAgents.Should().Be(32);
        options.MaxInboxMessages.Should().Be(32);
        options.MaxClosedEntries.Should().Be(1024);
        options.ClosedEntryRetention.Should().Be(TimeSpan.FromMinutes(30));

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    [Fact]
    public void Validate_AcceptsZeroDelegationDepth_ForACollaborationThatMayNotSpawn()
    {
        var options = new AgentCollaborationOptions { MaxDelegationDepth = 0 };

        options.Invoking(o => o.Validate()).Should().NotThrow();
    }

    public static TheoryData<AgentCollaborationOptions, string> InvalidOptions =>
        new()
        {
            {
                new AgentCollaborationOptions { MaxDelegationDepth = -1 },
                nameof(AgentCollaborationOptions.MaxDelegationDepth)
            },
            {
                new AgentCollaborationOptions { MaxTotalAgents = 0 },
                nameof(AgentCollaborationOptions.MaxTotalAgents)
            },
            {
                new AgentCollaborationOptions { MaxInboxMessages = 0 },
                nameof(AgentCollaborationOptions.MaxInboxMessages)
            },
            {
                new AgentCollaborationOptions { ClosedEntryRetention = TimeSpan.Zero },
                nameof(AgentCollaborationOptions.ClosedEntryRetention)
            },
            {
                new AgentCollaborationOptions { MaxClosedEntries = 0 },
                nameof(AgentCollaborationOptions.MaxClosedEntries)
            },
            {
                new AgentCollaborationOptions { TranscriptVisibility = (TranscriptVisibilityMode)99 },
                nameof(AgentCollaborationOptions.TranscriptVisibility)
            },
        };

    [Theory]
    [MemberData(nameof(InvalidOptions))]
    public void Validate_RejectsUnusableBound_NamingTheOffendingProperty(
        AgentCollaborationOptions options,
        string expectedParamName
    )
    {
        // The parameter name is asserted because it is the only part of the diagnostic that tells a
        // host which of six settings it got wrong.
        options
            .Invoking(o => o.Validate())
            .Should()
            .Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should()
            .Be(expectedParamName);
    }

    [Fact]
    public void Bundle_ValidatesOptions_AtConstruction()
    {
        var options = new AgentCollaborationOptions { MaxTotalAgents = 0 };

        FluentActions
            .Invoking(() => new AgentCollaborationBundle("collab-1", options))
            .Should()
            .Throw<ArgumentOutOfRangeException>();
    }
}
