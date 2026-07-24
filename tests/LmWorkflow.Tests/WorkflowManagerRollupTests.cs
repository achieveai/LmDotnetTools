using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Moq;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Coverage for the sample-server contract added on <see cref="WorkflowManager"/>: usage roll-up of an
///     isolated run into an external root sink (issue #196 parity), non-blocking <see cref="WorkflowManager.ListRuns"/>
///     summaries, and <see cref="WorkflowManager.TryGetRunLoop"/> exposing the live controller loop.
/// </summary>
public class WorkflowManagerRollupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static WorkflowManager NewManager(
        Func<IStreamingAgent> controllerFactory,
        IUsageSink? usageSink = null
    ) =>
        new(
            controllerFactory,
            EmptyControllerOptions(),
            // WorkflowManager takes a LATE-BOUND getter (a host may build it before its root loop exists);
            // wrap the eager test sink in one. A null sink stays a null getter (usage scoped to the run).
            rootUsageSink: usageSink is null ? null : () => usageSink
        );

    [Fact]
    public async Task StartAsync_FoldsControllerUsage_IntoProvidedUsageSink()
    {
        var sink = new RecordingUsageSink();
        var controller = ScriptedControllerMulti(UsageThenRoute);
        await using var manager = NewManager(() => controller.Object, usageSink: sink);

        var result = await manager.StartAsync("wf-usage", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("completed");

        // The isolated controller loop's own turn usage was relayed into the external root sink (#196).
        // Sub-agent (task) usage folds through the SAME controller-loop ledger, so this single forward hook
        // covers both paths; here the empty-template controller exercises the controller-own path.
        sink.TotalInputTokens.Should().Be(100);
        sink.TotalOutputTokens.Should().Be(40);
        sink.Records.Should().ContainSingle().Which.ProviderAttemptId.Should().Contain("gen-ctrl-1");
    }

    [Fact]
    public async Task ListRuns_ReportsRunningWorkflow_WithObjectiveStatusAndNode()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-list", MinimalDefinition(), WorkflowStartMode.Async);

        var run = manager.ListRuns().Should().ContainSingle().Subject;
        run.WorkflowId.Should().Be("wf-list");
        run.Objective.Should().Be("trivial"); // from MinimalValid's "objective"
        run.Status.Should().Be("running");
        run.CurrentNodeId.Should().Be("s"); // positioned on the start node before the controller routes
        run.StartedUtc.Should().BeOnOrBefore(DateTimeOffset.UtcNow);
        run.LastActivityUtc.Should().NotBeNull();

        // Release the controller and let it finish; ListRuns then reports the terminal state + node.
        gate.SetResult();
        _ = await manager.WaitAsync("wf-list", Timeout);

        var done = manager.ListRuns().Should().ContainSingle().Subject;
        done.Status.Should().Be("completed");
        done.CurrentNodeId.Should().Be("t");
    }

    [Fact]
    public async Task TryGetRunLoop_ReturnsLiveLoop_WhileRunning_ThenFalseWhenReleased()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-loop", MinimalDefinition(), WorkflowStartMode.Async);

        manager.TryGetRunLoop("wf-loop", out var loop).Should().BeTrue();
        loop.Should().NotBeNull();

        // An unknown id yields no loop.
        manager.TryGetRunLoop("nope", out var missing).Should().BeFalse();
        missing.Should().BeNull();

        // Once the run completes, the heavy graph (including the controller loop) is released, so the accessor
        // reports false. WaitAsync can observe the terminal snapshot slightly before the observer nulls the
        // handle, so poll briefly for that handoff (mirrors the existing terminal-then-disposal tests).
        gate.SetResult();
        _ = await manager.WaitAsync("wf-loop", Timeout);

        var released = false;
        for (var attempt = 0; attempt < 100 && !released; attempt++)
        {
            if (!manager.TryGetRunLoop("wf-loop", out _))
            {
                released = true;
            }
            else
            {
                await Task.Delay(20);
            }
        }

        released.Should().BeTrue();
    }

    [Fact]
    public async Task TryGetRunLoopOwningSubAgent_ReturnsFalse_WhenNoRunOwnsTheId()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-owner", MinimalDefinition(), WorkflowStartMode.Async);

        // A running workflow exists, but it has spawned no delegates, so no run owns this id.
        manager.TryGetRunLoopOwningSubAgent("no-such-delegate", out var loop).Should().BeFalse();
        loop.Should().BeNull();

        gate.SetResult();
        _ = await manager.WaitAsync("wf-owner", Timeout);
    }

    [Fact]
    public async Task ListRunDelegates_ReturnsEmpty_ForUnknownRun_AndRunWithoutDelegates()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-delegates", MinimalDefinition(), WorkflowStartMode.Async);

        // Unknown run → empty.
        manager.ListRunDelegates("nope").Should().BeEmpty();
        // A live run that spawned no delegates → empty (live loop path).
        manager.ListRunDelegates("wf-delegates").Should().BeEmpty();

        // After completion the loop is released; the retained (empty) snapshot path still returns empty
        // rather than throwing.
        gate.SetResult();
        _ = await manager.WaitAsync("wf-delegates", Timeout);
        manager.ListRunDelegates("wf-delegates").Should().BeEmpty();
    }

    [Fact]
    public async Task StartAsync_PersistsControllerConversation_WhenStoreProvided()
    {
        var store = new InMemoryConversationStore();
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = new WorkflowManager(
            () => controller.Object,
            EmptyControllerOptions(),
            controllerConversationStore: store);

        var result = await manager.StartAsync("wf-view", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("completed");

        // The workflow agent's OWN conversation (the controller's orchestration turns) is persisted under
        // the workflow-{id} thread, so it remains viewable — via the messages endpoint / the ⚙ workflow tab —
        // after the run completes and the controller loop is disposed.
        var messages = await store.LoadMessagesAsync("workflow-wf-view");
        messages.Should().NotBeEmpty();
    }

    [Fact]
    public async Task StartAsync_WithPreferredProvider_InvokesProfileFactory_AndItsControllerDrivesTheRun()
    {
        string? capturedProvider = null;
        var profileController = ScriptedController(DriveMinimalToTerminal);

        await using var manager = new WorkflowManager(
            // The fixed default must NOT be used when a preferred provider is supplied: NeverComplete would
            // hang the run, so a completed result proves the PROFILE's controller drove it.
            controllerAgentFactory: () => ScriptedController(NeverComplete).Object,
            controllerSubAgentOptions: EmptyControllerOptions(),
            controllerProfileByProvider: providerId =>
            {
                capturedProvider = providerId;
                return new WorkflowControllerProfile(() => profileController.Object, EmptyControllerOptions());
            }
        );

        var result = await manager.StartAsync(
            "wf-provider",
            MinimalDefinition(),
            WorkflowStartMode.Sync,
            preferredProvider: "test-anthropic"
        );

        result.Status.Should().Be("completed");
        capturedProvider.Should().Be("test-anthropic");
    }

    [Fact]
    public async Task StartAsync_WithoutPreferredProvider_UsesTheFixedDefaultFactory()
    {
        var factoryInvoked = false;
        var controller = ScriptedController(DriveMinimalToTerminal);

        await using var manager = new WorkflowManager(
            controllerAgentFactory: () => controller.Object,
            controllerSubAgentOptions: EmptyControllerOptions(),
            controllerProfileByProvider: _ =>
            {
                factoryInvoked = true;
                return new WorkflowControllerProfile(() => controller.Object, EmptyControllerOptions());
            }
        );

        var result = await manager.StartAsync("wf-default", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("completed");
        factoryInvoked.Should().BeFalse("no preferred provider was supplied, so the profile factory is not called");
    }

    [Fact]
    public async Task StartAsync_WithPreferredModel_FoldsOntoControllerDefaultOptions()
    {
        GenerateReplyOptions? capturedOptions = null;
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
            .Callback<IEnumerable<IMessage>, GenerateReplyOptions?, CancellationToken>(
                (_, opts, _) => capturedOptions ??= opts
            )
            .Returns(() => Task.FromResult(ToAsyncEnumerable([DriveMinimalToTerminal(++turn)])));

        await using var manager = new WorkflowManager(
            () => controller.Object,
            EmptyControllerOptions(),
            controllerDefaultOptions: new GenerateReplyOptions { ModelId = "configured-model" }
        );

        _ = await manager.StartAsync(
            "wf-model",
            MinimalDefinition(),
            WorkflowStartMode.Sync,
            preferredModel: "preferred-model"
        );

        capturedOptions.Should().NotBeNull();
        capturedOptions!.ModelId.Should().Be("preferred-model");
    }

    [Fact]
    public async Task StartAsync_WithProfile_ThatDoesNotExcludeWorkflowTools_ThrowsPerRun()
    {
        // The per-run profile's options are built OUTSIDE the ctor, so the structural exclusion is re-asserted
        // when the run resolves the profile — a profile whose (non-empty) templates could inherit the workflow
        // tools (no NonInheritedToolNames) must be rejected before the controller loop is built.
        var unexcludedOptions = new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["gp"] = new SubAgentTemplate
                {
                    SystemPrompt = "gp",
                    AgentFactory = () => ScriptedController(DriveMinimalToTerminal).Object,
                },
            },
        };

        await using var manager = new WorkflowManager(
            controllerAgentFactory: () => ScriptedController(DriveMinimalToTerminal).Object,
            controllerSubAgentOptions: EmptyControllerOptions(),
            controllerProfileByProvider: _ => new WorkflowControllerProfile(
                () => ScriptedController(DriveMinimalToTerminal).Object,
                unexcludedOptions
            )
        );

        var act = async () =>
            await manager.StartAsync(
                "wf-bad-profile",
                MinimalDefinition(),
                WorkflowStartMode.Sync,
                preferredProvider: "test"
            );

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*NonInheritedToolNames*");
    }

    /// <summary>Turn 1 reports a provider usage message alongside the routing tool call; turn 2 ends the run.</summary>
    private static IReadOnlyList<IMessage> UsageThenRoute(int turn) =>
        turn switch
        {
            1 =>
            [
                new UsageMessage
                {
                    Usage = new Usage { PromptTokens = 100, CompletionTokens = 40 },
                    GenerationId = "gen-ctrl-1",
                },
                ToolCall("SetCurrentNode", new JsonObject { ["nextNodeId"] = "t" }, "tc_route"),
            ],
            _ => [new TextMessage { Text = "Workflow finished.", Role = Role.Assistant }],
        };

    /// <summary>A fake external root sink that dedups relayed records by provider-attempt id, like a real one.</summary>
    private sealed class RecordingUsageSink : IUsageSink
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, UsageRecord> _byAttempt = new(StringComparer.Ordinal);

        public void RecordUsage(UsageRecord observation)
        {
            lock (_gate)
            {
                _byAttempt[observation.ProviderAttemptId] = observation;
            }
        }

        public IReadOnlyList<UsageRecord> Records
        {
            get
            {
                lock (_gate)
                {
                    return [.. _byAttempt.Values];
                }
            }
        }

        public long TotalInputTokens
        {
            get
            {
                lock (_gate)
                {
                    return _byAttempt.Values.Sum(r => r.InputTokens);
                }
            }
        }

        public long TotalOutputTokens
        {
            get
            {
                lock (_gate)
                {
                    return _byAttempt.Values.Sum(r => r.OutputTokens);
                }
            }
        }
    }
}
