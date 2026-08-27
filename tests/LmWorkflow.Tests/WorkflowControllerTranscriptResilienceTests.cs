using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Issue #498: reading a workflow controller's persisted transcript must degrade PER-RECORD, and the
///     per-record skip must not leave a tool call or result without its partner.
/// </summary>
/// <remarks>
///     <para>
///         <see cref="WorkflowControllerEndpoint.GetTranscriptAsync"/> is the third call site of the
///         bulk converter whose all-or-nothing behaviour was fixed for <c>MultiTurnAgentBase.RecoverAsync</c>
///         in #489/#495. The defect is the same and so is the required outcome, so these tests are written
///         against the same two origins the recovery tests pin
///         (<c>MultiTurnAgentBaseTests.RecoverAsync_DropsBothHalvesOfAToolCallPair_WhenEitherHalfIsCorrupt</c>
///         and <c>..._WhenItsPartnerRowIsSimplyAbsent</c>): a row that fails to deserialize, and a row that
///         was never written at all.
///     </para>
///     <para>
///         Every case carries a POSITIVE CONTROL — an unrelated healthy <see cref="TextMessage"/> that must
///         still come back, asserted by value and by exact count. Without it a mutation that returned an
///         empty transcript would satisfy "no orphan survives" and read exactly like a pass.
///     </para>
/// </remarks>
public class WorkflowControllerTranscriptResilienceTests
{
    private const string RunId = "prior-run";
    private const string ToolCallId = "call-1";
    private const string HealthyText = "healthy text";

    /// <summary>Only MessageJson is damaged — MessageType/Id survive, exactly as a bit-rotted row would.</summary>
    private const string CorruptJson = "{ this is not valid message json";

    [Fact]
    public async Task GetTranscriptAsync_SkipsACorruptRow_AndStillReturnsItsHealthySiblings()
    {
        // The bare per-record property, with no tool pairing involved: a corrupt row deliberately BETWEEN
        // two healthy ones. The pre-fix bulk converter threw on the first bad row and returned NOTHING, so
        // restoring the sibling AFTER the corrupt row is what proves the degradation is per-record and not
        // merely "stops at the first problem".
        var threadId = "wf-transcript-corrupt-row";
        var store = new InMemoryConversationStore();

        var before = Row(new TextMessage { Text = "before corrupt", Role = Role.User, RunId = RunId }, threadId, 1);
        var corrupt = before with { Id = "corrupt-record-1", Timestamp = 2, MessageJson = CorruptJson };
        var after = Row(new TextMessage { Text = "after corrupt", Role = Role.Assistant, RunId = RunId }, threadId, 3);

        await store.AppendMessagesAsync(threadId, [before, corrupt, after]);

        var transcript = await EndpointFor(threadId, store).GetTranscriptAsync();

        transcript.OfType<TextMessage>().Select(m => m.Text)
            .Should().Equal(["before corrupt", "after corrupt"]);
        // Exactly the two healthy siblings — the corrupt row contributes nothing, not even a placeholder.
        transcript.Should().HaveCount(2);
    }

    [Theory]
    // corruptTheCall: which half of the pair is the damaged row. Both directions are required — a fix that
    // only reconciles results against calls (or only the reverse) passes one of these and no-ops on the other.
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetTranscriptAsync_ReturnsNeitherHalfOfAToolPair_WhenEitherHalfIsCorrupt(
        bool corruptTheCall)
    {
        // ORIGIN A — corruption. A tool call and its result are TWO separate persisted rows
        // (MessagePersistenceConverter is strictly 1:1), so the per-record skip ORPHANS the partner of the
        // row it drops. Nothing downstream repairs that: MessageTransformationMiddleware returns null from
        // TryCreateToolCallAggregate when only one half is present and passes the unpaired message through
        // verbatim, and every provider rejects that shape with a 400. So the reader must drop BOTH halves.
        var threadId = $"wf-transcript-corrupt-{(corruptTheCall ? "call" : "result")}";
        var store = new InMemoryConversationStore();

        var text = Row(new TextMessage { Text = HealthyText, Role = Role.User, RunId = RunId }, threadId, 1);
        var callRow = Row(CallMessage(), threadId, 2);
        var resultRow = Row(ResultMessage(), threadId, 3);

        if (corruptTheCall)
        {
            callRow = callRow with { MessageJson = CorruptJson };
        }
        else
        {
            resultRow = resultRow with { MessageJson = CorruptJson };
        }

        await store.AppendMessagesAsync(threadId, [text, callRow, resultRow]);

        var transcript = await EndpointFor(threadId, store).GetTranscriptAsync();

        AssertNoOrphanSurvives(transcript);
    }

    [Theory]
    [InlineData(true)] // the RESULT row was never written — a dangling tool_use
    [InlineData(false)] // the CALL row was never written — a dangling tool_result
    public async Task GetTranscriptAsync_DropsAnUnpairedToolMessage_WhenItsPartnerRowIsSimplyAbsent(
        bool resultRowAbsent)
    {
        // ORIGIN B — no corruption anywhere. MultiTurnAgentBase.PersistMessageAsync appends one row at a
        // time and SWALLOWS an append failure, so a lost append leaves a permanently half-written tool
        // exchange in the store. This shape pre-dates the per-record skip entirely, which is why the repair
        // must be blind to WHY a partner is missing: a sweep gated on "a row was skipped" would leave this,
        // the older and likelier route, unrepaired.
        var threadId = $"wf-transcript-absent-{(resultRowAbsent ? "result" : "call")}";
        var store = new InMemoryConversationStore();

        var text = Row(new TextMessage { Text = HealthyText, Role = Role.User, RunId = RunId }, threadId, 1);
        var survivingHalf = Row(resultRowAbsent ? CallMessage() : ResultMessage(), threadId, 2);

        // The partner row is simply never appended.
        await store.AppendMessagesAsync(threadId, [text, survivingHalf]);

        var transcript = await EndpointFor(threadId, store).GetTranscriptAsync();

        AssertNoOrphanSurvives(transcript);
    }

    [Fact]
    public async Task GetTranscriptAsync_ReportsTheUnreadableAndUnpairedCounts_WhenRowsAreDropped()
    {
        // The reader's own doc promises a drop is reported. The per-row callback covers only HALF of that:
        // the pairing sweep has no callback at all, and its likelier origin — a lost append, with no
        // corruption anywhere — fires onSkipped ZERO times, so the commoner drop route was entirely silent.
        // The summary is what closes that, and it is asserted on the COUNTS, not merely on the line
        // existing.
        //
        // The three terms are deliberately DISTINCT (2 unreadable, 1 unpaired, 1 restored of 4 attempted)
        // so that transposing them, or being off by one in either, fails here instead of reading as a pass.
        var threadId = "wf-transcript-drop-counts";
        var store = new InMemoryConversationStore();
        var logger = new ListLogger();

        var healthy = Row(new TextMessage { Text = HealthyText, Role = Role.User, RunId = RunId }, threadId, 1);
        // Unreadable #1: a damaged row with no tool pairing involved at all.
        var corruptText = healthy with { Id = "corrupt-record-1", Timestamp = 2, MessageJson = CorruptJson };
        // Unreadable #2 is the result; the perfectly readable call it answers is then the UNPAIRED one, so
        // the two terms are produced by genuinely different mechanisms rather than counted twice.
        var callRow = Row(CallMessage(), threadId, 3);
        var corruptResult = Row(ResultMessage(), threadId, 4) with { MessageJson = CorruptJson };

        await store.AppendMessagesAsync(threadId, [healthy, corruptText, callRow, corruptResult]);

        var transcript = await EndpointFor(threadId, store, logger).GetTranscriptAsync();

        // Positive control: the summary is only meaningful if the read itself behaved as claimed.
        transcript.OfType<TextMessage>().Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be(HealthyText);
        transcript.Should().HaveCount(1);

        var summary = logger.Entries.Should()
            .ContainSingle(e => e.Message.Contains("persisted records for the workflow controller transcript"))
            .Which;
        summary.Level.Should().Be(
            LogLevel.Warning,
            "records were dropped, so the summary must not sit at Information");
        summary.Message.Should().Contain("Read 1 of 4 persisted records");
        summary.Message.Should().Contain("2 unreadable").And.Contain("1 unpaired");
    }

    [Fact]
    public async Task GetTranscriptAsync_ReportsNothing_WhenEveryRowIsReadableAndPaired()
    {
        // Non-vacuity control for the test above: the summary has to be DRIVEN by the drops. A version that
        // logged on every read would satisfy the count assertions and still tell an operator nothing, and a
        // healthy transcript is the overwhelmingly normal case.
        var threadId = "wf-transcript-clean-read";
        var store = new InMemoryConversationStore();
        var logger = new ListLogger();

        await store.AppendMessagesAsync(
            threadId,
            [
                Row(new TextMessage { Text = HealthyText, Role = Role.User, RunId = RunId }, threadId, 1),
                Row(CallMessage(), threadId, 2),
                Row(ResultMessage(), threadId, 3),
            ]);

        var transcript = await EndpointFor(threadId, store, logger).GetTranscriptAsync();

        transcript.Should().HaveCount(3, "nothing was corrupt and the tool exchange is complete");
        logger.Entries.Should().BeEmpty("nothing was dropped, so there is nothing to report");
    }

    /// <summary>
    ///     Pins the required outcome shared by all four pairing cases: NEITHER half of the exchange survives,
    ///     and the unrelated healthy row still does. The second half is the non-vacuity control — "no tool
    ///     message is present" is trivially true of an empty transcript, so the healthy row is asserted by
    ///     value and the count is pinned exactly.
    /// </summary>
    private static void AssertNoOrphanSurvives(IReadOnlyList<IMessage> transcript)
    {
        transcript.OfType<ToolCallMessage>().Should().BeEmpty(
            "a tool call with no matching result is rejected by every provider");
        transcript.OfType<ToolCallResultMessage>().Should().BeEmpty(
            "a tool result with no matching call is rejected by every provider");

        transcript.OfType<TextMessage>().Select(m => m.Text)
            .Should().ContainSingle().Which.Should().Be(HealthyText);
        transcript.Should().HaveCount(1);
    }

    private static WorkflowControllerEndpoint EndpointFor(
        string threadId,
        IConversationStore store,
        ILogger? logger = null) => new(() => AgentCollaborationStatuses.Completed, threadId, store, logger);

    private static PersistedMessage Row(IMessage message, string threadId, long timestamp) =>
        MessagePersistenceConverter.ToPersistedMessage(message, threadId, RunId) with { Timestamp = timestamp };

    private static ToolCallMessage CallMessage() =>
        new()
        {
            Role = Role.Assistant,
            RunId = RunId,
            ToolCallId = ToolCallId,
            FunctionName = "do_thing",
            FunctionArgs = "{}",
        };

    private static ToolCallResultMessage ResultMessage() =>
        new()
        {
            ToolCallId = ToolCallId,
            ToolName = "do_thing",
            Result = "ok",
            RunId = RunId,
        };
}
