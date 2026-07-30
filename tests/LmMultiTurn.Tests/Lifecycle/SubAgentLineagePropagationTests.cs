using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using FluentAssertions;
using Moq;
using Xunit;

namespace LmMultiTurn.Tests.Lifecycle;

/// <summary>
/// Covers what a subscriber needs in order to rebuild the agent tree from a flat event stream: every
/// event a spawned agent publishes says which run asked for it, through which tool call, and under
/// which sub-agent id.
/// </summary>
/// <remarks>
/// These drive a real child <see cref="MultiTurnAgentLoop"/> through <see cref="SubAgentManager"/>
/// rather than asserting on <see cref="MultiTurnLifecycleServices.ForSpawnedAgent"/> in isolation.
/// The derivation function was never the risky part — the risk is that the spawn path forgets to call
/// it, or calls it with lineage re-derived at the wrong moment.
/// </remarks>
public class SubAgentLineagePropagationTests
{
    private const string TemplateName = "test-agent";

    [Fact]
    public async Task SpawnedAgentsEventsCarryTheSpawningRunAndToolCall()
    {
        var publisher = new RecordingLifecyclePublisher();
        var manager = CreateManager(
            CreateBundle(publisher),
            parentThreadId: "parent-thread",
            parentRunId: "parent-run-7");

        _ = await manager.SpawnAsync(
            TemplateName,
            "do some work",
            spawningToolCallId: "call-abc");

        var started = publisher.CorrelationsFor(LifecycleEventTypes.RunStarted);
        started.Should().NotBeEmpty("the spawned loop starts a run of its own");

        var lineage = started[0];
        lineage.ParentThreadId.Should().Be("parent-thread");
        lineage.ParentRunId.Should().Be("parent-run-7");
        lineage.SpawningToolCallId.Should().Be("call-abc");
        lineage.SubAgentId.Should().NotBeNullOrWhiteSpace();

        // The child's own identity stays its own — lineage says where it came from, not who it is.
        lineage.ThreadId.Should().NotBe("parent-thread");
        lineage.RunId.Should().NotBeNullOrWhiteSpace().And.NotBe("parent-run-7");
    }

    [Fact]
    public async Task EveryEventFromOneSpawnCarriesTheSameSubAgentId()
    {
        var publisher = new RecordingLifecyclePublisher();
        var manager = CreateManager(CreateBundle(publisher));

        _ = await manager.SpawnAsync(TemplateName, "do some work", spawningToolCallId: "call-1");

        var subAgentIds = publisher
            .Events.Select(e => e.Correlation?.SubAgentId)
            .Where(id => id != null)
            .Distinct()
            .ToList();

        subAgentIds
            .Should()
            .ContainSingle("a subscriber groups a sub-agent's whole event stream by that one id");
    }

    [Fact]
    public async Task SpawnWithoutAToolCallStillReportsTheParentRun()
    {
        // Sub-agents can be spawned by host code rather than by a model tool call. That is a missing
        // tool call id, not missing lineage — the parent run is still what a subscriber attributes to.
        var publisher = new RecordingLifecyclePublisher();
        var manager = CreateManager(
            CreateBundle(publisher),
            parentThreadId: "parent-thread",
            parentRunId: "parent-run-9");

        _ = await manager.SpawnAsync(TemplateName, "do some work");

        var lineage = publisher.CorrelationsFor(LifecycleEventTypes.RunStarted)[0];
        lineage.SpawningToolCallId.Should().BeNull();
        lineage.ParentRunId.Should().Be("parent-run-9");
        lineage.SubAgentId.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task QueuedSpawnCapturesLineageWhenAccepted_NotWhenCapacityFrees()
    {
        var publisher = new RecordingLifecyclePublisher();
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent.SetupGet(a => a.ThreadId).Returns("parent-thread");
        _ = parent.SetupGet(a => a.CurrentRunId).Returns("parent-run-original");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        IStreamingAgent Factory()
        {
            var agent = new Mock<IStreamingAgent>();
            _ = agent.Setup(a => a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions?>(),
                    It.IsAny<CancellationToken>()))
                .Returns(() => Task.FromResult(Interlocked.Increment(ref calls) == 1
                    ? WaitThenReply(gate.Task)
                    : Reply()));
            return agent.Object;
        }

        var options = new SubAgentOptions
        {
            MaxConcurrentSubAgents = 1,
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                [TemplateName] = new SubAgentTemplate { SystemPrompt = "test", AgentFactory = Factory },
            },
        };
        await using var manager = new SubAgentManager(
            parent.Object,
            [],
            new Dictionary<string, ToolHandler>(),
            options,
            new MutableSubAgentTemplateSource(options.Templates),
            lifecycleServices: CreateBundle(publisher));

        _ = await manager.SpawnAsync(TemplateName, "block", runInBackground: true);
        _ = await manager.SpawnAsync(
            TemplateName,
            "queued",
            runInBackground: true,
            spawningToolCallId: "call-queued");
        _ = parent.SetupGet(a => a.CurrentRunId).Returns("parent-run-later");
        gate.SetResult();

        await WaitUntilAsync(
            () => publisher.CorrelationsFor(LifecycleEventTypes.RunStarted).Count >= 2,
            TimeSpan.FromSeconds(10));
        var queued = publisher.CorrelationsFor(LifecycleEventTypes.RunStarted)
            .Single(c => c.SpawningToolCallId == "call-queued");
        queued.ParentRunId.Should().Be("parent-run-original");
    }

    [Fact]
    public async Task RestartAttributesTheChildToTheRunThatSpawnedIt_NotWhateverRunIsInFlight()
    {
        // The regression this exists for: a restart rebuilds the loop long after the spawning run
        // ended. Re-deriving lineage at rebuild time would read the parent's CURRENT run — which by
        // then is a different run, or none — and silently re-parent the whole sub-tree.
        var publisher = new RecordingLifecyclePublisher();
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent.SetupGet(a => a.ThreadId).Returns("parent-thread");
        _ = parent.SetupGet(a => a.CurrentRunId).Returns("parent-run-original");

        var manager = CreateManager(CreateBundle(publisher), parent);

        var spawned = await manager.SpawnAsync(
            TemplateName,
            "do some work",
            name: "worker",
            spawningToolCallId: "call-original");
        _ = spawned.Should().NotBeNull();

        // The spawning run is over; the parent has moved on to another one.
        _ = parent.SetupGet(a => a.CurrentRunId).Returns("parent-run-later");

        _ = await manager.SendMessageAsync("worker", "keep going");

        var started = publisher.CorrelationsFor(LifecycleEventTypes.RunStarted);
        started.Should().HaveCountGreaterThan(1, "the restart starts a second run on the same child");

        // The spawn edge is on the first run, where nothing in-thread precedes it.
        started[0].ParentRunId.Should().Be("parent-run-original");

        // What carries the spawn across every later run: re-deriving lineage at rebuild time would
        // lose the tool call outright and read the parent's by-then-current run.
        started
            .Should()
            .OnlyContain(c => c.SpawningToolCallId == "call-original")
            .And.OnlyContain(c => c.ParentThreadId == "parent-thread")
            .And.NotContain(c => c.ParentRunId == "parent-run-later");

        // Same child across the restart, so a subscriber's grouping survives it.
        started.Select(c => c.SubAgentId).Distinct().Should().ContainSingle();

        // And the run after the spawn points at the run it continued, not back at the spawn: an
        // in-thread cause outranks lineage, so a subscriber walks the chain to reach the spawn.
        started[1].ParentRunId.Should().Be(started[0].RunId);
    }

    [Fact]
    public async Task WithoutLifecycleWiringTheChildPublishesNothing()
    {
        var publisher = new RecordingLifecyclePublisher();

        // The parent observes nothing, so the child inherits nothing — lineage with no subscriber to
        // read it is bookkeeping nobody asked for.
        var manager = CreateManager(lifecycleServices: null);

        _ = await manager.SpawnAsync(TemplateName, "do some work", spawningToolCallId: "call-1");

        publisher.Events.Should().BeEmpty();
    }

    [Fact]
    public void DerivedChildBundleDropsTheParentsModelButKeepsItsWiring()
    {
        // ModelId described the PARENT's model. Carried forward, it would beat the child's own
        // resolved model in ForAgent's `services.ModelId ?? modelId`, and every sub-agent event would
        // claim a model the child never called.
        var publisher = new RecordingLifecyclePublisher();
        var parent = CreateBundle(publisher) with { ModelId = "parent-model" };
        var lineage = new AgentLineage { ParentThreadId = "t", SubAgentId = "s" };

        var child = MultiTurnLifecycleServices.ForSpawnedAgent(parent, lineage);

        child.ModelId.Should().BeNull();
        child.Lineage.Should().BeSameAs(lineage);
        child.Publisher.Should().BeSameAs(parent.Publisher);
        child.SequenceAllocator.Should().BeSameAs(parent.SequenceAllocator, "one epoch across the tree");
        child.Approval.Should().BeSameAs(parent.Approval, "a gated parent did not ask for ungated children");

        MultiTurnLifecycleServices
            .ForSpawnedAgent(null, lineage)
            .Should()
            .BeSameAs(MultiTurnLifecycleServices.Disabled);
        MultiTurnLifecycleServices
            .ForSpawnedAgent(MultiTurnLifecycleServices.Disabled, lineage)
            .Should()
            .BeSameAs(MultiTurnLifecycleServices.Disabled);
    }

    private static MultiTurnLifecycleServices CreateBundle(RecordingLifecyclePublisher publisher) =>
        new() { Publisher = publisher };

    private static SubAgentManager CreateManager(
        MultiTurnLifecycleServices? lifecycleServices,
        string parentThreadId = "parent-thread",
        string? parentRunId = "parent-run")
    {
        var parent = new Mock<IMultiTurnAgent>();
        _ = parent.SetupGet(a => a.ThreadId).Returns(parentThreadId);
        _ = parent.SetupGet(a => a.CurrentRunId).Returns(parentRunId);
        return CreateManager(lifecycleServices, parent);
    }

    private static SubAgentManager CreateManager(
        MultiTurnLifecycleServices? lifecycleServices,
        Mock<IMultiTurnAgent> parent)
    {
        var template = new SubAgentTemplate
        {
            SystemPrompt = "You are a test agent.",
            AgentFactory = CreateRespondingAgent,
        };
        var options = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate> { [TemplateName] = template },
        };

        return new SubAgentManager(
            parentAgent: parent.Object,
            parentContracts: [],
            parentHandlers: new Dictionary<string, ToolHandler>(),
            options: options,
            source: new MutableSubAgentTemplateSource(options.Templates),
            lifecycleServices: lifecycleServices);
    }

    /// <summary>A provider that answers once with plain text, so the child's run reaches a terminal state.</summary>
    private static IStreamingAgent CreateRespondingAgent()
    {
        var agent = new Mock<IStreamingAgent>();
        _ = agent
            .Setup(a => a.GenerateReplyStreamingAsync(
                It.IsAny<IEnumerable<IMessage>>(),
                It.IsAny<GenerateReplyOptions?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToAsyncEnumerable([new TextMessage { Text = "done", Role = Role.Assistant }]));
        return agent.Object;
    }

    private static async IAsyncEnumerable<IMessage> WaitThenReply(Task gate)
    {
        await gate;
        yield return new TextMessage { Text = "done", Role = Role.Assistant };
    }

    private static async IAsyncEnumerable<IMessage> Reply()
    {
        yield return new TextMessage { Text = "done", Role = Role.Assistant };
        await Task.Yield();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (!predicate())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private static async IAsyncEnumerable<IMessage> ToAsyncEnumerable(IEnumerable<IMessage> messages)
    {
        foreach (var message in messages)
        {
            yield return message;
            await Task.Yield();
        }
    }
}
