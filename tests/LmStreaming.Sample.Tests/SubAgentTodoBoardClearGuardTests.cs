using System.Runtime.CompilerServices;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.Misc.Utils;

namespace LmStreaming.Sample.Tests;

/// <summary>
///     Bug 19 through the machinery that actually failed: a REAL sub-agent run loop, started by a real
///     <see cref="SubAgentManager" />, calling <c>bulk-initialize(clearExisting: true)</c> on the very
///     <see cref="TaskManager" /> instance the root agent is using.
/// </summary>
/// <remarks>
///     <para>
///         The board-level tests set the ambient actor by hand, which proves the guard but not the
///         wiring: nothing there fails if <c>SubAgentManager.RunUnderActorScopeAsync</c> is replaced by
///         a bare <c>agent.RunAsync(ct)</c>, and that is the failure direction that matters — a
///         sub-agent whose run loop carries no scope reads as the root and is PERMITTED to wipe the
///         board. This test closes that hole by never touching <c>AgentActorScope</c> itself: the only
///         thing that can set the actor here is the manager's own wiring.
///     </para>
///     <para>
///         Both providers are scripted <see cref="IStreamingAgent" /> mocks, so the loops, the function
///         registry, the tool dispatch, the spawn and the inherited toolset are all the production
///         types. The sub-agent inherits <c>bulk-initialize</c> the way production hands it down —
///         through the parent manager's inheritable contracts, resolving to the handler that closes
///         over the shared board.
///     </para>
///     <para>
///         The root's own clear runs through the SAME parent loop on a later turn rather than by
///         calling the board directly, so the positive case is dispatched exactly like the refused one
///         and differs only in who is running. Its archived pre-clear snapshot does double duty: it is
///         the proof that <c>OnCleared</c> fires for the root, and it is the proof that the sub-agent
///         changed nothing — had the refusal not held, the board captured there would be the
///         sub-agent's plan instead of the root's rows.
///     </para>
/// </remarks>
public class SubAgentTodoBoardClearGuardTests
{
    private const string ThreadId = "conv-clear-guard";
    private const string RootRowTitle = "Wire the SSE endpoint";
    private const string RootNote = "waiting on schema";
    private const string RootArtifact = "docs/spec.md";

    [Fact]
    public async Task SubAgentClearIsRefusedByTheRealRunLoop_AndTheRootsOwnClearStillArchivesAndSucceeds()
    {
        // --- The shared conversation board, with the kind of work bug 19 destroyed. ---
        var board = new TaskManager { ThreadId = ThreadId };
        _ = board.AddTask(RootRowTitle); // 1
        _ = board.AddTask("Add the map", "1"); // 1.1
        _ = board.AddTask("Vitest coverage"); // 2
        _ = board.AddNote("1", noteText: RootNote);
        _ = board.AttachArtifact("1", RootArtifact);
        _ = board.ClaimTask("1.1", "agent-a");
        _ = board.UpdateTask("1.1", "completed", agent: "agent-a");

        TodoBoardSnapshot? archivedAtRootClear = null;
        var clearedRaised = 0;
        board.OnCleared += snapshot =>
        {
            clearedRaised++;
            archivedAtRootClear = snapshot;
        };

        var registry = new FunctionRegistry();
        _ = registry.AddFunctionsFromObject(board, providerName: "TaskManager");

        // --- The sub-agent: asks for a fresh start, then reports back. ---
        // Its second turn is handed the tool result for the first, which is where the refusal is read.
        var subAgentContextLock = new object();
        List<IMessage> subAgentSecondTurnContext = [];
        var subAgentTurn = 0;

        var subAgentProvider = new Mock<IStreamingAgent>();
        _ = subAgentProvider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (messages, _, _) =>
                {
                    var turn = Interlocked.Increment(ref subAgentTurn);
                    if (turn == 1)
                    {
                        return Task.FromResult(
                            ToAsyncEnumerable([ClearCall("call_subagent_clear", "Sub-agent fresh start")])
                        );
                    }

                    lock (subAgentContextLock)
                    {
                        subAgentSecondTurnContext = [.. messages];
                    }

                    return Task.FromResult(
                        ToAsyncEnumerable([
                            new TextMessage { Text = "Reported back to the lead.", Role = Role.Assistant },
                        ])
                    );
                }
            );

        // --- The root agent: spawns the sub-agent, then clears the board itself, then answers. ---
        var parentTurn = 0;
        var parentProvider = new Mock<IStreamingAgent>();
        _ = parentProvider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns<IEnumerable<IMessage>, GenerateReplyOptions, CancellationToken>(
                (_, _, _) =>
                {
                    var turn = Interlocked.Increment(ref parentTurn);
                    return turn switch
                    {
                        // Synchronous spawn: the sub-agent has finished, and its clear has been
                        // decided, before this loop takes its next turn.
                        1 => Task.FromResult(
                            ToAsyncEnumerable([
                                new ToolCallMessage
                                {
                                    FunctionName = "Agent",
                                    FunctionArgs = JsonSerializer.Serialize(
                                        new { subagent_type = "wiper", prompt = "Re-plan the work" }
                                    ),
                                    ToolCallId = "call_spawn_wiper",
                                    Role = Role.Assistant,
                                },
                            ])
                        ),
                        2 => Task.FromResult(ToAsyncEnumerable([ClearCall("call_root_clear", "Root fresh start")])),
                        _ => Task.FromResult(
                            ToAsyncEnumerable([new TextMessage { Text = "Re-planned.", Role = Role.Assistant }])
                        ),
                    };
                }
            );

        var subAgentOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["wiper"] = new SubAgentTemplate
                {
                    Name = "wiper",
                    SystemPrompt = "You re-plan work on the shared board.",
                    AgentFactory = () => subAgentProvider.Object,
                },
            },
            MaxConcurrentSubAgents = 3,
        };

        await using var loop = new MultiTurnAgentLoop(
            parentProvider.Object,
            registry,
            threadId: ThreadId,
            subAgentOptions: subAgentOptions
        );

        // Bounded so a wiring regression that parks the run surfaces as a timeout rather than hanging
        // the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var runTask = loop.RunAsync(cts.Token);

        var produced = new List<IMessage>();
        await foreach (
            var message in loop.ExecuteRunAsync(
                new UserInput([new TextMessage { Text = "Re-plan the work", Role = Role.User }]),
                cts.Token
            )
        )
        {
            produced.Add(message);
        }

        // --- (a) The sub-agent's clear was refused, and it was told why. ---
        List<IMessage> subAgentSaw;
        lock (subAgentContextLock)
        {
            subAgentSaw = subAgentSecondTurnContext;
        }

        subAgentSaw
            .Should()
            .NotBeEmpty(
                "the sub-agent must have taken a second turn; without one its bulk-initialize never ran "
                    + "and this test would prove nothing"
            );

        var subAgentClearResult = ToolResults(subAgentSaw).FirstOrDefault(r => r.ToolCallId == "call_subagent_clear");

        subAgentClearResult.Should().NotBeNull("the sub-agent's bulk-initialize must have produced a tool result");
        subAgentClearResult!.ErrorCode.Should().Be("board_clear_not_permitted");
        subAgentClearResult.IsError.Should().BeTrue();
        subAgentClearResult
            .Result.Should()
            .Contain("clearExisting=false")
            .And.Contain("add-task", "the refusal must send the sub-agent to the path it is allowed to take");

        // --- (b) The root's clear succeeded, through the same dispatch, one turn later. ---
        var rootClearResult = ToolResults(produced).FirstOrDefault(r => r.ToolCallId == "call_root_clear");

        rootClearResult.Should().NotBeNull("the root agent's bulk-initialize must have produced a tool result");
        rootClearResult!.ErrorCode.Should().BeNull("a clear by the root agent is permitted");
        rootClearResult.IsError.Should().BeFalse();

        // --- (c) What the root's clear archived is the ROOT's board, untouched by the sub-agent. ---
        // This is the assertion that makes (a) non-vacuous at the state level: had the sub-agent's
        // clear landed, the pre-clear board captured here would be the sub-agent's single row.
        clearedRaised.Should().Be(1, "exactly one clear was permitted, so exactly one board was archived");
        archivedAtRootClear.Should().NotBeNull();
        archivedAtRootClear!.Tasks.Should().HaveCount(2);
        archivedAtRootClear.Tasks[0].Title.Should().Be(RootRowTitle);
        archivedAtRootClear.Tasks[0].Notes.Should().ContainSingle().Which.Should().Be(RootNote);
        archivedAtRootClear.Tasks[0].Artifacts.Should().ContainSingle().Which.Should().Be(RootArtifact);
        archivedAtRootClear
            .Tasks[0]
            .SubTasks.Should()
            .ContainSingle()
            .Which.Status.Should()
            .Be(TodoTaskStatus.Completed);
        archivedAtRootClear.Tasks[1].Title.Should().Be("Vitest coverage");

        // And the live board now holds only what the ROOT asked for.
        board.GetTasks().Should().ContainSingle().Which.Title.Should().Be("Root fresh start");

        await cts.CancelAsync();
        try
        {
            await runTask;
        }
        catch (OperationCanceledException)
        {
            // Cancelling the token is how this test ends the loop.
        }
    }

    /// <summary>A <c>bulk-initialize</c> call that asks to wipe the board first.</summary>
    private static ToolCallMessage ClearCall(string toolCallId, string newTaskTitle) =>
        new()
        {
            FunctionName = "bulk-initialize",
            FunctionArgs = JsonSerializer.Serialize(
                new { tasks = new[] { new { task = newTaskTitle } }, clearExisting = true }
            ),
            ToolCallId = toolCallId,
            Role = Role.Assistant,
        };

    /// <summary>
    ///     Every tool result in <paramref name="messages" />, whether the loop recorded it as a single
    ///     <see cref="ToolCallResultMessage" /> or inside an aggregate
    ///     <see cref="ToolsCallResultMessage" />. Both shapes reach a provider's context depending on
    ///     how the turn was assembled, and a test that knew only one would go quietly vacuous the day
    ///     the other is used.
    /// </summary>
    private static List<ToolCallResult> ToolResults(IEnumerable<IMessage> messages)
    {
        var results = new List<ToolCallResult>();
        foreach (var message in messages)
        {
            switch (message)
            {
                case ToolCallResultMessage single:
                    results.Add(
                        new ToolCallResult(single.ToolCallId, single.Result)
                        {
                            ToolName = single.ToolName,
                            IsError = single.IsError,
                            ErrorCode = single.ErrorCode,
                        }
                    );
                    break;

                case ToolsCallResultMessage aggregate:
                    results.AddRange(aggregate.ToolCallResults);
                    break;

                default:
                    // Every other message kind carries no tool result.
                    break;
            }
        }

        return results;
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
