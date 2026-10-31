using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The record a sub-agent leaves on its thread (#868) keeps its last result and its error text, cut to
/// <see cref="SubAgentRecord.MaxLastResultLength"/> characters so one long answer cannot bloat the
/// parent's metadata, and marked where it was cut.
/// </summary>
public class SubAgentRecordTests
{
    [Theory]
    [InlineData(SubAgentRecord.MaxLastResultLength, false)]
    [InlineData(20_000, true)]
    public void ALongResult_IsCutToTheCap_EndingInItsMarker(int length, bool cut)
    {
        var state = NewState();
        state.LastResult = new string('r', length);

        var kept = SubAgentRecord.From(state, "thread-parent", collaboration: null).LastResult!;

        AssertCut(kept, state.LastResult, SubAgentRecord.ResultTruncationMarker, cut);
    }

    [Theory]
    [InlineData(SubAgentRecord.MaxLastResultLength, false)]
    [InlineData(20_000, true)]
    public void ALongError_IsCutToTheCap_EndingInItsMarker(int length, bool cut)
    {
        var state = NewState();
        var error = new string('e', length);
        state.TryCompleteWithException(new InvalidOperationException(error)).Should().BeTrue();

        var kept = SubAgentRecord.From(state, "thread-parent", collaboration: null).ErrorMessage!;

        AssertCut(kept, error, SubAgentRecord.ErrorTruncationMarker, cut);
    }

    private static void AssertCut(string kept, string original, string marker, bool cut)
    {
        if (!cut)
        {
            kept.Should().Be(original, "text at the cap is kept whole");
            return;
        }

        kept.Length.Should().Be(SubAgentRecord.MaxLastResultLength);
        kept.Should().EndWith(marker);
        kept.Should().StartWith(original[..(SubAgentRecord.MaxLastResultLength - marker.Length)]);
    }

    private static SubAgentState NewState() =>
        new()
        {
            AgentId = "agent-1",
            TemplateName = "tmpl",
            Task = "do work",
            SpawnCapability = new SpawnCapabilityRecord(
                [],
                SpawnCapabilityRecord.ProjectedSource,
                [],
                [],
                [],
                [],
                false
            ),
            Agent = new Mock<IMultiTurnAgent>().Object,
            Template = new SubAgentTemplate
            {
                Name = "tmpl",
                SystemPrompt = "You are a test agent.",
                AgentFactory = () => new Mock<IStreamingAgent>().Object,
            },
        };
}
