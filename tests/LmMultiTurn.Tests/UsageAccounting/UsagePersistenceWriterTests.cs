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
        await writer.FlushAsync();

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

        await writer.FlushAsync();

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

        await writer.FlushAsync();

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
        await writer.FlushAsync();
        var first = count;

        writer.Schedule();
        await writer.FlushAsync();

        count.Should().BeGreaterThan(first);
    }

    [Fact]
    public async Task FlushAsync_DoesNotThrow_WhenPersistFaults()
    {
        var writer = new UsagePersistenceWriter(ct => throw new InvalidOperationException("boom"));

        writer.Schedule();
        var flush = async () => await writer.FlushAsync();

        await flush.Should().NotThrowAsync();
    }
}
