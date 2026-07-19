using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// WI #194 tasks 5-6: the agent-wide publication observer hooked at
/// <see cref="MultiTurnAgentBase.PublishToAllAsync(IMessage, CancellationToken)"/>. Proves an optional
/// <see cref="IAgentPublicationObserver"/> receives every publication (streamed provider
/// frames, <see cref="RunAssignmentMessage"/>, <see cref="NotifyMessage"/>, tool results, the
/// deferred-replacement marker, and the <see cref="RunCompletedMessage"/> terminal) with the SAME
/// <see cref="IMessage"/> instances and order v1 subscribers see, and that an observer failure
/// propagates to the publishing caller rather than being swallowed.
/// </summary>
public class AgentPublicationObserverTests
{
    #region Recording observer test double

    /// <summary>
    /// Records every publication it observes (thread-safe — production publishes can race across
    /// concurrent tool executions). Optionally throws for a publication matched by
    /// <see cref="_throwPredicate"/>, to prove failure propagation without swallowing.
    /// </summary>
    private sealed class RecordingObserver : IAgentPublicationObserver
    {
        private readonly object _lock = new();
        private readonly List<AgentPublication> _received = [];
        private readonly Func<AgentPublication, Exception?>? _throwPredicate;

        public RecordingObserver(Func<AgentPublication, Exception?>? throwPredicate = null)
        {
            _throwPredicate = throwPredicate;
        }

        public IReadOnlyList<AgentPublication> Received
        {
            get
            {
                lock (_lock)
                {
                    return [.. _received];
                }
            }
        }

        public ValueTask OnPublishedAsync(AgentPublication publication, CancellationToken ct)
        {
            var toThrow = _throwPredicate?.Invoke(publication);
            if (toThrow != null)
            {
                throw toThrow;
            }

            lock (_lock)
            {
                _received.Add(publication);
            }

            return ValueTask.CompletedTask;
        }
    }

    #endregion

    #region Minimal test double exposing PublishToAllAsync directly

    /// <summary>
    /// Minimal concrete <see cref="MultiTurnAgentBase"/> that never runs its own loop — messages
    /// are published directly via <see cref="PublishForTest(IMessage, CancellationToken)"/> so unit tests can exercise the
    /// observer hook without a provider/middleware pipeline.
    /// </summary>
    private sealed class ObserverTestAgent : MultiTurnAgentBase
    {
        public ObserverTestAgent(string threadId, IAgentPublicationObserver? publicationObserver = null)
            : base(threadId, publicationObserver: publicationObserver)
        {
        }

        protected override Task RunLoopAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask PublishForTest(IMessage message, CancellationToken ct = default) =>
            PublishToAllAsync(message, ct);

        public ValueTask PublishForTest(IMessage message, AgentPublicationKind kind, CancellationToken ct = default) =>
            PublishToAllAsync(message, kind, ct);
    }

    private static RunAssignmentMessage Assignment(string threadId, string runId, string genId) =>
        new() { Assignment = new RunAssignment(runId, genId), ThreadId = threadId };

    #endregion

    #region Unit-level: kind inference, explicit override, v1 parity, exception propagation

    [Fact]
    public async Task PublishToAllAsync_ObserverConfigured_ReceivesSameInstancesAndOrder_AsV1Subscriber_WithInferredKinds()
    {
        var observer = new RecordingObserver();
        await using var agent = new ObserverTestAgent("thread-unit-1", observer);

        const string runId = "run-1";
        const string genId = "gen-1";
        var assignment = Assignment("thread-unit-1", runId, genId);
        var textMsg = new TextMessage { Text = "hello", Role = Role.Assistant, RunId = runId, GenerationId = genId };

        // Published while the run's replay buffer is open (assignment -> text), so a subscriber
        // that joins AFTER these two — but before the terminal — replays them, exactly like a
        // reconnecting client. This lets the test avoid any subscribe/publish race.
        await agent.PublishForTest(assignment);
        await agent.PublishForTest(textMsg);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(assignment);
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(textMsg);

        var terminal = new RunCompletedMessage { CompletedRunId = runId, ThreadId = "thread-unit-1", GenerationId = genId };
        await agent.PublishForTest(terminal);

        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(terminal);

        // The observer saw the SAME three instances, in the SAME order, with kinds correctly
        // inferred purely from message type (no explicit kind was passed at any call site above).
        var received = observer.Received;
        received.Should().HaveCount(3);
        received[0].Message.Should().BeSameAs(assignment);
        received[0].Kind.Should().Be(AgentPublicationKind.RunAssignment);
        received[0].ThreadId.Should().Be("thread-unit-1");
        received[1].Message.Should().BeSameAs(textMsg);
        received[1].Kind.Should().Be(AgentPublicationKind.Message);
        received[2].Message.Should().BeSameAs(terminal);
        received[2].Kind.Should().Be(AgentPublicationKind.RunTerminal);
    }

    [Fact]
    public async Task PublishToAllAsync_ExplicitReplacementKind_OverridesInference()
    {
        var observer = new RecordingObserver();
        await using var agent = new ObserverTestAgent("thread-unit-2", observer);

        // A ToolCallResultMessage would otherwise infer as Message — the explicit-kind overload
        // (as used by MultiTurnAgentLoop.ResolveToolCallAsync for a deferred replacement) must win.
        var replacement = new ToolCallResultMessage
        {
            ToolCallId = "tc_1",
            Result = "resolved",
            Role = Role.Tool,
        };

        await agent.PublishForTest(replacement, AgentPublicationKind.Replacement);

        var received = observer.Received;
        received.Should().ContainSingle();
        received[0].Message.Should().BeSameAs(replacement);
        received[0].Kind.Should().Be(AgentPublicationKind.Replacement);
    }

    [Fact]
    public async Task PublishToAllAsync_NoObserverConfigured_V1SubscriberStillReceivesMessage()
    {
        // Regression guard: an agent with no observer configured must behave exactly as before —
        // this is the default (existing) construction path, exercised by every other test file.
        await using var agent = new ObserverTestAgent("thread-unit-3", publicationObserver: null);

        var msg = new TextMessage { Text = "hi", Role = Role.Assistant };
        await agent.PublishForTest(Assignment("thread-unit-3", "run-x", "gen-x"));
        await agent.PublishForTest(msg);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var subscriber = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeOfType<RunAssignmentMessage>();
        (await subscriber.MoveNextAsync()).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(msg);
    }

    [Fact]
    public async Task PublishToAllAsync_ObserverThrows_ExceptionPropagatesToCaller_ButV1FanOutAlreadyDelivered()
    {
        const string runId = "run-throw";
        const string genId = "gen-throw";
        var boom = new InvalidOperationException("observer boom");
        var textMsg = new TextMessage { Text = "will still reach v1", Role = Role.Assistant, RunId = runId, GenerationId = genId };

        var observer = new RecordingObserver(throwPredicate: pub => ReferenceEquals(pub.Message, textMsg) ? boom : null);
        await using var agent = new ObserverTestAgent("thread-unit-4", observer);

        // Open the replay buffer first (no throw for the assignment) and subscribe immediately
        // afterwards — SubscribeAsync registers the subscriber synchronously (before any await),
        // so calling (without yet awaiting) MoveNextAsync guarantees registration happens-before
        // the live publish below.
        var assignment = Assignment("thread-unit-4", runId, genId);
        await agent.PublishForTest(assignment);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var subscriber = agent.SubscribeAsync(cts.Token).GetAsyncEnumerator(cts.Token);
        var firstMoveNext = subscriber.MoveNextAsync();
        (await firstMoveNext).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(assignment);

        var secondMoveNext = subscriber.MoveNextAsync();

        var publishAct = async () => await agent.PublishForTest(textMsg);

        // The exception thrown by the observer propagates to the PUBLISHING CALLER (the run),
        // rather than being swallowed.
        (await publishAct.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);

        // Despite the observer throwing, the non-blocking v1 fan-out already delivered the SAME
        // message instance to the live subscriber — v1 behavior is completely unaffected.
        (await secondMoveNext).Should().BeTrue();
        subscriber.Current.Should().BeSameAs(textMsg);

        await subscriber.DisposeAsync();
    }

    #endregion

    #region Integration-level: MultiTurnAgentLoop + MessagePublishingMiddleware end-to-end

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }

    private static FunctionContract BuildContract(string name) => new()
    {
        Name = name,
        Description = $"Test contract for {name}",
        Parameters = [],
    };

    [Fact]
    public async Task MultiTurnAgentLoop_FullRun_ObserverReceivesFramesAssignmentToolResultAndTerminal_MatchingV1SubscriberOrder()
    {
        var mockAgent = new Mock<IStreamingAgent>();
        var reasoning = new ReasoningMessage { Reasoning = "thinking about it", Role = Role.Assistant };
        var text = new TextMessage { Text = "Let me check that.", Role = Role.Assistant };
        var toolCall = new ToolCallMessage
        {
            FunctionName = "get_status",
            FunctionArgs = "{}",
            ToolCallId = "tc_status",
            Role = Role.Assistant,
        };
        var finalText = new TextMessage { Text = "All good.", Role = Role.Assistant };

        var callCount = 0;
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([reasoning, text, toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([finalText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("get_status"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")));

        var observer = new RecordingObserver();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "thread-loop-1",
            publicationObserver: observer);

        using var cts = new CancellationTokenSource();
        var runTask = loop.RunAsync(cts.Token);

        var userInput = new UserInput([new TextMessage { Text = "Check status", Role = Role.User }], InputId: "in-1");
        var v1Messages = new List<IMessage>();
        await foreach (var msg in loop.ExecuteRunAsync(userInput, cts.Token))
        {
            v1Messages.Add(msg);
        }

        await cts.CancelAsync();

        // Provider-streamed text/reasoning frames, the tool call, its (non-deferred) result, the
        // run assignment, and the terminal all reached the observer — same instances, same order,
        // exactly as v1's subscriber (ExecuteRunAsync) received them.
        var observed = observer.Received;
        observed.Should().HaveCount(v1Messages.Count);
        for (var i = 0; i < v1Messages.Count; i++)
        {
            observed[i].Message.Should().BeSameAs(v1Messages[i], $"observer message #{i} must be the SAME instance v1 subscribers received");
        }

        observed.Should().ContainSingle(p => p.Kind == AgentPublicationKind.RunAssignment && IsRunAssignment(p.Message));
        observed.Should().ContainSingle(p => ReferenceEquals(p.Message, reasoning) && p.Kind == AgentPublicationKind.Message);
        observed.Should().ContainSingle(p => ReferenceEquals(p.Message, text) && p.Kind == AgentPublicationKind.Message);
        observed.Should().ContainSingle(p => ReferenceEquals(p.Message, toolCall) && p.Kind == AgentPublicationKind.Message);
        observed.Should().ContainSingle(p =>
            IsNonDeferredToolResultFor(p.Message, "tc_status") && p.Kind == AgentPublicationKind.Message);
        observed.Should().ContainSingle(p => ReferenceEquals(p.Message, finalText) && p.Kind == AgentPublicationKind.Message);
        observed.Should().ContainSingle(p => p.Kind == AgentPublicationKind.RunTerminal && IsRunCompleted(p.Message));
        observed.Should().OnlyContain(p => p.ThreadId == "thread-loop-1");
    }

    [Fact]
    public async Task MultiTurnAgentLoop_NotifyMessage_ReachesObserver_WithMessageKind_AndPreservedIdentity()
    {
        var mockAgent = new Mock<IStreamingAgent>();
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable([new TextMessage { Text = "ack", Role = Role.Assistant }])));

        var observer = new RecordingObserver();
        var registry = new FunctionRegistry();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "thread-loop-notify",
            publicationObserver: observer);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var subscribed = new TaskCompletionSource();
        var completed = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            subscribed.SetResult();
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                if (msg is RunCompletedMessage)
                {
                    completed.TrySetResult();
                }
            }
        }, cts.Token);
        await subscribed.Task;

        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "sub-agent done",
            generationId: "notify:fixed");
        await loop.SendAsync([notify]);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        observer.Received.Should().ContainSingle(p => ReferenceEquals(p.Message, notify) && p.Kind == AgentPublicationKind.Message);

        await cts.CancelAsync();
    }

    [Fact]
    public async Task MultiTurnAgentLoop_ResolveToolCallAsync_DeferredReplacement_ReachesObserver_MarkedReplacement()
    {
        var toolCall = new ToolCallMessage
        {
            FunctionName = "long_running_op",
            FunctionArgs = "{}",
            ToolCallId = "tc_long",
            Role = Role.Assistant,
        };
        var finalAssistantText = new TextMessage { Text = "Operation completed.", Role = Role.Assistant };

        var mockAgent = new Mock<IStreamingAgent>();
        var callCount = 0;
        mockAgent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>((_, _, _) =>
            {
                callCount++;
                return callCount == 1
                    ? Task.FromResult(ToAsyncEnumerable([toolCall]))
                    : Task.FromResult(ToAsyncEnumerable([finalAssistantText]));
            });

        var registry = new FunctionRegistry();
        registry.AddFunction(
            BuildContract("long_running_op"),
            (_, _, _) => Task.FromResult<ToolHandlerResult>(new ToolHandlerResult.Deferred()));

        var observer = new RecordingObserver();
        await using var loop = new MultiTurnAgentLoop(
            mockAgent.Object,
            registry,
            "thread-loop-deferred",
            publicationObserver: observer);

        using var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);

        var firstRunCompleted = new TaskCompletionSource();
        var secondRunCompleted = new TaskCompletionSource();
        var runCompleteCount = 0;
        var subscribed = new TaskCompletionSource();
        _ = Task.Run(async () =>
        {
            subscribed.SetResult();
            await foreach (var msg in loop.SubscribeAsync(cts.Token))
            {
                if (msg is RunCompletedMessage)
                {
                    runCompleteCount++;
                    if (runCompleteCount == 1)
                    {
                        firstRunCompleted.TrySetResult();
                    }
                    else if (runCompleteCount == 2)
                    {
                        secondRunCompleted.TrySetResult();
                    }
                }
            }
        }, cts.Token);
        await subscribed.Task;

        await loop.SendAsync([new TextMessage { Text = "Start the long op", Role = Role.User }]);
        await firstRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The initial deferred placeholder was published as an ordinary Message, not a
        // Replacement — it is the FIRST time this ToolCallId is published.
        observer.Received.Should().ContainSingle(p =>
            IsDeferredToolResultFor(p.Message, "tc_long") && p.Kind == AgentPublicationKind.Message);

        await loop.ResolveToolCallAsync("tc_long", "{\"status\":\"done\"}");
        await secondRunCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The resolution republishes the SAME ToolCallId's final content, explicitly marked as a
        // Replacement of the earlier placeholder.
        observer.Received.Should().ContainSingle(p =>
            IsResolvedToolResultFor(p.Message, "tc_long", "{\"status\":\"done\"}") && p.Kind == AgentPublicationKind.Replacement);

        await cts.CancelAsync();
    }

    #endregion

    #region Ordered dispatch: per-agent Sequence order, non-poisoning, and observer cancellation scope

    /// <summary>
    /// An observer that delegates to a caller-supplied callback per publication, so a test can
    /// inspect exactly what <see cref="CancellationToken"/> a publication was observed with, or
    /// block on a gate, without needing a bespoke observer type per test.
    /// </summary>
    private sealed class DelegateObserver : IAgentPublicationObserver
    {
        private readonly Func<AgentPublication, CancellationToken, ValueTask> _callback;

        public DelegateObserver(Func<AgentPublication, CancellationToken, ValueTask> callback)
        {
            _callback = callback;
        }

        public ValueTask OnPublishedAsync(AgentPublication publication, CancellationToken ct) =>
            _callback(publication, ct);
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #1/#2/#4): two publications race
    /// `PublishToAllAsync` concurrently. Publication A acquires the agent's internal replay lock
    /// FIRST (so it is assigned <see cref="AgentPublication.Sequence"/> 1), but is deliberately
    /// held — via <c>FanOutReadyDelayHookForTests</c> — AFTER its v1 fan-out/lock release and BEFORE
    /// it signals its dispatch node "ready", until publication B (Sequence 2, lock-acquired second)
    /// has ALREADY reached its own "ready" point. Despite B being ready first, the observer must
    /// still receive A before B — proving delivery order follows lock-acquisition (Sequence) order,
    /// not readiness/completion timing.
    /// </summary>
    [Fact]
    public async Task PublishToAllAsync_ConcurrentPublications_ObserverOrderMatchesReplayLockSequence_EvenWhenFirstPublisherReadySecond()
    {
        var observer = new RecordingObserver();
        await using var agent = new ObserverTestAgent("thread-order-1", observer);

        var msgA = new TextMessage { Text = "A", Role = Role.Assistant };
        var msgB = new TextMessage { Text = "B", Role = Role.Assistant };

        var bReachedReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.FanOutReadyDelayHookForTests = async publication =>
        {
            if (publication.Sequence == 1)
            {
                // Publication A: don't signal "ready" until B (the numerically LATER publication)
                // has already signalled its own readiness, then wait for the test's explicit release.
                await bReachedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await releaseA.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                bReachedReady.TrySetResult();
            }
        };

        // Sequence is assigned synchronously under the replay lock at the START of each call — by
        // the time this second call returns control here (both calls suspend on the hook above
        // before invoking the observer), A already holds Sequence 1 and B holds Sequence 2.
        var publishA = agent.PublishForTest(msgA).AsTask();
        var publishB = agent.PublishForTest(msgB).AsTask();

        // B already reached its own ready point (set synchronously inside the hook above); release
        // the deliberately-slower first publisher now.
        await bReachedReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseA.SetResult();

        await Task.WhenAll(publishA, publishB).WaitAsync(TimeSpan.FromSeconds(5));

        var received = observer.Received;
        received.Should().HaveCount(2);
        received[0].Message.Should().BeSameAs(msgA, "the observer must see the lock-acquisition-order-first publication first");
        received[0].Sequence.Should().Be(1);
        received[1].Message.Should().BeSameAs(msgB);
        received[1].Sequence.Should().Be(2);
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #2/#4): publication A's observer call
    /// throws. The bounded-lifetime dispatch chain must fault ONLY A's own publishing caller — a
    /// later publication B must still reach the observer normally, proving one failure does not
    /// permanently poison the chain.
    /// </summary>
    [Fact]
    public async Task PublishToAllAsync_FirstPublicationObserverThrows_LaterPublicationStillReachesObserver()
    {
        var boom = new InvalidOperationException("observer boom for A");
        var msgA = new TextMessage { Text = "A-fails", Role = Role.Assistant };
        var msgB = new TextMessage { Text = "B-succeeds", Role = Role.Assistant };

        var observer = new RecordingObserver(throwPredicate: pub => ReferenceEquals(pub.Message, msgA) ? boom : null);
        await using var agent = new ObserverTestAgent("thread-order-2", observer);

        var publishA = async () => await agent.PublishForTest(msgA);
        (await publishA.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(boom);

        // Must NOT hang or throw due to A's earlier observer failure.
        await agent.PublishForTest(msgB).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        observer.Received.Should().ContainSingle(p => ReferenceEquals(p.Message, msgB));
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #3): the CancellationToken handed to
    /// <see cref="IAgentPublicationObserver.OnPublishedAsync"/> must be the agent's own durability
    /// scope, never the publish caller's run/request token — cancelling the caller's token must not
    /// cancel/abort observer delivery.
    /// </summary>
    [Fact]
    public async Task PublishToAllAsync_CallerTokenAlreadyCancelled_ObserverStillInvoked_WithUncancelledScope()
    {
        CancellationToken? observedToken = null;
        var observer = new DelegateObserver((_, ct) =>
        {
            observedToken = ct;
            return ValueTask.CompletedTask;
        });
        await using var agent = new ObserverTestAgent("thread-cancel-scope", observer);

        using var callerCts = new CancellationTokenSource();
        await callerCts.CancelAsync();

        var msg = new TextMessage { Text = "x", Role = Role.Assistant };

        // The caller's token is ALREADY cancelled — observer delivery must still succeed.
        await agent.PublishForTest(msg, callerCts.Token);

        observedToken.Should().NotBeNull();
        observedToken!.Value.IsCancellationRequested.Should().BeFalse(
            "the observer's token must be the agent's own durability scope, not the (cancelled) caller token");
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #3): disposing the agent while an
    /// observer call is in flight must neither hang nor leak the dispatch chain — disposal signals
    /// the observer's own cancellation scope and completes once that in-flight call finishes.
    /// </summary>
    [Fact]
    public async Task DisposeAsync_ObserverCallInFlight_CancelsObserverScope_AndDoesNotHang()
    {
        var observerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken tokenSeenByObserver = default;

        var observer = new DelegateObserver(async (_, ct) =>
        {
            tokenSeenByObserver = ct;
            observerStarted.TrySetResult();
            try
            {
                await release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Expected once DisposeAsync cancels the observer's durability scope.
            }
        });

        var agent = new ObserverTestAgent("thread-dispose-hang", observer);
        var publishTask = agent.PublishForTest(new TextMessage { Text = "x", Role = Role.Assistant }).AsTask();

        await observerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = agent.DisposeAsync().AsTask();
        var winner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10)));
        winner.Should().Be(disposeTask, "DisposeAsync must not hang on an in-flight observer call");

        tokenSeenByObserver.IsCancellationRequested.Should().BeTrue(
            "disposal must cancel the observer's own durability scope");

        await publishTask;
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #1): models a real-world race — e.g. a
    /// ResolveToolCall result about to be published — that is blocked, via
    /// <c>PrePublishLockHookForTests</c>, at the exact instant BEFORE it can ever acquire the
    /// agent's internal replay lock, while <see cref="MultiTurnAgentBase.DisposeAsync"/> starts and
    /// runs to completion concurrently. Proves all three closed-TOCTOU guarantees at once:
    /// (1) disposal does not hang waiting on the blocked publish — it was never appended to the
    /// observer dispatch chain, so it is not part of disposal's tail snapshot; (2) once released,
    /// the blocked publish fails immediately with the documented, predictable
    /// <see cref="ObjectDisposedException"/> attributed to the agent itself — never an incidental
    /// <see cref="ObjectDisposedException"/> from a dispatch node lazily reading
    /// <c>CancellationTokenSource.Token</c> on an already-disposed scope; and (3) no dispatch node
    /// was ever appended for it, so the observer never silently "misses" it — the caller gets the
    /// documented disposal outcome instead.
    /// </summary>
    [Fact]
    public async Task PublishToAllAsync_BlockedBeforeLock_RacesDisposeAsync_FailsPredictablyWithObjectDisposedException()
    {
        var observer = new RecordingObserver();
        var agent = new ObserverTestAgent("thread-race-dispose", observer);

        var blockedPublishReachedGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBlockedPublish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        agent.PrePublishLockHookForTests = async () =>
        {
            blockedPublishReachedGate.TrySetResult();
            await releaseBlockedPublish.Task.WaitAsync(TimeSpan.FromSeconds(5));
        };

        var lateMessage = new TextMessage { Text = "late-resolve-tool-call-result", Role = Role.Assistant };
        var blockedPublishTask = agent.PublishForTest(lateMessage).AsTask();

        // Deterministically wait until the publish call is blocked BEFORE it could ever acquire
        // `_replayLock` / mutate the replay buffer / append a dispatch node.
        await blockedPublishReachedGate.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // DisposeAsync must run to completion WITHOUT waiting on the blocked publish: it was never
        // appended to the dispatch chain, so it cannot be part of the tail snapshot DisposeAsync
        // bounds its wait on.
        var disposeTask = agent.DisposeAsync().AsTask();
        var disposeWinner = await Task.WhenAny(disposeTask, Task.Delay(TimeSpan.FromSeconds(10)));
        disposeWinner.Should().Be(
            disposeTask,
            "DisposeAsync must not hang on a publish call that is blocked before it could ever acquire the replay lock");
        await disposeTask;

        // Now release the blocked publish. It must resolve promptly (never hang) and fail with the
        // documented, predictable outcome — not an incidental exception from a disposed CTS.
        releaseBlockedPublish.SetResult();

        var publishOutcome = await Task.WhenAny(blockedPublishTask, Task.Delay(TimeSpan.FromSeconds(5)));
        publishOutcome.Should().Be(blockedPublishTask, "the blocked publish must resolve promptly once released, never hang");

        var awaitBlockedPublish = () => blockedPublishTask;
        var thrown = await awaitBlockedPublish.Should().ThrowAsync<ObjectDisposedException>();
        thrown.Which.ObjectName.Should().Be(
            typeof(ObserverTestAgent).FullName,
            "this must be the documented agent-disposed outcome — thrown BEFORE any replay-buffer/fan-out/observer "
                + "mutation — never an ObjectDisposedException incidentally raised by reading `.Token` on an "
                + "already-disposed CancellationTokenSource deep inside a dispatch node");

        // No dispatch node was ever appended for the blocked publication — the observer never sees it.
        observer.Received.Should().BeEmpty();
    }

    /// <summary>
    /// WI #194 publication-observer quality finding (Must #4): the test-only
    /// <c>FanOutReadyDelayHookForTests</c> throws while publication A is signalling its dispatch
    /// node "ready". That must not permanently hang the chain: the "ready" TCS must still complete
    /// (in a `finally`) despite the hook faulting, so publication B's node — chained behind A's —
    /// still proceeds and B still reaches the observer normally.
    /// </summary>
    [Fact]
    public async Task PublishToAllAsync_FanOutReadyHookThrows_DoesNotHangChain_LaterPublicationStillReachesObserver()
    {
        var hookBoom = new InvalidOperationException("fan-out-ready hook boom for A");
        var observer = new RecordingObserver();
        await using var agent = new ObserverTestAgent("thread-hook-throws", observer);

        var msgA = new TextMessage { Text = "A-hook-throws", Role = Role.Assistant };
        var msgB = new TextMessage { Text = "B-succeeds", Role = Role.Assistant };

        agent.FanOutReadyDelayHookForTests = publication =>
            publication.Sequence == 1 ? throw hookBoom : Task.CompletedTask;

        var publishA = async () => await agent.PublishForTest(msgA);
        (await publishA.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(hookBoom);

        // Must NOT hang: A's "ready" TCS must have been completed in a `finally` despite the hook
        // throwing, letting B's dispatch node (chained behind A's) proceed once B is published.
        await agent.PublishForTest(msgB).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        observer.Received.Should().ContainSingle(p => ReferenceEquals(p.Message, msgB));
    }

    #endregion

    #region Helper predicates

    private static bool IsRunAssignment(IMessage message) => message is RunAssignmentMessage;

    private static bool IsRunCompleted(IMessage message) => message is RunCompletedMessage;

    private static bool IsNonDeferredToolResultFor(IMessage message, string toolCallId) =>
        message is ToolCallResultMessage { IsDeferred: false } tcr && tcr.ToolCallId == toolCallId;

    private static bool IsDeferredToolResultFor(IMessage message, string toolCallId) =>
        message is ToolCallResultMessage { IsDeferred: true } tcr && tcr.ToolCallId == toolCallId;

    private static bool IsResolvedToolResultFor(IMessage message, string toolCallId, string expectedResult) =>
        message is ToolCallResultMessage { IsDeferred: false } tcr
        && tcr.ToolCallId == toolCallId
        && tcr.Result == expectedResult;

    #endregion
}
