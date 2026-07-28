using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Models;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CodexSdkProvider.Models;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Agents;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.CopilotSdkProvider.Models;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmLifecycle.Payloads;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// The turn contract as the four real loops actually implement it: drive
/// <see cref="MultiTurnAgentLoop"/>, <see cref="ClaudeAgentLoop"/>, <see cref="CodexAgentLoop"/> and
/// <see cref="CopilotAgentLoop"/> with equivalent scripted provider streams and require them to
/// describe the resulting turn identically — one <c>run_started</c>, one <c>turn_completed</c>, one
/// <c>run_completed</c>, the same counts, the same ordinal, the same outcome — differing only in the
/// agent kind each one honestly reports as its own.
/// </summary>
/// <remarks>
/// <para>
/// <c>TurnLifecycleEmissionTests</c> pins the same promises at the seam every loop calls, using a
/// probe agent. This file is the other half of that argument: a seam that behaves correctly proves
/// nothing if a loop calls it in the wrong place, so everything here goes through a real loop and a
/// real provider stream, and asserts only on what a subscriber received.
/// </para>
/// <para>
/// "Equivalent scripted streams" means one assistant answer plus the provider's own usage report
/// plus whatever terminator that provider uses. The four wire formats look nothing alike — the raw
/// loop takes <see cref="IMessage"/> objects through its middleware pipeline, Claude takes SDK
/// messages ending in a <see cref="ResultEventMessage"/>, Codex takes <c>item.completed</c> /
/// <c>turn.completed</c> envelopes, Copilot takes <c>session/update</c> chunks closed by
/// <c>session/prompt/completed</c> — which is exactly why the events they publish have to look
/// alike.
/// </para>
/// </remarks>
public class LoopTurnContractTests
{
    private const string ThreadId = "loop-contract-thread";
    private const string ModelId = "the-model";
    private const string Answer = "the answer";
    private const string FirstFragment = "the ";
    private const string SecondFragment = "answer";
    private const int PromptTokens = 7;
    private const int CompletionTokens = 3;
    private const int TotalTokens = PromptTokens + CompletionTokens;
    private const string ProviderDied = "the provider went away";

    /// <summary>Which real loop a row exercises.</summary>
    /// <remarks>
    /// A serializable discriminator rather than a factory delegate: xUnit has to be able to name the
    /// row in a test explorer, and the four loops take four unrelated constructors that no single
    /// delegate shape covers honestly.
    /// </remarks>
    public enum Loop
    {
        /// <summary><see cref="MultiTurnAgentLoop"/>, driving a provider through the middleware pipeline.</summary>
        Raw,

        /// <summary><see cref="ClaudeAgentLoop"/>, driving the Claude Agent SDK.</summary>
        Claude,

        /// <summary><see cref="CodexAgentLoop"/>, driving the Codex CLI bridge.</summary>
        Codex,

        /// <summary><see cref="CopilotAgentLoop"/>, driving the Copilot CLI bridge.</summary>
        Copilot,
    }

    /// <summary>Every loop, paired with the agent kind it is required to report as its own.</summary>
    public static TheoryData<Loop, string> EveryLoopAndItsKind =>
        new()
        {
            { Loop.Raw, LifecycleAgentKinds.Raw },
            { Loop.Claude, LifecycleAgentKinds.Claude },
            { Loop.Codex, LifecycleAgentKinds.Codex },
            { Loop.Copilot, LifecycleAgentKinds.Copilot },
        };

    /// <summary>Every loop, for promises that do not turn on which one it is.</summary>
    public static TheoryData<Loop> EveryLoop =>
        [Loop.Raw, Loop.Claude, Loop.Codex, Loop.Copilot];

    #region One ordinary turn, described the same way by every loop

    [Theory]
    [MemberData(nameof(EveryLoopAndItsKind))]
    public async Task EveryLoopDescribesOneOrdinaryTurnTheSameWay(Loop loop, string expectedAgentKind)
    {
        var publisher = (await DriveAsync(loop, Script.WholeAnswer)).Events;

        RunAndTurnEventsOf(publisher)
            .Should()
            .Equal(
                [
                    LifecycleEventTypes.RunStarted,
                    LifecycleEventTypes.TurnCompleted,
                    LifecycleEventTypes.RunCompleted,
                ],
                "a subscriber pairs a start with a turn with a completion, in that order, whatever produced them");

        var started = Single<RunStartedPayload>(publisher, LifecycleEventTypes.RunStarted);
        var turn = Single<TurnCompletedPayload>(publisher, LifecycleEventTypes.TurnCompleted);
        var completed = Single<RunCompletedPayload>(publisher, LifecycleEventTypes.RunCompleted);

        started.AgentKind.Should().Be(
            expectedAgentKind,
            "the loop knows which implementation is running and is the only honest source for it");
        started.ModelId.Should().Be(ModelId);
        started.Cause.Kind.Should().Be(LifecycleRunCauseKinds.UserInput);
        started.WasForked.Should().BeFalse();

        turn.TurnIndex.Should().Be(1, "the first turn of a run is turn one on every loop");
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Completed);
        turn.MessageCount.Should().Be(
            2,
            "the equivalent script produced one answer and one usage report, however it was wired");
        turn.ToolCallCount.Should().Be(0);
        turn.Error.Should().BeNull("a turn that finished has nothing to explain");

        completed.Outcome.Should().Be(LifecycleRunOutcomes.Completed);
        completed.TurnCount.Should().Be(1);
        completed.Error.Should().BeNull();

        turn.RunId.Should().Be(started.RunId);
        completed.RunId.Should().Be(started.RunId);
        turn.GenerationId.Should().Be(
            started.GenerationId,
            "a run's first turn carries the generation id the run started with");
    }

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task EveryLoopStampsItsTurnEventWithTheThreadAndRunItBelongsTo(Loop loop)
    {
        var publisher = (await DriveAsync(loop, Script.WholeAnswer)).Events;

        var started = Single<RunStartedPayload>(publisher, LifecycleEventTypes.RunStarted);
        var correlation = publisher
            .CorrelationsFor(LifecycleEventTypes.TurnCompleted)
            .Should()
            .ContainSingle()
            .Subject;

        correlation.ThreadId.Should().Be(ThreadId);
        correlation.RunId.Should().Be(started.RunId);
        correlation.GenerationId.Should().Be(started.GenerationId);
        correlation.ParentRunId.Should().BeNull("a top-level run has no parent");
        correlation.SubAgentId.Should().BeNull();
    }

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task EveryLoopReportsTheTokenCountsItsProviderGaveIt(Loop loop)
    {
        var publisher = (await DriveAsync(loop, Script.WholeAnswer)).Events;

        var usage = Single<TurnCompletedPayload>(publisher, LifecycleEventTypes.TurnCompleted).Usage;

        usage.Should().NotBeNull("every scripted provider reported usage for its turn");
        usage!.PromptTokens.Should().Be(PromptTokens);
        usage.CompletionTokens.Should().Be(CompletionTokens);
        usage.TotalTokens.Should().Be(TotalTokens);
        usage.Completeness.Should().Be(
            LifecycleUsageCompleteness.Complete,
            "the one response the turn contained reported its usage");
    }

    #endregion

    #region Fragments describe how a message arrived, not what the turn produced

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task DeliveringTheSameAnswerInFragmentsChangesNothingTheTurnReports(Loop loop)
    {
        var whole = Single<TurnCompletedPayload>(
            (await DriveAsync(loop, Script.WholeAnswer)).Events,
            LifecycleEventTypes.TurnCompleted);

        var streamed = Single<TurnCompletedPayload>(
            (await DriveAsync(loop, Script.FragmentedAnswer)).Events,
            LifecycleEventTypes.TurnCompleted);

        streamed.MessageCount.Should().Be(
            whole.MessageCount,
            "deltas are how the answer arrived; the turn produced one answer either way");
        streamed.ToolCallCount.Should().Be(whole.ToolCallCount);
        streamed.TurnIndex.Should().Be(whole.TurnIndex);
        streamed.Outcome.Should().Be(whole.Outcome);
    }

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task AStreamedTurnIsStillReportedExactlyOnce(Loop loop)
    {
        var run = await DriveAsync(loop, Script.FragmentedAnswer);

        // Anti-vacuity guard: "fragments are not counted" is only worth asserting if fragments
        // actually flowed through the loop. Note this is the subscriber stream, not the finalizer's
        // — the raw loop publishes upstream of its joiner (MultiTurnAgentLoop.cs:253-258), so a
        // subscriber sees the deltas while the finalizer sees the message they were joined into.
        run.Streamed.OfType<TextUpdateMessage>().Should().NotBeEmpty(
            "a loop that silently dropped the deltas would satisfy the count below for the wrong reason");

        run.Events
            .Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted)
            .Should()
            .ContainSingle("a fragment is not a turn, so no number of them can produce a second event")
            .Which.GenerationId.Should()
            .Be(Single<RunStartedPayload>(run.Events, LifecycleEventTypes.RunStarted).GenerationId);
    }

    [Fact]
    public async Task ARunOfSeveralTurnsReportsEachTurnOnceUnderItsOwnOrdinal()
    {
        // Multi-turn is the raw loop's shape alone: Codex and Copilot are constructed with
        // maxTurnsPerRun: 1 because the CLI runs its own agentic loop behind one generation id, and
        // Claude opens exactly one turn per batch for the same reason.
        var publisher = new RecordingLifecyclePublisher();
        using var cts = new CancellationTokenSource();

        var agent = new Mock<IStreamingAgent>();
        var turnsRequested = 0;
        _ = agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(
                ++turnsRequested == 1
                    ? Replay(
                        [
                            new ToolCallMessage
                            {
                                FunctionName = "ping",
                                FunctionArgs = "{}",
                                ToolCallId = "tc_1",
                                Role = Role.Assistant,
                            },
                        ],
                        tail: null)
                    : Replay([Answered(), UsageReport()], tail: null)));

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            new FunctionContract
            {
                Name = "ping",
                Description = "Answers with pong.",
                Parameters = [],
            },
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("pong")));

        var loop = new MultiTurnAgentLoop(
            agent.Object,
            registry,
            ThreadId,
            defaultOptions: new GenerateReplyOptions { ModelId = ModelId },
            lifecycleServices: Bundle(publisher));

        _ = await DriveOneRunAsync(loop, Script.WholeAnswer, tail: null, cts);

        var turns = publisher.Payloads<TurnCompletedPayload>(LifecycleEventTypes.TurnCompleted);
        turns.Should().HaveCount(2, "the tool call bought a second turn and nothing bought a third");
        turns.Select(t => t.TurnIndex).Should().Equal(
            [1, 2],
            "turn ordinals are the subscriber's only way to order turns within a run");
        turns.Select(t => t.GenerationId).Distinct().Should().HaveCount(
            2,
            "each turn is its own generation, so a subscriber can key state by generation id");
        turns[0].ToolCallCount.Should().Be(1);
        turns[1].ToolCallCount.Should().Be(0);
        turns.Should().OnlyContain(t => t.Outcome == LifecycleTurnOutcomes.Completed);

        Single<RunCompletedPayload>(publisher, LifecycleEventTypes.RunCompleted)
            .TurnCount.Should()
            .Be(2);
    }

    #endregion

    #region A turn that ends badly is still reported once, with a stable outcome

    // A turn nobody reported is swept by the run's own terminalization and inherits the run's
    // outcome (RunTurnLifecycleFinalizer.TurnOutcomeForRun), so "the run reports the outcome of the
    // path it actually took" is what keeps these two promises true. A loop that terminalized every
    // path through one shared exit would tell a subscriber that a generation which died mid-stream
    // succeeded — which is why all four loops are held to this, not just the three that stream
    // through middleware.

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task ATurnThatDiesMidStreamIsReportedOnceCarryingTheRunsFailure(Loop loop)
    {
        var publisher = (await DriveAsync(loop, Script.DiesMidStream)).Events;

        var turn = Single<TurnCompletedPayload>(publisher, LifecycleEventTypes.TurnCompleted);
        var completed = Single<RunCompletedPayload>(publisher, LifecycleEventTypes.RunCompleted);

        turn.MessageCount.Should().Be(
            0,
            "only fragments had arrived, and a fragment is not a message the turn produced");
        turn.ToolCallCount.Should().Be(0);
        turn.TurnIndex.Should().Be(1);
        turn.Outcome.Should().Be(LifecycleTurnOutcomes.Error);
        turn.Error.Should().NotBeNull();
        turn.Error!.Message.Should().Contain(
            ProviderDied,
            "the turn carries the failure that ended it, not a placeholder");

        completed.Outcome.Should().Be(
            LifecycleRunOutcomes.Error,
            "the turn's outcome and the run's outcome must agree about what happened");
        completed.TurnCount.Should().Be(1, "the turn that failed still happened");
    }

    [Theory]
    [MemberData(nameof(EveryLoop))]
    public async Task ATurnCancelledMidStreamReportsOnlyItsCompleteMessages(Loop loop)
    {
        var publisher = (await DriveAsync(loop, Script.ParksUntilCancelled)).Events;

        var turn = Single<TurnCompletedPayload>(publisher, LifecycleEventTypes.TurnCompleted);

        turn.MessageCount.Should().Be(
            0,
            "the answer was still arriving in pieces when the cancellation landed");
        turn.ToolCallCount.Should().Be(0);
        turn.TurnIndex.Should().Be(1);
        turn.Outcome.Should().Be(
            LifecycleTurnOutcomes.Cancelled,
            "a cancelled turn has a stable outcome of its own — it is neither a success nor an error");

        Single<RunCompletedPayload>(publisher, LifecycleEventTypes.RunCompleted)
            .Outcome.Should()
            .Be(LifecycleRunOutcomes.Cancelled);
    }

    #endregion

    #region Driving the real loops

    /// <summary>What the scripted provider stream does, in the vocabulary all four loops share.</summary>
    private enum Script
    {
        /// <summary>One complete answer, then usage, then the provider's terminator.</summary>
        WholeAnswer,

        /// <summary>The same answer delivered as deltas first, then usage and the terminator.</summary>
        FragmentedAnswer,

        /// <summary>Deltas only, then the provider fails.</summary>
        DiesMidStream,

        /// <summary>Deltas only, then the provider stops producing until it is cancelled.</summary>
        ParksUntilCancelled,
    }

    /// <summary>How a scripted stream behaves once it has yielded everything it was given.</summary>
    private enum Ending
    {
        /// <summary>Ends the stream normally.</summary>
        Normal,

        /// <summary>Throws <see cref="ProviderDied"/>.</summary>
        Throws,

        /// <summary>Blocks until the run is cancelled.</summary>
        Parks,
    }

    /// <summary>
    /// What one scripted run produced: the lifecycle events a subscriber saw, and the messages the
    /// caller streamed out of the loop.
    /// </summary>
    /// <remarks>
    /// The streamed messages are what keeps the fragment assertions honest — without them, a loop
    /// that silently dropped the deltas would satisfy "fragments are not counted" for the wrong
    /// reason.
    /// </remarks>
    private sealed record DriveResult(
        RecordingLifecyclePublisher Events,
        IReadOnlyList<IMessage> Streamed);

    private static Task<DriveResult> DriveAsync(Loop loop, Script script) => loop switch
    {
        Loop.Raw => DriveRawAsync(script),
        Loop.Claude => DriveClaudeAsync(script),
        Loop.Codex => DriveCodexAsync(script),
        Loop.Copilot => DriveCopilotAsync(script),
        _ => throw new ArgumentOutOfRangeException(nameof(loop)),
    };

    private static async Task<DriveResult> DriveRawAsync(Script script)
    {
        var publisher = new RecordingLifecyclePublisher();
        using var cts = new CancellationTokenSource();
        var tail = new ScriptTail(EndingOf(script), cts.Token);

        var agent = new Mock<IStreamingAgent>();
        _ = agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(Replay(RawMessages(script), tail)));

        var loop = new MultiTurnAgentLoop(
            agent.Object,
            new FunctionRegistry(),
            ThreadId,
            defaultOptions: new GenerateReplyOptions { ModelId = ModelId },
            lifecycleServices: Bundle(publisher));

        return new DriveResult(publisher, await DriveOneRunAsync(loop, script, tail, cts));
    }

    private static async Task<DriveResult> DriveClaudeAsync(Script script)
    {
        var publisher = new RecordingLifecyclePublisher();
        using var cts = new CancellationTokenSource();
        var tail = new ScriptTail(EndingOf(script), cts.Token);

        var loop = new ClaudeAgentLoop(
            claudeOptions: new ClaudeAgentSdkOptions { Mode = ClaudeAgentSdkMode.Interactive },
            mcpServers: null,
            threadId: ThreadId,
            defaultOptions: new GenerateReplyOptions { ModelId = ModelId },
            clientFactory: (_, _) => new ScriptedClaudeClient(ClaudeMessages(script), tail),
            lifecycleServices: Bundle(publisher));

        return new DriveResult(publisher, await DriveOneRunAsync(loop, script, tail, cts));
    }

    private static async Task<DriveResult> DriveCodexAsync(Script script)
    {
        var publisher = new RecordingLifecyclePublisher();
        using var cts = new CancellationTokenSource();
        var tail = new ScriptTail(EndingOf(script), cts.Token);

        var loop = new CodexAgentLoop(
            new CodexSdkOptions { Model = ModelId },
            new Dictionary<string, CodexMcpServerConfig>(),
            functionRegistry: null,
            enabledTools: null,
            threadId: ThreadId,
            clientFactory: (_, _) => new ScriptedCodexClient(CodexEvents(script), tail),
            lifecycleServices: Bundle(publisher));

        return new DriveResult(publisher, await DriveOneRunAsync(loop, script, tail, cts));
    }

    private static async Task<DriveResult> DriveCopilotAsync(Script script)
    {
        var publisher = new RecordingLifecyclePublisher();
        using var cts = new CancellationTokenSource();
        var tail = new ScriptTail(EndingOf(script), cts.Token);

        var loop = new CopilotAgentLoop(
            new CopilotSdkOptions { Model = ModelId },
            threadId: ThreadId,
            clientFactory: (_, _) => new ScriptedCopilotClient(
                CopilotSessionId,
                CopilotEvents(script),
                tail),
            lifecycleServices: Bundle(publisher));

        return new DriveResult(publisher, await DriveOneRunAsync(loop, script, tail, cts));
    }

    /// <summary>
    /// Feeds one user input to a started loop, waits for the run to reach its terminal state, then
    /// stops and disposes the loop.
    /// </summary>
    /// <remarks>
    /// Two deterministic join points, no sleeping. A script that ends on its own is joined on
    /// <c>ExecuteRunAsync</c>, which returns at that run's own <see cref="RunCompletedMessage"/>. A
    /// script that parks never produces one, so it is joined on the stream itself reporting that it
    /// has emitted everything it will, and cancellation follows.
    /// </remarks>
    private static async Task<List<IMessage>> DriveOneRunAsync(
        MultiTurnAgentBase loop,
        Script script,
        ScriptTail? tail,
        CancellationTokenSource cts)
    {
        var streamed = new List<IMessage>();

        try
        {
            var runTask = loop.RunAsync(cts.Token);
            var input = new UserInput([new TextMessage { Text = "go", Role = Role.User }]);

            if (script == Script.ParksUntilCancelled)
            {
                // A parked run never reaches a RunCompletedMessage, so ExecuteRunAsync would never
                // return. Send the input instead and join on the stream having emitted everything
                // it is going to.
                _ = await loop.SendAsync([.. input.Messages], ct: cts.Token);
                await tail!.Reached.WaitAsync(TimeSpan.FromSeconds(20));
            }
            else
            {
                var drain = Task.Run(
                    async () =>
                    {
                        await foreach (var message in loop.ExecuteRunAsync(input, cts.Token))
                        {
                            streamed.Add(message);
                        }
                    },
                    cts.Token);

                await drain.WaitAsync(TimeSpan.FromSeconds(20));
            }

            await cts.CancelAsync();
            await SettleAsync(loop.StopAsync());
            await SettleAsync(runTask);

            return streamed;
        }
        finally
        {
            // Not `await using`: a loop whose provider threw leaves its run task faulted, and
            // disposal awaits that task again. The events were already published; who rethrows on
            // the way out is a separate question from what a subscriber saw.
            await SettleAsync(loop.DisposeAsync().AsTask());
        }
    }

    /// <summary>
    /// Awaits a teardown task, absorbing the scripted failure and the cancellation that the test
    /// deliberately caused.
    /// </summary>
    /// <remarks>
    /// Claude rethrows an unexpected provider exception out of its run loop, so it resurfaces from
    /// <c>StopAsync</c> when that awaits the loop task. These tests assert on what the loop
    /// published on its way out, not on who rethrows — but the task still has to be observed, or a
    /// faulted loop task outlives the test.
    /// </remarks>
    private static async Task SettleAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
            when (ex is InvalidOperationException or OperationCanceledException or TimeoutException)
        {
        }
    }

    private static MultiTurnLifecycleServices Bundle(RecordingLifecyclePublisher publisher) =>
        new() { Publisher = publisher, LifecycleStore = new InMemoryConversationStore() };

    private static Ending EndingOf(Script script) => script switch
    {
        Script.DiesMidStream => Ending.Throws,
        Script.ParksUntilCancelled => Ending.Parks,
        _ => Ending.Normal,
    };

    private static IReadOnlyList<string> RunAndTurnEventsOf(RecordingLifecyclePublisher publisher) =>
    [
        .. publisher.EventTypes.Where(type =>
            type is LifecycleEventTypes.RunStarted
                or LifecycleEventTypes.TurnCompleted
                or LifecycleEventTypes.RunCompleted),
    ];

    private static TPayload Single<TPayload>(RecordingLifecyclePublisher publisher, string eventType)
        where TPayload : class =>
        publisher.Payloads<TPayload>(eventType).Should().ContainSingle().Subject;

    #endregion

    #region The equivalent scripts, one wire format at a time

    private static TextMessage Answered() => new() { Text = Answer, Role = Role.Assistant };

    private static UsageMessage UsageReport() =>
        new()
        {
            Usage = new Usage
            {
                PromptTokens = PromptTokens,
                CompletionTokens = CompletionTokens,
                TotalTokens = TotalTokens,
            },
        };

    private static IReadOnlyList<IMessage> Deltas() =>
    [
        new TextUpdateMessage { Text = FirstFragment, Role = Role.Assistant },
        new TextUpdateMessage { Text = SecondFragment, Role = Role.Assistant },
    ];

    /// <summary>
    /// The raw loop's script. Its own <c>MessageUpdateJoinerMiddleware</c> turns the deltas back
    /// into the finalized <see cref="TextMessage"/>, which is why the streamed variant carries no
    /// complete text message of its own.
    /// </summary>
    private static IReadOnlyList<IMessage> RawMessages(Script script) => script switch
    {
        Script.WholeAnswer => [Answered(), UsageReport()],
        Script.FragmentedAnswer => [.. Deltas(), UsageReport()],
        _ => [.. Deltas()],
    };

    /// <summary>
    /// Claude's script. The SDK finalizes its own text, so the streamed variant carries the deltas
    /// and the complete message; <see cref="ResultEventMessage"/> is the terminator the loop reads.
    /// </summary>
    private static IReadOnlyList<IMessage> ClaudeMessages(Script script) => script switch
    {
        Script.WholeAnswer => [Answered(), UsageReport(), new ResultEventMessage { IsError = false }],
        Script.FragmentedAnswer =>
            [.. Deltas(), Answered(), UsageReport(), new ResultEventMessage { IsError = false }],
        _ => [.. Deltas()],
    };

    private const string CodexThreadStarted =
        """{"type":"thread.started","thread_id":"thread_loop_contract"}""";

    private static string CodexTurnCompleted() =>
        $$$"""
        {"type":"turn.completed","usage":{"input_tokens":{{{PromptTokens}}},"cached_input_tokens":0,"output_tokens":{{{CompletionTokens}}}}}
        """;

    private static IReadOnlyList<CodexTurnEventEnvelope> CodexEvents(Script script) => script switch
    {
        Script.WholeAnswer =>
        [
            CodexEvent("thread.started", CodexThreadStarted),
            CodexEvent("item.completed", CodexAgentMessage("item.completed", Answer)),
            CodexEvent("turn.completed", CodexTurnCompleted()),
        ],
        Script.FragmentedAnswer =>
        [
            CodexEvent("thread.started", CodexThreadStarted),
            CodexEvent("item.updated", CodexAgentMessage("item.updated", FirstFragment)),
            CodexEvent("item.updated", CodexAgentMessage("item.updated", Answer)),
            CodexEvent("item.completed", CodexAgentMessage("item.completed", Answer)),
            CodexEvent("turn.completed", CodexTurnCompleted()),
        ],
        _ =>
        [
            CodexEvent("thread.started", CodexThreadStarted),
            CodexEvent("item.updated", CodexAgentMessage("item.updated", FirstFragment)),
        ],
    };

    /// <summary>
    /// A Codex agent-message item. <c>item.updated</c> carries the cumulative snapshot the CLI
    /// sends, not the delta — the translator diffs it.
    /// </summary>
    private static string CodexAgentMessage(string eventType, string text) =>
        $$$"""
        {"type":"{{{eventType}}}","item":{"id":"msg_1","type":"agent_message","text":"{{{text}}}"}}
        """;

    private const string CopilotSessionId = "sess_loop_contract";

    private static string CopilotUsagePayload() =>
        $$$"""
        {"usage":{"inputTokens":{{{PromptTokens}}},"outputTokens":{{{CompletionTokens}}},"cachedInputTokens":0}}
        """;

    private static IReadOnlyList<CopilotTurnEventEnvelope> CopilotEvents(Script script) => script switch
    {
        // Copilot has no "whole answer" wire form: the CLI only ever streams chunks and consolidates
        // them at session/prompt/completed. One chunk is this provider's whole answer.
        Script.WholeAnswer =>
        [
            CopilotSessionUpdate(CopilotSessionId, CopilotChunk(Answer)),
            CopilotPromptCompletedEvent(CopilotUsagePayload()),
        ],
        Script.FragmentedAnswer =>
        [
            CopilotSessionUpdate(CopilotSessionId, CopilotChunk(FirstFragment)),
            CopilotSessionUpdate(CopilotSessionId, CopilotChunk(SecondFragment)),
            CopilotPromptCompletedEvent(CopilotUsagePayload()),
        ],
        _ => [CopilotSessionUpdate(CopilotSessionId, CopilotChunk(FirstFragment))],
    };

    private static string CopilotChunk(string text) =>
        $$$"""
        {"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"{{{text}}}"}}
        """;

    #endregion

    #region Scripted providers

    private static async IAsyncEnumerable<IMessage> Replay(
        IEnumerable<IMessage> messages,
        ScriptTail? tail,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }

        if (tail != null)
        {
            await tail.FinishAsync();
        }
    }

    /// <summary>
    /// What a scripted stream does after its last event: nothing, fail, or park until cancelled —
    /// plus a signal a test can join on, because a parked stream never reaches a run completion.
    /// </summary>
    private sealed class ScriptTail
    {
        private readonly TaskCompletionSource _reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly Ending _ending;
        private readonly CancellationToken _parkToken;

        public ScriptTail(Ending ending, CancellationToken parkToken)
        {
            _ending = ending;
            _parkToken = parkToken;
        }

        /// <summary>Completes once the stream has emitted everything it is going to emit.</summary>
        public Task Reached => _reached.Task;

        public async Task FinishAsync()
        {
            _ = _reached.TrySetResult();

            switch (_ending)
            {
                case Ending.Throws:
                    throw new InvalidOperationException(ProviderDied);

                case Ending.Parks:
                    await Task.Delay(Timeout.InfiniteTimeSpan, _parkToken);
                    break;

                case Ending.Normal:
                default:
                    break;
            }
        }
    }

    private static CodexTurnEventEnvelope CodexEvent(string name, string json)
    {
        var element = JsonDocument.Parse(json).RootElement.Clone();
        return new CodexTurnEventEnvelope
        {
            Type = name,
            Event = element,
            RequestId = Guid.NewGuid().ToString("N"),
            ThreadId = null,
        };
    }

    private static CopilotTurnEventEnvelope CopilotSessionUpdate(string sessionId, string updateJson)
    {
        using var updateDoc = JsonDocument.Parse(updateJson);
        var updateElement = updateDoc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session/update");
            writer.WriteString("sessionId", sessionId);
            writer.WritePropertyName("update");
            updateElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var envelopeDoc = JsonDocument.Parse(stream.ToArray());
        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = envelopeDoc.RootElement.Clone(),
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
        };
    }

    private static CopilotTurnEventEnvelope CopilotPromptCompletedEvent(string innerJson)
    {
        using var innerDoc = JsonDocument.Parse(innerJson);
        var inner = innerDoc.RootElement;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "session/prompt/completed");
            foreach (var property in inner.EnumerateObject())
            {
                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using var envelopeDoc = JsonDocument.Parse(stream.ToArray());
        return new CopilotTurnEventEnvelope
        {
            Type = "event",
            Event = envelopeDoc.RootElement.Clone(),
            RequestId = Guid.NewGuid().ToString("N"),
            SessionId = null,
        };
    }

    private sealed class ScriptedClaudeClient : IClaudeAgentSdkClient
    {
        private readonly IReadOnlyList<IMessage> _messages;
        private readonly ScriptTail _tail;

        public ScriptedClaudeClient(IReadOnlyList<IMessage> messages, ScriptTail tail)
        {
            _messages = messages;
            _tail = tail;
        }

        public bool IsRunning { get; private set; }

        public SessionInfo? CurrentSession { get; private set; }

        public ClaudeAgentSdkRequest? LastRequest { get; private set; }

        public Task StartAsync(ClaudeAgentSdkRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            IsRunning = true;
            return Task.CompletedTask;
        }

        public IAsyncEnumerable<IMessage> SendMessagesAsync(
            IEnumerable<IMessage> messages,
            CancellationToken cancellationToken = default) => StreamAsync(cancellationToken);

        public IAsyncEnumerable<IMessage> SubscribeToMessagesAsync(
            CancellationToken cancellationToken = default) => StreamAsync(cancellationToken);

        public Task SendAsync(IEnumerable<IMessage> messages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> SendExitCommandAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        public void Dispose() => IsRunning = false;

        private async IAsyncEnumerable<IMessage> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            CurrentSession = new SessionInfo
            {
                SessionId = "sess_loop_contract",
                CreatedAt = DateTime.UtcNow,
                ProjectRoot = "test",
            };

            foreach (var message in _messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
                await Task.Yield();
            }

            await _tail.FinishAsync();
        }
    }

    private sealed class ScriptedCodexClient : ICodexSdkClient
    {
        private readonly IReadOnlyList<CodexTurnEventEnvelope> _events;
        private readonly ScriptTail _tail;

        public ScriptedCodexClient(IReadOnlyList<CodexTurnEventEnvelope> events, ScriptTail tail)
        {
            _events = events;
            _tail = tail;
        }

        public string? CurrentCodexThreadId { get; private set; }

        public string? CurrentTurnId => null;

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CodexDynamicToolCallRequest, CancellationToken, Task<CodexDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeThreadAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCodexThreadId = options.ThreadId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CodexBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeThreadAsync(options, ct);

        public async IAsyncEnumerable<CodexTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }

            await _tail.FinishAsync();
        }

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ScriptedCopilotClient : ICopilotSdkClient
    {
        private readonly string _sessionId;
        private readonly IReadOnlyList<CopilotTurnEventEnvelope> _events;
        private readonly ScriptTail _tail;

        public ScriptedCopilotClient(
            string sessionId,
            IReadOnlyList<CopilotTurnEventEnvelope> events,
            ScriptTail tail)
        {
            _sessionId = sessionId;
            _events = events;
            _tail = tail;
        }

        public string? CurrentCopilotSessionId { get; private set; }

        public bool IsRunning { get; private set; }

        public string DependencyState => "ready";

        public void ConfigureDynamicToolExecutor(
            Func<CopilotDynamicToolCallRequest, CancellationToken, Task<CopilotDynamicToolCallResponse>>? executor)
        {
        }

        public Task StartOrResumeSessionAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
        {
            IsRunning = true;
            CurrentCopilotSessionId = _sessionId;
            return Task.CompletedTask;
        }

        public Task EnsureStartedAsync(CopilotBridgeInitOptions options, CancellationToken ct = default)
            => StartOrResumeSessionAsync(options, ct);

        public async IAsyncEnumerable<CopilotTurnEventEnvelope> RunStreamingAsync(
            string input,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var item in _events)
            {
                ct.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }

            await _tail.FinishAsync();
        }

        public Task InterruptTurnAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ShutdownAsync(TimeSpan? timeout = null, CancellationToken ct = default)
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    #endregion
}
