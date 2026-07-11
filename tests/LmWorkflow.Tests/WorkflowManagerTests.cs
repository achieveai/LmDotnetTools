using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmWorkflow.Ingest;
using FluentAssertions;
using Xunit;
using static AchieveAi.LmDotnetTools.LmWorkflow.Tests.StartWorkflowTestHarness;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Tests;

/// <summary>
///     Behavioral coverage for <see cref="WorkflowManager"/>: sync/async launch, proactive notify, non-blocking
///     Check, blocking + non-destructive-timeout Wait, duplicate/invalid/capacity/unknown errors, the
///     turn-budget-exhausted → Failed rail, notify-failure-doesn't-lose-result, and the long-lived / disposed-
///     but-queryable handle lifecycle that has no prior precedent in the codebase.
/// </summary>
public class WorkflowManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static WorkflowManager NewManager(
        Func<IStreamingAgent> controllerFactory,
        Func<NotifyMessage, CancellationToken, Task>? notifier = null,
        int maxConcurrentWorkflows = 8,
        int controllerMaxTurnsPerRun = 150,
        TimeSpan? gateWaitTimeout = null
    ) =>
        new(
            controllerFactory,
            EmptyControllerOptions(),
            completionNotifier: notifier,
            maxConcurrentWorkflows: maxConcurrentWorkflows,
            controllerMaxTurnsPerRun: controllerMaxTurnsPerRun,
            gateWaitTimeout: gateWaitTimeout
        );

    [Fact]
    public async Task StartAsync_Sync_DrivesToTerminal_ReturnsCompletedInline()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        var result = await manager.StartAsync("wf-sync", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("completed");
        result.IsComplete.Should().BeTrue();
        result.CurrentNodeId.Should().Be("t");
    }

    [Fact]
    public async Task StartAsync_Async_ReturnsStarted_AndProactivelyNotifiesOnCompletion()
    {
        var notified = new TaskCompletionSource<NotifyMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(
            () => controller.Object,
            notifier: (n, _) =>
            {
                notified.TrySetResult(n);
                return Task.CompletedTask;
            }
        );

        var started = await manager.StartAsync("wf-async", MinimalDefinition(), WorkflowStartMode.Async);
        started.Status.Should().Be("started");

        var notify = await notified.Task.WaitAsync(Timeout);
        notify.NotifyKind.Should().Be(NotifyKinds.WorkflowCompletion);
        notify.SourceToolCallId.Should().Be("wf-async");
        notify.SourceToolName.Should().Be("StartWorkflow");
    }

    [Fact]
    public async Task Check_ReturnsRunning_ThenCompleted_WithoutBlocking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-check", MinimalDefinition(), WorkflowStartMode.Async);

        // Running while the controller is gated.
        manager.Check("wf-check").Status.Should().Be("running");

        // Release and let it finish, then Check reports completed.
        gate.SetResult();
        _ = await manager.WaitAsync("wf-check", Timeout);
        manager.Check("wf-check").Status.Should().Be("completed");
    }

    [Fact]
    public async Task WaitAsync_Timeout_IsNonDestructive_ThenCompletes()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-wait", MinimalDefinition(), WorkflowStartMode.Async);

        // A short wait times out WITHOUT cancelling the run.
        var timedOut = await manager.WaitAsync("wf-wait", TimeSpan.FromMilliseconds(150));
        timedOut.Status.Should().Be("timeout");

        // The workflow is still alive: release it and a fresh Wait observes completion.
        gate.SetResult();
        var completed = await manager.WaitAsync("wf-wait", Timeout);
        completed.Status.Should().Be("completed");
    }

    [Fact]
    public async Task StartAsync_DuplicateWorkflowId_Throws()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("dup", MinimalDefinition(), WorkflowStartMode.Sync);

        var act = () => manager.StartAsync("dup", MinimalDefinition(), WorkflowStartMode.Async);
        await act.Should().ThrowAsync<DuplicateWorkflowException>();
    }

    [Theory]
    [InlineData(WorkflowStartMode.Sync)]
    [InlineData(WorkflowStartMode.Async)]
    public async Task StartAsync_InvalidDefinition_ThrowsSynchronously_NoSlotConsumed(WorkflowStartMode mode)
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        var act = () => manager.StartAsync("bad", InvalidDefinition(), mode);
        await act.Should().ThrowAsync<WorkflowValidationException>();

        // The id was never reserved, so it is still unknown (no slot consumed by a rejected definition).
        var check = () => manager.Check("bad");
        check.Should().Throw<UnknownWorkflowException>();
    }

    [Fact]
    public async Task StartAsync_AtCapacity_ThrowsBackpressure()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(
            () => controller.Object,
            maxConcurrentWorkflows: 1,
            gateWaitTimeout: TimeSpan.FromMilliseconds(200)
        );

        // First workflow holds the only slot (gated, still running).
        _ = await manager.StartAsync("cap-1", MinimalDefinition(), WorkflowStartMode.Async);

        var act = () => manager.StartAsync("cap-2", MinimalDefinition(), WorkflowStartMode.Async);
        await act.Should().ThrowAsync<WorkflowCapacityException>();

        // The rejected id was rolled back — it is unknown, and the cap is truly reached (not leaked).
        var check = () => manager.Check("cap-2");
        check.Should().Throw<UnknownWorkflowException>();

        gate.SetResult();
    }

    [Fact]
    public async Task Check_And_Wait_UnknownId_Throw()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        var check = () => manager.Check("nope");
        check.Should().Throw<UnknownWorkflowException>();

        var wait = () => manager.WaitAsync("nope", TimeSpan.FromSeconds(1));
        await wait.Should().ThrowAsync<UnknownWorkflowException>();
    }

    [Fact]
    public async Task Controller_ExhaustsTurnBudget_ResultIsFailed_NotHang()
    {
        var controller = ScriptedController(NeverComplete);
        // A tiny turn budget so the controller loop ends WITHOUT reaching a terminal node, quickly.
        await using var manager = NewManager(() => controller.Object, controllerMaxTurnsPerRun: 2);

        var result = await manager.StartAsync("wf-budget", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("failed");
        result.IsComplete.Should().BeFalse();
        result.Error.Should().Contain("terminal");
    }

    [Fact]
    public async Task NotifyDeliveryFailure_DoesNotLoseResult_StillQueryable()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(
            () => controller.Object,
            notifier: (_, _) => throw new InvalidOperationException("caller is gone")
        );

        _ = await manager.StartAsync("wf-notify-fail", MinimalDefinition(), WorkflowStartMode.Async);

        // Even though the proactive notify throws, the terminal result stays queryable.
        var completed = await manager.WaitAsync("wf-notify-fail", Timeout);
        completed.Status.Should().Be("completed");
        manager.Check("wf-notify-fail").Status.Should().Be("completed");
    }

    [Fact]
    public async Task LongLivedHandle_SurvivesMultipleInterleavedCheckAndWaitCalls()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-long", MinimalDefinition(), WorkflowStartMode.Async);

        // Interleave several external observations while the controller is held open.
        manager.Check("wf-long").Status.Should().Be("running");
        (await manager.WaitAsync("wf-long", TimeSpan.FromMilliseconds(100))).Status.Should().Be("timeout");
        manager.Check("wf-long").Status.Should().Be("running");
        (await manager.WaitAsync("wf-long", TimeSpan.FromMilliseconds(100))).Status.Should().Be("timeout");

        gate.SetResult();
        (await manager.WaitAsync("wf-long", Timeout)).Status.Should().Be("completed");
        manager.Check("wf-long").Status.Should().Be("completed");
    }

    [Fact]
    public async Task WaitAsync_HugeTimeout_ClampsWithoutThrowing_AndResolvesOnCompletion()
    {
        // Regression: a very large timeout must be CLAMPED (not throw out of Task.WaitAsync and get swallowed)
        // and resolve on the run's completion rather than on the raw out-of-range value.
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-hugewait", MinimalDefinition(), WorkflowStartMode.Async);

        // ~57 days in seconds — beyond Task.WaitAsync's accepted range if unclamped. The run completes
        // quickly, so the wait returns completed well before any timeout.
        var result = await manager.WaitAsync("wf-hugewait", TimeSpan.FromSeconds(5_000_000));
        result.Status.Should().Be("completed");
    }

    [Fact]
    public async Task DisposeAsync_WithInFlightWorkflow_DoesNotFault_NorLeakUnobservedException()
    {
        // Regression: disposing the manager while an async workflow is still running must not leave the
        // completion observer releasing an already-disposed gate (unobserved ObjectDisposedException).
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-dispose", MinimalDefinition(), WorkflowStartMode.Async);
        manager.Check("wf-dispose").Status.Should().Be("running");

        // Dispose mid-run — this drives the handle to completion and must await the observer cleanly.
        var dispose = async () => await manager.DisposeAsync();
        await dispose.Should().NotThrowAsync();

        gate.TrySetResult();
    }

    [Fact]
    public async Task WaitAsync_CallerCancelled_Throws_ButRunStaysAliveAndQueryable()
    {
        // Pins the non-destructive-cancellation invariant: cancelling a WaitAsync must NOT cancel the run.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var controller = GatedController(gate);
        await using var manager = NewManager(() => controller.Object);

        _ = await manager.StartAsync("wf-cancel", MinimalDefinition(), WorkflowStartMode.Async);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var wait = () => manager.WaitAsync("wf-cancel", TimeSpan.FromSeconds(30), cts.Token);
        await wait.Should().ThrowAsync<OperationCanceledException>();

        // The run is untouched: still running, and it can still complete.
        manager.Check("wf-cancel").Status.Should().Be("running");
        gate.SetResult();
        (await manager.WaitAsync("wf-cancel", Timeout)).Status.Should().Be("completed");
    }

    [Fact]
    public async Task Controller_ThatFaults_ResultIsFailed()
    {
        var controller = FaultingController();
        await using var manager = NewManager(() => controller.Object);

        var result = await manager.StartAsync("wf-fault", MinimalDefinition(), WorkflowStartMode.Sync);

        result.Status.Should().Be("failed");
        result.IsComplete.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task StartAsync_Async_Notify_UsesOriginatingToolCallId_WhenSupplied()
    {
        var notified = new TaskCompletionSource<NotifyMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(
            () => controller.Object,
            notifier: (n, _) =>
            {
                notified.TrySetResult(n);
                return Task.CompletedTask;
            }
        );

        _ = await manager.StartAsync("wf-corr", MinimalDefinition(), WorkflowStartMode.Async, default, "toolcall-123");

        var notify = await notified.Task.WaitAsync(Timeout);
        // Correlated to the originating StartWorkflow tool call, not the workflowId.
        notify.SourceToolCallId.Should().Be("toolcall-123");
        notify.Label.Should().Be("wf-corr");
    }

    [Fact]
    public async Task StartAsync_AfterDispose_ThrowsObjectDisposed()
    {
        var manager = NewManager(() => ScriptedController(DriveMinimalToTerminal).Object);
        await manager.DisposeAsync();

        var act = () => manager.StartAsync("after-dispose", MinimalDefinition(), WorkflowStartMode.Sync);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task WaitAsync_NegativeTimeout_ThrowsArgumentOutOfRange()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);
        _ = await manager.StartAsync("neg", MinimalDefinition(), WorkflowStartMode.Sync);

        var act = () => manager.WaitAsync("neg", TimeSpan.FromSeconds(-5));
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Check_AfterTerminalAndDisposal_StillReturnsCompleted()
    {
        var controller = ScriptedController(DriveMinimalToTerminal);
        await using var manager = NewManager(() => controller.Object);

        // Sync completion; the completion handler disposes the handle once terminal.
        var sync = await manager.StartAsync("wf-disposed", MinimalDefinition(), WorkflowStartMode.Sync);
        sync.Status.Should().Be("completed");

        // Give the completion continuation a moment to run its dispose, then prove Check still works.
        await manager.WaitAsync("wf-disposed", Timeout);
        var check = manager.Check("wf-disposed");
        check.Status.Should().Be("completed");
        check.IsComplete.Should().BeTrue();
    }
}
