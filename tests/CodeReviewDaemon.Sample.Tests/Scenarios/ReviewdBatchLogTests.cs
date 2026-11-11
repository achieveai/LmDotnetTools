extern alias Reviewd;

using Reviewd::Reviewd.Sample;
using BatchFixture = CodeReviewDaemon.Sample.Tests.Scenarios.ReviewdRunnerTests.BatchFixture;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
///     What the review batch tells operators and keep-alive through its output, read from the console it writes to.
///     The scheduling itself runs on <see cref="BatchFixture" />, as in <see cref="ReviewdRunnerTests" />.
/// </summary>
[Collection(ConsoleCapturingCollection.Name)]
public sealed class ReviewdBatchLogTests : IDisposable
{
    private readonly TextWriter _stdout = Console.Out;
    private readonly TextWriter _stderr = Console.Error;
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();

    public ReviewdBatchLogTests()
    {
        Console.SetOut(TextWriter.Synchronized(_out));
        Console.SetError(TextWriter.Synchronized(_err));
    }

    public void Dispose()
    {
        Console.SetOut(_stdout);
        Console.SetError(_stderr);
        _out.Dispose();
        _err.Dispose();
    }

    private static int Count(string text, string line) =>
        text.Split('\n').Count(value => value.StartsWith(line, StringComparison.Ordinal));

    // The batch keeps running between selections, so it says when a selection is done: once nothing runs and nothing
    // more can start, it stamps the queue idle on that selection and logs the finished line keep-alive waits for.
    // Starting any review clears the stamp.
    [Fact]
    public async Task An_idle_queue_is_stamped_and_logged_once_per_selection_and_cleared_by_a_launch()
    {
        using var batch = new BatchFixture(0);
        batch.WatchDelay = batch.LiveTick;
        batch.PutSelection(1);
        var first = batch.SelectionDigest;
        string? idleAtSecondLaunch = "unread";
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.OnRunPrAsync = pr =>
        {
            if (pr == "2")
            {
                idleAtSecondLaunch = batch.Read(batch.StateKey)!["idle"]?.GetValue<string>();
                secondStarted.TrySetResult();
            }
            return Task.CompletedTask;
        };

        var schedule = batch.RunLiveAsync();
        await batch.UntilAsync(() => batch.Read(batch.StateKey)?["idle"] is not null);
        await batch.AfterTicks(8);
        batch.Read(batch.StateKey)!["idle"]!.GetValue<string>().Should().Be(first);
        Count(_out.ToString(), "Batch scheduling finished:").Should().Be(1, "idle ticks after the first stay quiet");
        batch.PutSelection(2);
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await batch.UntilAsync(() => Count(_out.ToString(), "Batch scheduling finished:") == 2);
        await batch.AfterTicks(8);
        batch.Cancellation.Cancel();
        await schedule;

        idleAtSecondLaunch.Should().BeNull("a launch clears the stamp in its own transaction");
        batch.Read(batch.StateKey)!["idle"]!.GetValue<string>().Should().Be(batch.SelectionDigest);
        Count(_out.ToString(), "Batch scheduling finished:").Should().Be(2);
        batch.Runs.Should().Equal("1:fresh", "2:fresh");
    }

    // A queue save that fails leaves the queue unlike what runs, so admission stops for the whole workspace and says
    // so once. The review already running finishes; nothing new starts.
    [Fact]
    public async Task A_failed_queue_save_suspends_all_admission_and_says_so()
    {
        using var batch = new BatchFixture(0);
        batch.WatchDelay = batch.LiveTick;
        batch.PutSelection(2);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.OnRunPrAsync = async pr =>
        {
            if (pr != "1")
                return;
            firstStarted.TrySetResult();
            await release.Task;
        };

        var schedule = batch.RunLiveAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        batch.FailQueueWrites(true);
        release.SetResult();
        await batch.UntilAsync(() => _err.ToString().Contains("Scheduling suspended", StringComparison.Ordinal));
        await batch.AfterTicks(8);
        batch.FailQueueWrites(false);
        await batch.AfterTicks(4);
        batch.Cancellation.Cancel();
        await schedule;

        var errors = _err.ToString();
        Count(errors, $"Scheduling suspended: queue {batch.StateKey} not saved: ").Should().Be(1);
        errors.Should().Contain("Admission is suspended; nothing new starts.");
        errors.Should().NotContain("Other safe slots continue");
        batch.Runs.Should().Equal("1:fresh");
    }

    // An admission pass that fails (here, an unreadable attempt row) is logged once, retried every tick, and once
    // the row reads again the queued PR starts exactly once.
    [Fact]
    public async Task A_failed_admission_pass_is_logged_once_and_recovers_on_the_same_selection()
    {
        using var batch = new BatchFixture(0);
        batch.WatchDelay = batch.LiveTick;
        batch.PutSelection(2);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        batch.OnRunPrAsync = async pr =>
        {
            if (pr == "2")
            {
                secondStarted.TrySetResult();
                return;
            }
            firstStarted.TrySetResult();
            await release.Task;
        };
        var attemptKey = "reviewd/review-attempt/" + PrCommands.ParseRepo(BatchFixture.Repo).NormalizedKey + "/2";

        var schedule = batch.RunLiveAsync();
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        batch.PutRaw(attemptKey, "{not json");
        await batch.AfterTicks(8);
        batch.Delete(attemptKey);
        release.SetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await batch.AfterTicks(4);
        batch.Cancellation.Cancel();
        await schedule;

        Count(_err.ToString(), "Admission skipped; retrying every 5 seconds: ").Should().Be(1);
        batch.Runs.Should().Equal("1:fresh", "2:fresh");
    }

    // A lock file this user cannot open yet is retried like a lease another batch holds: once per distinct error,
    // and scheduling starts only once the lease is held. Cancellation still ends the wait.
    [Fact]
    public async Task The_batch_lease_is_retried_through_permission_and_sharing_errors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"reviewd-lease-{Guid.NewGuid():N}.lock");
        var attempts = 0;
        var delays = 0;

        using (
            var lease = await ReviewBatch.AcquireBatchLeaseAsync(
                () =>
                    attempts++ switch
                    {
                        < 2 => throw new UnauthorizedAccessException("Access to the lease is denied."),
                        2 => throw new IOException("The lease is held by another batch."),
                        _ => new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None),
                    },
                (_, _) =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                CancellationToken.None
            )
        )
            lease.CanWrite.Should().BeTrue();

        attempts.Should().Be(4);
        delays.Should().Be(3);
        Count(_err.ToString(), "Batch lease unavailable; retrying every 5 seconds: Access").Should().Be(1);
        Count(_err.ToString(), "Batch lease unavailable; retrying every 5 seconds: The lease").Should().Be(1);
        File.Delete(path);

        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        var wait = () =>
            ReviewBatch.AcquireBatchLeaseAsync(
                () => throw new UnauthorizedAccessException("denied"),
                (delay, token) => Task.Delay(delay, token),
                canceled.Token
            );
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }
}
