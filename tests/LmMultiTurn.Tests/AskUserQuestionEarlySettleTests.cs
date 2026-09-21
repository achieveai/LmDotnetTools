using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests;

/// <summary>
/// Bug #5: a run parked on <c>AskUserQuestion</c> used to fail outright the moment anything other
/// than a pure <see cref="NotifyMessage"/> batch arrived — the loop's own deferred-tool precondition
/// in <c>ExecuteTurnAsync</c> threw, because <c>AllMessagesAreNotifications</c> rejects a user
/// <see cref="TextMessage"/>, a peer <see cref="AgentMessage"/>, or any batch that merely
/// <em>mixes</em> one of those with a notification.
///
/// <para>
/// The parked call now settles early with a named, non-error placeholder so the turn proceeds, and
/// the human's real answer is redirected into the conversation as an injected question+answer pair
/// rather than being refused as a conflict.
/// </para>
/// </summary>
public class AskUserQuestionEarlySettleTests
{
    private readonly Mock<IStreamingAgent> _mockAgent = new();
    private readonly Mock<ILogger<MultiTurnAgentLoop>> _loggerMock = new();

    private const string QuestionCallId = "tc_q1";

    private static string QuestionArgs() =>
        JsonSerializer.Serialize(
            new
            {
                context = "Picking a paint colour for the shed.",
                questions = new[]
                {
                    new
                    {
                        id = "colour",
                        prompt = "Which colour?",
                        options = new[] { new { label = "Red" }, new { label = "Blue" } },
                    },
                },
            }
        );

    private static ToolCallMessage QuestionCall() =>
        new()
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = QuestionArgs(),
            ToolCallId = QuestionCallId,
            Role = Role.Assistant,
        };

    // ---------------------------------------------------------------- criteria 1 & 2

    [Fact]
    public async Task UserTextMessage_WhileParkedOnQuestion_SettlesEarly_AndTheRequestCarriesACompletePair()
    {
        var fixture = await ParkOnQuestionAsync();

        await fixture.Loop.SendAsync([new TextMessage { Text = "actually, stop and do X", Role = Role.User }]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        // The deferral is gone and the second provider request carries a complete tool_use/tool_result
        // pair plus the interrupting message — asserted on what the fake provider was handed, not on logs.
        (await fixture.Loop.GetDeferredToolCallsAsync())
            .Should()
            .BeEmpty();
        fixture.Requests.Should().HaveCountGreaterThanOrEqualTo(2);

        var request = fixture.Requests[^1];
        ExtractCallIds(request).Should().ContainSingle(id => id == QuestionCallId);
        var settled = ExtractResults(request).Should().ContainSingle(r => r.ToolCallId == QuestionCallId).Subject;
        settled.IsDeferred.Should().BeFalse();
        settled.IsError.Should().BeFalse();
        settled.Result.Should().Contain(EarlySettlePlaceholders.EarlySettleStatus);
        settled.Result.Should().Contain(AskUserQuestionToolProvider.EarlySettlePlaceholder);
        request.OfType<TextMessage>().Should().Contain(t => t.Text == "actually, stop and do X");

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    [Fact]
    public async Task PeerAgentMessage_WhileParkedOnQuestion_SettlesEarly_AndTheRequestCarriesACompletePair()
    {
        var fixture = await ParkOnQuestionAsync();

        var peer = AgentMessage.Create(
            messageId: "am-1",
            agentMessageType: AgentMessageType.Question,
            fromAgentId: "agent-b",
            fromName: "Bee",
            body: "are you done with the shed?"
        );
        await fixture.Loop.SendAsync([peer]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().BeEmpty();

        var request = fixture.Requests[^1];
        var settled = ExtractResults(request).Should().ContainSingle(r => r.ToolCallId == QuestionCallId).Subject;
        settled.IsDeferred.Should().BeFalse();
        settled.Result.Should().Contain(EarlySettlePlaceholders.EarlySettleStatus);
        request.OfType<AgentMessage>().Should().ContainSingle(m => m.MessageId == "am-1");

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 3 (control)

    [Fact]
    public async Task PureNotificationBatch_WhileParkedOnQuestion_StillFoldsIn_AndWritesNoPlaceholder()
    {
        var fixture = await ParkOnQuestionAsync();

        var notify = NotifyMessage.Create(
            NotifyKinds.SubAgentCompletion,
            detail: "bg done",
            sourceToolName: "Agent",
            sourceToolCallId: "call-x"
        );
        await fixture.Loop.SendAsync([notify]);

        await AchieveAi.LmDotnetTools.LmTestUtils.Wait.UntilAsync(
            () =>
                fixture.Collector.Snapshot().OfType<NotifyMessage>().Any(n => n.Detail == "bg done")
                || fixture.Requests.Count > 1
                || fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Any(m => m.IsError),
            because: "the parked loop folded the notify into history and published its pill live"
        );

        // The cheapest ending still wins: no second turn, no placeholder, the question stays parked.
        fixture.Requests.Should().HaveCount(1);
        (await fixture.Loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == QuestionCallId);
        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        fixture
            .Loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Should()
            .ContainSingle(r => r.ToolCallId == QuestionCallId)
            .Which.IsDeferred.Should()
            .BeTrue();

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 4

    [Fact]
    public async Task RealAnswerAfterEarlySettle_IsRedirected_CarriesQuestionAndAnswer_AndHistoryKeepsOneToolResult()
    {
        var fixture = await ParkOnQuestionAsync();

        await fixture.Loop.SendAsync([new TextMessage { Text = "wait, hold on", Role = Role.User }]);
        await fixture.Collector.WaitForCompletionsAsync(2);

        var outcome = await fixture.Loop.TryResolveToolCallAsync(QuestionCallId, "Blue");
        outcome.Should().Be(ResolveToolCallOutcome.Resolved);

        await fixture.Collector.WaitForCompletionsAsync(3);

        // Exactly one tool_result for the call: the redirect must not append a second one.
        fixture
            .Loop.GetHistorySnapshot()
            .OfType<ToolCallResultMessage>()
            .Where(r => r.ToolCallId == QuestionCallId)
            .Should()
            .ContainSingle();

        // The injected turn carries the ORIGINAL QUESTION as well as the answer, so a model whose
        // view no longer contains the exchange is not handed an unmoored reply.
        var injected = fixture
            .Loop.GetHistorySnapshot()
            .OfType<TextMessage>()
            .Should()
            .ContainSingle(t => t.Text.Contains(QuestionCallId, StringComparison.Ordinal))
            .Subject;
        injected.Text.Should().Contain("Which colour?");
        injected.Text.Should().Contain("Blue");
        injected.Role.Should().Be(Role.User);

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 9

    [Fact]
    public async Task NothingInterrupts_TheTranscriptIsUnchanged()
    {
        var fixture = await ParkOnQuestionAsync();

        await fixture.Loop.ResolveToolCallAsync(QuestionCallId, "Blue");
        await fixture.Collector.WaitForCompletionsAsync(2);

        // Golden transcript for the common path, captured from the pre-change build. Any drift here
        // is a regression in the path that nothing interrupts.
        Describe(fixture.Requests[^1])
            .Should()
            .Equal(
                "text:User:paint the shed",
                "tool_call:Assistant:" + QuestionCallId,
                "tool_result:User:" + QuestionCallId + ":Blue"
            );

        fixture.Collector.Snapshot().OfType<RunCompletedMessage>().Should().NotContain(m => m.IsError);
        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 5

    [Fact]
    public async Task AnswerArrivingWhileTheSettleIsMidCommit_IsDeliveredExactlyOnce_AndLeavesOneToolResult()
    {
        // The exact race the one-shot ending exists for. The store holds the early settle open at its
        // history write, so the answer arrives while the coordinator claim is still held - the window
        // in which the answer used to come back Conflict and be thrown away.
        var store = new GatedReplaceStore(new InMemoryConversationStore());
        var fixture = await ParkOnQuestionAsync(store);

        store.ArmGate();
        await fixture.Loop.SendAsync([new TextMessage { Text = "stop, do X", Role = Role.User }]);
        await store.WaitUntilInsideReplaceAsync();

        var outcome = await fixture.Loop.TryResolveToolCallAsync(QuestionCallId, "Blue");
        store.ReleaseGate();

        outcome.Should().Be(ResolveToolCallOutcome.Resolved, "the answer must be taken, never refused");
        await fixture.Collector.WaitForCompletionsAsync(2);

        // A redelivery of the very same answer must not inject it a second time.
        (await fixture.Loop.TryResolveToolCallAsync(QuestionCallId, "Blue"))
            .Should()
            .Be(ResolveToolCallOutcome.Duplicate);

        await AchieveAi.LmDotnetTools.LmTestUtils.Wait.UntilAsync(
            () =>
                fixture
                    .Loop.GetHistorySnapshot()
                    .OfType<TextMessage>()
                    .Any(t => t.Text.Contains(QuestionCallId, StringComparison.Ordinal)),
            because: "the answer is injected as its own turn"
        );

        ExtractResults(fixture.Loop.GetHistorySnapshot())
            .Where(r => r.ToolCallId == QuestionCallId)
            .Should()
            .ContainSingle("exactly one ending reaches history");
        fixture
            .Loop.GetHistorySnapshot()
            .OfType<TextMessage>()
            .Where(t => t.Text.Contains(QuestionCallId, StringComparison.Ordinal))
            .Should()
            .ContainSingle("the answer is delivered exactly once");

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 7

    [Fact]
    public async Task CancellingTheTurn_IsNotAnInterruption_AndSettlesNothing()
    {
        var fixture = await ParkOnQuestionAsync();

        await fixture.Cts.CancelAsync();
        await Task.Delay(150);

        // Cancellation stops the loop; it must never be mistaken for input arriving, and so must never
        // leave a deferred_to_notification behind.
        ExtractResults(fixture.Loop.GetHistorySnapshot())
            .Should()
            .NotContain(r =>
                r.Result != null
                && r.Result.Contains(EarlySettlePlaceholders.EarlySettleStatus, StringComparison.Ordinal)
            );

        // And a wait on the interruption signal reports cancellation as cancellation.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await FluentActions
            .Awaiting(() => fixture.Loop.WaitForRealInputAsync(cancelled.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        await fixture.DisposeAsync();
    }

    // ---------------------------------------------------------------- criterion 8

    [Fact]
    public async Task TheAnswerSurvivesARebuildOfTheLoop()
    {
        // A mode or provider switch disposes the loop and builds a new one over the same store. The
        // in-memory ending goes with it; the placeholder the settle wrote into history does not, and
        // that is what the rebuilt loop recognises.
        var store = new InMemoryConversationStore();
        var fixture = await ParkOnQuestionAsync(store, "rebuild-thread");

        await fixture.Loop.SendAsync([new TextMessage { Text = "stop, do X", Role = Role.User }]);
        await fixture.Collector.WaitForCompletionsAsync(2);
        await fixture.DisposeAsync();

        await using var rebuilt = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            "rebuild-thread",
            store: store,
            logger: _loggerMock.Object
        );
        using var cts = new CancellationTokenSource();
        _ = rebuilt.RunAsync(cts.Token);

        await AchieveAi.LmDotnetTools.LmTestUtils.Wait.UntilAsync(
            () => rebuilt.GetHistorySnapshot().Count > 0,
            because: "the rebuilt loop restores the conversation before anything resolves against it"
        );

        (await rebuilt.TryResolveToolCallAsync(QuestionCallId, "Blue")).Should().Be(ResolveToolCallOutcome.Resolved);

        await AchieveAi.LmDotnetTools.LmTestUtils.Wait.UntilAsync(
            () =>
                rebuilt
                    .GetHistorySnapshot()
                    .OfType<TextMessage>()
                    .Any(t => t.Text.Contains(QuestionCallId, StringComparison.Ordinal)),
            because: "the redirect survived the rebuild"
        );

        var injected = rebuilt
            .GetHistorySnapshot()
            .OfType<TextMessage>()
            .Single(t => t.Text.Contains(QuestionCallId, StringComparison.Ordinal));
        injected.Text.Should().Contain("Which colour?");
        injected.Text.Should().Contain("Blue");

        await cts.CancelAsync();
    }

    // ---------------------------------------------------------------- harness

    /// <summary>
    /// A stable, readable rendering of a provider request. Single tool calls are normalized into the
    /// aggregate forms before they reach the provider, so both shapes are flattened to the same text.
    /// </summary>
    private static List<string> Describe(IEnumerable<IMessage> messages) =>
        [
            .. messages.Select(m =>
                m switch
                {
                    ToolCallMessage tc => $"tool_call:{tc.Role}:{tc.ToolCallId}",
                    ToolsCallMessage tcs => $"tool_call:{tcs.Role}:"
                        + string.Join(",", tcs.ToolCalls.Select(c => c.ToolCallId)),
                    ToolCallResultMessage tr => $"tool_result:{tr.Role}:{tr.ToolCallId}:{tr.Result}",
                    ToolsCallResultMessage trs => $"tool_result:{trs.Role}:"
                        + string.Join(",", trs.ToolCallResults.Select(r => $"{r.ToolCallId}:{r.Result}")),
                    TextMessage t => $"text:{t.Role}:{t.Text}",
                    _ => $"{m.GetType().Name}:{m.Role}",
                }
            ),
        ];

    /// <summary>Tool call ids in a request, whichever of the two shapes carries them.</summary>
    private static List<string> ExtractCallIds(IEnumerable<IMessage> messages) =>
        [
            .. messages
                .OfType<ToolCallMessage>()
                .Select(tc => tc.ToolCallId!)
                .Concat(messages.OfType<ToolsCallMessage>().SelectMany(m => m.ToolCalls.Select(c => c.ToolCallId!))),
        ];

    /// <summary>
    /// Turn 1 asks the question and parks; every later turn answers with plain text. Returns once the
    /// question is confirmed outstanding.
    /// </summary>
    private async Task<Fixture> ParkOnQuestionAsync(IConversationStore? store = null, string? threadId = null)
    {
        var requests = new List<List<IMessage>>();
        var gate = new object();
        var turn = 0;
        _mockAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (msgs, _, _) =>
                {
                    int index;
                    lock (gate)
                    {
                        requests.Add([.. msgs]);
                        index = ++turn;
                    }

                    return Task.FromResult(
                        index == 1
                            ? ToAsyncEnumerable([QuestionCall()])
                            : ToAsyncEnumerable([new TextMessage { Text = $"ack {index}", Role = Role.Assistant }])
                    );
                }
            );

        var loop = new MultiTurnAgentLoop(
            _mockAgent.Object,
            new FunctionRegistry(),
            threadId ?? "early-settle-thread",
            store: store,
            logger: _loggerMock.Object
        );

        var cts = new CancellationTokenSource();
        _ = loop.RunAsync(cts.Token);
        var collector = new LoopMessageCollector(loop, cts.Token);

        await loop.SendAsync([new TextMessage { Text = "paint the shed", Role = Role.User }]);
        await collector.WaitForCompletionsAsync(1);
        (await loop.GetDeferredToolCallsAsync()).Should().ContainSingle(p => p.ToolCallId == QuestionCallId);

        return new Fixture(loop, collector, cts, requests, gate);
    }

    private sealed record Fixture(
        MultiTurnAgentLoop Loop,
        LoopMessageCollector Collector,
        CancellationTokenSource Cts,
        List<List<IMessage>> RawRequests,
        object Gate
    )
    {
        public IReadOnlyList<List<IMessage>> Requests
        {
            get
            {
                lock (Gate)
                {
                    return [.. RawRequests];
                }
            }
        }

        public async Task DisposeAsync()
        {
            await Cts.CancelAsync();
            await Loop.DisposeAsync();
            Cts.Dispose();
        }
    }

    internal static List<ToolCallResultMessage> ExtractResults(IEnumerable<IMessage> messages) =>
        [
            .. messages
                .OfType<ToolCallResultMessage>()
                .Concat(
                    messages
                        .OfType<ToolsCallResultMessage>()
                        .SelectMany(m =>
                            m.ToolCallResults.Select(r => new ToolCallResultMessage
                            {
                                ToolCallId = r.ToolCallId,
                                Result = r.Result,
                                IsError = r.IsError,
                            })
                        )
                ),
        ];

    internal static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        IEnumerable<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var msg in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return msg;
            await Task.Yield();
        }
    }
}

/// <summary>Collects a loop's published output on a background task for assertions.</summary>
internal sealed class LoopMessageCollector
{
    private readonly List<IMessage> _messages = [];
    private readonly object _gate = new();
    private readonly Drain _drain;
    private volatile int _completions;

    public LoopMessageCollector(MultiTurnAgentLoop loop, CancellationToken ct) =>
        _drain = LoopSubscription.StartDraining(
            loop,
            msg =>
            {
                lock (_gate)
                {
                    _messages.Add(msg);
                }

                if (msg is RunCompletedMessage)
                {
                    _completions++;
                }
            },
            ct
        );

    public List<IMessage> Snapshot()
    {
        lock (_gate)
        {
            return [.. _messages];
        }
    }

    public async Task WaitForCompletionsAsync(int count)
    {
        try
        {
            await _drain.WaitAsync(PollAsync(count), TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Expected {count} run completion(s); saw {_completions}.");
        }
    }

    private async Task PollAsync(int count)
    {
        while (_completions < count)
        {
            await Task.Delay(25);
        }
    }
}

/// <summary>
/// An <see cref="IConversationStore"/> that can hold a single <c>ReplaceMessageAsync</c> open, so a
/// test can drive the window in which a resolution has claimed a deferred call and has not yet
/// committed it. Everything else is delegated untouched.
/// </summary>
internal sealed class GatedReplaceStore(InMemoryConversationStore inner) : IConversationStore
{
    private readonly TaskCompletionSource _inside = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _armed;

    public void ArmGate() => Interlocked.Exchange(ref _armed, 1);

    public Task WaitUntilInsideReplaceAsync() => _inside.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void ReleaseGate() => _release.TrySetResult();

    public async Task ReplaceMessageAsync(string threadId, PersistedMessage replacement, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _armed, 0) == 1)
        {
            _ = _inside.TrySetResult();
            await _release.Task;
        }

        await inner.ReplaceMessageAsync(threadId, replacement, ct);
    }

    public Task AppendMessagesAsync(
        string threadId,
        IReadOnlyList<PersistedMessage> messages,
        CancellationToken ct = default
    ) => inner.AppendMessagesAsync(threadId, messages, ct);

    public Task<IReadOnlyList<PersistedMessage>> LoadMessagesAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMessagesAsync(threadId, ct);

    public Task SaveMetadataAsync(string threadId, ThreadMetadata metadata, CancellationToken ct = default) =>
        inner.SaveMetadataAsync(threadId, metadata, ct);

    public Task<ThreadMetadata?> LoadMetadataAsync(string threadId, CancellationToken ct = default) =>
        inner.LoadMetadataAsync(threadId, ct);

    public Task UpdateMetadataAsync(
        string threadId,
        Func<ThreadMetadata?, ThreadMetadata> update,
        CancellationToken ct = default
    ) => inner.UpdateMetadataAsync(threadId, update, ct);

    public Task DeleteThreadAsync(string threadId, CancellationToken ct = default) =>
        inner.DeleteThreadAsync(threadId, ct);

    public Task<IReadOnlyList<ThreadMetadata>> ListThreadsAsync(
        int limit = 50,
        int offset = 0,
        ConversationListOptions? options = null,
        CancellationToken ct = default
    ) => inner.ListThreadsAsync(limit, offset, options, ct);
}
