extern alias Reviewd;

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmTestUtils.Logging;
using CodeReviewDaemon.Sample.Agents;
using Microsoft.Extensions.Logging;
using Reviewd::Reviewd.Sample;
using Xunit.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Seen live (2026-11-20): under lock contention on a 9 GB conversation store the review host took up to 143 s to
/// answer the sub-agent tree, past the S2S client's 120 s timeout. That timeout is a cancellation no wait asked for:
/// a slow host, not a failed review. These pin how the shared wait (<see cref="ReviewRunner.WaitAsync"/>) reads
/// again, how the judge and merge waits that share it behave, and that the batch never quarantines a PR for it.
/// A timeout is scripted as the exception HttpClient throws for its own timeout, so no HTTP timeout waits on a real
/// clock. Only the judge case waits on real time: its outage limit is 500 ms of UtcNow, under a 5 s guard.
/// </summary>
public sealed class ReviewdTreeTimeoutTests : LoggingTestBase
{
    public ReviewdTreeTimeoutTests(ITestOutputHelper output)
        : base(output) { }

    [Theory]
    [InlineData(2, true, 4, 3)]
    // Five timed-out reads in a row are retried; the next answered read keeps the wait.
    [InlineData(5, true, 7, 6)]
    // The sixth timed-out read in a row ends the wait.
    [InlineData(6, false, 6, 5)]
    public async Task A_tree_read_that_times_out_is_read_again_within_its_own_budget(
        int timeouts,
        bool succeeds,
        int expectedReads,
        int expectedDelays
    )
    {
        LogTestStart(new { timeouts, succeeds });
        var host = new TreeHost(Logger, read => read <= timeouts ? TreeRead.TimeOut : TreeRead.Answer);
        var delays = 0;

        var run = () =>
            Wait(
                host,
                null,
                _ =>
                {
                    delays++;
                    return Task.CompletedTask;
                },
                CancellationToken.None
            );

        if (succeeds)
        {
            await run();
        }
        else
        {
            await run.Should().ThrowAsync<TaskCanceledException>();
        }
        LogData("outcome", new { host.TreeReads, delays });
        host.TreeReads.Should().Be(expectedReads, "each timed-out read is read again, and idle needs two snapshots");
        delays.Should().Be(expectedDelays);
    }

    // The budget is for timeouts in a row: an answered read between two full runs of five starts it over.
    [Fact]
    public async Task An_answered_tree_read_resets_the_consecutive_timeout_budget()
    {
        LogTestStart(
            new
            {
                timedOutReads = "1-5 and 7-11",
                answeredReads = "6 and 12 on",
                budget = 5,
            }
        );
        var host = new TreeHost(
            Logger,
            read => read is (>= 1 and <= 5) or (>= 7 and <= 11) ? TreeRead.TimeOut : TreeRead.Answer
        );

        await Wait(host, null, _ => Task.CompletedTask, CancellationToken.None);

        LogData("outcome", new { host.TreeReads });
        host.TreeReads.Should().Be(13, "the timeouts drop the idle snapshot between them, so two more follow");
    }

    // A cancelled wait is not a slow host: it stops at once, with no further read or delay. Cancelled during the first
    // read, during the delay after a timeout, or during the read after a timeout, an answer (reset) and a timeout.
    [Theory]
    [InlineData("first-read", 1, 0)]
    [InlineData("retry-delay", 1, 1)]
    [InlineData("read-after-reset", 4, 3)]
    public async Task A_cancelled_wait_stops_without_another_read_or_delay(
        string cancelAt,
        int expectedReads,
        int expectedDelays
    )
    {
        LogTestStart(new { cancelAt });
        using var stop = new CancellationTokenSource();
        var delayTokens = new List<CancellationToken>();
        var host = new TreeHost(
            Logger,
            read =>
                cancelAt switch
                {
                    "first-read" => TreeRead.CancelWait,
                    "retry-delay" => TreeRead.TimeOut,
                    _ => read switch
                    {
                        1 or 3 => TreeRead.TimeOut,
                        2 => TreeRead.Answer,
                        _ => TreeRead.CancelWait,
                    },
                },
            stop
        );
        var delays = 0;

        var run = () =>
            Wait(
                host,
                null,
                token =>
                {
                    delays++;
                    delayTokens.Add(token);
                    if (cancelAt == "retry-delay")
                    {
                        stop.Cancel();
                    }
                    token.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
                stop.Token
            );

        await run.Should().ThrowAsync<OperationCanceledException>();
        LogData("outcome", new { host.TreeReads, delays });
        host.TreeReads.Should().Be(expectedReads);
        delays.Should().Be(expectedDelays);
        // Without the wait's token a cancel during the delay would surface only at the next HTTP call.
        delayTokens.Where(token => token != stop.Token).Should().BeEmpty("each delay waits on the wait's token");
    }

    // A timed-out tree read says nothing about whether the input runs, so the never-started grace restarts after it,
    // like a busy read. One clock minute per poll: NotStarted from t0, the tree read at t9 times out, and the answered
    // NotStarted read at t10 is a fresh stretch (counted as one, it would trip the 10-minute grace there). The input
    // completes on a later poll.
    [Fact]
    public async Task A_tree_timeout_between_two_idle_stretches_restarts_the_never_started_grace()
    {
        var start = DateTimeOffset.Parse("2026-11-20T05:00:00Z", CultureInfo.InvariantCulture);
        var now = start;
        LogTestStart(
            new
            {
                clockStart = start,
                minutesPerDelay = 1,
                notStartedPolls = "1-12",
                timedOutTreeRead = 10,
                completedFromPoll = 13,
                grace = ReviewRunner.NeverStartedGrace,
            }
        );
        var host = new TreeHost(Logger, read => read == 10 ? TreeRead.TimeOut : TreeRead.Answer)
        {
            InputStatus = polls => polls < 13 ? "NotStarted" : "Completed",
        };

        var status = await Wait(
            host,
            "input-1",
            _ =>
            {
                now = now.AddMinutes(1);
                return Task.CompletedTask;
            },
            CancellationToken.None,
            () => now
        );

        LogData("outcome", new { host.StatusReads, minutes = (now - start).TotalMinutes });
        status!.Status.Should().Be("Completed");
        host.StatusReads.Should().Be(14, "completion is confirmed by a second idle read");
    }

    // The judge waits through the shared wait with its own outage clock, which only a full poll restarts. A tree read
    // that keeps timing out is not an answer: the retries wait through the judge's plain delay, the clock runs on, and
    // the judge gives up like any outage instead of waiting forever.
    [Fact]
    public async Task A_judge_whose_tree_reads_keep_timing_out_gives_up_like_an_outage()
    {
        LogTestStart(
            new
            {
                input = "InProgress",
                rootBusy = true,
                treeReads = "all time out",
                outageLimit = "500 ms of real UtcNow",
                guard = "5 s",
            }
        );
        var host = new TreeHost(Logger, _ => TreeRead.TimeOut) { InputStatus = _ => "InProgress", RootBusy = true };
        using var http = host.Client();
        var client = new LmStreamingS2SClient(http, null, null, null);

        // The judge's own clock, read before the wait exists, so it can only start before the judge stamps its own.
        var started = DateTimeOffset.UtcNow;
        var wait = () =>
            JudgeRunner
                .WaitForInputAsync(
                    client,
                    "thread",
                    "input-1",
                    async _ => await Task.Yield(),
                    CancellationToken.None,
                    () => false,
                    new JudgeRunner.HostOutage(
                        TimeSpan.FromMilliseconds(1),
                        TimeSpan.FromMilliseconds(4),
                        TimeSpan.FromMilliseconds(500),
                        TimeSpan.FromSeconds(10)
                    ),
                    "judge #975",
                    CancellationToken.None
                )
                .WaitAsync(TimeSpan.FromSeconds(5));

        (await wait.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should()
            .Contain("has not answered")
            .And.Contain("Rerun to resume the wait; nothing is resent.");
        var elapsed = DateTimeOffset.UtcNow - started;
        LogData("outcome", new { host.TreeReads, elapsedMs = elapsed.TotalMilliseconds });
        host.TreeReads.Should()
            .BeGreaterThanOrEqualTo(
                6,
                "the shared wait spends its whole timeout budget before the outage loop sees one"
            );
        elapsed
            .Should()
            .BeGreaterThanOrEqualTo(
                TimeSpan.FromMilliseconds(500),
                "the judge gives up only once the host has been silent for its whole outage limit"
            );
    }

    [Fact]
    public async Task Batch_does_not_quarantine_a_pr_whose_review_host_read_timed_out()
    {
        // Seen live (2026-11-20): four PRs failed with "HttpClient.Timeout of 120 seconds elapsing" while the host's
        // conversation store was under lock contention. The signature hides the number, so every such timeout on one
        // head reads as the same failure. A slow host says nothing about the PR: it still gets the lane's retries,
        // but it is never quarantined for it. Each run claims the slot (a new attempt row), so it is not the
        // before-claim timeout that was already retried.
        using var batch = new ReviewdRunnerTests.BatchFixture(0);
        LogTestStart(
            new
            {
                pr = "1",
                slots = new[] { 0 },
                eachFreshRun = "claims, records a failed-released attempt, then throws the 120 s HttpClient timeout",
                clockStepSeconds = (int)ReviewBatch.QuarantineSpacing.TotalSeconds,
                quarantineAfter = ReviewBatch.QuarantineAfter,
            }
        );
        var claims = 0;
        batch.OnRunAsync = () =>
        {
            if (batch.Runs[^1] != "1:fresh")
            {
                return Task.CompletedTask;
            }
            batch.PutAttempt(
                "1",
                new JsonObject
                {
                    ["slot"] = 0,
                    ["stage"] = "failed-released",
                    ["cleanupState"] = "released",
                    ["claimToken"] = $"claim-{++claims}",
                }
            );
            batch.AdvanceSeconds((int)ReviewBatch.QuarantineSpacing.TotalSeconds);
            Logger.LogDebug("Fresh run {Claim} times out after its claim", claims);
            throw HostTimeout();
        };

        await batch.ExecuteAsync();

        LogData("outcome", new { runs = batch.Runs, entry = batch.Entries()[0].ToJsonString() });
        batch.Runs.Count(run => run == "1:fresh").Should().Be(5);
        batch.Entries()[0]["status"]!.GetValue<string>().Should().Be("held");
        batch.Read(ReviewBatch.FailureKey(ReviewdRunnerTests.BatchFixture.Repo, "1")).Should().BeNull();
    }

    private static TaskCanceledException HostTimeout() =>
        new(
            "The request was canceled due to the configured HttpClient.Timeout of 120 seconds elapsing.",
            new TimeoutException("A task was canceled.")
        );

    private static async Task<S2SStatusResult?> Wait(
        TreeHost host,
        string? input,
        Func<CancellationToken, Task> delay,
        CancellationToken ct,
        Func<DateTimeOffset>? clock = null
    )
    {
        using var http = host.Client();
        var client = new LmStreamingS2SClient(http, null, null, null);
        return await ReviewRunner.WaitAsync(client, "thread", input, delay, ct, clock);
    }

    internal enum TreeRead
    {
        Answer,
        TimeOut,
        CancelWait,
    }

    // A host whose sub-agent tree read number n (from 1) answers an empty tree, times out, or has the wait cancelled
    // while it is in flight. The root is idle unless RootBusy; an input's status comes from InputStatus by read count.
    private sealed class TreeHost(ILogger logger, Func<int, TreeRead> script, CancellationTokenSource? stop = null)
        : HttpMessageHandler
    {
        private int _treeReads;
        private int _statusReads;

        public int TreeReads => _treeReads;

        public int StatusReads => _statusReads;

        public Func<int, string> InputStatus { get; init; } = _ => "Completed";

        public bool RootBusy { get; init; }

        public HttpClient Client() => new(this, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/status", StringComparison.Ordinal))
            {
                var status = InputStatus(Interlocked.Increment(ref _statusReads));
                return Json($$$"""{"status":"{{{status}}}","runId":"run-1","response":{"text":"done"}}""");
            }
            if (path.EndsWith("/run-state", StringComparison.Ordinal))
            {
                return Json(
                    RootBusy
                        ? """{"threadId":"thread","isInProgress":true,"currentRunId":"run-1"}"""
                        : """{"threadId":"thread","isInProgress":false,"currentRunId":null}"""
                );
            }
            if (!path.EndsWith("/subagents", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Unexpected route " + path);
            }
            var read = Interlocked.Increment(ref _treeReads);
            var outcome = script(read);
            logger.LogDebug("Tree read {Read}: {Outcome}", read, outcome);
            if (outcome == TreeRead.TimeOut)
            {
                throw HostTimeout();
            }
            if (outcome == TreeRead.CancelWait)
            {
                await stop!.CancelAsync();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return Json("""{"schemaVersion":1,"nodes":[]}""");
        }

        private static HttpResponseMessage Json(string value) =>
            new(HttpStatusCode.OK) { Content = new StringContent(value) };
    }
}
