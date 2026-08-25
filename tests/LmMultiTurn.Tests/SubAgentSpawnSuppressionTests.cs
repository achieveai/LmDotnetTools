using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Covers <see cref="UserInput.SuppressSubAgentSpawning"/>: a single input can forbid the run it drives from
/// starting NEW sub-agents, without touching the thread's standing configuration. This is what lets a caller
/// that has just waited on a sub-agent completion barrier ask for a synthesis turn that cannot re-open the
/// barrier it waited on — while reading from and following up with the children it already has stays available.
/// </summary>
public class SubAgentSpawnSuppressionTests
{
    /// <summary>
    /// The guarantee is PER TURN: Agent disappears from the advertised contracts on the suppressed run and is
    /// back on the next one, and the read/follow-up tools are never withdrawn.
    /// </summary>
    [Fact]
    public async Task Suppressed_input_hides_the_Agent_tool_for_that_run_only()
    {
        var advertisedPerCall = new List<IReadOnlyList<string>>();
        var parent = TextOnlyParent(advertisedPerCall);

        await using var loop = CreateLoop(parent);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        await DrainAsync(loop, NewInput("first"), cts.Token);
        await DrainAsync(loop, NewInput("second", suppressSpawning: true), cts.Token);
        await DrainAsync(loop, NewInput("third"), cts.Token);

        advertisedPerCall.Should().HaveCount(3, "each run should have produced exactly one model call");

        advertisedPerCall[0].Should().Contain("Agent", "a normal turn may spawn sub-agents");
        advertisedPerCall[1].Should().NotContain(
            "Agent", "the suppressed input must not advertise a way to start new sub-agents");
        advertisedPerCall[2].Should().Contain(
            "Agent", "suppression is scoped to its own run and released afterwards");

        advertisedPerCall.Should().AllSatisfy(
            names => names.Should().Contain(["SendMessage", "CheckAgent"]),
            "reading from and following up with EXISTING sub-agents is never suppressed");

        await cts.CancelAsync();
    }

    /// <summary>
    /// Hiding the contract is not enough on its own: the model can replay an <c>Agent</c> call from earlier
    /// history. The handler must refuse it too, so the guarantee holds even then.
    /// </summary>
    [Fact]
    public async Task Suppressed_input_refuses_a_replayed_Agent_tool_call()
    {
        var spawnAttempts = 0;
        var parent = SpawnThenTextParent();

        await using var loop = CreateLoop(parent, onSpawn: () => spawnAttempts++);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(loop, NewInput("go", suppressSpawning: true), cts.Token);

        var results = messages.OfType<ToolCallResultMessage>().ToList();
        results.Should().Contain(
            r => r.ToolName == "Agent" && r.IsError && r.Result.Contains("not available for this turn"),
            "the Agent handler must refuse a replayed spawn on a suppressed run");
        spawnAttempts.Should().Be(0, "no sub-agent should have been started");

        await cts.CancelAsync();
    }

    /// <summary>
    /// The interleaving a caller cannot control: it only learns that the host QUEUED its message, never
    /// whether a run happened to be executing, so a flagged input routinely lands mid-run and is injected
    /// between turns. Deriving suppression from the batch that STARTED the run left exactly the turn the
    /// flagged input is asking about free to spawn — and the receipt had already promised otherwise.
    /// </summary>
    [Fact]
    public async Task Suppressed_input_injected_into_a_running_run_suppresses_the_next_turn()
    {
        var advertisedPerCall = new List<IReadOnlyList<string>>();
        MultiTurnAgentLoop? loop = null;
        SendReceipt? injectedReceipt = null;

        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            Contract("ping"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("pong")));

        // Turn 1 calls a tool (so the run continues to turn 2) and, while that turn is still executing,
        // the suppressed input is queued — exactly the race. Turn 2 then drains and injects it.
        var parent = RecordingParent(
            advertisedPerCall,
            turn =>
            {
                if (turn > 1)
                {
                    return [new TextMessage { Text = "answer", Role = Role.Assistant }];
                }

                injectedReceipt = loop!
                    .TrySendAsync(NewInput("synthesize", suppressSpawning: true))
                    .AsTask()
                    .GetAwaiter()
                    .GetResult();

                return
                [
                    new ToolCallMessage
                    {
                        FunctionName = "ping",
                        FunctionArgs = "{}",
                        ToolCallId = "call_ping_1",
                        Role = Role.Assistant,
                    },
                ];
            });

        await using var created = CreateLoop(parent, registry: registry);
        loop = created;
        using var cts = new CancellationTokenSource();
        _ = created.RunAsync(cts.Token);

        _ = await DrainAsync(created, NewInput("review"), cts.Token);

        advertisedPerCall.Should().HaveCount(2, "the tool call should have driven a second turn");
        advertisedPerCall[0].Should().Contain("Agent", "the run started unsuppressed");
        advertisedPerCall[1].Should().NotContain(
            "Agent",
            "the turn that first sees the injected input must already have lost the spawn tool");
        advertisedPerCall[1].Should().Contain(
            ["SendMessage", "CheckAgent"], "only spawning is suppressed");

        injectedReceipt.Should().NotBeNull();
        injectedReceipt!.SpawningSuppressed.Should().BeTrue(
            "this loop enforces the flag, so its receipt may promise the guarantee");

        // The latch is released with the run, but disposal happens on the producer's continuation
        // AFTER DrainAsync observes the terminal message — so a later turn getting the tool back is
        // not necessarily visible the instant the drain loop exits. Poll for it instead of asserting
        // immediately.
        await WaitForConditionAsync(
            () => created.SubAgentTools!.GetFunctions().Select(f => f.Contract.Name).Contains("Agent"),
            "the run-scoped suppression was disposed once the run ended, putting the Agent tool back",
            cts.Token);

        await cts.CancelAsync();
    }

    /// <summary>
    /// A suppressed run that pauses on a deferred tool call resumes as a NEW run (the loop always mints a
    /// fresh run id), so the latch has to be carried over deliberately. If it were not, the caller's
    /// guarantee would evaporate at the first deferral — precisely the turns where a barrier-following
    /// synthesis is most likely to be waiting on something.
    /// </summary>
    [Fact]
    public async Task Suppression_survives_a_deferral_pause_into_the_resumed_run()
    {
        var advertisedPerCall = new List<IReadOnlyList<string>>();
        var resumedTurnStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var parent = RecordingParent(
            advertisedPerCall,
            turn => turn == 1
                ? [DeferringToolCall()]

                // Signalled AFTER the resumed turn's contracts were recorded, so the assertion below
                // never races the model call it is about.
                : Answer(resumedTurnStarted));

        await using var loop = CreateLoop(parent, registry: DeferringRegistry());
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        // Run 1: suppressed, pauses on the deferral.
        _ = await DrainAsync(loop, NewInput("synthesize", suppressSpawning: true), cts.Token);

        // Resolving auto-resumes into run 2, which carries no input of its own.
        await loop.ResolveToolCallAsync("call_wait_1", "children settled");
        await resumedTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        advertisedPerCall.Should().HaveCount(2);
        advertisedPerCall.Should().AllSatisfy(
            names => names.Should().NotContain("Agent"),
            "the resumed run continues the suppressed run and must not regain the spawn tool");

        await cts.CancelAsync();
    }

    /// <summary>
    /// The latch owns a reference-counted provider scope, so a run that ends by THROWING has to release it
    /// just as a clean one does. A leaked scope would silently strip the spawn tool from every later turn on
    /// the thread — a suppression bug that only shows up as an agent that mysteriously stopped fanning out.
    /// </summary>
    [Fact]
    public async Task Suppression_is_released_when_the_run_fails()
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider rejected the request"));

        await using var loop = CreateLoop(mock.Object);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var messages = await DrainAsync(loop, NewInput("boom", suppressSpawning: true), cts.Token);
        messages.OfType<RunCompletedMessage>().Should().NotBeEmpty("the run must have finished, in error");

        // DrainAsync returns as soon as it observes the terminal RunCompletedMessage, but the producer
        // releases RunSpawnSuppression from a continuation that runs AFTER that terminal publish — an
        // immediate assertion here races that continuation and can observe the scope as not yet
        // disposed. Poll for it instead (production always releases it, just not synchronously with
        // the terminal message reaching this side of the channel).
        await WaitForConditionAsync(
            () => loop.SubAgentTools!.GetFunctions().Select(f => f.Contract.Name).Contains("Agent"),
            "the failed run released its suppression scope, putting the Agent tool back",
            cts.Token);

        await cts.CancelAsync();
    }

    /// <summary>
    /// The latch that carries suppression across a deferral pause is meaningless if it only lives in
    /// memory: the pause can last as long as whatever the deferral is waiting on, so a host restart in
    /// between is ordinary rather than exotic. The acknowledgement was already given to the caller, so the
    /// marker has to be durable next to the deferral it belongs to and be restored with it.
    /// </summary>
    [Fact]
    public async Task Suppression_survives_a_host_restart_into_the_resumed_run()
    {
        const string threadId = "spawn-suppression-restart";
        var store = new InMemoryConversationStore();
        using var cts = new CancellationTokenSource();

        var beforeRestart = new List<IReadOnlyList<string>>();
        await using (var first = CreateLoop(
            RecordingParent(beforeRestart, _ => [DeferringToolCall()]),
            registry: DeferringRegistry(),
            store: store,
            threadId: threadId))
        {
            _ = first.RunAsync(cts.Token);
            _ = await DrainAsync(first, NewInput("synthesize", suppressSpawning: true), cts.Token);

            // History is persisted fire-and-forget, so wait for the deferral itself to land: a restart
            // can only restore what the previous process actually wrote.
            await WaitForPersistedAsync(
                store,
                threadId,
                history => history.OfType<ToolCallResultMessage>().Any(r => r.IsDeferred),
                "the deferral was persisted, so a restart has something to restore",
                cts.Token);
        }

        beforeRestart.Should().ContainSingle().Which.Should().NotContain(
            "Agent", "the run that took the input was suppressed before the restart");

        // Restart: a brand-new loop over the same store, with nothing carried in memory.
        var afterRestart = new List<IReadOnlyList<string>>();
        var resumedTurnStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var second = CreateLoop(
            RecordingParent(afterRestart, _ => Answer(resumedTurnStarted)),
            registry: DeferringRegistry(),
            store: store,
            threadId: threadId);

        _ = await second.RecoverAsync(cts.Token);
        _ = second.RunAsync(cts.Token);

        // Precondition, not the subject: without the restored deferral there is no resumed run for the
        // suppression marker to apply to, and the assertion below would fail for the wrong reason.
        var restored = await second.GetDeferredToolCallsAsync(cts.Token);
        _ = restored.Should().ContainSingle(
            "the restart must restore the deferral the resume continues from");

        await second.ResolveToolCallAsync(DeferredToolCallId, "children settled");
        await resumedTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        afterRestart.Should().ContainSingle().Which.Should().NotContain(
            "Agent", "the acknowledged guarantee must outlive the process that made it");

        await cts.CancelAsync();
    }

    [Fact]
    public async Task Suppressed_notification_after_restart_is_persisted_under_the_restored_parked_run()
    {
        const string threadId = "spawn-suppression-parked-restart";
        var store = new InMemoryConversationStore();
        using var cts = new CancellationTokenSource();

        await using (var first = CreateLoop(
            RecordingParent([], _ => [DeferringToolCall()]),
            registry: DeferringRegistry(),
            store: store,
            threadId: threadId))
        {
            _ = first.RunAsync(cts.Token);
            _ = await DrainAsync(first, NewInput("review"), cts.Token);
            await WaitForPersistedAsync(
                store,
                threadId,
                history => history.OfType<ToolCallResultMessage>().Any(r => r.IsDeferred),
                "the deferral was persisted",
                cts.Token);
        }

        await using var second = CreateLoop(
            RecordingParent([], _ => [new TextMessage { Text = "unused", Role = Role.Assistant }]),
            registry: DeferringRegistry(),
            store: store,
            threadId: threadId);
        _ = await second.RecoverAsync(cts.Token);
        _ = second.RunAsync(cts.Token);

        var receipt = await second.TrySendAsync(
            new UserInput(
                [new NotifyMessage
                {
                    NotifyKind = NotifyKinds.SubAgentCompletion,
                    Label = "researcher",
                    Detail = "child settled",
                }],
                SuppressSubAgentSpawning: true),
            cts.Token);
        receipt!.SpawningSuppressed.Should().BeTrue();

        await WaitForPersistedAsync(
            store,
            threadId,
            history => history.OfType<NotifyMessage>().Any(),
            "the notification was persisted under the restored parked run",
            cts.Token);

        await cts.CancelAsync();
    }

    /// <summary>
    /// A notification that arrives while the conversation is parked on an unresolved deferral cannot start
    /// a turn, so it is folded into history and delivered by the RESUME. Suppression it asked for must ride
    /// along to that run: the receipt has already told the caller the guarantee holds, and there is no
    /// other run for it to apply to.
    /// </summary>
    [Fact]
    public async Task Suppressed_notification_parked_on_a_deferral_suppresses_the_run_that_delivers_it()
    {
        const string threadId = "spawn-suppression-parked";
        var store = new InMemoryConversationStore();
        var advertisedPerCall = new List<IReadOnlyList<string>>();
        var resumedTurnStarted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var parent = RecordingParent(
            advertisedPerCall,
            turn => turn == 1 ? [DeferringToolCall()] : Answer(resumedTurnStarted));

        await using var loop = CreateLoop(
            parent, registry: DeferringRegistry(), store: store, threadId: threadId);
        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        // Run 1 is NOT suppressed and parks on the deferral.
        _ = await DrainAsync(loop, NewInput("review"), cts.Token);
        advertisedPerCall.Should().ContainSingle().Which.Should().Contain(
            "Agent", "the parked run started unsuppressed");

        var receipt = await loop.TrySendAsync(
            new UserInput(
                [
                    new NotifyMessage
                    {
                        NotifyKind = NotifyKinds.SubAgentCompletion,
                        Label = "researcher",
                        Detail = "child settled",
                    },
                ],
                SuppressSubAgentSpawning: true),
            cts.Token);

        receipt!.SpawningSuppressed.Should().BeTrue(
            "this loop enforces the flag, so accepting the input acknowledged the guarantee");

        // The fold is observable through the store: it persists the notification under the parked run.
        await WaitForPersistedAsync(
            store,
            threadId,
            history => history.OfType<NotifyMessage>().Any(),
            "the parked notification was folded into persisted history",
            cts.Token);

        await loop.ResolveToolCallAsync(DeferredToolCallId, "children settled");
        await resumedTurnStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);

        advertisedPerCall.Should().HaveCount(2);
        advertisedPerCall[1].Should().NotContain(
            "Agent",
            "the run that finally delivers the parked notification is the run its receipt was about");

        await cts.CancelAsync();
    }

    /// <summary>
    /// The receipt reports ENFORCEMENT, not the request. A host relays it to a caller as a guarantee, so an
    /// agent with no suppression machinery must leave it false rather than echo what it was asked for.
    /// </summary>
    [Fact]
    public async Task Receipt_reports_suppression_only_when_it_was_actually_requested()
    {
        var parent = TextOnlyParent([]);
        await using var loop = CreateLoop(parent);

        var plain = await loop.TrySendAsync(NewInput("plain"));
        var suppressed = await loop.TrySendAsync(NewInput("suppressed", suppressSpawning: true));

        plain!.SpawningSuppressed.Should().BeFalse("nothing was asked for, so nothing is promised");
        suppressed!.SpawningSuppressed.Should().BeTrue();
    }

    /// <summary>
    /// <c>SendAsync</c> enqueues the very same <see cref="UserInput"/> into the very same run loop, so its
    /// receipt has to make the same enforcement statement. Leaving it unstamped there would tell a caller
    /// on that path that the guarantee is unavailable when it is in fact being enforced.
    /// </summary>
    [Fact]
    public async Task SendAsync_makes_the_same_enforcement_statement_as_TrySendAsync()
    {
        var parent = TextOnlyParent([]);
        await using var loop = CreateLoop(parent);

        var plain = await loop.SendAsync(NewInput("plain"));
        var suppressed = await loop.SendAsync(NewInput("suppressed", suppressSpawning: true));

        plain.SpawningSuppressed.Should().BeFalse("nothing was asked for, so nothing is promised");
        suppressed.SpawningSuppressed.Should().BeTrue();
    }

    #region Helpers

    /// <summary>Tool call id of the deferring call issued by <see cref="DeferringToolCall"/>.</summary>
    private const string DeferredToolCallId = "call_wait_1";

    private static UserInput NewInput(string text, bool suppressSpawning = false) =>
        new(
            [new TextMessage { Text = text, Role = Role.User }],
            SuppressSubAgentSpawning: suppressSpawning);

    /// <summary>A registry whose single tool always defers, so a turn calling it parks the run.</summary>
    private static FunctionRegistry DeferringRegistry()
    {
        var registry = new FunctionRegistry();
        _ = registry.AddFunction(
            Contract("wait_for_children"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));
        return registry;
    }

    /// <summary>The model turn that calls <see cref="DeferringRegistry"/>'s deferring tool.</summary>
    private static ToolCallMessage DeferringToolCall() => new()
    {
        FunctionName = "wait_for_children",
        FunctionArgs = "{}",
        ToolCallId = DeferredToolCallId,
        Role = Role.Assistant,
    };

    /// <summary>
    /// A plain-text answer that signals <paramref name="turnStarted"/> as it is produced — i.e. after the
    /// turn's contracts have already been recorded, so a test never races the model call it asserts on.
    /// </summary>
    private static List<IMessage> Answer(TaskCompletionSource<bool> turnStarted)
    {
        _ = turnStarted.TrySetResult(true);
        return [new TextMessage { Text = "answer", Role = Role.Assistant }];
    }

    /// <summary>
    /// Waits until persisted history satisfies <paramref name="condition"/>. <c>AddToHistory</c> persists
    /// fire-and-forget, so anything a test asserts about what a RESTART — or a run parked between turns —
    /// can see has to wait for the write itself, not merely for the run that scheduled it.
    /// </summary>
    /// <remarks>
    /// The private deadline loop this replaced tested the deadline BEFORE each evaluation, so a write
    /// that landed during the final sleep produced a timeout with the condition already true.
    /// <see cref="Wait"/> evaluates first and checks the deadline afterwards, which removes that
    /// window — it matters here because these waits run under solution-wide parallel load, where a
    /// 20ms delay routinely takes ten times that (#343).
    /// </remarks>
    private static Task WaitForPersistedAsync(
        IConversationStore store,
        string threadId,
        Func<IReadOnlyList<IMessage>, bool> condition,
        string because,
        CancellationToken ct)
    {
        return Wait.UntilAsync(
            async () => condition(
                MessagePersistenceConverter.FromPersistedMessages(
                    await store.LoadMessagesAsync(threadId, ct))),
            because,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            ct);
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> is true. Mirrors <see cref="WaitForPersistedAsync"/>'s
    /// bounded-poll shape for the same reason: a terminal message reaching <c>DrainAsync</c> and the
    /// producer's continuation that releases <c>RunSpawnSuppression</c> are two separate,
    /// unsynchronized events, so anything asserting on the disposal that continuation performs has to
    /// poll for it rather than check once immediately after the terminal message is observed.
    /// </summary>
    private static Task WaitForConditionAsync(
        Func<bool> condition,
        string because,
        CancellationToken ct)
    {
        return Wait.UntilAsync(
            condition,
            because,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromMilliseconds(20),
            ct);
    }

    private static MultiTurnAgentLoop CreateLoop(
        IStreamingAgent parent,
        Action? onSpawn = null,
        FunctionRegistry? registry = null,
        IConversationStore? store = null,
        string threadId = "spawn-suppression-thread")
    {
        var subAgent = new Mock<IStreamingAgent>();
        _ = subAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([
                new TextMessage { Text = "child done", Role = Role.Assistant },
            ])));

        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["researcher"] = new()
                {
                    Name = "researcher",
                    SystemPrompt = "You are a research assistant.",
                    AgentFactory = () =>
                    {
                        onSpawn?.Invoke();
                        return subAgent.Object;
                    },
                },
            },
            MaxConcurrentSubAgents = 3,
        };

        return new MultiTurnAgentLoop(
            parent,
            registry ?? new FunctionRegistry(),
            threadId: threadId,
            store: store,
            subAgentOptions: options);
    }

    /// <summary>A parent that answers with plain text, recording the tool names it was offered each call.</summary>
    private static IStreamingAgent TextOnlyParent(List<IReadOnlyList<string>> advertisedPerCall) =>
        RecordingParent(
            advertisedPerCall,
            _ => [new TextMessage { Text = "answer", Role = Role.Assistant }]);

    /// <summary>
    /// A parent that records the tool names it was offered on every call and replies with whatever
    /// <paramref name="reply"/> returns for that 1-based turn number. Turn-aware so a test can drive a
    /// multi-turn run (tool call, then text) and assert the CONTRACTS turn by turn — which is where
    /// suppression is observable.
    /// <para>
    /// Replies are stamped with the run identity the loop advertised, because that is what every real
    /// provider does (<see cref="MessageExtensions.WithIds(IMessage, GenerateReplyOptions?)"/>) and what
    /// persisted history therefore carries. Without it a restart restores messages with no run or
    /// generation id and can never resume.
    /// </para>
    /// </summary>
    private static IStreamingAgent RecordingParent(
        List<IReadOnlyList<string>> advertisedPerCall,
        Func<int, List<IMessage>> reply)
    {
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, options, _) =>
                {
                    int turn;
                    lock (advertisedPerCall)
                    {
                        advertisedPerCall.Add(options?.Functions?.Select(f => f.Name).ToList() ?? []);
                        turn = advertisedPerCall.Count;
                    }

                    return Task.FromResult(ToAsyncEnumerable(
                        [.. reply(turn).Select(m => m.WithIds(options))]));
                });
        return mock.Object;
    }

    private static FunctionContract Contract(string name) => new()
    {
        Name = name,
        Description = $"Test contract for {name}",
        Parameters = [],
    };

    /// <summary>A parent that calls <c>Agent</c> on its first turn (as if replaying history) then answers.</summary>
    private static IStreamingAgent SpawnThenTextParent()
    {
        var calls = 0;
        var mock = new Mock<IStreamingAgent>();
        _ = mock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
                        ? ToAsyncEnumerable([
                            new ToolCallMessage
                            {
                                FunctionName = "Agent",
                                FunctionArgs = JsonSerializer.Serialize(new
                                {
                                    subagent_type = "researcher",
                                    prompt = "Research the topic",
                                }),
                                ToolCallId = "call_agent_1",
                                Role = Role.Assistant,
                            },
                        ])
                        : ToAsyncEnumerable([
                            new TextMessage { Text = "answer", Role = Role.Assistant },
                        ])));
        return mock.Object;
    }

    private static async Task<List<IMessage>> DrainAsync(
        MultiTurnAgentLoop loop,
        UserInput input,
        CancellationToken ct)
    {
        var messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(input, ct))
        {
            messages.Add(msg);
        }

        return messages;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    #endregion
}
