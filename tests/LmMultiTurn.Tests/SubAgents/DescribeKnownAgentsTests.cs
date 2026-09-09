using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The one sentence every "who did you mean?" refusal ends with. Four sites share it — a refused
/// SendMessage, an unwaitable WaitForAgents, and the unknown-target corrections CheckAgent and
/// WaitAgent give — so that they cannot drift into four vocabularies for the same question.
/// </summary>
/// <remarks>
/// Exercised directly rather than only through the four handlers because the properties that matter
/// here are properties of the SENTENCE: it names agents, it is stable across repeats, and it never
/// truncates silently. Each handler's own test pins which roster it hands in, which is the part that
/// differs between them.
/// </remarks>
public class DescribeKnownAgentsTests
{
    [Fact]
    public void NamesEveryAgent_WithTheNameFirstAndTheIdBehindIt()
    {
        var sentence = SubAgentToolProvider.DescribeKnownAgents([("agent-2", "reviewer"), ("agent-3", "builder")]);

        sentence
            .Should()
            .Be(
                "Address one of these by name: builder (agent-3), reviewer (agent-2).",
                "the name leads because it is the handle the caller should use; the id trails for the "
                    + "case where a name is genuinely not enough"
            );
    }

    [Fact]
    public void OrdersByName_SoARepeatedFailureReadsIdentically()
    {
        // A model that gets the same refusal twice should see the same sentence twice. A listing that
        // reshuffled would invite it to believe the roster changed between its two attempts.
        (string, string)[] roster = [("agent-9", "zoe"), ("agent-2", "adam"), ("agent-4", "mia")];

        var first = SubAgentToolProvider.DescribeKnownAgents(roster);
        var second = SubAgentToolProvider.DescribeKnownAgents([.. roster.Reverse()]);

        first.Should().Be(second);
        first.Should().Be("Address one of these by name: adam (agent-2), mia (agent-4), zoe (agent-9).");
    }

    [Fact]
    public void AnAgentWhoseNameIsItsId_IsPrintedOnce()
    {
        // A legacy spawn can leave an agent with no name of its own, and it reports its id as its name
        // so no caller has to decide what to print. "agent-3 (agent-3)" would read as two handles where
        // there is one, and the parenthesised id exists precisely because it differs from the name.
        var sentence = SubAgentToolProvider.DescribeKnownAgents([("agent-3", "agent-3")]);

        sentence.Should().Be("Address one of these by name: agent-3.");
    }

    [Fact]
    public void AnEmptyRoster_SaysSo_RatherThanTrailingOffAfterTheColon()
    {
        var sentence = SubAgentToolProvider.DescribeKnownAgents([]);

        sentence.Should().Be("There are no other agents to address right now.");
    }

    [Fact]
    public void ACappedListing_AnnouncesThatItIsCapped()
    {
        // A silently truncated list is worse than no list: it invites the reader to conclude the agent
        // it wanted does not exist, and the next move after that conclusion is spawning a duplicate.
        var roster = Enumerable
            .Range(1, SubAgentToolProvider.MaxListedAgentIds + 5)
            .Select(i => ($"agent-{i}", $"worker-{i:D2}"))
            .ToArray();

        var sentence = SubAgentToolProvider.DescribeKnownAgents(roster);

        sentence
            .Should()
            .Contain($"(showing {SubAgentToolProvider.MaxListedAgentIds} of {roster.Length})")
            .And.Contain("worker-01");

        sentence
            .Should()
            .NotContain("worker-25", "the cap has to actually cap — announcing a limit it does not apply is worse");
    }
}
