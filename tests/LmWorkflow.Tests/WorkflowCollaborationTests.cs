using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmCore.Core;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Middleware;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using AchieveAi.LmDotnetTools.LmTestUtils;
using AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;
using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using AchieveAi.LmDotnetTools.LmWorkflow.Tools;
using FluentAssertions;
using Moq;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Issue #244, workflow half: a workflow controller launched from inside a collaborating hierarchy is a
///     VISIBLE structural node that costs NO delegation budget. Everything here is written against that one
///     property and its consequences — where the controller lands, where its delegates land, what survives a
///     restart, and what happens when the hierarchy refuses to admit it.
/// </summary>
/// <remarks>
///     The controller's approval exemption and the structural tool exclusion are pinned by
///     <see cref="WorkflowSessionApprovalExemptionTests"/> and
///     <see cref="WorkflowControllerToolRestrictionTests"/>; the tests here only assert that turning
///     collaboration ON does not weaken either.
/// </remarks>
public class WorkflowCollaborationTests
{
    private const string RootAgentId = "root-1";

    [Fact]
    public async Task TheControllerIsAVisibleNodeAtTheCallersOwnDelegationDepth()
    {
        var caller = Root();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-collab-thread",
            instanceId: "wf-collab-1",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var node = caller.Directory.FindById(WorkflowCollaboration.ComposeControllerAgentId("wf-collab-1"));
        node.Should().NotBeNull("the controller is a real node in the hierarchy, not a hidden implementation detail");

        // Structurally BELOW the caller...
        node!.ParentAgentId.Should().Be(RootAgentId);
        node.StructuralDepth.Should().Be(1);
        node.Kind.Should().Be(AgentKind.WorkflowController);

        // ...but at the caller's OWN delegation depth: the controller hop is free.
        node.DelegationDepth.Should().Be(caller.Context.DelegationDepth);

        // Trusted metadata is derived from the workflow's own labels, never from anything a model supplied.
        node.Role.Should().Be(WorkflowCollaboration.ControllerRole);
        node.Description.Should().Be("trivial", "the description comes from the definition's objective");
        node.AgentType.Should().Be(WorkflowCollaboration.ControllerAgentType);

        // The same node is what gets persisted, and what the controller's own loop is holding.
        handle.CollaborationNode!.AgentId.Should().Be(node.AgentId);
        handle.Loop.Collaboration!.AgentId.Should().Be(node.AgentId);
    }

    [Fact]
    public async Task ADelegateLandsExactlyWhereAnOrdinarySubAgentOfTheCallerWouldHave()
    {
        // Non-vacuity: MaxDelegationDepth is 1. The delegate is only admissible BECAUSE the controller
        // consumed no budget — had the controller cost a hop, this spawn would be refused at depth 2.
        var caller = Root(maxDelegationDepth: 1);
        caller.Options.MaxDelegationDepth.Should().Be(1);

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: SpawningSubAgentOptions(),
            controllerAgent: ScriptedController(DriveAndSpawnDelegate).Object,
            threadId: "wf-collab-delegate-thread",
            instanceId: "wf-collab-delegate",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // The spawn actually ran (rather than being refused and silently discarded).
        handle.Runtime.IsComplete.Should().BeTrue();
        handle.Outputs["analyze"]!["task"]!["summary"]!.GetValue<string>().Should().Be("analyzed-by-subagent");

        var delegateNode = caller.Directory.Snapshot().SingleOrDefault(e => e.Kind == AgentKind.WorkflowDelegate);
        delegateNode.Should().NotBeNull("a controller's spawn is a workflow delegate, not a free sub-agent");
        delegateNode!.ParentAgentId.Should().Be(WorkflowCollaboration.ComposeControllerAgentId("wf-collab-delegate"));
        delegateNode.StructuralDepth.Should().Be(2, "root → controller → delegate");
        delegateNode.DelegationDepth.Should().Be(1, "one hop from the caller, as a direct sub-agent would be");
    }

    [Fact]
    public async Task ADelegatesPublishedIdentityComesFromTheAuthoredTaskAndNodeNotTheControllersWords()
    {
        var caller = Root();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: SpawningSubAgentOptions(),
            controllerAgent: ScriptedController(turn =>
                SpawnTurn(turn, LabelledTaskWorkflow, "hijacked-role", "hijacked description")
            ).Object,
            threadId: "wf-collab-trusted-thread",
            instanceId: "wf-collab-trusted",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        handle.Runtime.IsComplete.Should().BeTrue("the delegate must really have been admitted and run");

        var delegateNode = caller.Directory.Snapshot().Single(e => e.Kind == AgentKind.WorkflowDelegate);

        // Trusted sources: the task's authored label and the owning node's authored title.
        delegateNode.Role.Should().Be("Topic analyst");
        delegateNode.Description.Should().Be("Workflow task 'Topic analyst' for node 'Analyze'.");

        // The controller supplied both fields and both were conflicting; neither reached the directory.
        delegateNode.Role.Should().NotBe("hijacked-role");
        delegateNode.Description.Should().NotContain("hijacked");
    }

    [Fact]
    public async Task AnUnlabelledTaskFallsBackToItsAuthoredIdRatherThanTheControllersRole()
    {
        // The shared fixture's task carries no label, so the id is the remaining definition-owned label.
        var caller = Root();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: SpawningSubAgentOptions(),
            controllerAgent: ScriptedController(turn =>
                SpawnTurn(turn, Phase3Fixtures.LinearBlockingAgent, "hijacked-role", "hijacked description")
            ).Object,
            threadId: "wf-collab-unlabelled-thread",
            instanceId: "wf-collab-unlabelled",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var delegateNode = caller.Directory.Snapshot().Single(e => e.Kind == AgentKind.WorkflowDelegate);
        delegateNode.Role.Should().Be("task");
        delegateNode.Description.Should().Be("Workflow task 'task' for node 'Analyze'.");
    }

    [Fact]
    public async Task ARoleFixedTemplateStillOutranksTheWorkflowsOwnLabel()
    {
        // The pre-existing fixed/customizable rule is unchanged: a template that pins its own role keeps it,
        // because that role is already trusted. Only the description then comes from the definition.
        var caller = Root();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "Analyze the topic and finish.",
            inputs: null,
            definition: null,
            subAgentOptions: SpawningSubAgentOptions(fixedRole: "code-reviewer"),
            controllerAgent: ScriptedController(turn =>
                SpawnTurn(turn, LabelledTaskWorkflow, "hijacked-role", "hijacked description")
            ).Object,
            threadId: "wf-collab-fixedrole-thread",
            instanceId: "wf-collab-fixedrole",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var delegateNode = caller.Directory.Snapshot().Single(e => e.Kind == AgentKind.WorkflowDelegate);
        delegateNode.Role.Should().Be("code-reviewer");
        delegateNode.Description.Should().Be("Workflow task 'Topic analyst' for node 'Analyze'.");
    }

    [Fact]
    public async Task ASnapshotWrittenBeforeCollaborationExistedStillResumes()
    {
        // A pre-#244 snapshot has no `collaboration` field at all. It must load unchanged — and the resumed
        // run is admitted with freshly derived metadata rather than refusing for lack of a persisted node.
        var store = new InMemoryWorkflowStore();
        var snapshot = WorkflowInstanceSnapshot.FromJson(LegacySnapshotJson("wf-legacy-1"));
        snapshot.Collaboration.Should().BeNull();
        await store.SaveAsync("wf-legacy-1", snapshot);

        var caller = Root();

        await using var resumed = await WorkflowSession.ResumeAsync(
            instanceId: "wf-legacy-1",
            store: store,
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-legacy-thread",
            callerCollaboration: caller
        );

        await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        resumed.IsComplete.Should().BeTrue();
        resumed.CollaborationNode!.Role.Should().Be(WorkflowCollaboration.ControllerRole);
        resumed.CollaborationNode.Description.Should().Be("trivial");
    }

    [Fact]
    public async Task AResumeReacquiresUnderTheSameIdentityAndReusesItsTrustedMetadataVerbatim()
    {
        // Trusted metadata is validated ONCE, at the original spawn. A restart re-reads it from the
        // snapshot rather than re-deriving it, so a restart is not an opening to relabel a node.
        var store = new InMemoryWorkflowStore();

        await using (
            var first = await WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: EmptyControllerOptions(),
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-reacquire-thread",
                store: store,
                instanceId: "wf-reacquire-1",
                callerCollaboration: Root()
            )
        )
        {
            await first.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        }

        // Rewind the run and rewrite its node with metadata the derivation would NEVER produce, so reuse
        // is provable rather than coincidental.
        var persisted = (await store.LoadAsync("wf-reacquire-1"))!;
        persisted.Collaboration.Should().NotBeNull("a collaborating run persists its node for resume");
        await store.SaveAsync(
            "wf-reacquire-1",
            persisted with
            {
                CurrentNodeId = "s",
                IsComplete = false,
                Collaboration = persisted.Collaboration! with
                {
                    Role = "pinned-role",
                    Description = "pinned description",
                },
            }
        );

        // A restart means a NEW hierarchy: capacity is reacquired from the live caller, not inherited.
        var restarted = Root();

        await using var resumed = await WorkflowSession.ResumeAsync(
            instanceId: "wf-reacquire-1",
            store: store,
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-reacquire-thread",
            callerCollaboration: restarted
        );

        await resumed.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        restarted.Directory.Capacity.InUse.Should().Be(1, "the resumed controller holds a permit again");

        resumed
            .CollaborationNode!.AgentId.Should()
            .Be(WorkflowCollaboration.ComposeControllerAgentId("wf-reacquire-1"), "the identity is deterministic");
        resumed.CollaborationNode.Role.Should().Be("pinned-role");
        resumed.CollaborationNode.Description.Should().Be("pinned description");
    }

    [Fact]
    public async Task AnExhaustedAgentCapRefusesTheLaunchVisiblyAndBeforeAnyLoopIsBuilt()
    {
        var caller = Root(maxTotalAgents: 1);
        using var hog = caller.Directory.TryAcquireCapacity("someone-else");
        hog.Should().NotBeNull("the test needs to hold the collaboration's only permit");

        var act = () =>
            WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: EmptyControllerOptions(),
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-capacity-thread",
                instanceId: "wf-capacity-1",
                callerCollaboration: caller
            );

        (await act.Should().ThrowAsync<WorkflowCollaborationException>())
            .Which.FailureCode.Should()
            .Be(SubAgentCollaborationFailureCodes.CapacityExhausted);

        // Nothing partial was left behind: no node, and the permit count is untouched.
        caller.Directory.FindById(WorkflowCollaboration.ComposeControllerAgentId("wf-capacity-1")).Should().BeNull();
        caller.Directory.Capacity.InUse.Should().Be(1);
    }

    [Fact]
    public async Task TearingTheRunDownSettlesTheNodeAndReturnsItsPermit()
    {
        var caller = Root();

        await using (
            var handle = await WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: EmptyControllerOptions(),
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-settle-thread",
                instanceId: "wf-settle-1",
                callerCollaboration: caller
            )
        )
        {
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
            caller.Directory.Capacity.InUse.Should().Be(1, "a live controller holds a permit");
        }

        var node = caller.Directory.FindById(WorkflowCollaboration.ComposeControllerAgentId("wf-settle-1"));
        node!.Status.Should().Be(AgentCollaborationStatuses.Completed);
        node.IsLive.Should().BeFalse("a finished controller stays inspectable but is no longer addressable");
        caller.Directory.Capacity.InUse.Should().Be(0, "the permit is returned exactly once, at teardown");
    }

    [Fact]
    public async Task TearingTheRunDownAbandonsTheQuestionsNobodyIsLeftToAnswer()
    {
        // A controller that vanishes is only half-settled if the directory is updated and the ledger is
        // not: the node stops being addressable, so nothing can ever deliver an answer, and nothing else
        // in the system closes the entry. The asker would wait for the lifetime of the collaboration.
        var caller = Root();
        var controllerId = WorkflowCollaboration.ComposeControllerAgentId("wf-abandon-1");
        string question;

        await using (
            var handle = await WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: EmptyControllerOptions(),
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-abandon-thread",
                instanceId: "wf-abandon-1",
                callerCollaboration: caller
            )
        )
        {
            await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

            var sent = caller.Bundle.TrySend(caller.AgentId, controllerId, AgentMessageType.Question);
            sent.Succeeded.Should().BeTrue("the controller is still a live, addressable node");
            question = sent.MessageId!;
        }

        caller.Bundle.Ledger.Find(question)!.State.Should().Be(AgentMessageDeliveryState.Abandoned);
        caller.Bundle.Ledger.Find(question)!.ReasonCode.Should().Be(AgentCollaborationBundle.TargetLeftReasonCode);
        caller.Bundle.Ledger.GetOpenInbound(controllerId).Should().BeEmpty();
        caller.Bundle.Ledger.GetOpenOutbound(caller.AgentId).Should().BeEmpty();
    }

    [Fact]
    public async Task ABlockedSnapshotFlushDoesNotHoldTheCollaborationsCapacity()
    {
        // Capacity is a lease on EXISTENCE, not on teardown I/O. Retention and the permit are independent:
        // the finished node stays inspectable either way, but a store that never returns must not be able to
        // freeze the whole hierarchy's breadth budget behind one disposing run.
        var caller = Root();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new BlockingWorkflowStore(release.Task);

        var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-slowflush-thread",
            store: store,
            instanceId: "wf-slowflush-1",
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        caller.Directory.Capacity.InUse.Should().Be(1, "a live controller holds a permit");

        // The save chain is genuinely stuck before teardown starts, so the drain really will block.
        await store.FirstSaveEntered.WaitAsync(TimeSpan.FromSeconds(30));

        var disposal = handle.DisposeAsync();
        await Wait.UntilAsync(
            () => caller.Directory.Capacity.InUse == 0,
            "the controller's permit was released",
            TimeSpan.FromSeconds(10),
            observed: () => $"caller.Directory.Capacity.InUse={caller.Directory.Capacity.InUse}"
        );

        caller.Directory.Capacity.InUse.Should().Be(0, "the permit must not wait on an unbounded store flush");
        disposal.IsCompleted.Should().BeFalse("non-vacuity: teardown is still inside the blocked flush");

        release.SetResult();
        await disposal;
    }

    /// <summary>A store whose saves park until released, standing in for a slow or wedged backend.</summary>
    private sealed class BlockingWorkflowStore(Task release) : IWorkflowStore
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the first save has parked, so a test can wait for a stuck chain.</summary>
        public Task FirstSaveEntered => _entered.Task;

        public async Task SaveAsync(
            string instanceId,
            WorkflowInstanceSnapshot snapshot,
            CancellationToken ct = default
        )
        {
            _ = _entered.TrySetResult();
            await release;
        }

        public Task<WorkflowInstanceSnapshot?> LoadAsync(string instanceId, CancellationToken ct = default) =>
            Task.FromResult<WorkflowInstanceSnapshot?>(null);

        public Task DeleteAsync(string instanceId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    [Fact]
    public async Task AWorkflowControllerCannotLaunchAnotherWorkflow()
    {
        var caller = Root();

        await using var outer = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(NeverComplete).Object,
            threadId: "wf-nested-outer-thread",
            instanceId: "wf-nested-outer",
            callerCollaboration: caller
        );

        await outer.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        // The controller's OWN handle is what a nested launch would be made with.
        var act = () =>
            WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: EmptyControllerOptions(),
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-nested-inner-thread",
                instanceId: "wf-nested-inner",
                callerCollaboration: outer.Loop.Collaboration
            );

        (await act.Should().ThrowAsync<WorkflowCollaborationException>())
            .Which.FailureCode.Should()
            .Be(WorkflowCollaboration.NestedWorkflowFailureCode);
    }

    [Fact]
    public async Task ARefusedAdmissionReachesTheModelAsARecoverableToolErrorNotAThrow()
    {
        var caller = Root();

        await using var outer = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(NeverComplete).Object,
            threadId: "wf-tool-refusal-thread",
            instanceId: "wf-tool-refusal-outer",
            callerCollaboration: caller
        );

        await outer.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        await using var manager = new WorkflowManager(
            () => ScriptedController(DriveMinimalToTerminal).Object,
            EmptyControllerOptions()
        );
        var provider = new StartWorkflowToolProvider(manager, callerCollaboration: () => outer.Loop.Collaboration);
        var start = provider.GetFunctions().Single(f => f.Contract.Name == "StartWorkflowAgent");

        var result = (ToolHandlerResult.Resolved)
            await start.Handler(
                new JsonObject
                {
                    ["workflowId"] = "wf-tool-refusal-inner",
                    ["workflow"] = JsonNode.Parse(WorkflowFixtures.MinimalValid),
                    ["mode"] = "async",
                }.ToJsonString(),
                new ToolCallContext(),
                CancellationToken.None
            );

        result.Payload.IsError.Should().BeTrue();
        result.Payload.ErrorCode.Should().Be(WorkflowCollaboration.NestedWorkflowFailureCode);
    }

    [Fact]
    public async Task TheLaunchToolsStillNeverReachADelegateWhileCollaborating()
    {
        // Collaboration must not become a way around the structural exclusion: a delegate one hop deeper
        // still cannot see the controller's workflow-state tools OR the launch family.
        var external = new InheritableToolSnapshot(
            [
                new FunctionContract
                {
                    Name = "Foo",
                    Description = "Foo",
                    Parameters = [],
                },
                new FunctionContract
                {
                    Name = StartWorkflowToolProvider.StartWorkflowToolName,
                    Description = "launch",
                    Parameters = [],
                },
            ],
            new Dictionary<string, ToolHandler>(StringComparer.Ordinal)
            {
                ["Foo"] = (_, _, _) => Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
                [StartWorkflowToolProvider.StartWorkflowToolName] = (_, _, _) =>
                    Task.FromResult<ToolHandlerResult>(ToolHandlerResult.FromText("ok")),
            }
        );

        var options = SpawningSubAgentOptions() with
        {
            NonInheritedToolNames = [.. WorkflowToolProvider.AllToolNames, .. StartWorkflowToolProvider.ToolNames],
            ExternalInheritableTools = external,
        };

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: options,
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-collab-tools-thread",
            instanceId: "wf-collab-tools",
            includeAuthoringTool: false,
            callerCollaboration: Root()
        );

        var inheritable = handle.Loop.SubAgentManager!.GetInheritableToolSnapshot().Contracts.Select(c => c.Name);

        inheritable.Should().Contain("Foo", "ordinary host tools still flow down");
        inheritable
            .Should()
            .NotContain([
                StartWorkflowToolProvider.StartWorkflowToolName,
                "GetWorkflow",
                "SetCurrentNode",
                "SetState",
                "SetNotes",
            ]);
    }

    [Fact]
    public async Task TheControllerEndpointOnlyAcceptsDeliveryWhileItsLoopIsBound()
    {
        // The controller is registered BEFORE its loop exists (the loop needs the handle the registration
        // produces), so the endpoint is late-bound. Unbound it must refuse recoverably rather than throw or
        // pretend to have delivered.
        var complete = false;
        var endpoint = new WorkflowControllerEndpoint(
            () => complete ? AgentCollaborationStatuses.Completed : AgentCollaborationStatuses.Running,
            "wf-endpoint-thread",
            conversationStore: null
        );

        var outcome = await endpoint.DeliverAsync(Message());
        outcome.IsDelivered.Should().BeFalse();
        outcome.ReasonCode.Should().Be(WorkflowControllerEndpoint.NotRunningReasonCode);

        (await endpoint.GetStatusAsync()).Should().Be(AgentCollaborationStatuses.Running);
        complete = true;
        (await endpoint.GetStatusAsync()).Should().Be(AgentCollaborationStatuses.Completed);

        // With no conversation store there is nothing to read, and that is an empty transcript, not a fault.
        (await endpoint.GetTranscriptAsync())
            .Should()
            .BeEmpty();
    }

    [Fact]
    public async Task TheControllerEndpointReadsTheRunsOwnTranscript()
    {
        var conversationStore = new InMemoryConversationStore();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-transcript-thread",
            instanceId: "wf-transcript-1",
            conversationStore: conversationStore,
            callerCollaboration: Root()
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        var endpoint = new WorkflowControllerEndpoint(
            () => AgentCollaborationStatuses.Completed,
            "wf-transcript-thread",
            conversationStore
        );

        (await endpoint.GetTranscriptAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task CollaborationDoesNotRearmTheControllersApprovalGate()
    {
        // The exemption and collaboration are independent. Deny-all is the sharp case: were joining a
        // hierarchy to quietly rearm the gate, the controller's first SetCurrentNode would be refused and
        // the workflow would never leave `s`.
        var gate = RecordingToolApprovalGate.Denying("nothing may run");
        var caller = Root();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-collab-approval-thread",
            instanceId: "wf-collab-approval",
            lifecycleServices: GatedBy(gate),
            callerCollaboration: caller
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        gate.WasConsulted.Should().BeFalse("the controller's own steps are still not a host action to approve");
        handle.Runtime.IsComplete.Should().BeTrue();
        handle.Runtime.CurrentNodeId.Should().Be("t");
        caller
            .Directory.FindById(WorkflowCollaboration.ComposeControllerAgentId("wf-collab-approval"))
            .Should()
            .NotBeNull();
    }

    private static MultiTurnLifecycleServices GatedBy(IToolApprovalGate gate) =>
        new() { Approval = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [gate] }) };

    [Fact]
    public async Task ALaunchThatFailsAfterAdmissionDoesNotStrandItsCapacityPermit()
    {
        // Between admission and the handle there is a window (loop construction, history recovery) with no
        // handle for the caller to dispose. A throw there must still return the permit, or a hierarchy would
        // bleed capacity to launches that never ran.
        var caller = Root(maxTotalAgents: 2);

        var act = () =>
            WorkflowSession.StartAsync(
                objective: "drive",
                inputs: null,
                definition: MinimalDefinition(),
                subAgentOptions: SpawningSubAgentOptions() with
                {
                    MaxConcurrentSubAgents = 0,
                },
                controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
                threadId: "wf-leak-thread",
                instanceId: "wf-leak-1",
                callerCollaboration: caller
            );

        _ = await act.Should().ThrowAsync<ArgumentOutOfRangeException>();

        caller.Directory.Capacity.InUse.Should().Be(0, "the permit is returned even though no handle exists");
        caller
            .Directory.FindById(WorkflowCollaboration.ComposeControllerAgentId("wf-leak-1"))!
            .IsLive.Should()
            .BeFalse();
    }

    /// <summary>
    ///     A registered root to launch from. <see cref="AgentCollaborationSetup.CreateRoot"/> only mints the
    ///     handle; the directory rejects a child whose parent it has never seen, so the root is registered here.
    /// </summary>
    private static AgentCollaborationSetup Root(int maxTotalAgents = 8, int maxDelegationDepth = 1)
    {
        var setup = AgentCollaborationSetup.CreateRoot(
            new AgentCollaborationOptions { MaxTotalAgents = maxTotalAgents, MaxDelegationDepth = maxDelegationDepth },
            collaborationId: "collab-test",
            agentId: RootAgentId,
            name: "root"
        );

        _ = setup.Directory.TryRegister(setup.Context, setup.Name, AgentCollaborationStatuses.Running);
        return setup;
    }

    /// <summary>Sub-agent templates whose delegate always answers with a valid <c>{summary}</c> payload.</summary>
    /// <param name="fixedRole">
    ///     When set, the template pins this role, which must outrank both the workflow's authored label and
    ///     anything the controller supplies.
    /// </param>
    private static SubAgentOptions SpawningSubAgentOptions(string? fixedRole = null)
    {
        var subAgent = new Mock<IStreamingAgent>();
        _ = subAgent
            .Setup(a =>
                a.GenerateReplyStreamingAsync(
                    It.IsAny<IEnumerable<IMessage>>(),
                    It.IsAny<GenerateReplyOptions>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                Task.FromResult(
                    ToAsyncEnumerable([
                        new TextMessage { Text = """{ "summary": "analyzed-by-subagent" }""", Role = Role.Assistant },
                    ])
                )
            );

        return new SubAgentOptions
        {
            Templates = new Dictionary<string, SubAgentTemplate>
            {
                ["general-purpose"] = new SubAgentTemplate
                {
                    Name = "general-purpose",
                    SystemPrompt = "You are a general-purpose analysis agent.",
                    AgentFactory = () => subAgent.Object,
                    Role = fixedRole,
                    RoleMode = fixedRole is null ? SubAgentRoleMode.Customizable : SubAgentRoleMode.Fixed,
                },
            },
        };
    }

    /// <summary>
    ///     Authors a linear workflow, routes into its one delegating node, spawns the unit, then finishes.
    ///     The spawn carries <c>role</c>/<c>description</c> because collaboration makes both mandatory.
    /// </summary>
    private static IMessage DriveAndSpawnDelegate(int turn) =>
        SpawnTurn(
            turn,
            Phase3Fixtures.LinearBlockingAgent,
            "analyst",
            "Analyzes the topic for the workflow's analyze node."
        );

    /// <summary>
    ///     The same drive script over an arbitrary definition, with the <c>role</c>/<c>description</c> the
    ///     controller puts on the Agent call left to the caller so a test can supply conflicting values.
    /// </summary>
    private static IMessage SpawnTurn(int turn, string definitionJson, string role, string description) =>
        turn switch
        {
            1 => ToolCall(
                "SetWorkflow",
                new JsonObject { ["definition"] = JsonNode.Parse(definitionJson) },
                "tc_setwf"
            ),
            2 => ToolCall(
                "SetCurrentNode",
                new JsonObject { ["completedNodeId"] = "start", ["nextNodeId"] = "analyze" },
                "tc_route_analyze"
            ),
            3 => ToolCall(
                "Agent",
                new JsonObject
                {
                    ["subagent_type"] = "general-purpose",
                    ["prompt"] = "Spawn the analysis task.",
                    ["name"] = "analyze:1:task",
                    ["role"] = role,
                    ["description"] = description,
                },
                "tc_agent"
            ),
            4 => ToolCall(
                "SetCurrentNode",
                new JsonObject
                {
                    ["completedNodeId"] = "analyze",
                    ["nextNodeId"] = "done",
                    ["result"] = new JsonObject { ["summary"] = "final-result" },
                },
                "tc_route_done"
            ),
            _ => new TextMessage { Text = "Workflow finished.", Role = Role.Assistant },
        };

    /// <summary>
    ///     The shared linear fixture with an authored <c>label</c> on its one task, so a test can tell the
    ///     label apart from the task id. Derived from the fixture rather than copied so the two cannot drift.
    /// </summary>
    private static readonly string LabelledTaskWorkflow = Phase3Fixtures.LinearBlockingAgent.Replace(
        "\"id\": \"task\",",
        "\"id\": \"task\",\n              \"label\": \"Topic analyst\",",
        StringComparison.Ordinal
    );

    /// <summary>A snapshot exactly as a pre-#244 build wrote it: no <c>collaboration</c> field anywhere.</summary>
    private static string LegacySnapshotJson(string instanceId) =>
        $$"""
            {
              "schemaVersion": 1,
              "instanceId": "{{instanceId}}",
              "definition": {{WorkflowFixtures.MinimalValid}},
              "currentNodeId": "s",
              "isComplete": false,
              "step": 1,
              "inputs": {},
              "state": {},
              "outputs": {},
              "notes": {},
              "visits": { "s": 1 },
              "tasks": []
            }
            """;

    private static AgentMessage Message() =>
        new()
        {
            MessageId = "msg-1",
            AgentMessageType = AgentMessageType.Steer,
            FromAgentId = RootAgentId,
            FromName = "root",
            Body = "look at the second node",
        };
}
