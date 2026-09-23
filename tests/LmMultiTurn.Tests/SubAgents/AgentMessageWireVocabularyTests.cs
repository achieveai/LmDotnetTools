using System.Text.RegularExpressions;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The envelope a receiver reads and the vocabulary <c>SendMessage</c> accepts are one vocabulary.
/// </summary>
/// <remarks>
/// <para>
/// They were two. The envelope carried the C# member name, so a delegated agent read
/// <c>type="DelegateTask"</c> and was told to report progress as <c>TaskUpdate</c> — and both
/// spellings were refused by the tool it had to send them with, which advertises snake_case. The
/// failure looked intermittent rather than systematic because the three single-word types
/// (<c>Question</c>, <c>Steer</c>, <c>Response</c>) survive lower-casing unchanged; only the two
/// multi-word ones actually broke.
/// </para>
/// <para>
/// Pinned as a round trip over every sendable type rather than as two more spellings in the parser's
/// table, because the property that was violated is the round trip: whatever a type is CALLED in a
/// message an agent receives must be a thing that agent can then SEND. A new message type added with
/// a two-word name would break the same way, and the loop below is what refuses to let that ship.
/// </para>
/// </remarks>
public class AgentMessageWireVocabularyTests
{
    /// <summary>
    /// Every type an agent may send. <see cref="AgentMessageType.DeliveryFailure"/> is excluded
    /// deliberately: the collaboration mints it, and the tool refuses it under every spelling.
    /// </summary>
    public static TheoryData<AgentMessageType> SendableTypes =>
        [
            AgentMessageType.Question,
            AgentMessageType.DelegateTask,
            AgentMessageType.TaskUpdate,
            AgentMessageType.Steer,
            AgentMessageType.Response,
        ];

    /// <summary>Every <c>…msg-type="X"</c> and <c>type="X"</c> value the envelope states.</summary>
    private static IReadOnlyList<string> TypeNamesStatedBy(string envelope) =>
        [.. Regex.Matches(envelope, "(?:^|[ -])(?:msg-)?type=\"([^\"]+)\"").Select(m => m.Groups[1].Value)];

    [Theory]
    [MemberData(nameof(SendableTypes))]
    public void EveryTypeTheEnvelopeNames_IsATypeTheSendingToolAccepts(AgentMessageType sent)
    {
        var envelope = AgentMessage
            .Create("agentmsg-1", sent, fromAgentId: "agent-2", fromName: "planner", body: "do the thing")
            .Text;

        var stated = TypeNamesStatedBy(envelope);

        stated.Should().NotBeEmpty("the envelope always states the type it carries");

        foreach (var name in stated)
        {
            SubAgentToolProvider
                .TryParseMessageType(name, out _)
                .Should()
                .BeTrue(
                    "the envelope told the receiver the type is '{0}', so SendMessage has to take that "
                        + "word back — the receiver has no other spelling to copy",
                    name
                );
        }
    }

    [Theory]
    [MemberData(nameof(SendableTypes))]
    public void TheTypeTheEnvelopeStates_RoundTripsBackToTheSameType(AgentMessageType sent)
    {
        var envelope = AgentMessage.Create("agentmsg-1", sent, "agent-2", "planner", body: "b").Text;
        var stated = TypeNamesStatedBy(envelope)[0];

        SubAgentToolProvider.TryParseMessageType(stated, out var parsed).Should().BeTrue();
        parsed.Should().Be(sent, "a spelling that parses to a DIFFERENT type is worse than one that is refused");
    }

    [Theory]
    [InlineData("DelegateTask", AgentMessageType.DelegateTask)]
    [InlineData("TaskUpdate", AgentMessageType.TaskUpdate)]
    public void ThePascalCaseSpellingTheEnvelopeUsedToCarry_IsStillAccepted(string legacy, AgentMessageType expected)
    {
        // Permanently, not transitionally: a retained transcript keeps the old envelope forever, and an
        // agent resuming against one would otherwise have its reply refused for quoting what it read.
        SubAgentToolProvider.TryParseMessageType(legacy, out var parsed).Should().BeTrue();
        parsed.Should().Be(expected);
    }

    [Fact]
    public void DeliveryFailure_IsRefusedUnderBothSpellings()
    {
        // The widened vocabulary must not have widened this: only the collaboration mints one.
        SubAgentToolProvider.TryParseMessageType("delivery_failure", out _).Should().BeFalse();
        SubAgentToolProvider.TryParseMessageType("DeliveryFailure", out _).Should().BeFalse();
    }
}
