using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Compaction;

/// <summary>
/// One fixture per protected-tail rule where a naive cut at the candidate would violate it (#683;
/// spec 679 §2.4, §12.2): the cut only moves earlier, and a cut-blocking state the rows cannot place
/// refuses it with a typed reason.
/// </summary>
public sealed class CutSelectorTests
{
    /// <summary>A floor of one row, so short fixtures exercise the rule under test rather than R3.</summary>
    private static readonly CutSelectorOptions Short = ThreadFixture.Options(minTail: ThreadFixture.TokensPerRow);

    private static CutDecision.Cut ExpectCut(CutDecision decision) =>
        decision.Should().BeOfType<CutDecision.Cut>().Subject;

    private static CutDecision.Skipped ExpectSkipped(CutDecision decision, string reason)
    {
        var skipped = decision.Should().BeOfType<CutDecision.Skipped>().Subject;
        skipped.Reason.Should().Be(reason);
        return skipped;
    }

    // ---- R1: turn boundary and tool adjacency ------------------------------------------------

    [Fact]
    public void R1_CandidateOnAToolCallRow_MovesBeforeThePair()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(3);
        var callOfTurn2 = ThreadFixture.TurnEnd(1) + 1;

        var cut = ExpectCut(CutSelector.Select(thread.Request(callOfTurn2, Short)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(1), "the call at seq 4 must stay with its result at seq 5");
        cut.CandidateSeq.Should().Be(callOfTurn2);
    }

    [Fact]
    public void R1_CandidateBetweenRowsOfOneGeneration_MovesToThePreviousBoundary()
    {
        // Assistant text and a tool call emitted in the same generation, then the result.
        var thread = new ThreadFixture().Human("go").ToolTurns(2).Assistant("thinking aloud");
        var generation = thread.Rows[^1].Message.GenerationId!;
        _ = thread.ToolTurn(generationId: generation);

        var textSeq = ThreadFixture.TurnEnd(2) + 1;
        var cut = ExpectCut(CutSelector.Select(thread.Request(textSeq, Short)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(2), "a generation is cut only after its last row");
    }

    [Fact]
    public void R1_ResultRowThatClosesAGeneration_IsAcceptedUnchanged()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(4);

        var cut = ExpectCut(CutSelector.Select(thread.Request(ThreadFixture.TurnEnd(2), Short)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(2));
    }

    [Fact]
    public void R1_CallRowWhoseResultNeverArrived_IsNeverTheCut()
    {
        // The stream died after the call, then a new run continued the thread: the orphan row ends its
        // generation, so the boundary rule accepts it and only the pairing rule refuses it (#686 M16).
        var thread = new ThreadFixture().Human("go").ToolTurns(2).ToolCall();
        var orphanCall = thread.LastSeq;
        _ = thread.Run("run-2").Human("carry on").ToolTurns(2);

        var cut = ExpectCut(CutSelector.Select(thread.Request(orphanCall, Short)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(2), "a call row without its result may not end a checkpoint");
    }

    // ---- R2: mid-run cut, instruction travels whole ---------------------------------------------

    [Fact]
    public void R2_OneInstructionAndAHundredToolTurns_CutsAtTurn60_AndQuotesTheInstructionWhole()
    {
        var thread = new ThreadFixture().Human("fix the flaky test").ToolTurns(100);

        var cut = ExpectCut(CutSelector.Select(thread.Request(ThreadFixture.TurnEnd(60))));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(60));
        cut.CurrentRunId.Should().Be("run-1");
        cut.CurrentInstruction.Should().ContainSingle().Which.Should().BeSameAs(thread.Rows[0]);
        cut.Recovery.IsClean.Should().BeTrue();

        // The tail the projection dispatches is turns 61–100, and the envelope leads with the instruction.
        var checkpoint = CheckpointFor(cut, thread);
        var view = AgentContextProjection.Default.Build("system", thread.Rows, checkpoint);
        view.Skip(2).Should().HaveCount(80).And.OnlyContain(m => m is ToolCallMessage || m is ToolCallResultMessage);
        view[2].Should().BeOfType<ToolCallMessage>().Which.FunctionArgs.Should().Be("""{"turn":61}""");
        checkpoint
            .RenderEnvelope(CheckpointRenderOptions.Default)
            .Split('\n')[1]
            .Should()
            .Be("## Current instruction (verbatim, seq 1)");
        view[1].Should().BeOfType<TextMessage>().Which.Text.Should().Contain("- [seq 1] fix the flaky test");
    }

    [Fact]
    public void R2_NotifyRowsAfterTheInstruction_AreNotQuotedAsInstruction()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(4).Notify().ToolTurns(4);

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, Short)));

        cut.CurrentInstruction.Should().ContainSingle().Which.Seq.Should().Be(1);
    }

    // ---- R3: current-run floor ----------------------------------------------------------------

    [Fact]
    public void R3_CurrentRunShorterThanMinTail_StaysWholeInTheTail_AndCurrentInstructionIsEmpty()
    {
        var thread = new ThreadFixture()
            .Human("first task")
            .ToolTurns(5)
            .Run("run-2")
            .Human("second task")
            .ToolTurns(2);
        var firstRunEnd = ThreadFixture.TurnEnd(5);
        var options = ThreadFixture.Options(minTail: 100); // run-2 is 5 rows = 50 tokens

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, options)));

        cut.Seq.Should().Be(firstRunEnd, "the whole current run stays when it is shorter than the floor");
        cut.CurrentRunId.Should().Be("run-2");
        cut.CurrentInstruction.Should().BeEmpty();
        var view = AgentContextProjection.Default.Build(null, thread.Rows, CheckpointFor(cut, thread));
        view.Skip(1).Should().HaveCount(5).And.OnlyContain(m => m.RunId == "run-2");
    }

    [Fact]
    public void R3_LongCurrentRun_KeepsAtLeastMinTailTokensOfIt()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(20); // 41 rows
        var options = ThreadFixture.Options(minTail: 100); // ten rows

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, options)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(15), "rows 32–41 are the ten the floor keeps");
        cut.TailTokens.Should().Be(100);
    }

    [Fact]
    public void R3_TheCheckpointRowInTheCurrentRun_CountsNothingTowardsTheFloor_BecauseItIsNeverSent()
    {
        // A checkpoint row sits past its boundary in the run it was made in. Counted at its envelope's size it satisfied
        // the floor on its own, so a tail of one real row passed R3 and a manual request that had nothing to compact
        // was accepted.
        var thread = new ThreadFixture().Human("go").ToolTurns(3); // 1..7
        _ = thread
            .Checkpoint(
                new CompactionCheckpointMessage
                {
                    CheckpointId = "cp-1",
                    Boundary = new CheckpointBoundary { Seq = 3, MessageId = "m3" },
                    Trigger = CompactionTrigger.Manual,
                    Manifest = new ContextManifest(),
                    Narrative = "n",
                }
            ) // 8
            .ToolTurns(1); // 9..10
        var options = ThreadFixture.Options(minTail: 30) with
        {
            Estimator = m => m is CompactionCheckpointMessage ? 1_000 : ThreadFixture.TokensPerRow,
        };

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, options, activeBoundarySeq: 3)));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(2), "rows 6, 7, 9 and 10 are the real tail the floor keeps");
    }

    [Fact]
    public void R3_WhenTheRealTailPastACheckpointRowIsBelowTheFloor_TheWholeRunStays_AsTailOnly()
    {
        // Rows 4-7, 9 and 10 are 60 real tokens against a 100-token floor, so R3 keeps them all. A large checkpoint envelope
        // at seq 8 must not lift the run over the floor and let a cut through.
        var thread = new ThreadFixture().Human("go").ToolTurns(3); // 1..7
        _ = thread
            .Checkpoint(
                new CompactionCheckpointMessage
                {
                    CheckpointId = "cp-1",
                    Boundary = new CheckpointBoundary { Seq = 3, MessageId = "m3" },
                    Trigger = CompactionTrigger.Manual,
                    Manifest = new ContextManifest(),
                    Narrative = "n",
                }
            ) // 8
            .ToolTurns(1); // 9..10
        var options = ThreadFixture.Options(minTail: 100) with
        {
            Estimator = m => m is CompactionCheckpointMessage ? 1_000 : ThreadFixture.TokensPerRow,
        };

        var skipped = ExpectSkipped(
            CutSelector.Select(thread.Request(thread.LastSeq, options, activeBoundarySeq: 3)),
            CompactionReasons.NoSafeBoundary
        );

        skipped.TailOnly.Should().BeTrue("the real tail is under the floor and nothing else refused a cut");
    }

    /// <summary>Run-1: a human row and six tool turns (seq 1–13). Run-2: woken by a non-human input, six tool turns (seq 14–26).</summary>
    private static ThreadFixture WokenRun(string wakeUp)
    {
        var thread = new ThreadFixture().Human("build the parser").ToolTurns(6).Run("run-2");
        _ =
            wakeUp == "notify"
                ? thread.Notify(label: "agent-1 finished")
                : thread.Agent(AgentMessageType.Response, "the parser is done");
        return thread.ToolTurns(6);
    }

    [Theory]
    [InlineData("response")]
    [InlineData("notify")]
    public void R3_ARunWokenByAnAgentResponseOrANotification_IsTheCurrentRun_SoTheCutReachesIntoIt(string wakeUp)
    {
        // A Workspace root is routinely woken only by its sub-agents. Anchoring R3 to the last human row kept the
        // whole woken run in the tail and let the cut eat into the finished run before it.
        var thread = WokenRun(wakeUp);

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, ThreadFixture.Options(minTail: 30))));

        cut.Seq.Should().Be(22, "run-2 keeps its last 30 tokens (seq 23–26); the cut lands on the result before them");
    }

    [Fact]
    public void R3_ACutInsideARunWokenByANotification_StillQuotesTheEarlierHumanInstruction()
    {
        var thread = WokenRun("notify");

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, ThreadFixture.Options(minTail: 30))));

        cut.Seq.Should().BeGreaterThan(14, "the cut is inside run-2");
        cut.CurrentRunId.Should().Be("run-1", "the instruction's run, not the woken one");
        cut.CurrentInstruction.Should().ContainSingle().Which.Should().BeSameAs(thread.Rows[0]);
        CutSelector.CurrentInstructionRows(thread.Rows, cut.Seq).Should().ContainSingle().Which.Seq.Should().Be(1);
    }

    // ---- R4: corrections ----------------------------------------------------------------------

    [Fact]
    public void R4_RunWithAMidRunInjection_IsKeptWhole()
    {
        var thread = new ThreadFixture()
            .Human("old task")
            .ToolTurns(2)
            .Run("run-2")
            .Human("new task")
            .ToolTurns(3)
            .Human("no, the other file")
            .ToolTurns(3);
        var run1End = ThreadFixture.TurnEnd(2);
        var candidate = run1End + 1 + (2 * 3); // the result closing run-2's third turn

        var cut = ExpectCut(CutSelector.Select(thread.Request(candidate, ThreadFixture.Options(minTail: 10))));

        cut.Seq.Should().Be(run1End, "a corrected run is not split while it is recent");
    }

    [Fact]
    public void R4_AgentMessagesAfterAnAssistantRow_DoNotKeepTheRunWhole_AndAreStillQuotedAsInstruction()
    {
        // A sub-agent's Steer or Question lands mid-run as a user-role row. Treating it as a correction made a
        // multi-agent run uncuttable; quoting it in the current instruction keeps what it said.
        var thread = new ThreadFixture()
            .Human("old task")
            .ToolTurns(2)
            .Run("run-2")
            .Human("new task")
            .ToolTurns(3)
            .AgentSteer("focus on the parser")
            .ToolTurns(3);
        var run2Human = ThreadFixture.TurnEnd(2) + 1;
        var steer = run2Human + (2 * 3) + 1;
        var candidate = steer + (2 * 2); // the result closing the second turn after the steer

        var cut = ExpectCut(CutSelector.Select(thread.Request(candidate, ThreadFixture.Options(minTail: 10))));

        cut.Seq.Should().Be(candidate, "an agent message is not a human correction");
        cut.CurrentInstruction.Select(r => r.Seq).Should().Equal(run2Human, steer);
        CutSelector.CurrentInstructionRows(thread.Rows, cut.Seq).Select(r => r.Seq).Should().Equal(run2Human, steer);
    }

    [Theory]
    [InlineData(AgentMessageType.Question, false)]
    [InlineData(AgentMessageType.TaskUpdate, false)]
    [InlineData(AgentMessageType.Response, false)]
    [InlineData(AgentMessageType.DeliveryFailure, false)]
    [InlineData(AgentMessageType.Steer, true)]
    [InlineData(AgentMessageType.DelegateTask, true)]
    public void CurrentInstruction_QuotesOnlyDirectiveAgentMessages_OtherAgentChatterIsTailContent(
        AgentMessageType type,
        bool quoted
    )
    {
        // Only a Steer or a DelegateTask tells this agent what to do. A Response or TaskUpdate is content, like a
        // notification: quoting it whole in every checkpoint is how 3.6k tokens of chatter reached the envelope.
        var thread = new ThreadFixture()
            .Human("old task")
            .ToolTurns(2)
            .Run("run-2")
            .Human("new task")
            .ToolTurns(2)
            .Agent(type, "a message from another agent")
            .ToolTurns(3);
        var run2Human = ThreadFixture.TurnEnd(2) + 1;
        var agent = run2Human + (2 * 2) + 1;
        var candidate = agent + (2 * 2);

        var cut = ExpectCut(CutSelector.Select(thread.Request(candidate, ThreadFixture.Options(minTail: 10))));

        cut.Seq.Should().Be(candidate);
        long[] expected = quoted ? [run2Human, agent] : [run2Human];
        cut.CurrentInstruction.Select(r => r.Seq).Should().Equal(expected);
        CutSelector.CurrentInstructionRows(thread.Rows, cut.Seq).Select(r => r.Seq).Should().Equal(expected);
        thread.Rows.Single(r => r.Seq == agent).IsHumanRow.Should().Be(quoted);
    }

    [Fact]
    public void CurrentInstruction_ADelegateTaskOpeningAChildThread_AnchorsTheChildsCurrentRun()
    {
        // A child thread has no human row: the parent's DelegateTask is its instruction, and a Response it later
        // receives does not take that role.
        var thread = new ThreadFixture()
            .Run("child-run")
            .Agent(AgentMessageType.DelegateTask, "implement the parser")
            .ToolTurns(3)
            .Agent(AgentMessageType.Response, "the answer to your question")
            .ToolTurns(3);

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq - 2, ThreadFixture.Options(minTail: 10))));

        cut.CurrentRunId.Should().Be("child-run");
        cut.CurrentInstruction.Select(r => r.Seq).Should().Equal(1);
    }

    [Fact]
    public void R4_AHumanInjectionAfterAgentMessages_StillKeepsTheRunWhole()
    {
        var thread = new ThreadFixture()
            .Human("old task")
            .ToolTurns(2)
            .Run("run-2")
            .Human("new task")
            .ToolTurns(2)
            .AgentSteer("focus on the parser")
            .ToolTurns(2)
            .Human("no, the other file")
            .ToolTurns(3);
        var run1End = ThreadFixture.TurnEnd(2);

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq - 2, ThreadFixture.Options(minTail: 10))));

        cut.Seq.Should().Be(run1End, "a human correction still protects the run");
    }

    [Fact]
    public void R4_RunThatStartedAfterAnErroredRun_IsKeptWhole()
    {
        var thread = new ThreadFixture().Human("old task").ToolTurns(2).Run("run-2").Human("retry").ToolTurns(6);
        var run1End = ThreadFixture.TurnEnd(2);
        var candidate = run1End + 1 + (2 * 3);
        var ledger = new[]
        {
            new RunLedgerEntry("t", "run-1", RunStatus.Errored, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new RunLedgerEntry(
                "t",
                "run-2",
                RunStatus.InProgress,
                [],
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch
            ),
        };

        var cut = ExpectCut(
            CutSelector.Select(thread.Request(candidate, ThreadFixture.Options(minTail: 10), runs: ledger))
        );

        cut.Seq.Should().Be(run1End);
    }

    [Fact]
    public void R4_RunAfterARunCompactionRefusedForSize_IsNotKeptWhole()
    {
        // The errored run failed only because no view fit; protecting its retry would make the retry uncuttable.
        var thread = new ThreadFixture().Human("old task").ToolTurns(2).Run("run-2").Human("retry").ToolTurns(6);
        var run1End = ThreadFixture.TurnEnd(2);
        var candidate = run1End + 1 + (2 * 3);
        var ledger = new[]
        {
            new RunLedgerEntry("t", "run-1", RunStatus.Errored, [], DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            new RunLedgerEntry(
                "t",
                "run-2",
                RunStatus.InProgress,
                [],
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch
            ),
        };

        var cut = ExpectCut(
            CutSelector.Select(
                thread.Request(
                    candidate,
                    ThreadFixture.Options(minTail: 10) with
                    {
                        SizeRefusedRunIds = new HashSet<string>(StringComparer.Ordinal) { "run-1" },
                    },
                    runs: ledger
                )
            )
        );

        cut.Seq.Should().Be(candidate);
    }

    [Fact]
    public void R4_CorrectionOlderThanTheLookback_MayBeSplit()
    {
        var thread = new ThreadFixture()
            .Human("task one")
            .ToolTurns(2)
            .Human("correction")
            .ToolTurns(2)
            .Run("run-2")
            .Human("task two")
            .ToolTurns(2)
            .Run("run-3")
            .Human("task three")
            .ToolTurns(2);
        var insideRun1 = ThreadFixture.TurnEnd(1);

        var cut = ExpectCut(
            CutSelector.Select(thread.Request(insideRun1, ThreadFixture.Options(minTail: 10, lookback: 1)))
        );

        cut.Seq.Should().Be(insideRun1, "only the last run is protected when the lookback is one");
    }

    // ---- R6: cut-blocking state ---------------------------------------------------------------

    [Fact]
    public void R6_DeferredAskUserQuestionAtTurn30_RefusesTurn60_AndMovesBeforeTurn30()
    {
        var thread = new ThreadFixture().Human("fix the flaky test").ToolTurns(100, deferredAt: 30);

        var cut = ExpectCut(CutSelector.Select(thread.Request(ThreadFixture.TurnEnd(60))));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(29), "the placeholder at turn 30 must stay in the tail");
        cut.CurrentInstruction.Should().ContainSingle().Which.Seq.Should().Be(1);
        cut.Recovery.Should().Be(new RecoveryStateAtCut());
    }

    [Fact]
    public void R6_ParkedWaitAtTurn30_RefusesTurn60_AndMovesBeforeTurn30()
    {
        var thread = new ThreadFixture()
            .Human("fix the flaky test")
            .ToolTurns(100, deferredAt: 30, deferredTool: "Wait");

        var cut = ExpectCut(CutSelector.Select(thread.Request(ThreadFixture.TurnEnd(60))));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(29));
        cut.CurrentInstruction.Should().ContainSingle().Which.Seq.Should().Be(1);
        cut.Recovery.Should().Be(new RecoveryStateAtCut());
    }

    [Fact]
    public void R6_DeferredRowInEveryEarlierTurn_LeavesNoSafeBoundary()
    {
        // The first row of the thread is the deferred call: no boundary exists before it.
        var thread = new ThreadFixture().ToolTurns(3, deferredAt: 1);

        var skipped = ExpectSkipped(
            CutSelector.Select(thread.Request(thread.LastSeq, Short)),
            CompactionReasons.NoSafeBoundary
        );

        skipped.Recovery.DeferredToolCalls.Should().Be(1);
    }

    [Fact]
    public void R6_OwedContinuation_RefusesTheCut()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10);

        var skipped = ExpectSkipped(
            CutSelector.Select(thread.Request(thread.LastSeq, loopState: new CutBlockingState(OwedContinuations: 1))),
            CompactionReasons.UnsafeState
        );

        skipped.Recovery.OwedContinuations.Should().Be(1);
    }

    [Fact]
    public void R6_InterruptedTurn_RefusesTheCut()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10);

        var skipped = ExpectSkipped(
            CutSelector.Select(thread.Request(thread.LastSeq, loopState: new CutBlockingState(InterruptedTurns: 1))),
            CompactionReasons.UnsafeState
        );

        skipped.Recovery.InterruptedTurns.Should().Be(1);
    }

    [Fact]
    public void Observe_CountsDeferredAndParkedRows_ByTool()
    {
        // Asymmetric on purpose: one of each counts 1/1 whichever counter each branch increments.
        var thread = new ThreadFixture()
            .Human("go")
            .ToolTurn(tool: "AskUserQuestion", deferred: true)
            .ToolTurn(tool: "AskUserQuestion", deferred: true)
            .ToolTurn(tool: "Wait", deferred: true)
            .ToolTurn();

        var recovery = CutSelector.Observe(thread.Rows, CutBlockingState.Clean, upToSeq: long.MaxValue);

        recovery.DeferredToolCalls.Should().Be(2);
        recovery.ParkedWaits.Should().Be(1);
    }

    // ---- R7: size ceiling is a preference -----------------------------------------------------

    [Fact]
    public void R7_TailAboveMaxTail_IsReportedNotEnforced()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(20);
        var options = ThreadFixture.Options(minTail: 100, maxTail: 50);

        var cut = ExpectCut(CutSelector.Select(thread.Request(thread.LastSeq, options)));

        cut.TailTokens.Should().Be(100);
        cut.ExceedsMaxTail.Should().BeTrue();
    }

    // ---- Boundaries of the search -------------------------------------------------------------

    [Fact]
    public void ActiveBoundary_IsAFloor_TheCutNeverMovesAtOrBelowIt()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10);

        ExpectSkipped(
            CutSelector.Select(
                thread.Request(ThreadFixture.TurnEnd(4), Short, activeBoundarySeq: ThreadFixture.TurnEnd(6))
            ),
            CompactionReasons.NoSafeBoundary
        );
    }

    [Fact]
    public void RightAfterACheckpoint_EveryCandidateInTheKeptTail_IsSkippedAsTailOnly()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10);

        var skipped = ExpectSkipped(
            CutSelector.Select(
                thread.Request(
                    thread.LastSeq,
                    ThreadFixture.Options(minTail: 100),
                    activeBoundarySeq: ThreadFixture.TurnEnd(8)
                )
            ),
            CompactionReasons.NoSafeBoundary
        );

        skipped.TailOnly.Should().BeTrue("R3 keeps every row past the boundary and nothing else refused a cut");
    }

    [Fact]
    public void AnOpenToolCallPastTheBoundary_IsNotTailOnly()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10).ToolCall();

        var skipped = ExpectSkipped(
            CutSelector.Select(
                thread.Request(
                    thread.LastSeq,
                    ThreadFixture.Options(minTail: 100),
                    activeBoundarySeq: ThreadFixture.TurnEnd(8)
                )
            ),
            CompactionReasons.NoSafeBoundary
        );

        skipped.TailOnly.Should().BeFalse("a call still waiting for its result blocks the cut right now");
    }

    [Fact]
    public void ADeferredRowPastTheBoundary_IsNotTailOnly()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(10, deferredAt: 10);

        var skipped = ExpectSkipped(
            CutSelector.Select(
                thread.Request(
                    thread.LastSeq,
                    ThreadFixture.Options(minTail: 100),
                    activeBoundarySeq: ThreadFixture.TurnEnd(8)
                )
            ),
            CompactionReasons.NoSafeBoundary
        );

        skipped.TailOnly.Should().BeFalse("the deferred placeholder blocks the cut right now");
    }

    [Fact]
    public void AProtectedRunThatRefusesCutsTheTailFloorAllows_IsNotTailOnly()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(3).Human("no, the other file").ToolTurns(3);

        var skipped = ExpectSkipped(
            CutSelector.Select(thread.Request(thread.LastSeq, Short)),
            CompactionReasons.NoSafeBoundary
        );

        skipped.TailOnly.Should().BeFalse("R3 allows cuts inside the run; R4 refuses them");
    }

    [Fact]
    public void EmptyThread_HasNoSafeBoundary()
    {
        ExpectSkipped(CutSelector.Select(new ThreadFixture().Request(1)), CompactionReasons.NoSafeBoundary);
    }

    [Fact]
    public void CandidatePastTheLastRow_IsClampedToTheLastRow()
    {
        var thread = new ThreadFixture().Human("go").ToolTurns(3);

        var cut = ExpectCut(CutSelector.Select(thread.Request(999, ThreadFixture.Options(minTail: 10))));

        cut.Seq.Should().Be(ThreadFixture.TurnEnd(2), "a floor of ten tokens keeps one row");
    }

    [Fact]
    public void CurrentInstructionRows_AreRecomputedFromTheRows_NotTrusted()
    {
        var thread = new ThreadFixture().Human("start").ToolTurns(3).Human("also this").ToolTurns(3);

        var rows = CutSelector.CurrentInstructionRows(thread.Rows, ThreadFixture.TurnEnd(3) + 1);

        rows.Select(r => r.Seq).Should().Equal(1, ThreadFixture.TurnEnd(3) + 1);
        CutSelector.CurrentInstructionRows(thread.Rows, ThreadFixture.TurnEnd(3)).Select(r => r.Seq).Should().Equal(1);
    }

    private static CompactionCheckpointMessage CheckpointFor(CutDecision.Cut cut, ThreadFixture thread) =>
        new()
        {
            CheckpointId = "cp-test-1",
            Boundary = new CheckpointBoundary { Seq = cut.Seq, MessageId = thread.Rows[(int)cut.Seq - 1].MessageId! },
            Trigger = CompactionTrigger.Preemptive,
            Manifest = new ContextManifest
            {
                CurrentInstruction =
                [
                    .. cut.CurrentInstruction.Select(r => new QuotedItem { Seq = r.Seq, Quote = r.Text! }),
                ],
                Recovery = cut.Recovery,
            },
            Narrative = "test",
        };
}
