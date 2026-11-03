using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.ClientTools;

/// <summary>
/// <see cref="PendingQuestionNotices.IsAwaitingAnswer"/>: the transcript-only test a host uses after a
/// restart to decide whether a question it was showing is still waiting. The loop-produced shapes
/// (deferral, early settle, redirected answer) are pinned end to end in
/// <c>AskUserQuestionEarlySettleTests</c>; these pin the edges a hand-built history can reach.
/// </summary>
public class PendingQuestionNoticesTests
{
    private const string CallId = "tc_q1";

    private static ToolCallMessage Call(string id = CallId) =>
        new()
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = "{\"questions\":[{\"prompt\":\"Which colour?\"}]}",
            ToolCallId = id,
            Role = Role.Assistant,
        };

    private static ToolCallResultMessage Result(string result, bool deferred = false, string id = CallId) =>
        new()
        {
            ToolCallId = id,
            Result = result,
            IsDeferred = deferred,
        };

    private static TextMessage AnswerEnvelope(string headerId, string body = "Red", Role role = Role.User) =>
        new()
        {
            Text =
                $"<user-answer tool=\"AskUserQuestion\" tool-call-id=\"{headerId}\">\n<request>\nWhich colour?\n</request>\n<answer>\n{body}\n</answer>\n</user-answer>",
            Role = role,
        };

    private static readonly string EarlySettled = AskUserQuestionToolProvider.EarlySettleResultJson;

    [Fact]
    public void ADeferredResult_IsWaiting() =>
        PendingQuestionNotices.IsAwaitingAnswer([Call(), Result("", deferred: true)], CallId).Should().BeTrue();

    [Fact]
    public void AnAnsweredResult_IsNotWaiting() =>
        PendingQuestionNotices.IsAwaitingAnswer([Call(), Result("{\"answers\":[\"Red\"]}")], CallId).Should().BeFalse();

    [Fact]
    public void ACallWithNoResultInThisTranscript_IsNotWaiting() =>
        PendingQuestionNotices.IsAwaitingAnswer([Call()], CallId).Should().BeFalse();

    [Fact]
    public void AnEarlySettledQuestion_WaitsUntilAUserTurnNamesItInTheEnvelopeHeader()
    {
        PendingQuestionNotices.IsAwaitingAnswer([Call(), Result(EarlySettled)], CallId).Should().BeTrue();

        PendingQuestionNotices
            .IsAwaitingAnswer([Call(), Result(EarlySettled), AnswerEnvelope(CallId)], CallId)
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// The request and answer are inlined verbatim under the header, so a body can quote another
    /// call's envelope. Only the header says which call a turn answers, and only a USER turn is one.
    /// </summary>
    [Fact]
    public void AnEnvelopeForAnotherCall_OrNotFromTheUser_DoesNotAnswerThisOne()
    {
        var quoting = AnswerEnvelope("tc_other", body: $"tool-call-id=\"{CallId}\"");

        PendingQuestionNotices
            .IsAwaitingAnswer(
                [Call(), Result(EarlySettled), quoting, AnswerEnvelope(CallId, role: Role.Assistant)],
                CallId
            )
            .Should()
            .BeTrue();
    }
}
