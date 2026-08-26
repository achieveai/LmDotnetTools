using CodeReviewDaemon.Sample.Orchestration;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class DaemonAdmissionCoordinatorTests
{
    [Fact]
    public void Held_state_admits_no_work()
    {
        var coordinator = new DaemonAdmissionCoordinator(DaemonAdmissionState.Held);
        coordinator.TryAdmit().Should().BeNull();
        coordinator.ActiveWorkCount.Should().Be(0);
    }

    [Fact]
    public async Task Drain_blocks_new_admission_and_waits_for_existing_work()
    {
        var coordinator = new DaemonAdmissionCoordinator(DaemonAdmissionState.Active);
        var lease = coordinator.TryAdmit();
        lease.Should().NotBeNull();

        var drain = coordinator.BeginDrainAsync(CancellationToken.None);
        drain.IsCompleted.Should().BeFalse();
        coordinator.TryAdmit().Should().BeNull();

        lease!.Dispose();
        await drain.WaitAsync(TimeSpan.FromSeconds(1));
        coordinator.State.Should().Be(DaemonAdmissionState.Draining);
    }
}
