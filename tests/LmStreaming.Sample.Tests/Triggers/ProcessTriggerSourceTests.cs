using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="ProcessTriggerSource"/>. The source only OBSERVES a process's exit —
/// tests inject a <see cref="FakeProcessObserver"/> so the predicate logic can be exercised without
/// any real Bash-tool process. Registration tests confirm the "process" kind is sandbox-gated.
/// </summary>
public class ProcessTriggerSourceTests
{
    private static TriggerArmRequest ArmReq(string argsJson) =>
        new()
        {
            WaitId = "tc-" + Guid.NewGuid().ToString("N"),
            Kind = ProcessTriggerSource.KindName,
            ArgsJson = argsJson,
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    private sealed class NoopSink : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static readonly NoopSink NoopSinkInstance = new();

    private sealed class CompletingSink(TaskCompletionSource<TriggerFireEvent> tcs) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            tcs.TrySetResult(fire);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingSink(Action onFire) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            onFire();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Test double for <see cref="IProcessExitObserver"/>: lets the test signal a process exit for
    /// a given handle at any time (before or after <see cref="WaitForExitAsync"/> is called). Also
    /// exposes <see cref="WaitUntilObservingAsync"/> so a disposal test can deterministically wait
    /// until the source has actually started observing (registered its cancellation callback)
    /// before disposing — otherwise "dispose" and "signal exit" would race directly on the shared
    /// completion source with no defined winner, which is a test-timing concern, not a production
    /// behavior worth asserting on.
    /// </summary>
    private sealed class FakeProcessObserver : IProcessExitObserver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProcessExit>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _observing = new();
        private int _observeCount;

        /// <summary>How many times <see cref="WaitForExitAsync"/> has been called, across handles.</summary>
        public int ObserveCount => Volatile.Read(ref _observeCount);

        public Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct)
        {
            Interlocked.Increment(ref _observeCount);
            var tcs = _pending.GetOrAdd(
                handle,
                _ => new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            // Register the cancellation callback BEFORE signaling "observing" so a test that awaits
            // WaitUntilObservingAsync is guaranteed the callback is already in place before it acts.
            ct.Register(() => tcs.TrySetCanceled(ct));
            _observing
                .GetOrAdd(
                    handle,
                    _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                )
                .TrySetResult(true);
            return tcs.Task;
        }

        public void SignalExit(string handle, int exitCode, string stdout)
        {
            var tcs = _pending.GetOrAdd(
                handle,
                _ => new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously)
            );
            tcs.TrySetResult(new ProcessExit(exitCode, stdout));
        }

        public Task WaitUntilObservingAsync(string handle) =>
            _observing
                .GetOrAdd(
                    handle,
                    _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                )
                .Task;

        /// <summary>Accepts every handle — these tests exercise predicate/lifecycle logic, not validation.</summary>
        public void ValidateHandle(string handle) { }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> that CAPTURES posted continuations instead of running
    /// them. Installing it makes <c>await Task.Yield()</c> park indefinitely, so a test can inspect
    /// exactly what an arm did before its yield resumed — with no thread-pool race in the assertion.
    /// <see cref="Drain"/> releases the captured continuations onto the thread pool afterwards so
    /// the armed trigger goes on to behave normally.
    /// </summary>
    private sealed class ManualPumpSyncContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public void Drain()
        {
            while (_posted.TryDequeue(out var item))
            {
                ThreadPool.QueueUserWorkItem(_ => item.Callback(item.State));
            }
        }
    }

    [Fact]
    public async Task Arm_StartsObservingExit_BeforeItYields()
    {
        // Regression guard for the arm-window shape PR #462 fixed in FileTailTriggerSource. The
        // watch loop opens with `await Task.Yield()`; anything it does AFTER that yield happens on a
        // continuation that may not run until well after ArmAsync's caller has resumed. The normal
        // caller ordering is "arm the wait, then do the thing that exits" — so if the observation
        // were only registered after the yield, an exit landing in that window would be seen by an
        // observer that had not subscribed yet.
        //
        // Today's FakeProcessObserver (and the intended real one) is LEVEL-triggered: it records the
        // exit and hands it to whoever asks later, so a late subscription still sees it and nothing
        // is lost. That is the only reason the old ordering was safe, and it is an invariant of the
        // OBSERVER, not of this source — an edge-triggered observer (subscribe-to-an-event) would
        // drop the exit silently and the wait would block forever on a watcher that looks healthy.
        // This test pins the ordering itself so no future observer can reintroduce that race:
        // WaitForExitAsync must be called synchronously, inside ArmAsync, before the yield.
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        var pump = new ManualPumpSyncContext();
        var previous = SynchronizationContext.Current;
        IArmedTrigger handle;
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            // ArmAsync returns an already-completed ValueTask, so this await does not itself post to
            // the pump; the only thing parked there is the watch loop's own Task.Yield().
            handle = await src.ArmAsync(ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);

            observer
                .ObserveCount.Should()
                .Be(
                    1,
                    "the exit observation must be registered synchronously within ArmAsync, not on the "
                        + "post-yield continuation, so no exit can land in the arm window unobserved"
                );
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        // Non-vacuity: with the ordering pinned, the trigger still fires end to end.
        await using (handle)
        {
            pump.Drain();
            observer.SignalExit("h1", exitCode: 0, stdout: "ok");

            var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
            evt.Payload.Should().Contain("\"exitCode\":0");
        }
    }

    [Fact]
    public async Task Fire_WhenObservedProcessExitsWithMatchingCode()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 0, stdout: "ok");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("\"exitCode\":0");
    }

    [Fact]
    public async Task Fire_Payload_OmitsRawStdout_NoPatternConfigured()
    {
        // Regression: the fire payload must never carry raw process stdout (it can hold
        // secrets/PII and flows into history/model/UI) — only metadata like exitCode and,
        // when a stdoutPattern was configured, whether it matched.
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 0, stdout: "super-secret-token-xyz");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().NotContain("super-secret-token-xyz");
        evt.Payload.Should().NotContain("\"stdout\"");
        evt.Payload.Should().Contain("\"exitCode\":0");
        evt.Payload.Should().Contain("\"stdoutMatched\":false");
    }

    [Fact]
    public async Task Fire_Payload_StdoutMatchedTrue_WhenPatternConfiguredAndMatched()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","stdoutPattern":"^DONE$"}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 0, stdout: "DONE");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("\"stdoutMatched\":true");
        evt.Payload.Should().NotContain("\"stdout\"");
    }

    [Fact]
    public async Task NoFire_WhenExitCodePredicateFails()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fireCount = 0;
        var sink = new CountingSink(() => Interlocked.Increment(ref fireCount));

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 1, stdout: "boom");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        fireCount.Should().Be(0, "exit(1) does not satisfy expectExitCode:0");
    }

    [Fact]
    public async Task NoFire_WhenStdoutPatternPredicateFails()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fireCount = 0;
        var sink = new CountingSink(() => Interlocked.Increment(ref fireCount));

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","stdoutPattern":"DONE"}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 0, stdout: "still running");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        fireCount.Should().Be(0, "stdout does not match the required pattern");
    }

    [Fact]
    public async Task Fire_WhenStdoutPatternMatches()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","stdoutPattern":"^DONE$"}"""),
            sink,
            CancellationToken.None
        );

        observer.SignalExit("h1", exitCode: 3, stdout: "DONE");

        var evt = await fired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        evt.Payload.Should().Contain("\"exitCode\":3");
    }

    [Fact]
    public async Task Arm_Rejects_MissingHandle()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);

        var act = () => src.ArmAsync(ArmReq("{}"), NoopSinkInstance, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ArmAsync_Throws_WhenBackedByNoopObserver()
    {
        // Regression: with no real exit observer wired in, arming used to park harmlessly until
        // the wait's own ceiling timeout — a slow, confusing way to fail. It must now fail fast at
        // arm time with a clear reason (maps to the runtime's invalid_args rejection).
        var src = new ProcessTriggerSource(NoopProcessExitObserver.Instance);

        var act = () =>
            src.ArmAsync(ArmReq("""{"handle":"h1","expectExitCode":0}"""), NoopSinkInstance, CancellationToken.None)
                .AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Dispose_StopsFurtherFires()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fireCount = 0;
        var sink = new CountingSink(() => Interlocked.Increment(ref fireCount));

        var handle = await src.ArmAsync(ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);
        // Wait until the source has actually started observing before disposing, so dispose is
        // guaranteed to win the race against a subsequent SignalExit (see FakeProcessObserver docs).
        await observer.WaitUntilObservingAsync("h1").WaitAsync(TimeSpan.FromSeconds(5));
        await handle.DisposeAsync();

        observer.SignalExit("h1", exitCode: 0, stdout: "ok");
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        fireCount.Should().Be(0, "a disposed handle must never fire");
    }

    [Fact]
    public void Registration_OmittedWhenSandboxDisabled()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: false);
        options.AdditionalRegistrations.Should().NotContain(r => r.Kind == ProcessTriggerSource.KindName);
    }

    [Fact]
    public void Registration_PresentWhenSandboxEnabled()
    {
        var options = SampleTriggerRegistrations.Build(sandboxEnabled: true);
        options.AdditionalRegistrations.Should().Contain(r => r.Kind == ProcessTriggerSource.KindName);
    }
}
