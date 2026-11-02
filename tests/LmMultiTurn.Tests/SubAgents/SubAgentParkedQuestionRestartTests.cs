using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.SubAgents;

/// <summary>
/// A sub-agent parked on its own <c>AskUserQuestion</c> is running when its host stops, so a rebuilt root
/// does not restore it, and before <see cref="SubAgentManager.TryResolveToolCallAsync"/> its question could
/// never be answered again. The answer must rebuild it (and each parent between it and the root) from the
/// saved records, and land on its own thread.
/// </summary>
public class SubAgentParkedQuestionRestartTests
{
    private const string RootThread = "thread-parked-question";
    private const string CallId = "tc_color";

    [Fact]
    public async Task AnAnswerAfterARestart_RebuildsTheParkedSubAgent_AndResolvesItOnItsOwnThread()
    {
        var store = new InMemoryConversationStore();
        var options = Options(store);
        string thread;
        await using (var first = RootLoop(store, options))
        {
            _ = first.RunAsync();
            await first.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
            _ = await first.SubAgentManager!.SpawnAsync("asker", "pick a color", name: "asker", runInBackground: true);
            thread = await ParkedThreadAsync(first.SubAgentManager, "asker");
        }

        await using var rebuilt = RootLoop(store, options);
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var manager = rebuilt.SubAgentManager!;
        manager.ListAgents().Should().BeEmpty("a sub-agent that was running at shutdown is not restored");

        (await manager.TryResolveToolCallAsync(thread + "-stale", CallId, "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.NotFound, "an unknown thread never falls back to another one");
        (await manager.TryResolveToolCallAsync(thread, "tc_other", "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.NotFound, "the sub-agent never parked that call");
        manager.ListAgents().Should().BeEmpty("a refused answer wakes nothing");
        (await manager.TryResolveToolCallAsync(thread, CallId, "Red")).Should().Be(ResolveToolCallOutcome.Resolved);

        await Wait.UntilAsync(
            () => manager.ListAgents().Any(a => a.Name == "asker" && a.Status == SubAgentStatus.Completed),
            "the answer's run finished the sub-agent",
            observed: () => string.Join(", ", manager.ListAgents().Select(a => $"{a.Name}={a.Status}"))
        );
        manager.CheckAgents(["asker"]).Entries.Single().LastResult.Should().Be("chose Red");
        (await manager.TryResolveToolCallAsync(thread, CallId, "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.Duplicate, "the same answer again changes nothing");
    }

    [Fact]
    public async Task AnAnswerToACallAlreadyAnswered_AfterARestart_LeavesTheFinishedSubAgentAsItIs()
    {
        var store = new InMemoryConversationStore();
        var options = Options(store);
        string thread;
        await using (var first = RootLoop(store, options))
        {
            _ = first.RunAsync();
            await first.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var manager = first.SubAgentManager!;
            _ = await manager.SpawnAsync("asker", "pick a color", name: "asker", runInBackground: true);
            thread = await ParkedThreadAsync(manager, "asker");
            (await manager.TryResolveToolCallAsync(thread, CallId, "Red")).Should().Be(ResolveToolCallOutcome.Resolved);
            await Wait.UntilAsync(
                () => manager.ListAgents().Any(a => a.Name == "asker" && a.Status == SubAgentStatus.Completed),
                "the answer's run finished the sub-agent",
                observed: () => string.Join(", ", manager.ListAgents().Select(a => $"{a.Name}={a.Status}"))
            );
        }

        await using var rebuilt = RootLoop(store, options);
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var restored = rebuilt.SubAgentManager!;

        (await restored.TryResolveToolCallAsync(thread, CallId, "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.Duplicate, "the transcript already holds this answer");
        (await restored.TryResolveToolCallAsync(thread, CallId, "Blue"))
            .Should()
            .Be(ResolveToolCallOutcome.Conflict, "the transcript holds a different answer");
        (await restored.TryResolveToolCallAsync(thread, "tc_other", "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.NotFound);

        restored
            .ListAgents()
            .Select(a => $"{a.Name}={a.Status}")
            .Should()
            .Equal(["asker=Completed"], "a refused answer neither restarts nor re-registers the sub-agent");
        (restored.TryGetAgent("asker", out var asker) && asker is MultiTurnAgentLoop)
            .Should()
            .BeFalse("its loop was never rebuilt");
    }

    [Fact]
    public async Task AnAnswerAfterARestart_ToAQuestionSettledEarly_RebuildsTheSubAgent_AndRedirectsItOnce()
    {
        var store = new InMemoryConversationStore();
        var options = Options(store);
        string thread;
        await using (var first = RootLoop(store, options))
        {
            _ = first.RunAsync();
            await first.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var manager = first.SubAgentManager!;
            _ = await manager.SpawnAsync("asker", "pick a color", name: "asker", runInBackground: true);
            thread = await ParkedThreadAsync(manager, "asker");
            manager.TryGetAgent("asker", out var asker).Should().BeTrue();
            _ = await asker
                .Should()
                .BeOfType<MultiTurnAgentLoop>()
                .Subject.SendAsync([new TextMessage { Text = "hold on", Role = Role.User }]);
            await Wait.UntilAsync(
                () => manager.ListAgents().Any(a => a.Name == "asker" && a.Status == SubAgentStatus.Completed),
                "the interrupting input settled the question early and finished the sub-agent",
                observed: () => string.Join(", ", manager.ListAgents().Select(a => $"{a.Name}={a.Status}"))
            );
            manager.CheckAgents(["asker"]).Entries.Single().LastResult.Should().Be(StillWaiting);
        }

        await using (var rebuilt = RootLoop(store, options))
        {
            _ = rebuilt.RunAsync();
            await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var manager = rebuilt.SubAgentManager!;

            (await manager.TryResolveToolCallAsync(thread, CallId, "Red"))
                .Should()
                .Be(ResolveToolCallOutcome.Resolved, "a question settled early is still open to its answer");
            manager.TryGetAgent("asker", out var woken).Should().BeTrue();
            woken.Should().BeOfType<MultiTurnAgentLoop>().Which.ThreadId.Should().Be(thread);
            await Wait.UntilAsync(
                () => manager.CheckAgents(["asker"]).Entries.Single().LastResult == "chose Red",
                "the redirected answer reached the sub-agent's next run",
                observed: () => manager.CheckAgents(["asker"]).Entries.Single().LastResult ?? "(none)"
            );
            await Wait.UntilAsync(
                () => manager.ListAgents().Any(a => a.Name == "asker" && a.Status == SubAgentStatus.Completed),
                "the answer's run finished the sub-agent",
                observed: () => string.Join(", ", manager.ListAgents().Select(a => $"{a.Name}={a.Status}"))
            );
        }

        await using var again = RootLoop(store, options);
        _ = again.RunAsync();
        await again.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var restored = again.SubAgentManager!;

        (await restored.TryResolveToolCallAsync(thread, CallId, "Red"))
            .Should()
            .Be(ResolveToolCallOutcome.Duplicate, "the transcript already carries this answer");
        (await restored.TryResolveToolCallAsync(thread, CallId, "Blue"))
            .Should()
            .Be(ResolveToolCallOutcome.Conflict, "the transcript carries a different answer");
        (restored.TryGetAgent("asker", out var asker2) && asker2 is MultiTurnAgentLoop)
            .Should()
            .BeFalse("a refused answer does not rebuild the sub-agent");
    }

    [Fact]
    public async Task AnAnswerAfterARestart_ReachesAGrandchild_ThroughTheParentItRebuilds()
    {
        var store = new InMemoryConversationStore();
        var options = Options(store);
        string leadThread;
        string thread;
        await using (var first = RootLoop(store, options, NestedCollaboration()))
        {
            _ = first.RunAsync();
            await first.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
            (
                await first.SubAgentManager!.SpawnAsync(
                    "lead",
                    "lead the work",
                    name: "lead",
                    role: "lead",
                    description: "Leads the work."
                )
            )
                .Should()
                .Be("lead done");
            first.SubAgentManager.TryGetAgent("lead", out var lead).Should().BeTrue();
            var leadLoop = lead.Should().BeOfType<MultiTurnAgentLoop>().Subject;
            leadThread = leadLoop.ThreadId;
            _ = await leadLoop.SubAgentManager!.SpawnAsync(
                "asker",
                "pick a color",
                name: "asker",
                runInBackground: true,
                role: "asker",
                description: "Asks a question."
            );
            thread = await ParkedThreadAsync(leadLoop.SubAgentManager, "asker");
        }

        await using var rebuilt = RootLoop(store, options, NestedCollaboration());
        _ = rebuilt.RunAsync();
        await rebuilt.WaitUntilReadyAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var manager = rebuilt.SubAgentManager!;
        // The grandchild's parked relay can restart the lead, so the lead is either restored as finished
        // or, still running at shutdown, not restored at all. Neither has a loop until it is needed.
        (manager.TryGetAgent("lead", out var before) && before is MultiTurnAgentLoop)
            .Should()
            .BeFalse();

        (await manager.TryResolveToolCallAsync(thread, CallId, "Red")).Should().Be(ResolveToolCallOutcome.Resolved);

        manager.TryGetAgent("lead", out var woken).Should().BeTrue();
        var wokenLoop = woken.Should().BeOfType<MultiTurnAgentLoop>().Subject;
        wokenLoop.ThreadId.Should().Be(leadThread);
        var nested = wokenLoop.SubAgentManager!;
        await Wait.UntilAsync(
            () => nested.ListAgents().Any(a => a.Name == "asker" && a.Status == SubAgentStatus.Completed),
            "the answer's run finished the grandchild",
            observed: () => string.Join(", ", nested.ListAgents().Select(a => $"{a.Name}={a.Status}"))
        );
        nested.CheckAgents(["asker"]).Entries.Single().LastResult.Should().Be("chose Red");
        manager.ListAgents().Select(a => a.Name).Should().Equal(["lead"], "the grandchild stays with its own parent");
    }

    /// <summary>Waits until the named sub-agent's loop holds the parked question, and returns its thread.</summary>
    private static async Task<string> ParkedThreadAsync(SubAgentManager manager, string name)
    {
        MultiTurnAgentLoop? loop = null;
        await Wait.UntilAsync(
            async () =>
                manager.TryGetAgent(name, out var agent)
                && (loop = agent as MultiTurnAgentLoop) is not null
                && (await loop.GetDeferredToolCallsAsync()).Any(d => d.ToolCallId == CallId),
            "the sub-agent parked on its question"
        );
        return loop!.ThreadId;
    }

    private static SubAgentOptions Options(IConversationStore store) =>
        new()
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["asker"] = new() { SystemPrompt = "You ask.", AgentFactory = AskingProvider },
                ["lead"] = new() { SystemPrompt = "You lead.", AgentFactory = () => Answering("lead done") },
            },
            DefaultConversationStoreFactory = _ => store,
        };

    private static AgentCollaborationSetup NestedCollaboration() =>
        AgentCollaborationSetup.CreateRoot(new AgentCollaborationOptions { MaxDelegationDepth = 2 });

    private static MultiTurnAgentLoop RootLoop(
        IConversationStore store,
        SubAgentOptions options,
        AgentCollaborationSetup? collaboration = null
    ) =>
        new(
            Answering("root answer"),
            new FunctionRegistry(),
            RootThread,
            store: store,
            subAgentOptions: options,
            collaboration: collaboration
        );

    /// <summary>Asks which color until the history holds the answer, then reports it.</summary>
    private static IStreamingAgent AskingProvider()
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
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
                    var answer = AnswerIn(messages);
                    return Task.FromResult(
                        answer is not null && answer.Contains("deferred_to_notification", StringComparison.Ordinal)
                            ? ToAsyncEnumerable(new TextMessage { Text = StillWaiting, Role = Role.Assistant })
                        : answer is null
                            ? ToAsyncEnumerable(
                                new ToolCallMessage
                                {
                                    FunctionName = AskUserQuestionToolProvider.ToolName,
                                    FunctionArgs = AskArgs,
                                    ToolCallId = CallId,
                                    Role = Role.Assistant,
                                }
                            )
                        : ToAsyncEnumerable(new TextMessage { Text = $"chose {answer}", Role = Role.Assistant })
                    );
                }
            );
        return provider.Object;
    }

    private const string StillWaiting = "still waiting for a color";

    /// <summary>
    /// The answer to the question, in whichever result shape the loop replays it, or the answer the loop
    /// redirected into the conversation after an early settle.
    /// </summary>
    private static string? AnswerIn(IEnumerable<IMessage> messages) =>
        messages
            .OfType<TextMessage>()
            .Select(t => t.Text)
            .LastOrDefault(t => t.Contains($"tool-call-id=\"{CallId}\"", StringComparison.Ordinal))
            ?.Split("<answer>\n")[1]
            .Split("\n</answer>")[0]
        ?? messages
            .SelectMany(m =>
                m switch
                {
                    ToolCallResultMessage { IsDeferred: false } r => [(r.ToolCallId, r.Result)],
                    ToolsCallResultMessage r => r.ToolCallResults.Select(x => (x.ToolCallId, x.Result)),
                    ToolsCallAggregateMessage a => a.ToolsCallResult.ToolCallResults.Select(x =>
                        (x.ToolCallId, x.Result)
                    ),
                    _ => [],
                }
            )
            .LastOrDefault(r => r.ToolCallId == CallId)
            .Result;

    private static readonly string AskArgs = JsonSerializer.Serialize(
        new
        {
            context = "Need a color.",
            questions = new[]
            {
                new
                {
                    prompt = "Which color?",
                    options = new object[] { new { label = "Red" }, new { label = "Blue" } },
                },
            },
        }
    );

    private static IStreamingAgent Answering(string text)
    {
        var provider = new Mock<IStreamingAgent>();
        _ = provider
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable(new TextMessage { Text = text, Role = Role.Assistant })));
        return provider.Object;
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(params IMessage[] messages)
    {
        foreach (var message in messages)
        {
            yield return message;
        }

        await Task.CompletedTask;
    }
}
