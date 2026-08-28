using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     A FRESH <see cref="WorkflowSession.StartAsync"/> launch must begin with an EMPTY controller
///     conversation, even when the caller-chosen thread id collides with a prior run already persisted in
///     the shared conversation store. This is the regression guard for the "workflow agent inherited a
///     previous workflow conversation" bug: the controller thread is <c>workflow-{workflowId}</c> where the
///     id is chosen by the launching agent, so a reused/clashing id maps to a thread that the shared
///     conversation store already holds messages for. Without an explicit
///     suppression, <c>MultiTurnAgentBase.RunAsync</c> auto-recovers that prior run's messages on startup and
///     the controller "inherits" a previous workflow conversation. Recovery is reserved for the deliberate
///     <see cref="WorkflowSession.ResumeAsync"/> path — see <see cref="ResumeBehavioralTests"/> for its
///     counterpart (resume DOES recover).
/// </summary>
public class WorkflowControllerFreshStartTests
{
    private const string ThreadId = "workflow-clashing-id";
    private const string Run1Marker = "RUN1-PRIVATE-CONTROLLER-CONTEXT-MARKER";

    [Fact]
    public async Task FreshStart_OnThreadWithPriorRun_DoesNotInheritPreviousConversation()
    {
        var conversationStore = new InMemoryConversationStore();
        var subAgentOptions = BuildSubAgentOptions();

        // --- Run #1: a fresh launch that persists a distinctive controller conversation under ThreadId. ---
        var firstController = ScriptedController(_ => new TextMessage { Text = Run1Marker, Role = Role.Assistant });
        await using (
            var first = await WorkflowSession.StartAsync(
                objective: "First run objective.",
                inputs: null,
                definition: null,
                subAgentOptions: subAgentOptions,
                controllerAgent: firstController.Object,
                threadId: ThreadId,
                conversationStore: conversationStore
            )
        )
        {
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }

        conversationStore
            .GetMessageCount(ThreadId)
            .Should()
            .BeGreaterThan(0, "run #1 must persist controller history under the thread so the clash is real");

        // --- Run #2: a SEPARATE fresh launch under the SAME thread id. It must NOT recover run #1. ---
        IReadOnlyList<IMessage>? secondRunFirstTurnContext = null;
        var secondController = new Mock<IStreamingAgent>();
        secondController
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (IEnumerable<IMessage> messages, GenerateReplyOptions _, CancellationToken _) =>
                {
                    // Capture the context handed to the controller on its FIRST turn.
                    secondRunFirstTurnContext ??= [.. messages];
                    return Task.FromResult(
                        ToAsyncEnumerable([new TextMessage { Text = "Second run done.", Role = Role.Assistant }])
                    );
                }
            );

        await using (
            var second = await WorkflowSession.StartAsync(
                objective: "Second run objective.",
                inputs: null,
                definition: null,
                subAgentOptions: subAgentOptions,
                controllerAgent: secondController.Object,
                threadId: ThreadId,
                conversationStore: conversationStore
            )
        )
        {
            await second.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }

        secondRunFirstTurnContext.Should().NotBeNull("the second controller must have run at least one turn");
        secondRunFirstTurnContext!
            .Select(TextOf)
            .Should()
            .NotContain(
                text => text.Contains(Run1Marker),
                "a fresh workflow launch must not inherit a previous run's controller conversation"
            );
    }

    private static string TextOf(IMessage message) =>
        message is TextMessage text ? text.Text ?? string.Empty : string.Empty;

    /// <summary>A sub-agent stub (unused by these text-only controllers, but required by the options).</summary>
    private static SubAgentOptions BuildSubAgentOptions()
    {
        var subAgentMock = new Mock<IStreamingAgent>();
        subAgentMock
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                Task.FromResult(
                    ToAsyncEnumerable([new TextMessage { Text = """{ "summary": "ok" }""", Role = Role.Assistant }])
                )
            );

        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "general-purpose",
                    SystemPrompt = "You are a general-purpose agent.",
                    AgentFactory = () => subAgentMock.Object,
                },
            },
        };
    }

    private static Mock<IStreamingAgent> ScriptedController(Func<int, IMessage> script)
    {
        var controller = new Mock<IStreamingAgent>();
        var turn = 0;
        controller
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([script(++turn)])));
        return controller;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(
        List<IMessage> messages,
        [EnumeratorCancellation] CancellationToken ct = default
    )
    {
        foreach (var message in messages)
        {
            ct.ThrowIfCancellationRequested();
            yield return message;
            await Task.Yield();
        }
    }
}
