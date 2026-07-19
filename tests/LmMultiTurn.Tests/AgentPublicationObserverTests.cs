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
