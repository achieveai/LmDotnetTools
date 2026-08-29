using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using FluentAssertions;
using Xunit;

namespace LmMultiTurn.Tests.Collaboration;

/// <summary>
/// Covers the root-wide agent budget: it never blocks, it is released exactly once, and a lease
/// outlives the method that took it.
/// </summary>
/// <remarks>
/// Exactly-once release is the property that matters. The paths that can release a lease — normal
/// completion, error, stop, dispose, an abandoned restart — overlap in practice, and a second release
/// would hand back a permit that was never taken, letting the collaboration quietly exceed the one
/// bound that per-manager gates cannot enforce.
/// </remarks>
public class AgentCapacityLimiterTests
{
    [Fact]
    public void TryAcquire_HandsOutPermitsUpToCapacity_ThenRefusesWithoutBlocking()
    {
        var limiter = new AgentCapacityLimiter(2);

        var first = limiter.TryAcquire("agent-a");
        var second = limiter.TryAcquire("agent-b");
        var third = limiter.TryAcquire("agent-c");

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        // Null rather than a wait: a blocking acquire combined with the required
        // root-lease-before-manager-gate ordering would be a deadlock waiting to be found.
        third.Should().BeNull();
        limiter.InUse.Should().Be(2);
        limiter.Available.Should().Be(0);
    }

    [Fact]
    public void Release_ReturnsThePermit_AndIsIdempotent()
    {
        var limiter = new AgentCapacityLimiter(1);
        var lease = limiter.TryAcquire("agent-a")!;

        lease.Release().Should().BeTrue();
        lease.IsReleased.Should().BeTrue();
        limiter.InUse.Should().Be(0);

        // A second release from an overlapping teardown path must not create a permit.
        lease.Release().Should().BeFalse();
        lease.Dispose();
        limiter.InUse.Should().Be(0);
    }

    [Fact]
    public void ReleasedPermit_IsAvailableToTheNextAgent()
    {
        var limiter = new AgentCapacityLimiter(1);
        var first = limiter.TryAcquire("agent-a")!;

        limiter.TryAcquire("agent-b").Should().BeNull();
        first.Dispose();

        var second = limiter.TryAcquire("agent-b");
        second.Should().NotBeNull();
        second!.AgentId.Should().Be("agent-b");
    }

    [Fact]
    public void TryAcquire_UnderConcurrency_NeverOvershootsCapacity()
    {
        const int capacity = 8;
        var limiter = new AgentCapacityLimiter(capacity);

        var leases = new AgentCapacityLease?[64];
        Parallel.For(0, leases.Length, i => leases[i] = limiter.TryAcquire($"agent-{i}"));

        leases.Count(lease => lease is not null).Should().Be(capacity);
        limiter.InUse.Should().Be(capacity);

        Parallel.ForEach(leases, lease => lease?.Release());
        limiter.InUse.Should().Be(0);
    }

    [Fact]
    public void ConcurrentReleasesOfOneLease_ReturnExactlyOnePermit()
    {
        var limiter = new AgentCapacityLimiter(4);
        _ = limiter.TryAcquire("agent-a");
        _ = limiter.TryAcquire("agent-b");
        var lease = limiter.TryAcquire("agent-c")!;

        var succeeded = 0;
        Parallel.For(
            0,
            32,
            _ =>
            {
                if (lease.Release())
                {
                    _ = Interlocked.Increment(ref succeeded);
                }
            }
        );

        succeeded.Should().Be(1);
        limiter.InUse.Should().Be(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsUnusableCapacity(int capacity)
    {
        FluentActions.Invoking(() => new AgentCapacityLimiter(capacity)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void TryAcquire_RejectsBlankIdentity()
    {
        var limiter = new AgentCapacityLimiter(1);

        FluentActions.Invoking(() => limiter.TryAcquire("  ")).Should().Throw<ArgumentException>();
    }
}
