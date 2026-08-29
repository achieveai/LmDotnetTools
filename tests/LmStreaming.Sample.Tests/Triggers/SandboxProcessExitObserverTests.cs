using System.Collections.Concurrent;
using System.Text;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using AchieveAi.LmDotnetTools.Sandbox;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// Unit tests for <see cref="SandboxProcessExitObserver"/> — the issue #142 Bash-tool exit bridge.
/// A scripted <see cref="FakeWaitFileReader"/> plays the workspace files API and a
/// <see cref="FakeTimeProvider"/> drives the poll loop, so no wall-clock waiting and no sandbox.
/// </summary>
public class SandboxProcessExitObserverTests
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    /// <summary>Wall-clock ceiling used ONLY as a failure bound on awaited tasks, never as a synchronizer.</summary>
    private static readonly TimeSpan FailureBound = TimeSpan.FromSeconds(10);

    private static SandboxException PathNotFound() =>
        new(SandboxErrorKind.NotFound, "no such path") { ErrorCode = "path_not_found" };

    private static SandboxException SessionEvicted() =>
        new(SandboxErrorKind.NotFound, "no such session") { ErrorCode = "session_not_found" };

    /// <summary>
    /// Scripted <see cref="ISandboxWaitFileReader"/>: per-path byte content or a throw, mutable
    /// mid-test, plus a monotone count of read attempts so the poll loop can be sequenced
    /// deterministically against clock advances.
    /// </summary>
    private sealed class FakeWaitFileReader : ISandboxWaitFileReader
    {
        private readonly ConcurrentDictionary<string, Func<byte[]>> _paths = new();
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public ConcurrentQueue<(string Path, long? MaxBytes)> Requests { get; } = new();

        public void SetContent(string path, string content) => _paths[path] = () => Encoding.UTF8.GetBytes(content);

        public void SetThrow(string path, Func<SandboxException> factory) => _paths[path] = () => throw factory();

        public Task<byte[]> ReadAsync(string relativePath, long? maxBytes, CancellationToken ct)
        {
            Interlocked.Increment(ref _reads);
            Requests.Enqueue((relativePath, maxBytes));
            if (_paths.TryGetValue(relativePath, out var supply))
            {
                try
                {
                    return Task.FromResult(supply());
                }
                catch (SandboxException ex)
                {
                    return Task.FromException<byte[]>(ex);
                }
            }

            return Task.FromException<byte[]>(PathNotFound());
        }
    }

    /// <summary>Captures log entries so the fault-streak visibility contract is assertable.</summary>
    private sealed class ListLogger : ILogger<SandboxProcessExitObserver>
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static SandboxProcessExitObserver Observer(
        FakeWaitFileReader reader,
        FakeTimeProvider time,
        ListLogger? logger = null
    ) => new(reader, time, PollInterval, logger);

    /// <summary>
    /// Advances the fake clock one poll interval at a time until <paramref name="condition"/> holds.
    /// The yield lets the observer's continuation (read → next Task.Delay registration) run between
    /// advances; the iteration cap turns a stuck loop into a failed assertion instead of a hang.
    /// </summary>
    private static async Task AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, string because)
    {
        for (var i = 0; i < 10_000 && !condition(); i++)
        {
            time.Advance(PollInterval);
            await Task.Yield();
        }

        condition().Should().BeTrue(because);
    }

    // ---- handle validation --------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../escape")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData(".hidden")]
    [InlineData("a b")]
    [InlineData("h@ndle")]
    public void WaitForExit_RejectsInvalidHandle_SynchronouslyAndWithoutReading(string handle)
    {
        var reader = new FakeWaitFileReader();
        var observer = Observer(reader, new FakeTimeProvider());

        // Action (not Func<Task>): the rejection contract is a SYNCHRONOUS throw from the wrapper,
        // before any task is returned — an async-materialized ArgumentException would fail this.
        Action act = () => _ = observer.WaitForExitAsync(handle, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
        reader.Reads.Should().Be(0, "a rejected handle must never reach a path");
    }

    [Fact]
    public void WaitForExit_RejectsOverlongHandle()
    {
        var observer = Observer(new FakeWaitFileReader(), new FakeTimeProvider());
        var overlong = new string('a', SandboxProcessExitObserver.MaxHandleLength + 1);

        Action act = () => _ = observer.WaitForExitAsync(overlong, CancellationToken.None);

        act.Should().Throw<ArgumentException>();
    }

    // ---- level-triggered completion -----------------------------------------------------------

    [Fact]
    public async Task PreRecordedExit_CompletesWithoutAnyClockAdvance()
    {
        // The level-trigger proof: an exit recorded BEFORE anyone observes is still seen — the
        // ProcessTriggerSource arm-window comment makes this the invariant a real observer must hold.
        var reader = new FakeWaitFileReader();
        reader.SetContent(".lm-waits/job1/exit", "0\n");
        reader.SetContent(".lm-waits/job1/out", "done");
        var observer = Observer(reader, new FakeTimeProvider());

        var exit = await observer.WaitForExitAsync("job1", CancellationToken.None).WaitAsync(FailureBound);

        exit.ExitCode.Should().Be(0);
        exit.Stdout.Should().Be("done");
    }

    [Fact]
    public async Task ExitAppearingAfterPolls_CompletesWithCodeAndStdout()
    {
        var reader = new FakeWaitFileReader();
        var time = new FakeTimeProvider();
        var observer = Observer(reader, time);

        var task = observer.WaitForExitAsync("job2", CancellationToken.None);
        await AdvanceUntilAsync(time, () => reader.Reads >= 3, "the loop should keep polling while the file is absent");
        task.IsCompleted.Should().BeFalse("no exit file has been written yet");

        reader.SetContent(".lm-waits/job2/exit", "42");
        reader.SetContent(".lm-waits/job2/out", "tail of output");
        await AdvanceUntilAsync(time, () => task.IsCompleted, "the next poll should observe the recorded exit");

        var exit = await task;
        exit.ExitCode.Should().Be(42);
        exit.Stdout.Should().Be("tail of output");
    }

    [Fact]
    public async Task MissingOutFile_YieldsEmptyStdout()
    {
        var reader = new FakeWaitFileReader();
        reader.SetContent(".lm-waits/job3/exit", "7");
        var observer = Observer(reader, new FakeTimeProvider());

        var exit = await observer.WaitForExitAsync("job3", CancellationToken.None).WaitAsync(FailureBound);

        exit.ExitCode.Should().Be(7);
        exit.Stdout.Should().BeEmpty();
    }

    [Fact]
    public async Task UnparseableExitFile_RepollsUntilItParses()
    {
        // `echo $? > exit` creates the file before the code lands; a not-yet-parseable read must be
        // "not yet", never a fault that completes or faults the wait.
        var reader = new FakeWaitFileReader();
        var time = new FakeTimeProvider();
        reader.SetContent(".lm-waits/job4/exit", "");
        var observer = Observer(reader, time);

        var task = observer.WaitForExitAsync("job4", CancellationToken.None);
        await AdvanceUntilAsync(time, () => reader.Reads >= 2, "an empty exit file should be re-polled");
        task.IsCompleted.Should().BeFalse();

        reader.SetContent(".lm-waits/job4/exit", "5");
        await AdvanceUntilAsync(time, () => task.IsCompleted, "the parseable content should complete the wait");
        (await task).ExitCode.Should().Be(5);
    }

    // ---- cancellation ---------------------------------------------------------------------------

    [Fact]
    public async Task Cancellation_CancelsTheWait()
    {
        var reader = new FakeWaitFileReader();
        var time = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var observer = Observer(reader, time);

        var task = observer.WaitForExitAsync("job5", cts.Token);
        await AdvanceUntilAsync(time, () => reader.Reads >= 1, "the loop should have started polling");

        cts.Cancel();

        await FluentActions
            .Awaiting(() => task.WaitAsync(FailureBound))
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    // ---- fault visibility (#161 silent-inertness discipline) -----------------------------------

    [Fact]
    public async Task PersistentReadFaults_WarnOnceAfterThreshold_AndKeepPolling()
    {
        var reader = new FakeWaitFileReader();
        var time = new FakeTimeProvider();
        var logger = new ListLogger();
        reader.SetThrow(
            ".lm-waits/job6/exit",
            () => new SandboxException(SandboxErrorKind.Unavailable, "gateway busy")
        );
        var observer = Observer(reader, time, logger);

        var task = observer.WaitForExitAsync("job6", CancellationToken.None);
        await AdvanceUntilAsync(time, () => reader.Reads >= 12, "the loop must survive transient faults");

        task.IsCompleted.Should().BeFalse("faults are transient by policy; the wait's TTL is the bound");
        logger
            .Entries.Count(e => e.Level == LogLevel.Warning)
            .Should()
            .Be(1, "the streak warns once at the threshold, not once per tick");

        // Recovery: the exit file becomes readable, the wait completes, and the recovery is logged.
        reader.SetContent(".lm-waits/job6/exit", "0");
        await AdvanceUntilAsync(time, () => task.IsCompleted, "a healthy read should complete the wait");
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Information);
    }

    [Fact]
    public async Task SessionEviction_IsAFault_NotAHealthyNotExitedPoll()
    {
        // NotFound alone is ambiguous: the gateway answers 404 for an evicted session too. Only the
        // explicit path_not_found may read as "not exited yet" — an evicted session must climb the
        // fault streak, or a dead session polls exactly like a long-running process until TTL.
        var reader = new FakeWaitFileReader();
        var time = new FakeTimeProvider();
        var logger = new ListLogger();
        reader.SetThrow(".lm-waits/job7/exit", SessionEvicted);
        var observer = Observer(reader, time, logger);

        _ = observer.WaitForExitAsync("job7", CancellationToken.None);
        await AdvanceUntilAsync(time, () => reader.Reads >= 12, "the loop keeps polling under eviction");

        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning, "an evicted session must not be silent");
    }

    [Fact]
    public async Task UnreadableStdout_DegradesToEmpty_WithExitCodePreserved()
    {
        var reader = new FakeWaitFileReader();
        var logger = new ListLogger();
        reader.SetContent(".lm-waits/job8/exit", "3");
        reader.SetThrow(".lm-waits/job8/out", () => new SandboxException(SandboxErrorKind.TransportTimeout, "slow"));
        var observer = Observer(reader, new FakeTimeProvider(), logger);

        var exit = await observer.WaitForExitAsync("job8", CancellationToken.None).WaitAsync(FailureBound);

        exit.ExitCode.Should().Be(3);
        exit.Stdout.Should().BeEmpty();
        logger.Entries.Should().Contain(e => e.Level == LogLevel.Warning);
    }

    // ---- composed arm-time rejection (#598 review F-001) ----------------------------------------

    private sealed class NoopTriggerSink : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

    private static TriggerArmRequest ProcessArmRequest(string handle) =>
        new()
        {
            WaitId = "w-" + Guid.NewGuid().ToString("N"),
            Kind = ProcessTriggerSource.KindName,
            ArgsJson = JsonSerializer.Serialize(new { handle }),
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5),
        };

    /// <summary>
    /// The F-001 pin, at the COMPOSED path where the defect lived: a real
    /// <see cref="ProcessTriggerSource"/> over a real <see cref="SandboxProcessExitObserver"/> must
    /// reject an invalid handle synchronously FROM ArmAsync. An observer-only unit test proves
    /// nothing here — the source's ObserveExit converts a WaitForExitAsync throw into a faulted,
    /// never-awaited watch, which arms "successfully" and parks to TTL.
    /// </summary>
    [Theory]
    [InlineData("bad/handle")]
    [InlineData("../escape")]
    [InlineData(".hidden")]
    [InlineData("a b")]
    public void ComposedArm_InvalidHandle_ThrowsSynchronouslyFromArm(string handle)
    {
        var reader = new FakeWaitFileReader();
        var source = new ProcessTriggerSource(Observer(reader, new FakeTimeProvider()));

        Action act = () =>
            _ = source.ArmAsync(ProcessArmRequest(handle), new NoopTriggerSink(), CancellationToken.None);

        act.Should().Throw<ArgumentException>();
        reader.Reads.Should().Be(0, "a rejected arm must never have started observing");
    }

    [Fact]
    public async Task ComposedArm_ValidHandle_ArmsAndDisposes()
    {
        var reader = new FakeWaitFileReader();
        var source = new ProcessTriggerSource(Observer(reader, new FakeTimeProvider()));

        var armed = await source.ArmAsync(ProcessArmRequest("job-ok"), new NoopTriggerSink(), CancellationToken.None);

        armed.Should().NotBeNull();
        await armed.DisposeAsync();
    }

    // ---- adapter --------------------------------------------------------------------------------

    private sealed class RecordingBrowser : IWorkspaceFileBrowser
    {
        public (string SessionId, string Path, long? MaxBytes)? LastRead { get; private set; }

        public Task<byte[]> ReadWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            long? maxBytes,
            CancellationToken ct = default
        )
        {
            LastRead = (sessionId, relativePath, maxBytes);
            return Task.FromResult(Encoding.UTF8.GetBytes("9"));
        }

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionAsync(
            string threadId,
            string persistedWorkspaceId,
            SandboxCredential? requestCredential,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<SandboxSessionResolution> ResolveThreadWorkspaceSessionForBackgroundAsync(
            string threadId,
            string persistedWorkspaceId,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<IReadOnlyList<SandboxDirectoryEntry>> ListWorkspaceDirectoryAsync(
            string sessionId,
            string relativePath,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task WriteWorkspaceFileBytesAsync(
            string sessionId,
            string relativePath,
            byte[] bytes,
            CancellationToken ct = default
        ) => throw new NotSupportedException();

        public Task<SandboxCommandResult> ExecuteWorkspaceCommandAsync(
            string sessionId,
            SandboxCommand command,
            CancellationToken ct = default
        ) => throw new NotSupportedException();
    }

    [Fact]
    public async Task WorkspaceWaitFileReader_ForwardsSessionScopedReads()
    {
        var browser = new RecordingBrowser();
        var reader = new WorkspaceWaitFileReader(browser, "sess-1");

        var bytes = await reader.ReadAsync(".lm-waits/h/exit", 256, CancellationToken.None);

        Encoding.UTF8.GetString(bytes).Should().Be("9");
        browser.LastRead.HasValue.Should().BeTrue();
        var (sessionId, path, maxBytes) = browser.LastRead!.Value;
        sessionId.Should().Be("sess-1");
        path.Should().Be(".lm-waits/h/exit");
        maxBytes.Should().Be(256);
    }

    [Fact]
    public void WorkspaceWaitFileReader_GuardsConstruction()
    {
        var browser = new RecordingBrowser();
        FluentActions.Invoking(() => new WorkspaceWaitFileReader(null!, "s")).Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new WorkspaceWaitFileReader(browser, " ")).Should().Throw<ArgumentException>();
    }
}
