using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// Pins the parked-question decision at a run's terminal boundary (#95).
/// <para>
/// A child that ends its run by calling <c>AskUserQuestion</c> is NOT finished — its loop still
/// holds the deferred call, and the human's answer starts the next run. The manager decides this
/// from two independent signals, and the tests here exist because only one of them is ordered:
/// </para>
/// <list type="number">
/// <item><description>the <see cref="AskUserQuestionToolProvider.ToolName"/>
/// <see cref="ToolCallMessage"/> on the monitor's OWN stream, which by construction arrives ahead of
/// the <see cref="RunCompletedMessage"/> of the same run;</description></item>
/// <item><description><c>HasPendingAskUserQuestionAsync</c>, which asks the child loop's deferred-call
/// registry — a DIFFERENT object, not ordered against this run reporting completion.</description></item>
/// </list>
/// <para>
/// When only signal 2 existed and it lost that race, the run was treated as terminal: the caller got
/// the literal <c>"(no text response)"</c> placeholder, the permit was released and the agent was torn
/// down, destroying the answer the human was about to unblock. Measured live at roughly 1 in 10.
/// </para>
/// <para>
/// These tests do not sample that race — they force it. <see cref="SubAgentManager.TestAgentFactoryOverride"/>
/// supplies a <see cref="FakeMultiTurnAgent"/>, which is not a <c>MultiTurnAgentLoop</c>, and
/// <c>HasPendingAskUserQuestionAsync</c> opens with <c>if (state.Agent is not MultiTurnAgentLoop) return false</c>.
/// So the registry probe answers false <i>deterministically</i> — the losing side of the race, on every
/// run — while the fake's stream still emits the tool call in its real position. Signal 1 is therefore
/// the only thing that can carry the decision, which is exactly what needs pinning. Nothing here waits
/// on a duration or a rate.
/// </para>
/// </summary>
public class SubAgentManagerParkedQuestionRaceTests : IAsyncLifetime
{
    private readonly Mock<IMultiTurnAgent> _parentMock = new();
    private SubAgentManager? _manager;

    public Task InitializeAsync()
    {
        _parentMock
            .Setup(p => p.SendAsync(
                It.IsAny<List<IMessage>>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SendReceipt("receipt-1", null, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_manager != null)
        {
            await _manager.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForegroundSpawn_ChildParksQuestionWhileRegistryProbeAnswersFalse_KeepsWaitingInsteadOfHandingBackThePlaceholder()
    {
        // The defect state, constructed rather than waited for: the child's AskUserQuestion is on the
        // stream, and the registry probe says "nothing parked" — because the fake agent is not a
        // MultiTurnAgentLoop, so the probe's type guard returns false every time. Before the stream
        // signal existed this run was classified terminal and the caller was handed "(no text response)".
        var questions = new List<NotifyMessage>();
        _manager = CreateManager(
            descendantQuestionSink: (notify, _) =>
            {
                lock (questions)
                {
                    questions.Add(notify);
                }

                return ValueTask.CompletedTask;
            });

        // Set inside the child's stream AFTER the run completion has been yielded and control has come
        // back for the next message — an exact barrier on "the monitor finished handling the completion",
        // not a sleep. Every assertion below is taken past this point.
        var completionHandled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, ct) => ParkedQuestionStream(completionHandled, ct),
        };

        var spawnTask = _manager.SpawnAsync("parking", "task", runInBackground: false);

        await completionHandled.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The headline defect, asserted first and in a shape whose failure message shows what the caller
        // actually received. Asserting on the returned value rather than on elapsed time keeps this a
        // statement about the classification: pre-fix the task is already completed by this barrier,
        // because the completion ran straight through to the terminal branch.
        var handedBack = spawnTask.IsCompleted ? await spawnTask : null;
        handedBack.Should().BeNull(
            "a parked question is not a finished run — the foreground caller must still be waiting for " +
            "the human's answer rather than holding a result, and the result it holds when this run is " +
            "misclassified as terminal is the \"(no text response)\" placeholder, with the real answer " +
            "destroyed along with the torn-down agent");

        // The positive signal: the manager published the parked question to the root conversation, which
        // is the ONLY way a human learns a blocking foreground spawn needs their input rather than having
        // silently hung. This branch is not reached at all when the run is misclassified as terminal.
        NotifyMessage question;
        lock (questions)
        {
            questions.Should().ContainSingle(
                "a run that parked a question must publish exactly one descendant-question notification");
            question = questions[0];
        }

        question.NotifyKind.Should().Be(NotifyKinds.DescendantQuestion);
        question.Detail.Should().Contain("[AwaitingAnswer]");
    }

    [Fact]
    public async Task ForegroundSpawn_ChildCompletesWithoutParkingAQuestion_StillReturnsTheRealAnswer()
    {
        // The other direction, and the reason it is here: removing a wrong classification can leave no
        // classification. If the parked-question signal ever fired for a run that never asked anything,
        // every ordinary sub-agent would be held non-terminal forever — its permit never released and its
        // caller never answered. That failure is invisible to the test above, which only ever asserts
        // that a run does NOT complete.
        _manager = CreateManager(descendantQuestionSink: (_, _) => ValueTask.CompletedTask);
        _manager.TestAgentFactoryOverride = (_, _) => new FakeMultiTurnAgent
        {
            SubscribeImpl = (_, ct) => PlainAnswerStream(ct),
        };

        var result = await _manager
            .SpawnAsync("parking", "task", runInBackground: false)
            .WaitAsync(TimeSpan.FromSeconds(30));

        result.Should().Be("the real answer");
    }

    #region Helpers

    /// <summary>
    /// A run whose last act is to park an <c>AskUserQuestion</c>: the tool call, then a completion that
    /// is terminal by every flag the manager can read directly (<c>HasPendingMessages</c> false,
    /// <c>IsError</c> false) and carries no assistant text — so a misclassification substitutes the
    /// placeholder rather than merely returning early text. The subscription then stays open, as a real
    /// child's does while it waits for the human.
    /// </summary>
    private static async IAsyncEnumerable<IMessage> ParkedQuestionStream(
        TaskCompletionSource<bool> completionHandled,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new ToolCallMessage
        {
            FunctionName = AskUserQuestionToolProvider.ToolName,
            FunctionArgs = "{\"question\":\"which branch?\"}",
            ToolCallId = "call-ask-1",
        };

        yield return new RunCompletedMessage { CompletedRunId = "run-1" };

        // Reached only once the consumer asks for the next message, i.e. after the monitor's completion
        // handling has returned.
        _ = completionHandled.TrySetResult(true);
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    /// <summary>An ordinary run: assistant text, then a terminal completion.</summary>
    private static async IAsyncEnumerable<IMessage> PlainAnswerStream(
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new TextMessage { Role = Role.Assistant, Text = "the real answer" };
        yield return new RunCompletedMessage { CompletedRunId = "run-1" };
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    private SubAgentManager CreateManager(
        Func<NotifyMessage, CancellationToken, ValueTask> descendantQuestionSink)
    {
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["parking"] = new()
                {
                    Name = "parking",
                    SystemPrompt = "You are a test agent.",
                    AgentFactory = () => throw new NotSupportedException(
                        "Bypassed by TestAgentFactoryOverride; should never be invoked."),
                },
            },
            MaxConcurrentSubAgents = 2,
        };

        return new SubAgentManager(
            parentAgent: _parentMock.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            descendantQuestionSink: descendantQuestionSink);
    }

    #endregion
}
