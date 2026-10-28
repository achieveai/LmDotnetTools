using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Tests.UsageAccounting;

/// <summary>
///     Verifies the serialized/coalesced usage writer (#196): a scheduled write becomes durable only once
///     awaited via <see cref="UsagePersistenceWriter.FlushAsync" />, bursts coalesce rather than running one
///     write per observation, and a later schedule after a drain triggers a fresh write.
/// </summary>
public class UsagePersistenceWriterTests(ITestOutputHelper output) : LoggingTestBase(output)
{
    [Fact]
    public async Task WaitForScheduledAsync_DoesNotWaitForObservationsScheduledAfterItsBoundary()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        var writer = new UsagePersistenceWriter(async ct =>
        {
            if (Interlocked.Increment(ref writes) == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            else
            {
                secondStarted.SetResult();
                await releaseSecond.Task;
            }
        });

        writer.Schedule();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var capturedBoundary = writer.WaitForScheduledAsync();
        var shutdownBoundary = writer.FlushAsync();
        for (var i = 0; i < 50; i++)
        {
            writer.Schedule();
        }
        var laterBoundary = writer.WaitForScheduledAsync();
        try
        {
            secondStarted.Task.IsCompleted.Should().BeFalse("writes are serialized");
            releaseFirst.SetResult();
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Logger.LogInformation(
                "Captured usage boundary: CapturedCompleted={CapturedCompleted}, LaterCompleted={LaterCompleted}, ShutdownCompleted={ShutdownCompleted}, Writes={Writes}",
                capturedBoundary.IsCompleted,
                laterBoundary.IsCompleted,
                shutdownBoundary.IsCompleted,
                writes
            );
            capturedBoundary.IsCompleted.Should().BeTrue("its captured write is already durable");
            (await capturedBoundary).Should().BeTrue();
            laterBoundary.IsCompleted.Should().BeFalse("the later observation is still held in storage");
            shutdownBoundary.IsCompleted.Should().BeFalse("shutdown must drain later scheduled observations too");
        }
        finally
        {
            releaseFirst.TrySetResult();
            releaseSecond.TrySetResult();
            (await capturedBoundary).Should().BeTrue();
            (await laterBoundary).Should().BeTrue();
            (await shutdownBoundary).Should().BeTrue();
        }
        writes.Should().Be(2, "the fifty later observations coalesce into one serialized trailing write");
    }

    [Fact]
    public async Task Schedule_WhileAFailureIsBeingReported_IsRetried()
    {
        var retried = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        UsagePersistenceWriter? writer = null;
        writer = new UsagePersistenceWriter(
            async ct =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    throw new InvalidOperationException("injected storage failure");
                }

                retried.TrySetResult();
            },
            // An observation that lands while the failure is still being logged.
            onError: _ => writer!.Schedule()
        );

        writer.Schedule();

        await retried.Task.WaitAsync(TimeSpan.FromSeconds(5));
        (await writer.FlushAsync()).Should().BeTrue();
        attempts.Should().Be(2);
        Logger.LogInformation("Schedule during failure reporting retried: Attempts={Attempts}", attempts);
    }

    [Fact]
    public async Task WaitForScheduledAsync_ReportsTheFailureBeforeReleasingItsWaiter()
    {
        var reporting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finishReporting = new ManualResetEventSlim();
        var writer = new UsagePersistenceWriter(
            async ct =>
            {
                await Task.Yield();
                throw new InvalidOperationException("injected storage failure");
            },
            onError: error =>
            {
                reporting.SetResult();
                _ = finishReporting.Wait(TimeSpan.FromSeconds(10));
            }
        );

        writer.Schedule();
        var boundary = writer.WaitForScheduledAsync();
        await reporting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            // A caller that sees the failed boundary must find the failure already reported.
            boundary.IsCompleted.Should().BeFalse("the failure is still being reported");
        }
        finally
        {
            finishReporting.Set();
        }

        (await boundary.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
    }

    [Fact]
    public async Task WaitForScheduledAsync_FailedWriteReleasesCapturedAndLaterWaiters_ThenRecovers()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var durableValue = 0;
        var observedValue = 1;
        var errors = 0;
        var writer = new UsagePersistenceWriter(
            async ct =>
            {
                var capturedValue = observedValue;
                if (Interlocked.Increment(ref attempts) == 1)
                {
                    started.SetResult();
                    await release.Task;
                    throw new InvalidOperationException("injected storage failure");
                }
                durableValue = capturedValue;
            },
            onError: _ => Interlocked.Increment(ref errors)
        );
        writer.Schedule();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var capturedBoundary = writer.WaitForScheduledAsync();
        observedValue = 2;
        writer.Schedule();
        var laterBoundary = writer.WaitForScheduledAsync();
        release.SetResult();

        (await capturedBoundary.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        (await laterBoundary.WaitAsync(TimeSpan.FromSeconds(10))).Should().BeFalse();
        durableValue.Should().Be(0, "failed storage cannot advance durability");
        errors.Should().Be(1);
        (await writer.FlushAsync()).Should().BeTrue();
        durableValue.Should().Be(2, "recovery retains the later pending observation");
        attempts.Should().Be(2);
        Logger.LogInformation(
            "Recovered usage boundary: DurableValue={DurableValue}, Attempts={Attempts}",
            durableValue,
            attempts
        );
    }

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
            onError: _ => Interlocked.Increment(ref errors)
        );

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
