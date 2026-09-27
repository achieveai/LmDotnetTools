using CodeReviewDaemon.Sample.Hosting;

namespace CodeReviewDaemon.Sample.Tests.Hosting;

/// <summary>
/// <see cref="RunPrCoordinatorLease"/> wraps <see cref="WorkflowCoordinatorLease.Acquire"/> so
/// <c>--run-pr</c> fails closed on a lock conflict instead of crashing (security review round 1). These
/// tests exercise the real OS-level file lock rather than a fake: the whole point of this type is that a
/// second acquirer for the SAME database path — the daemon, or another <c>--run-pr</c> invocation — must
/// come back <see cref="RunPrCoordinatorLeaseOutcome.IsAcquired"/> <c>false</c>, not throw.
/// </summary>
public sealed class RunPrCoordinatorLeaseTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        "codereviewdaemon-tests",
        Guid.NewGuid().ToString("N") + ".db"
    );

    [Fact]
    public void Acquires_the_lease_when_nothing_else_holds_it()
    {
        using var outcome = RunPrCoordinatorLease.TryAcquire(_databasePath);

        outcome.IsAcquired.Should().BeTrue();
        outcome.FailureReason.Should().BeNull();
    }

    [Fact]
    public void Fails_closed_instead_of_throwing_when_the_daemon_already_holds_the_lease()
    {
        using var daemonLease = WorkflowCoordinatorLease.Acquire(_databasePath);

        using var outcome = RunPrCoordinatorLease.TryAcquire(_databasePath);

        outcome.IsAcquired.Should().BeFalse();
        outcome.FailureReason.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Can_acquire_again_once_a_prior_holder_disposes_its_lease()
    {
        var first = RunPrCoordinatorLease.TryAcquire(_databasePath);
        first.IsAcquired.Should().BeTrue();
        first.Dispose();

        using var second = RunPrCoordinatorLease.TryAcquire(_databasePath);

        second.IsAcquired.Should().BeTrue("disposing the first outcome must release the OS handle");
    }

    public void Dispose()
    {
        foreach (var suffix in new[] { "", ".coordinator.lock" })
        {
            try
            {
                if (File.Exists(_databasePath + suffix))
                {
                    File.Delete(_databasePath + suffix);
                }
            }
            catch
            {
                // best-effort temp cleanup
            }
        }
    }
}
