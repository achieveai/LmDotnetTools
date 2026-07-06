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

        public Task<ProcessExit> WaitForExitAsync(string handle, CancellationToken ct)
        {
            var tcs = _pending.GetOrAdd(
                handle,
                _ => new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously));
            // Register the cancellation callback BEFORE signaling "observing" so a test that awaits
            // WaitUntilObservingAsync is guaranteed the callback is already in place before it acts.
            ct.Register(() => tcs.TrySetCanceled(ct));
            _observing
                .GetOrAdd(handle, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
                .TrySetResult(true);
            return tcs.Task;
        }

        public void SignalExit(string handle, int exitCode, string stdout)
        {
            var tcs = _pending.GetOrAdd(
                handle,
                _ => new TaskCompletionSource<ProcessExit>(TaskCreationOptions.RunContinuationsAsynchronously));
            tcs.TrySetResult(new ProcessExit(exitCode, stdout));
        }

        public Task WaitUntilObservingAsync(string handle) =>
            _observing
                .GetOrAdd(handle, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task;
    }

    [Fact]
    public async Task Fire_WhenObservedProcessExitsWithMatchingCode()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sink = new CompletingSink(fired);

        await using var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);

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
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);

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
            ArmReq("""{"handle":"h1","stdoutPattern":"^DONE$"}"""), sink, CancellationToken.None);

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
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);

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
            ArmReq("""{"handle":"h1","stdoutPattern":"DONE"}"""), sink, CancellationToken.None);

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
            ArmReq("""{"handle":"h1","stdoutPattern":"^DONE$"}"""), sink, CancellationToken.None);

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

        var act = () => src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), NoopSinkInstance, CancellationToken.None).AsTask();

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Dispose_StopsFurtherFires()
    {
        var observer = new FakeProcessObserver();
        var src = new ProcessTriggerSource(observer);
        var fireCount = 0;
        var sink = new CountingSink(() => Interlocked.Increment(ref fireCount));

        var handle = await src.ArmAsync(
            ArmReq("""{"handle":"h1","expectExitCode":0}"""), sink, CancellationToken.None);
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
