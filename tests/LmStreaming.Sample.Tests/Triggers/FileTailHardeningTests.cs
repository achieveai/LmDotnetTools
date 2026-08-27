using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;
using AchieveAi.LmDotnetTools.LmStreaming.Sample.Triggers;
using AchieveAi.LmDotnetTools.LmTestUtils;
using Microsoft.Extensions.Logging;

namespace LmStreaming.Sample.Tests.Triggers;

/// <summary>
/// The #161 hardening surface for <see cref="FileTailTriggerSource"/>: privacy redaction and the
/// metadata-only content mode (a matched line is forwarded into model context and persisted
/// history, so credentials in it must not survive), and the consecutive-failure accounting that
/// stops a structurally blind poll loop from passing for a healthy one.
/// </summary>
public class FileTailHardeningTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "file-tail-hardening-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static TriggerArmRequest ArmReq(string path) =>
        new()
        {
            WaitId = "tc-" + Guid.NewGuid().ToString("N"),
            Kind = FileTailTriggerSource.KindName,
            ArgsJson = System.Text.Json.JsonSerializer.Serialize(new { path }),
            ArmedAt = DateTimeOffset.UtcNow,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(10),
        };

    private sealed class CompletingSink(TaskCompletionSource<TriggerFireEvent> tcs) : ITriggerEventSink
    {
        public ValueTask FireAsync(TriggerFireEvent fire, CancellationToken cancellationToken)
        {
            tcs.TrySetResult(fire);
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Captures log entries so a test can assert the watcher actually said something.</summary>
    private sealed class CapturingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    #region Content mode

    [Fact]
    public async Task MetadataOnlyMode_WithholdsTheLineContentEntirely()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "app.log");
        await File.WriteAllTextAsync(file, "");
        var src = new FileTailTriggerSource([root], FileTailContentMode.MetadataOnly);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await src.ArmAsync(ArmReq(file), new CompletingSink(fired), CancellationToken.None);
        await File.AppendAllTextAsync(file, "ORDER 4417 shipped to 14 Privet Drive\n");

        await Wait.UntilAsync(() => fired.Task.IsCompleted, "the file_tail watcher reported the append");
        var evt = await fired.Task;

        // Not merely redacted — nothing from the line survives, including text no pattern would
        // ever match. That is the whole distinction between this mode and Redacted.
        evt.Payload.Should().NotContain("ORDER");
        evt.Payload.Should().NotContain("Privet Drive");
        evt.Payload.Should().Contain("content withheld", "the model must still learn the event happened");
    }

    [Theory]
    [InlineData("connect: Server=db1;Password=hunter2;Uid=sa", "hunter2")]
    [InlineData("GET /v1 api_key=abcd1234efgh5678ijkl", "abcd1234efgh5678ijkl")]
    [InlineData("aws key AKIAIOSFODNN7EXAMPLE rotated", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("slack hook xoxb-123456789012-abcdefghij failed", "xoxb-123456789012-abcdefghij")]
    [InlineData("jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r wrong", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r")]
    public void Redact_RemovesTheSecret(string line, string secret)
    {
        var redacted = TriggerContentRedactor.Redact(line);

        redacted.Should().NotContain(secret);
        redacted.Should().Contain("[redacted]");
    }

    [Fact]
    public void Redact_LeavesOrdinaryDiagnosticTextAlone()
    {
        // The redactor earns its place only if the line is still worth reading afterwards. A
        // redactor that eats ordinary log text would push every deployment to MetadataOnly.
        const string line = "WARN pool exhausted after 30s: 12 of 12 connections busy, retrying in 500ms";

        TriggerContentRedactor.Redact(line).Should().Be(line);
    }

    #endregion

    #region Consecutive-failure accounting

    [Fact]
    public void PollFaultStreak_StaysQuietBelowThreshold_ThenWarnsOnceAtIt()
    {
        var streak = new PollFaultStreak(warnAfter: 3, repeatEvery: 5);

        streak.RecordFailure().Should().BeFalse("one failed tick is a rotation race");
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeTrue("the third consecutive failure crosses the threshold");
        streak.RecordFailure().Should().BeFalse("the threshold warning is emitted once, not per tick");
    }

    [Fact]
    public void PollFaultStreak_RepeatsPeriodically_SoALongFaultStaysVisible()
    {
        var streak = new PollFaultStreak(warnAfter: 3, repeatEvery: 5);
        for (var i = 0; i < 3; i++)
        {
            streak.RecordFailure();
        }

        // 4..7 are silent; 8 (== warnAfter + repeatEvery) warns again.
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeTrue("a fault lasting hours must not go quiet after one line");
    }

    [Fact]
    public void PollFaultStreak_ReportsRecoveryOnlyWhenItHadWarned()
    {
        var streak = new PollFaultStreak(warnAfter: 2, repeatEvery: 5);

        streak.RecordFailure();
        streak.RecordSuccess().Should().BeFalse("nothing was ever reported, so there is nothing to retract");

        streak.RecordFailure();
        streak.RecordFailure().Should().BeTrue();
        streak.RecordSuccess().Should().BeTrue("a warning with no recovery line leaves the fault looking live");
        streak.RecordSuccess().Should().BeFalse("recovery is reported once");
    }

    [Fact]
    public void PollFaultStreak_ResetsTheCountOnAHealthyTick()
    {
        var streak = new PollFaultStreak(warnAfter: 3, repeatEvery: 5);

        streak.RecordFailure();
        streak.RecordFailure();
        streak.RecordSuccess();
        streak.Consecutive.Should().Be(0);

        // Two more failures must NOT reach the threshold: the streak restarted.
        streak.RecordFailure().Should().BeFalse();
        streak.RecordFailure().Should().BeFalse();
    }

    [Fact]
    public async Task AVanishedFile_IsReportedInsteadOfLeavingTheWatcherSilentlyInert()
    {
        var root = CreateTempDir();
        var file = Path.Combine(root, "app.log");
        await File.WriteAllTextAsync(file, "");
        var logger = new CapturingLogger();
        var src = new FileTailTriggerSource([root], FileTailContentMode.Redacted, logger);
        var fired = new TaskCompletionSource<TriggerFireEvent>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var handle = await src.ArmAsync(ArmReq(file), new CompletingSink(fired), CancellationToken.None);

        // The file the wait was armed against goes away. The loop keeps polling and can never fire
        // again; before #161 it said nothing at all, so the wait's eventual TTL expiry read as
        // "nothing matched" rather than "nothing could be observed".
        File.Delete(file);

        await Wait.UntilAsync(
            () => logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("no file at its armed path")),
            "the file_tail watcher warned that its armed path has gone missing",
            timeout: TimeSpan.FromSeconds(20),
            observed: () => $"entries: [{string.Join(" | ", logger.Entries.Select(e => e.Level + ": " + e.Message))}]");

        fired.Task.IsCompleted.Should().BeFalse("nothing was appended, so nothing may fire");
    }

    #endregion
}
