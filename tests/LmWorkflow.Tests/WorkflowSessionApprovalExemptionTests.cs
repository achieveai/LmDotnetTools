using AchieveAi.LmDotnetTools.LmCore.Approval;
using AchieveAi.LmDotnetTools.LmLifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmTestUtils;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     The workflow controller is exempt from tool approval: its tools are the engine's own steps —
///     advance a node, write state — so an approver has nothing to decide, and a workflow parked behind a
///     verdict that never comes is a workflow that never finishes. Observation is kept; only the gate goes.
/// </summary>
/// <remarks>
///     The stripping happens in <c>WorkflowSession.BuildLoop</c>, the single place a controller loop is
///     built, so it holds for <c>StartAsync</c>, <c>ResumeAsync</c>, and <c>WorkflowManager</c> alike
///     rather than depending on each caller to remember.
/// </remarks>
public class WorkflowSessionApprovalExemptionTests
{
    [Fact]
    public async Task ADenyAllApproverIsNeverConsultedAndTheWorkflowStillCompletes()
    {
        // Deny-all is the sharp case: were the controller gated at all, its very first SetCurrentNode
        // would be refused and the workflow would never leave `start`.
        var gate = RecordingToolApprovalGate.Denying("nothing may run");

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-approval-exempt-thread",
            lifecycleServices: GatedBy(gate)
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        gate.WasConsulted.Should().BeFalse("the controller's own steps are not a host action to approve");
        handle.Runtime.IsComplete.Should().BeTrue();
        handle.Runtime.CurrentNodeId.Should().Be("t");
    }

    [Fact]
    public async Task StrippingTheGateDoesNotStopTheControllerBeingWatched()
    {
        // The exemption is narrow. A host that wired up lifecycle observation still sees the
        // controller's run — dropping the gate must not quietly drop the subscriber with it.
        var publisher = new CountingLifecyclePublisher();
        var gate = RecordingToolApprovalGate.Denying();

        await using var handle = await WorkflowSession.StartAsync(
            objective: "drive",
            inputs: null,
            definition: MinimalDefinition(),
            subAgentOptions: EmptyControllerOptions(),
            controllerAgent: ScriptedController(DriveMinimalToTerminal).Object,
            threadId: "wf-approval-observed-thread",
            lifecycleServices: GatedBy(gate) with { Publisher = publisher }
        );

        await handle.Completion.WaitAsync(TimeSpan.FromSeconds(30));

        publisher.Count.Should().BeGreaterThan(0);
        gate.WasConsulted.Should().BeFalse();
    }

    [Fact]
    public void TheSameApproverWouldHaveBlockedAnOrdinaryLoop()
    {
        // Non-vacuity: "never consulted" only means something if this bundle gates anything at all.
        // The behavioral half — a raw MultiTurnAgentLoop refusing the call — is pinned by
        // LmMultiTurn.Tests' ToolApprovalGateIntegrationTests; here it is enough to show the bundle
        // handed to the session was armed, and that the session is what disarms it.
        var services = GatedBy(RecordingToolApprovalGate.Denying());

        services.Approval.IsEnabled.Should().BeTrue();
        MultiTurnLifecycleServices
            .ForObservationOnly(services)
            .Approval.IsEnabled.Should()
            .BeFalse();
    }

    private static MultiTurnLifecycleServices GatedBy(IToolApprovalGate gate) =>
        new()
        {
            Approval = new ToolInvocationPreparer(new ToolApprovalOptions { Gates = [gate] }),
        };

    /// <summary>
    ///     Counts published events. A counter rather than the richer recorder in LmMultiTurn.Tests: the
    ///     assertion here is only "observation survived", and the recorder is internal to that assembly.
    /// </summary>
    private sealed class CountingLifecyclePublisher : ILifecyclePublisher
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public ValueTask PublishAsync(
            LifecycleEventEnvelope envelope,
            CancellationToken ct = default)
        {
            _ = Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }
}
