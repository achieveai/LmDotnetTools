using CodeReviewDaemon.Sample.Hosting;
using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task #81, round 4, items 2+3 — proves <see cref="RunPrOrchestration.ExecuteAsync"/>'s sequencing
/// guarantees with fake delegates: no live provider, no live ASP.NET host, no real coordinator-lease file.
/// </summary>
public sealed class RunPrOrchestrationTests
{
    private static Task<RunSinglePrPrepareResult> AcceptedPrepare(CancellationToken _) =>
        Task.FromResult(
            new RunSinglePrPrepareResult
            {
                Accepted = true,
                Target = null,
                Provider = null,
                Descriptor = null,
            }
        );

    private static RunPrCoordinatorLeaseOutcome Acquired() =>
        RunPrCoordinatorLeaseOutcome.Acquired(
            new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite, FileShare.None)
        );

    [Fact]
    public async Task A_rejected_prepare_never_attempts_a_lease_or_starts_the_host()
    {
        var leaseAttempts = 0;
        var hostStarts = 0;
        var admitCalls = 0;
        var hostStops = 0;

        var result = await RunPrOrchestration.ExecuteAsync(
            prepareAsync: _ =>
                Task.FromResult(RunSinglePrPrepareResult.Rejected(RunSinglePrRejectionReason.OutsideAllowList)),
            acquireLease: () =>
            {
                leaseAttempts++;
                return Acquired();
            },
            startHostAsync: () =>
            {
                hostStarts++;
                return Task.CompletedTask;
            },
            admitAndRunAsync: (_, _) =>
            {
                admitCalls++;
                return Task.FromResult(new RunSinglePrResult { Admitted = true });
            },
            stopHostAsync: () =>
            {
                hostStops++;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.OutsideAllowList);
        leaseAttempts.Should().Be(0, "a rejection PrepareAsync already determined must not even attempt a lease");
        hostStarts.Should().Be(0, "the host must never start for a run that was always going to be refused");
        admitCalls.Should().Be(0);
        hostStops.Should().Be(0, "a host that never started has nothing to stop");
    }

    [Fact]
    public async Task Lease_refusal_prevents_the_host_from_ever_starting()
    {
        var hostStarts = 0;
        var admitCalls = 0;
        var hostStops = 0;

        var result = await RunPrOrchestration.ExecuteAsync(
            prepareAsync: AcceptedPrepare,
            acquireLease: () => RunPrCoordinatorLeaseOutcome.Unavailable("daemon already holds the lease"),
            startHostAsync: () =>
            {
                hostStarts++;
                return Task.CompletedTask;
            },
            admitAndRunAsync: (_, _) =>
            {
                admitCalls++;
                return Task.FromResult(new RunSinglePrResult { Admitted = true });
            },
            stopHostAsync: () =>
            {
                hostStops++;
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.CoordinatorLeaseUnavailable);
        hostStarts.Should().Be(0, "a lease that could not be acquired must never let the host come up at all");
        admitCalls.Should().Be(0);
        hostStops.Should().Be(0);
    }

    [Fact]
    public async Task The_host_is_stopped_even_when_admit_and_run_throws()
    {
        var hostStops = 0;

        var act = () =>
            RunPrOrchestration.ExecuteAsync(
                prepareAsync: AcceptedPrepare,
                acquireLease: Acquired,
                startHostAsync: () => Task.CompletedTask,
                admitAndRunAsync: (_, _) => throw new InvalidOperationException("boom"),
                stopHostAsync: () =>
                {
                    hostStops++;
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

        await act.Should().ThrowAsync<InvalidOperationException>();
        hostStops.Should().Be(1, "the host must be stopped even when the run itself throws");
    }

    [Fact]
    public async Task A_graceful_cancellation_mid_run_still_stops_the_host_and_reports_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var hostStops = 0;

        var result = await RunPrOrchestration.ExecuteAsync(
            prepareAsync: AcceptedPrepare,
            acquireLease: Acquired,
            startHostAsync: () => Task.CompletedTask,
            admitAndRunAsync: (_, ct) =>
            {
                cts.Cancel();
                throw new OperationCanceledException(ct);
            },
            stopHostAsync: () =>
            {
                hostStops++;
                return Task.CompletedTask;
            },
            cts.Token
        );

        result.Admitted.Should().BeFalse();
        result.RejectionReason.Should().Be(RunSinglePrRejectionReason.Cancelled);
        hostStops.Should().Be(1, "Ctrl+C mid-run must still stop the host, exactly like any other exit");
    }

    [Fact]
    public async Task An_unrelated_operation_cancelled_exception_still_propagates_after_stopping_the_host()
    {
        // Only a cancellation caused by THIS run's own token is treated as a graceful shutdown; a stray
        // OperationCanceledException from something else (e.g. an inner HTTP timeout) must not be swallowed.
        using var cts = new CancellationTokenSource();
        var hostStops = 0;

        var act = () =>
            RunPrOrchestration.ExecuteAsync(
                prepareAsync: AcceptedPrepare,
                acquireLease: Acquired,
                startHostAsync: () => Task.CompletedTask,
                admitAndRunAsync: (_, _) => throw new OperationCanceledException("unrelated timeout"),
                stopHostAsync: () =>
                {
                    hostStops++;
                    return Task.CompletedTask;
                },
                cts.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
        hostStops.Should().Be(1);
    }

    [Fact]
    public async Task The_happy_path_starts_the_host_admits_and_stops_the_host_in_order()
    {
        var order = new List<string>();

        var result = await RunPrOrchestration.ExecuteAsync(
            prepareAsync: AcceptedPrepare,
            acquireLease: Acquired,
            startHostAsync: () =>
            {
                order.Add("start");
                return Task.CompletedTask;
            },
            admitAndRunAsync: (_, _) =>
            {
                order.Add("admit");
                return Task.FromResult(new RunSinglePrResult { Admitted = true, PrId = "7" });
            },
            stopHostAsync: () =>
            {
                order.Add("stop");
                return Task.CompletedTask;
            },
            CancellationToken.None
        );

        result.Admitted.Should().BeTrue();
        result.PrId.Should().Be("7");
        order.Should().Equal("start", "admit", "stop");
    }
}
