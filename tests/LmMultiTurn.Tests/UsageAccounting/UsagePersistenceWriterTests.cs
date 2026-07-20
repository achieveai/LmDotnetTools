using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using FluentAssertions;
using Xunit;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
///     Verifies the serialized/coalesced usage writer (#196): a scheduled write becomes durable only once
///     awaited via <see cref="UsagePersistenceWriter.FlushAsync" />, bursts coalesce rather than running one
///     write per observation, and a later schedule after a drain triggers a fresh write.
/// </summary>
public class UsagePersistenceWriterTests
{
    [Fact]
    public async Task FlushAsync_AwaitsScheduledWrite()
    {
        var count = 0;
        var writer = new UsagePersistenceWriter(ct =>
        {
            _ = Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        writer.Schedule();
        var durable = await writer.FlushAsync();

        durable.Should().BeTrue();
        count.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FlushAsync_IsNoOp_WhenNothingScheduled()
    {
        var count = 0;
        var writer = new UsagePersistenceWriter(ct =>
        {
            _ = Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        var durable = await writer.FlushAsync();

        durable.Should().BeTrue();
        count.Should().Be(0);
    }

    [Fact]
    public async Task Schedule_CoalescesBurst_IntoFarFewerWrites()
    {
        var persisted = 0;
        var writer = new UsagePersistenceWriter(async ct =>
        {
            _ = Interlocked.Increment(ref persisted);
            await Task.Yield();
        });

        for (var i = 0; i < 50; i++)
        {
            writer.Schedule();
        }

        var durable = await writer.FlushAsync();

        durable.Should().BeTrue();
        persisted.Should().BeGreaterThan(0);
        persisted.Should().BeLessThan(50);
    }

    [Fact]
    public async Task Schedule_AfterDrain_TriggersAnotherWrite()
    {
        var count = 0;
        var writer = new UsagePersistenceWriter(ct =>
        {
            _ = Interlocked.Increment(ref count);
            return Task.CompletedTask;
        });

        writer.Schedule();
        _ = await writer.FlushAsync();
        var first = count;

        writer.Schedule();
        _ = await writer.FlushAsync();

        count.Should().BeGreaterThan(first);
    }

    [Fact]
    public async Task FlushAsync_ReportsNotDurable_AndInvokesOnError_WhenPersistFails()
    {
        // A failed authoritative write must NOT be reported as a clean durability boundary — Flush returns
        // false and the failure is surfaced once via onError.
        var errors = 0;
        var writer = new UsagePersistenceWriter(
            ct => throw new InvalidOperationException("boom"),
            onError: _ => Interlocked.Increment(ref errors));

        writer.Schedule();
        var durable = await writer.FlushAsync();

        durable.Should().BeFalse();
        errors.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FlushAsync_BecomesDurable_WhenAFailedWriteLaterSucceeds()
    {
        // The failed write is retained; once the store recovers, a later flush persists it and reports true.
        var fail = true;
        var writer = new UsagePersistenceWriter(ct =>
        {
            if (Volatile.Read(ref fail))
            {
                throw new InvalidOperationException("boom");
            }

            return Task.CompletedTask;
        });

        writer.Schedule();
        (await writer.FlushAsync()).Should().BeFalse();

        Volatile.Write(ref fail, false);
        (await writer.FlushAsync()).Should().BeTrue();
    }
}
