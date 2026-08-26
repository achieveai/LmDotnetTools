using System.Runtime.CompilerServices;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// The per-site regression guards for the two <c>SubAgentManager</c> accept paths named in #434 that
/// PR #436 left untested: <c>RelayDescendantQuestionToParentAsync</c> (a descendant's pending
/// question surfaced to the root) and <c>SendToParentAsync</c> (a background sub-agent's result).
/// </summary>
/// <remarks>
/// <para>
/// These do not prove the mechanism — <see cref="LmMultiTurn.Tests.InputAcceptanceObserverTests"/>
/// does that, at the two places a receipt id is minted. What they catch is a SITE being rerouted off
/// those mint sites: <c>MultiTurnAgentBase</c> has raw enqueue paths
/// (<c>EnqueueRawAsync</c>/<c>TryEnqueueRaw</c>) that put an input in the channel without minting or
/// announcing anything, and a relay moved onto one of them would keep working, keep delivering, and
/// silently stop being covered by the accepted-input ledger. That is the #418 hole all over again,
/// and nothing else in the suite would notice.
/// </para>
/// <para>
/// So each test correlates the two facts rather than asserting either alone: it finds the
/// <see cref="QueuedInput"/> that actually landed in the parent's channel, reads the receipt id off
/// it, and requires THAT id to have been reported. A rerouted site still delivers a queued input, so
/// an assertion on delivery alone would stay green; an assertion on "the observer heard something"
/// alone would be satisfied by any other accept on the thread.
/// </para>
/// <para>
/// The parent here is a real <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.MultiTurnAgentBase"/>
/// (loop never started, so relayed inputs sit in the channel) rather than the Moq
/// <c>IMultiTurnAgent</c> the other sub-agent suites use. A mock parent cannot pin this at all: its
/// <c>SendAsync</c> is a stub, so the mint site under test never runs.
/// </para>
/// </remarks>
public class SubAgentParentRelayObserverTests : IAsyncLifetime
{
    private const string ParentThreadId = "thread-parent-relay";

    private readonly Mock<IStreamingAgent> _subAgentMock = new();
    private readonly RecordingObserver _observer = new();
    private readonly ObservedTestAgent _parent = new(ParentThreadId);
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parent.InputAcceptanceObserver = _observer;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            // Bounded: an unbounded teardown turns one stalled test into an aborted run (#362).
            await Wait.ForTeardownAsync(_manager, "the sub-agent manager under test");
        }

        await _parent.DisposeAsync();
    }

    [Fact]
    public async Task TheSubAgentCompletionRelay_ReportsItsAccept()
    {
        // Site: SubAgentManager.SendToParentAsync. A background spawn's result is delivered onto the
        // POOLED parent as a real turn, from inside LmMultiTurn, by a monitor task the host never
        // sees. A grantee handoff landing between that accept and the run that would start it is the
        // exact #418 shape, and the host cannot record this accept because it does not know it
        // happened.
        SetupSubAgentResponse([new TextMessage { Text = "the child's answer", Role = Role.Assistant }]);
        _manager = CreateManager();

        _ = await _manager.SpawnAsync("test-agent", "do the thing", runInBackground: true);

        var relayed = await AwaitRelayOfKind(NotifyKinds.SubAgentCompletion);

        _observer.AcceptedSnapshot().Should().Contain((ParentThreadId, relayed.ReceiptId),
            "the id the parent's channel actually holds is the id the ledger has to hold — a relay "
                + "rerouted onto the loop's raw enqueue would still deliver this input under an id "
                + "nobody announced");
    }

    [Fact]
    public async Task TheDescendantQuestionRelay_ReportsItsAccept()
    {
        // Site: SubAgentManager.RelayDescendantQuestionToParentAsync, the default descendant-question
        // sink when no upstream root target was supplied. It fires the moment a descendant parks on
        // its own AskUserQuestion, so it is the one relay that reaches the parent while the whole
        // spawn tree is otherwise idle — precisely when a handoff reads the entry releasable.
        var askArgs = JsonSerializer.Serialize(new
        {
            context = "Need input before continuing.",
            questions = new[]
            {
                new
                {
                    prompt = "Which color?",
                    options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                },
            },
        });
        SetupSubAgentResponse([
            new ToolCallMessage
            {
                FunctionName = AskUserQuestionToolProvider.ToolName,
                FunctionArgs = askArgs,
                ToolCallId = "tc_color",
                Role = Role.Assistant,
            },
        ]);
        _manager = CreateManager();

        _ = await _manager.SpawnAsync("test-agent", "Pick a color", runInBackground: true);

        var relayed = await AwaitRelayOfKind(NotifyKinds.DescendantQuestion);

        _observer.AcceptedSnapshot().Should().Contain((ParentThreadId, relayed.ReceiptId),
            "this relay is one of the three accepts #434 exists for: it happens inside LmMultiTurn, "
                + "against a pooled parent, and no host call site can cover it");
    }

    /// <summary>
    /// Polls the parent's input channel until a relay carrying <paramref name="notifyKind"/> has
    /// landed, and returns the queued entry.
    /// </summary>
    /// <remarks>
    /// Drains into an accumulator rather than peeking, because a parked spawn relays TWO
    /// notifications (the descendant question, then the awaiting-answer completion) and the one under
    /// test is not always first. Accumulating also removes the race a "wait, then drain once" shape
    /// has: the relay reports BEFORE it enqueues, so observing the report does not mean the input has
    /// landed yet.
    /// </remarks>
    private async Task<QueuedInput> AwaitRelayOfKind(string notifyKind)
    {
        var received = new List<QueuedInput>();
        await Wait.UntilAsync(
            () =>
            {
                received.AddRange(_parent.DrainQueuedInputs());
                return received.Exists(queued => CarriesNotifyKind(queued, notifyKind));
            },
            $"the parent received the {notifyKind} relay",
            TimeSpan.FromSeconds(10));

        var relayed = received.Find(queued => CarriesNotifyKind(queued, notifyKind))!;
        relayed.ReceiptId.Should().NotBeNullOrEmpty(
            "an input queued under no id could never be retired by the run assignment that takes it");
        return relayed;
    }

    private static bool CarriesNotifyKind(QueuedInput queued, string notifyKind) =>
        queued.Input.Messages.Exists(message =>
            message is NotifyMessage notify && notify.NotifyKind == notifyKind);

    private SubAgentManager CreateManager()
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["test-agent"] = new()
                {
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => _subAgentMock.Object,
                },
            },
            MaxConcurrentSubAgents = 5,
        };

        return new SubAgentManager(
            parentAgent: _parent,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates));
    }

    private void SetupSubAgentResponse(List<IMessage> messages) =>
        _subAgentMock
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(ToAsyncEnumerable(messages)));

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
