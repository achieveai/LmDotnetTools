using System.Collections.Concurrent;
using System.Diagnostics;
using LmStreaming.Sample.Services;

namespace LmStreaming.Sample.Tests.Services;

/// <summary>
/// Concurrency contract of <see cref="TranscriptFlushScheduler"/> (#251). The scheduler is a deliberate
/// copy of <c>UsagePersistenceWriter</c>'s Schedule/Drain shape — that type is internal to LmMultiTurn and
/// visible only to test assemblies, not to the sample's production assembly — so it carries its own tests.
/// The two copy-specific behaviours are pinned here: pending work is a SET of keys (one conversation's
/// pending flush is never dropped by another's), and a failing key is caught PER KEY so it cannot strand
/// every other conversation.
/// <para>
/// Synchronisation is by <see cref="TaskCompletionSource"/> / <see cref="SemaphoreSlim"/> throughout. The
/// only timeouts are failure bounds (a hang fails the test) and two deliberately-bounded negative windows,
/// noted where they appear — neither can produce a false failure.
/// </para>
/// </summary>
public sealed class TranscriptFlushSchedulerTests
{
    private static readonly TimeSpan FailureTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Long enough to observe a flush that should not happen; short enough to keep tests fast.</summary>
    private static readonly TimeSpan NegativeWindow = TimeSpan.FromMilliseconds(300);

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Records which keys were flushed, and lets a test await the Nth flush of a given key.</summary>
    private sealed class FlushRecorder
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _signals = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _flushed = new();

        public IReadOnlyCollection<string> Flushed => _flushed;

        public SemaphoreSlim SignalFor(string key) =>
            _signals.GetOrAdd(key, static _ => new SemaphoreSlim(0));

        public void Record(string key)
        {
            _flushed.Enqueue(key);
            _ = SignalFor(key).Release();
        }

        /// <summary>Awaits one more flush of <paramref name="key"/>; fails the test rather than hanging.</summary>
        public async Task WaitForFlushAsync(string key)
        {
            var observed = await SignalFor(key).WaitAsync(FailureTimeout);
            observed.Should().BeTrue($"'{key}' should have been flushed within {FailureTimeout}");
        }
    }

    /// <summary>
    /// AC 8. <c>Schedule()</c> must not put I/O on the caller's thread. Proven two ways at once, both
    /// deterministic: the flush callback never completes until the test releases it (so a <c>Schedule</c>
    /// that awaited it would deadlock and fail on the xunit timeout rather than pass by luck), and the
    /// callback provably runs on a different thread than the one that called <c>Schedule</c> — which is what
    /// the <c>await Task.Yield()</c> at the top of the drain buys, since <c>Schedule</c> starts the drain
    /// while holding its lock.
    /// </summary>
    [Fact]
    public async Task Schedule_ReturnsWithoutRunningTheFlushOnTheCallersThread()
    {
        var entered = Signal();
        var release = Signal();
        var flushThreadId = 0;
        using var scheduler = new TranscriptFlushScheduler(
            (_, _) =>
            {
                flushThreadId = Environment.CurrentManagedThreadId;
                entered.SetResult();
                return release.Task;
            }
        );
        var callerThreadId = Environment.CurrentManagedThreadId;

        scheduler.Schedule("a");

        await entered.Task.WaitAsync(FailureTimeout);
        flushThreadId
            .Should()
            .NotBe(
                callerThreadId,
                "the drain yields before touching the flush, so no I/O can run on the subscriber's thread"
            );
        release.SetResult();
    }

    [Fact]
    public async Task TwoDistinctKeys_AreBothFlushed()
    {
        var recorder = new FlushRecorder();
        using var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                recorder.Record(key);
                return Task.CompletedTask;
            }
        );

        scheduler.Schedule("a");
        scheduler.Schedule("b");

        await recorder.WaitForFlushAsync("a");
        await recorder.WaitForFlushAsync("b");
    }

    /// <summary>
    /// The reason pending work is a SET and not one <c>bool</c> slot: with a single slot, scheduling
    /// conversation B while conversation A's flush is in flight silently consumes A's re-arm, so A's newest
    /// turn never reaches disk.
    /// </summary>
    [Fact]
    public async Task KeyScheduledWhileAnotherKeyIsInFlight_IsNotDropped()
    {
        var recorder = new FlushRecorder();
        var entered = Signal();
        var release = Signal();
        using var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                if (key == "a" && entered.TrySetResult())
                {
                    return release.Task;
                }

                recorder.Record(key);
                return Task.CompletedTask;
            }
        );

        scheduler.Schedule("a");
        await entered.Task.WaitAsync(FailureTimeout);
        scheduler.Schedule("a");
        scheduler.Schedule("b");
        release.SetResult();

        await recorder.WaitForFlushAsync("a");
        await recorder.WaitForFlushAsync("b");
    }

    /// <summary>
    /// THE regression test for this copy. <c>UsagePersistenceWriter</c>'s catch re-arms the pending flag and
    /// returns, aborting the drain — so with N keys one permanently-broken conversation strands every other
    /// one, and the re-armed failing key is picked first by the restarted drain and aborts it again. Here the
    /// catch is per key, so a healthy key keeps flushing across cycle after cycle beside a key that always
    /// throws.
    /// </summary>
    [Fact]
    public async Task PermanentlyFailingKey_DoesNotStrandAHealthyKey()
    {
        const int cycles = 5;
        var recorder = new FlushRecorder();
        var errors = new ConcurrentQueue<(string Key, Exception Error)>();
        using var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                if (key == "broken")
                {
                    throw new InvalidOperationException("this conversation's flush is permanently broken");
                }

                recorder.Record(key);
                return Task.CompletedTask;
            },
            (key, error) => errors.Enqueue((key, error))
        );

        for (var cycle = 0; cycle < cycles; cycle++)
        {
            scheduler.Schedule("broken");
            scheduler.Schedule("healthy");
            await recorder.WaitForFlushAsync("healthy");
        }

        recorder
            .Flushed.Count(key => key == "healthy")
            .Should()
            .BeGreaterThanOrEqualTo(cycles, "a broken conversation must never block a healthy one");
        errors.Should().NotBeEmpty();
        errors
            .Select(entry => entry.Key)
            .Should()
            .AllBe("broken", "the failure is reported against the key that failed");
        errors.Select(entry => entry.Error).Should().AllBeOfType<InvalidOperationException>();
    }

    /// <summary>
    /// Re-scheduling a key whose flush is already in flight must earn exactly one more flush: the key left
    /// the pending set before the flush began, so re-adding it is neither lost (the new lines would never be
    /// written) nor duplicated per call (a burst of turn boundaries would multiply gateway calls).
    /// </summary>
    [Fact]
    public async Task ReschedulingAKeyMidFlight_CoalescesIntoExactlyOneMoreFlush()
    {
        var recorder = new FlushRecorder();
        var entered = Signal();
        var release = Signal();
        var calls = 0;
        var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                {
                    entered.SetResult();
                    return release.Task;
                }

                recorder.Record(key);
                return Task.CompletedTask;
            }
        );

        scheduler.Schedule("a");
        await entered.Task.WaitAsync(FailureTimeout);
        scheduler.Schedule("a");
        scheduler.Schedule("a");
        scheduler.Schedule("a");
        release.SetResult();
        await recorder.WaitForFlushAsync("a");

        // Dispose waits on the drain, so once it returns the call count can no longer move.
        scheduler.Dispose();
        calls.Should().Be(2, "three re-schedules of an in-flight key collapse into one follow-up flush");
    }

    [Fact]
    public void Dispose_WhenIdle_DoesNotThrowAndIsIdempotent()
    {
        var scheduler = new TranscriptFlushScheduler((_, _) => Task.CompletedTask);

        var dispose = scheduler.Dispose;

        dispose.Should().NotThrow();
        dispose.Should().NotThrow();
    }

    /// <summary>
    /// Disposal waits BOUNDED on the in-flight flush. The callback here ignores the cancellation token, so
    /// this proves the bound itself rather than cooperative cancellation — host teardown must not hang on a
    /// gateway call that never returns.
    /// </summary>
    [Fact]
    public async Task Dispose_WithAFlushThatNeverCompletes_ReturnsPromptly()
    {
        var entered = Signal();
        var release = Signal();
        var scheduler = new TranscriptFlushScheduler(
            (_, _) =>
            {
                entered.SetResult();
                return release.Task;
            },
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(150)
        );
        scheduler.Schedule("a");
        await entered.Task.WaitAsync(FailureTimeout);

        var elapsed = Stopwatch.StartNew();
        scheduler.Dispose();
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(FailureTimeout, "the wait on an in-flight drain is bounded");
        release.SetResult();
    }

    /// <summary>
    /// Proves the CUT: there is no disposal-time flush. A key pending when <c>Dispose</c> is called is
    /// dropped, not written — an awaited shutdown flush loses a race with the sandbox session registry's
    /// disposal guard and is swallowed anyway, so the gap is accepted and documented rather than papered over.
    /// The negative window can only weaken this assertion, never make it fail spuriously.
    /// </summary>
    [Fact]
    public async Task Dispose_DoesNotFlushKeysThatWereStillPending()
    {
        var recorder = new FlushRecorder();
        var entered = Signal();
        var release = Signal();
        var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                if (key == "a")
                {
                    entered.SetResult();
                    return release.Task;
                }

                recorder.Record(key);
                return Task.CompletedTask;
            },
            shutdownDrainTimeout: TimeSpan.FromMilliseconds(150)
        );

        scheduler.Schedule("a");
        await entered.Task.WaitAsync(FailureTimeout);
        scheduler.Schedule("b");
        scheduler.Dispose();
        release.SetResult();

        var flushedB = await recorder.SignalFor("b").WaitAsync(NegativeWindow);
        flushedB.Should().BeFalse("disposal drops pending work; it deliberately does not flush it");
        recorder.Flushed.Should().NotContain("b");
    }

    [Fact]
    public async Task Schedule_AfterDispose_IsANoOp()
    {
        var recorder = new FlushRecorder();
        var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                recorder.Record(key);
                return Task.CompletedTask;
            }
        );
        scheduler.Dispose();

        var schedule = () => scheduler.Schedule("a");

        schedule.Should().NotThrow("a best-effort mirror must not throw into the subscriber loop");
        (await recorder.SignalFor("a").WaitAsync(NegativeWindow))
            .Should()
            .BeFalse("nothing scheduled after disposal is flushed");
    }

    /// <summary>
    /// The error channel must not be able to stop the loop: a throwing <c>onError</c> would otherwise fault
    /// the drain task and leave the fault unobserved.
    /// </summary>
    [Fact]
    public async Task ThrowingErrorCallback_DoesNotStopTheDrain()
    {
        var recorder = new FlushRecorder();
        using var scheduler = new TranscriptFlushScheduler(
            (key, _) =>
            {
                if (key == "broken")
                {
                    throw new InvalidOperationException("flush failed");
                }

                recorder.Record(key);
                return Task.CompletedTask;
            },
            (_, _) => throw new InvalidOperationException("and so did the logger")
        );

        scheduler.Schedule("broken");
        scheduler.Schedule("healthy");

        await recorder.WaitForFlushAsync("healthy");
    }

    [Fact]
    public void Constructor_RejectsANullFlushCallback()
    {
        var construct = () => new TranscriptFlushScheduler(null!);

        construct.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Schedule_RejectsAMissingKey(string? key)
    {
        using var scheduler = new TranscriptFlushScheduler((_, _) => Task.CompletedTask);

        var schedule = () => scheduler.Schedule(key!);

        schedule.Should().Throw<ArgumentException>();
    }
}
