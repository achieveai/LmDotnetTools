using System.Net;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Pins the route back for a run the poll can no longer reach. The poll enumerates OPEN PRs inside a recency
/// window and nothing else in the daemon reads <c>review_run</c> again, so a run left non-terminal when its PR
/// merges, closes, or goes quiet is orphaned permanently — the retry that would have healed it never arrives.
/// Two properties carry the weight: every stranded run must reach a terminal status, and a run whose head has
/// already been re-reviewed must never be resumed (on a posting daemon that would publish a stale diff).
/// </summary>
public sealed class StrandedRunReconcilerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Grace = TimeSpan.FromHours(6);

    /// <summary>The retry-pending fast window (#429) — deliberately far shorter than <see cref="Grace"/>.</summary>
    private static readonly TimeSpan RetryGrace = TimeSpan.FromMinutes(45);

    // ── the defect: a stranded run is never retried ───────────────────────────────────────────────

    [Fact]
    public async Task A_stranded_run_whose_pr_is_still_open_is_handed_back_to_the_orchestrator()
    {
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Judged));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Should()
            .ContainSingle(
                "the poll can no longer reach this run, so the reconciler is the only thing that can retry it"
            )
            .Which.Id.Should()
            .Be(11);
        harness.Retired.Should().BeEmpty("an open PR's run is resumed, not written off");
    }

    [Fact]
    public async Task A_resumed_run_carries_the_freshly_observed_lifecycle_not_the_stale_persisted_one()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11, lifecycle: PrLifecycleState.Closed))
            .WithLifecycle(PrLifecycle.Open);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Should()
            .ContainSingle()
            .Which.PrLifecycleState.Should()
            .Be(
                PrLifecycleState.Open,
                "the orchestrator halts any run it is handed with a non-open lifecycle, so a stale persisted "
                    + "state would silently turn every resume into a no-op"
            );
    }

    [Fact]
    public async Task A_stranded_run_whose_pr_has_merged_is_retired_without_being_resumed() =>
        await AssertClosedPrIsRetired(PrLifecycle.Merged, PrLifecycleState.Merged);

    [Fact]
    public async Task A_stranded_run_whose_pr_was_abandoned_is_retired_without_being_resumed() =>
        await AssertClosedPrIsRetired(PrLifecycle.Abandoned, PrLifecycleState.Abandoned);

    // Takes the internal enums, so it cannot be a public [Theory] — the two facts above supply the cases.
    private static async Task AssertClosedPrIsRetired(PrLifecycle lifecycle, PrLifecycleState expected)
    {
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Reviewed)).WithLifecycle(lifecycle);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty("there is nothing left to review on a PR that has closed");
        harness
            .Retired.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                (11L, ReviewStage.Reviewed, WorkflowStatus.Completed, expected),
                "this is the same rule PrOrchestrator applies to a PR it observes as no longer open: stop working "
                    + "the run, at the stage it reached, without marking it failed"
            );
    }

    // ── the safety rail: a superseded run must never be resumed ───────────────────────────────────

    [Fact]
    public async Task A_superseded_run_is_retired_without_even_asking_the_provider()
    {
        var harness = new Harness().WithRows(Row(id: 11, superseded: true));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Should()
            .BeEmpty(
                "a later run has already reviewed a newer head; resuming this one would review — and on a posting "
                    + "daemon publish — a diff that no longer stands"
            );
        harness.LifecycleLookups.Should().Be(0, "supersession is decided from the store alone");
        harness.Retired.Should().ContainSingle().Which.Item3.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task A_run_whose_stages_are_all_done_is_retired_rather_than_resumed()
    {
        // A crash between the last stage's write and its terminal status leaves a row at the final stage with a
        // non-terminal status — stranded by the letter of the sweep, but with nothing left to do:
        // StageMachine.RemainingStages of a complete stage is empty, so the orchestrator would execute no stage
        // and return. Resuming it therefore burned a resume slot every pass to accomplish nothing, and the pass
        // whose job is to drain stranded runs could never drain this one. It is a pure function of the row, so
        // it is answered before the provider is asked at all.
        var harness = new Harness().WithRows(Row(id: 11, stage: StageMachine.Terminal));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty("there is no remaining stage to run");
        harness.LifecycleLookups.Should().Be(0, "a row with no work left needs no provider call to settle");
        harness.Retired.Should().ContainSingle().Which.Item3.Should().Be(WorkflowStatus.Completed);
    }

    [Fact]
    public async Task A_run_still_short_of_the_final_stage_is_resumed_rather_than_retired()
    {
        // The over-refusal pin for the retirement above: it must key on the run being COMPLETE, not merely on
        // being far along. Retiring at the second-to-last stage would silently write off reviews that still owe
        // their final stage — the exact permanent-abandonment this whole sweep exists to prevent.
        var lastIncomplete = StageMachine.Order[^2];
        var harness = new Harness().WithRows(Row(id: 11, stage: lastIncomplete));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().ContainSingle().Which.Id.Should().Be(11);
        harness.Retired.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_the_orchestrator_resolved_to_a_different_row_is_retired_so_it_cannot_be_re_picked()
    {
        var harness = new Harness().WithRows(Row(id: 11)).WithResumeResolvingTo(runId: 48);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Retired.Should()
            .ContainSingle(
                "the orchestrator resolves a run by identity tuple, so it can settle a further-progressed sibling "
                    + "at the same head instead — leaving this row stranded and re-picked on every later pass"
            )
            .Which.Item1.Should()
            .Be(11L);
    }

    // ── the cap: a weeks-old backlog must not release all at once ─────────────────────────────────

    [Fact]
    public async Task The_resume_cap_bounds_one_pass_and_the_rest_are_deferred_not_dropped()
    {
        var harness = new Harness().WithRows(Row(11), Row(12), Row(13), Row(14)).WithMaxResumes(2);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal([11L, 12L], "the cap is two per pass, oldest first");
        harness.Retired.Should().BeEmpty("a deferred run is still open work — it must not be written off");
        harness
            .Log.Should()
            .Contain(
                e =>
                    e.Contains("deferred", StringComparison.OrdinalIgnoreCase)
                    && e.Contains("13", StringComparison.Ordinal),
                "a cap that silently shortens the pass reads as 'nothing left to do'"
            );
    }

    [Fact]
    public async Task Retiring_a_closed_pr_never_consumes_a_resume_slot()
    {
        var harness = new Harness()
            .WithRows(Row(11, superseded: true), Row(12, superseded: true), Row(13))
            .WithMaxResumes(1);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal([13L], "bookkeeping costs nothing, so it must not crowd out the one run that needed real work");
    }

    [Fact]
    public async Task A_resume_that_throws_still_spends_the_slot_it_used()
    {
        // A slot is not a unit of success — it is a unit of work: a lease, a clone, and a review's remaining
        // stages, which is precisely the cost the cap exists to bound. Charging it only when the resume comes
        // back cleanly makes a failing run free, so a backlog of runs that all fail is not bounded at all and
        // every one of them gets a full attempt on every pass. That is the shape the cap was written against,
        // and it is the one the reconciler hits, because a run reaches this listing by having gone wrong once
        // already. Nothing else brakes this path either: PrOrchestrator.ReconcileAsync resets the retry
        // governor for the run it is handed, so the cap is the only limit on how much a single pass can spend.
        var harness = new Harness().WithRows(Row(11), Row(12)).WithMaxResumes(1).WithResumeThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Order.Should()
            .Equal(
                ["write:11", "resume:11"],
                "run 11 spent the pass's one slot the moment it was claimed, so run 12 must not be touched at all"
            );
        harness
            .Log.Should()
            .Contain(
                e =>
                    e.Contains("deferred", StringComparison.OrdinalIgnoreCase)
                    && e.Contains("12", StringComparison.Ordinal),
                "run 12 is deferred rather than dropped — the next pass is where it gets its turn"
            );
    }

    // ── isolation: one bad run never aborts the pass ──────────────────────────────────────────────

    [Fact]
    public async Task A_run_whose_pr_the_provider_cannot_find_is_retired_rather_than_stranded_again()
    {
        var harness = new Harness()
            .WithRows(Row(id: 141))
            .WithLifecycleThrowingFor(141, new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Retired.Should()
            .ContainSingle(
                "the daemon's own store holds a run seeded against a number that is not a PR; without this the "
                    + "lookup throws on every pass and the run stays stranded, one level further out"
            )
            .Which.Should()
            .Be((141L, ReviewStage.Discovered, WorkflowStatus.Completed, PrLifecycleState.Abandoned));
        harness.Log.Should().NotContain(e => e.Contains("failed to settle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_that_is_merely_unreachable_does_not_retire_the_run()
    {
        var harness = new Harness()
            .WithRows(Row(id: 11))
            .WithLifecycleThrowingFor(
                11,
                new HttpRequestException("Bad gateway", null, HttpStatusCode.ServiceUnavailable)
            );

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Retired.Should()
            .BeEmpty(
                "a 5xx, a 401 or a timeout says nothing about the PR's state — writing the run off on one would "
                    + "discard live work over a blip"
            );
        harness
            .StateWrites.Should()
            .ContainSingle(
                "the failure still has to be written down somewhere: `updated_at` is the only thing short of a "
                    + "terminal status that takes a row out of the stranded listing, so a run settled by writing "
                    + "nothing at all stays eligible and is re-read and re-failed on every single pass"
            )
            .Which.Should()
            .Be(
                (11L, ReviewStage.Discovered, WorkflowStatus.RetryPending, PrLifecycleState.Open),
                "the backoff re-writes the state the row already had — it buys a grace period, it decides "
                    + "nothing about the run"
            );
        harness
            .Log.Should()
            .Contain(e => e.Contains("could not reach the github provider for run 11", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_whose_provider_lookup_throws_is_backed_off_and_the_pass_continues()
    {
        var harness = new Harness().WithRows(Row(11), Row(12)).WithLifecycleThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal([12L], "one unreachable provider must not strand the rest of the backlog all over again");
        harness
            .Log.Should()
            .Contain(e => e.Contains("could not reach the github provider for run 11", StringComparison.Ordinal));
        harness
            .Log.Should()
            .NotContain(
                e => e.Contains("failed to settle run 11", StringComparison.Ordinal),
                "the lookup failure is settled where it happens; reaching the pass-level catch would mean nothing "
                    + "was written for the run, which is the state that makes it eligible again immediately"
            );
    }

    [Fact]
    public async Task A_backed_off_run_is_not_counted_against_the_pass_as_a_cap_deferral()
    {
        // The cap notice explains deferrals by one cause — the resume cap — and an operator sizes the cap from
        // its number. A backed-off run never reached the cap check and cost no slot, so folding it in would
        // report the wrong cause and argue for raising a cap that was never the constraint.
        var harness = new Harness().WithRows(Row(11), Row(12)).WithLifecycleThrowingFor(runId: 11).WithMaxResumes(1);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal(
                [12L],
                "the unreachable run spent no slot, so the one slot this pass had was still there for run 12"
            );
        harness
            .Log.Should()
            .NotContain(
                e => e.Contains("deferred", StringComparison.OrdinalIgnoreCase),
                "nothing was held back by the cap on this pass"
            );
    }

    [Fact]
    public async Task A_lookup_that_times_out_is_backed_off_rather_than_aborting_the_pass()
    {
        // An HttpClient per-request timeout surfaces as TaskCanceledException, which IS an
        // OperationCanceledException. A filter written on the type alone therefore excludes exactly the timeout
        // — the single most likely way an unreachable provider actually fails — and lets one slow call abort the
        // pass and re-strand every run queued behind it.
        var harness = new Harness()
            .WithRows(Row(11), Row(12))
            .WithLifecycleThrowingFor(
                11,
                new TaskCanceledException(
                    "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."
                )
            );

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal([12L], "the pass must survive one slow provider call");
        harness
            .StateWrites.Should()
            .Contain(
                w => w.Item1 == 11L,
                "a timed-out lookup is backed off exactly like any other unreachable provider"
            );
    }

    [Fact]
    public async Task A_resume_that_times_out_does_not_abort_the_pass()
    {
        var harness = new Harness()
            .WithRows(Row(11), Row(12))
            .WithResumeThrowingFor(
                11,
                new TaskCanceledException("HttpClient.Timeout elapsed while the review was posting")
            );

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal(
                [12L],
                "a resume runs the review's whole remaining pipeline, so a timeout inside it is ordinary — and it "
                    + "arrives as a TaskCanceledException, which the pass must not mistake for its own shutdown"
            );
    }

    [Fact]
    public async Task A_cancelled_sweep_stops_instead_of_backing_the_run_off()
    {
        // The other side of admitting TaskCanceledException: a real shutdown must still get out. The two are
        // told apart by the TOKEN, not by the exception, and nothing about a shutdown should be written to the
        // store on the way past — a stamp there would push a run that was never even looked at out of the
        // listing for a full grace period.
        using var cts = new CancellationTokenSource();
        var harness = new Harness()
            .WithRows(Row(11), Row(12))
            .WithLifecycleThrowingFor(
                11,
                new OperationCanceledException("the daemon is shutting down"),
                before: cts.Cancel
            );

        var sweep = async () => await harness.Reconciler().SweepAsync(cts.Token);

        await sweep
            .Should()
            .ThrowAsync<OperationCanceledException>(
                "a shutdown is not a provider problem and is not this class's to swallow"
            );
        harness.Resumed.Should().BeEmpty("the pass stops where it was cancelled");
        harness.StateWrites.Should().BeEmpty("a shutdown must never be recorded as a run's backoff");
    }

    [Fact]
    public async Task A_run_whose_resume_throws_is_logged_and_the_next_run_is_still_settled()
    {
        var harness = new Harness().WithRows(Row(11), Row(12)).WithResumeThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal(
                [12L],
                "a resume runs the review's remaining stages, so it fails for far more reasons than the "
                    + "lifecycle lookup does — one failing review must not re-strand the rest of the backlog"
            );
        harness.Log.Should().Contain(e => e.Contains("failed to settle run 11", StringComparison.Ordinal));
        harness
            .Retired.Should()
            .BeEmpty(
                "the run is still open work: leaving it non-terminal is what lets it come back once it has been "
                    + "untouched for the grace period again"
            );
    }

    [Fact]
    public async Task An_open_run_is_claimed_before_the_resume_rather_than_after_it()
    {
        // `updated_at` is the ONLY thing that takes a row out of the stranded listing short of a terminal
        // status, and the resume is not guaranteed to write it: the orchestrator returns early for a run with no
        // stages left, and a resume that throws before reaching a stage leaves the row exactly as it found it.
        // Without a write of its own the reconciler re-lists the same row on the very next pass, re-logs
        // "resuming", and re-charges it against the cap — forever, crowding out the backlog the pass exists to
        // drain. Ordering matters as much as the write: a stamp taken afterwards would leave the takeover
        // invisible for the whole review.
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Judged));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Order.Should()
            .Equal(
                ["write:11", "resume:11"],
                "the claim is what makes the takeover survive a resume that does nothing"
            );
        harness
            .StateWrites.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                (11L, ReviewStage.Judged, WorkflowStatus.RetryPending, PrLifecycleState.Open),
                "the claim re-writes the state the row already had — it advances the timestamp, it does not decide "
                    + "anything about the run"
            );
        harness.Retired.Should().BeEmpty("an open PR's run is claimed, not written off");
    }

    [Fact]
    public async Task A_run_whose_resume_throws_is_still_left_claimed()
    {
        var harness = new Harness().WithRows(Row(id: 11, stage: ReviewStage.Judged)).WithResumeThrowingFor(runId: 11);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .StateWrites.Should()
            .ContainSingle(
                "a failing resume is the case that most needs the claim: it writes nothing itself, so this row would "
                    + "otherwise be re-picked and re-failed on every pass with nothing in the store to show for it"
            )
            .Which.Item3.Should()
            .Be(
                WorkflowStatus.RetryPending,
                "the run is still open work — the claim holds it for a grace period, it does not retire it"
            );
    }

    [Fact]
    public async Task An_empty_backlog_is_silent()
    {
        var harness = new Harness();

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Log.Should().BeEmpty("the steady state is no stranded runs, on every poll cycle, forever");
    }

    // ── the retry-pending fast path (#429) ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_retry_pending_run_is_resumed_off_the_fast_listing_alone()
    {
        // The abandonment listing is EMPTY here on purpose. Before #429 that was the only listing, so a
        // RetryPending run on a PR outside the poll's recency window — the case this whole class exists for —
        // sat until it aged past the six-hour abandonment window, honouring a retry decision the orchestrator
        // had already made, six hours late.
        var harness = new Harness().WithRetryPendingRows(Row(id: 11, stage: ReviewStage.Judged));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal([11L]);
    }

    [Fact]
    public async Task A_superseded_run_on_the_fast_listing_is_retired_not_resumed()
    {
        // The fast listing shares the store's `superseded` subquery rather than restating it, and this is what
        // that sharing buys: reacting sooner must not mean publishing a review of a commit pair that a later
        // run has already replaced — it would only make the stale comment arrive faster.
        var harness = new Harness().WithRetryPendingRows(Row(id: 11, superseded: true));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty();
        harness.Retired.Should().ContainSingle().Which.Item1.Should().Be(11L);
    }

    [Fact]
    public async Task The_resume_cap_bounds_both_listings_together_not_each_one_separately()
    {
        // The design constraint #429 states outright. StrandedRunMaxResumesPerSweep exists to stop a backlog
        // becoming a burst of concurrent reviews — and, on a posting daemon, a burst of comments. A second
        // listing that carried a budget of its own would silently double that burst the day it was added,
        // while the configured number stayed the same and told an operator otherwise.
        var harness = new Harness().WithRetryPendingRows(Row(11), Row(12)).WithRows(Row(13), Row(14)).WithMaxResumes(2);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal([11L, 12L], "two slots, spent on the runs that are owed a retry — not two slots per listing");
        harness.Retired.Should().BeEmpty("a deferred run is still open work");
    }

    [Fact]
    public async Task The_fast_listing_is_settled_before_the_abandonment_listing()
    {
        // Ordering is the whole point of the path: when the pass cannot resume everything, the slots must go to
        // the runs the orchestrator explicitly asked to retry, not to the ones that merely aged out.
        var harness = new Harness().WithRows(Row(13)).WithRetryPendingRows(Row(11)).WithMaxResumes(1);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Select(r => r.Id).Should().Equal([11L]);
    }

    [Fact]
    public async Task A_run_that_appears_on_both_listings_is_settled_exactly_once()
    {
        // Not an edge case: a RetryPending run that keeps failing eventually satisfies BOTH predicates, and
        // that is the steady state of the runs this pass sees. Settled twice it would be handed to the
        // orchestrator twice concurrently and charge two of the pass's slots for one run.
        var harness = new Harness().WithRetryPendingRows(Row(11)).WithRows(Row(11), Row(12)).WithMaxResumes(2);

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness
            .Resumed.Select(r => r.Id)
            .Should()
            .Equal(
                [11L, 12L],
                "run 11 is one run however many listings name it, so the second slot was still there for run 12"
            );
        harness.LifecycleLookups.Should().Be(2, "settling run 11 twice would ask the provider about it twice");
    }

    [Fact]
    public async Task With_the_fast_path_off_a_retry_pending_run_waits_the_abandonment_window()
    {
        // The zeroed-knob shape. The fast listing is never read, so a RetryPending run reaches the pass only by
        // the abandonment listing — exactly the behaviour before #429, which is what "off" has to mean.
        var harness = new Harness().WithoutFastPath().WithRetryPendingRows(Row(11));

        await harness.Reconciler().SweepAsync(CancellationToken.None);

        harness.Resumed.Should().BeEmpty();
        harness.Log.Should().BeEmpty("nothing reached this pass, so it has nothing to report");
    }

    [Fact]
    public void A_fast_window_at_or_beyond_the_abandonment_window_is_refused_at_construction()
    {
        // A "fast" path slower than the slow one is a misconfiguration that reads, in appsettings, as the
        // feature working. Refusing at construction turns it into a boot failure an operator can see rather
        // than a knob that quietly does the opposite of its name.
        var construct = () =>
            new StrandedRunReconciler(
                listStrandedRuns: (_, _) => [],
                getPrLifecycleAsync: (_, _) => Task.FromResult(PrLifecycle.Open),
                resumeAsync: (run, _) => Task.FromResult(run),
                updateRunState: (_, _, _, _) => { },
                timeProvider: new FakeTimeProvider(Now),
                grace: Grace,
                scanLimit: 50,
                maxResumesPerPass: 2,
                logger: new CapturingLogger<StrandedRunReconciler>([]),
                listRetryPendingRuns: (_, _) => [],
                retryPendingGrace: Grace
            );

        construct.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── resolving the configured fast window (#439) ───────────────────────────────────────────────
    //
    // The composition root used to restate the constructor's rule inline, so the two could disagree and only
    // find out at host start. These pin the resolution the host actually calls, and the last one pins that what
    // it returns is something the constructor above accepts — the join the drift would break.

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_zero_or_negative_retry_window_resolves_to_the_fast_path_being_off(double minutes)
    {
        // "Off" has to be representable, because it is the documented meaning of 0 and the value that leaves
        // RetryPending draining on the abandonment window exactly as it did before #429.
        //
        // THE NEGATIVE ROW IS THE ONE THAT PROVES ANYTHING — keep it. Zero is refused twice over: the guard
        // returns early, and without the guard TimeSpan.FromMinutes(0) is Zero and Math.Min(0, positive) is
        // still 0. So a zero-only theory goes GREEN against "<= 0" rewritten as "< 0", and green against the
        // guard being deleted outright. Verified by mutation, not assumed. A negative minutes value is what
        // distinguishes them: deleting the guard resolves -1 to a NEGATIVE window, which the reconciler's
        // constructor then rejects, turning a knob an operator merely typed a sign into at a boot failure.
        // Same shape as #431's NaN rows, in the same codebase, for the same reason.
        StrandedRunReconciler.ResolveRetryPendingGrace(minutes, Grace).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void A_retry_window_inside_the_abandonment_window_is_taken_exactly_as_configured()
    {
        StrandedRunReconciler
            .ResolveRetryPendingGrace(45, Grace)
            .Should()
            .Be(
                TimeSpan.FromMinutes(45),
                "the ordinary case must pass through untouched, or the clamp is silently rewriting good config"
            );
    }

    [Theory]
    [InlineData(360)] // exactly the 6-hour abandonment window
    [InlineData(600)] // beyond it
    public void A_retry_window_at_or_beyond_the_abandonment_window_is_pulled_just_inside_it(double minutes)
    {
        // Strictly inside, not merely "not greater": equal is the case the constructor refuses, and a clamp
        // that lands ON the boundary would turn a tunable knob into the boot failure it exists to avoid.
        var resolved = StrandedRunReconciler.ResolveRetryPendingGrace(minutes, Grace);

        resolved.Should().Be(Grace - TimeSpan.FromTicks(1));
        resolved.Should().BeLessThan(Grace);
    }

    [Fact]
    public void A_clamped_retry_window_is_one_the_reconciler_will_actually_accept()
    {
        // The join. The clamp is only worth anything if its output satisfies the rule it was clamping toward,
        // so this drives the resolved value straight into the constructor that refuses a slow "fast" path.
        var construct = () =>
            new StrandedRunReconciler(
                listStrandedRuns: (_, _) => [],
                getPrLifecycleAsync: (_, _) => Task.FromResult(PrLifecycle.Open),
                resumeAsync: (run, _) => Task.FromResult(run),
                updateRunState: (_, _, _, _) => { },
                timeProvider: new FakeTimeProvider(Now),
                grace: Grace,
                scanLimit: 50,
                maxResumesPerPass: 2,
                logger: new CapturingLogger<StrandedRunReconciler>([]),
                listRetryPendingRuns: (_, _) => [],
                retryPendingGrace: StrandedRunReconciler.ResolveRetryPendingGrace(600, Grace)
            );

        construct
            .Should()
            .NotThrow("a window the host clamped must be one this constructor takes, or the clamp bought nothing");
    }

    [Fact]
    public void A_retry_window_too_large_to_be_a_timespan_is_refused_rather_than_clamped()
    {
        // The honest boundary. The clamp handles an in-range overshoot; it is NOT general typo-safety, because
        // an unrepresentable value throws out of TimeSpan.FromMinutes before any comparison sees it. Pinning
        // that here keeps the doc-comment from drifting back into claiming a safety the code does not provide.
        var resolve = () => StrandedRunReconciler.ResolveRetryPendingGrace(double.MaxValue, Grace);

        resolve.Should().Throw<OverflowException>();
    }

    // ── the store query ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_store_lists_only_non_terminal_runs_older_than_the_grace_period()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stale = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        var fresh = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        var finished = store.CreateOrGetReviewRun(SampleRun(repoId, "103"));
        store.UpdateReviewRunState(finished.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Open);
        Backdate(db, stale.Id, Now - TimeSpan.FromDays(9));
        Backdate(db, finished.Id, Now - TimeSpan.FromDays(9));

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 50);

        stranded
            .Select(s => s.Run.Id)
            .Should()
            .Equal(
                [stale.Id],
                "a completed run needs no route back, and a run inside the grace period is still the poll's to "
                    + "work — a healthy run stamps updated_at at every stage boundary (run {0} is fresh)",
                fresh.Id
            );
    }

    [Fact]
    public void The_abandonment_listing_excludes_a_permanently_parked_run()
    {
        // The listing is a parked run's LAST remaining route back into the daemon, so this conjunct is what
        // makes the park real rather than advisory. Note what does NOT do the work here: this listing selects
        // `workflow_status <> 'Completed'`, and parking writes `Failed`, which still satisfies that — so
        // without `parked_at IS NULL` the row comes straight back on the next pass and is resumed through
        // ReconcileAsync, which resets the in-memory governor. That is the exact loop the fix ends.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var parked = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        var live = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        store.TryMarkReviewRunParked(parked.Id, Now, "Reviewed: the sub-agent barrier never settled").Should().BeTrue();
        foreach (var id in new[] { parked.Id, live.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store
            .ListStrandedRuns(Now - Grace, limit: 50)
            .Select(s => s.Run.Id)
            .Should()
            .Equal(
                [live.Id],
                "run {0} spent its durable budget and must never be handed back to the orchestrator again",
                parked.Id
            );
    }

    [Fact]
    public void The_fast_listing_excludes_a_run_whose_row_carries_a_park_instant()
    {
        // Said plainly, because it is the difference between a real pin and a vacuous one: through the store's
        // own API a parked row is ALSO `Failed`, and `Failed` already fails this listing's
        // `workflow_status = 'RetryPending'` test — so a park written the ordinary way would be excluded here
        // whether or not the conjunct exists, and asserting on one would prove nothing about the conjunct.
        // The park instant is therefore written directly, leaving the status where the orchestrator's stage
        // catch puts it, so the ONLY thing that can exclude this row is `parked_at IS NULL`. The two listings
        // share one query body precisely so neither can drift out from under a park.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var parked = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        var live = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        StampParkedAt(db, parked.Id, Now);
        foreach (var id in new[] { parked.Id, live.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromHours(1));
        }

        var fast = store.ListRetryPendingRuns(Now - RetryGrace, limit: 50);

        fast.Select(s => s.Run.Id)
            .Should()
            .Equal(
                [live.Id],
                "run {0} is parked; the fast path exists to react SOONER, which for a parked run means "
                    + "re-reviewing it sooner",
                parked.Id
            );
    }

    [Fact]
    public void The_store_flags_a_run_that_a_later_run_for_the_same_pr_has_superseded()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var older = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var newer = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-2" });
        var only = store.CreateOrGetReviewRun(SampleRun(repoId, "102"));
        foreach (var id in new[] { older.Id, newer.Id, only.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 50);

        stranded.Should().HaveCount(3);
        stranded
            .Single(s => s.Run.Id == older.Id)
            .Superseded.Should()
            .BeTrue("run {0} reviewed a later head of the same PR", newer.Id);
        stranded.Single(s => s.Run.Id == newer.Id).Superseded.Should().BeFalse();
        stranded
            .Single(s => s.Run.Id == only.Id)
            .Superseded.Should()
            .BeFalse("supersession is per PR — another PR's runs say nothing about this one");
    }

    [Fact]
    public void The_fast_listing_selects_retry_pending_alone_on_a_window_of_its_own()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var retrying = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        var running = store.CreateOrGetReviewRun(
            SampleRun(repoId, "102") with
            {
                WorkflowStatus = WorkflowStatus.Running,
            }
        );
        var finished = store.CreateOrGetReviewRun(SampleRun(repoId, "103"));
        store.UpdateReviewRunState(finished.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Open);
        var justFailed = store.CreateOrGetReviewRun(SampleRun(repoId, "104"));
        foreach (var id in new[] { retrying.Id, running.Id, finished.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromHours(1));
        }

        Backdate(db, justFailed.Id, Now - TimeSpan.FromMinutes(1));

        var fast = store.ListRetryPendingRuns(Now - RetryGrace, limit: 50);

        fast.Select(s => s.Run.Id)
            .Should()
            .Equal(
                [retrying.Id],
                "RetryPending is the one status written as a DECISION to retry. Run {0} is Running and run {1} is "
                    + "Completed — for those, age is the only evidence there is, which is the abandonment window's "
                    + "question, not this one's. Run {2} failed a minute ago and has not yet waited its window.",
                running.Id,
                finished.Id,
                justFailed.Id
            );
    }

    [Fact]
    public void The_fast_listing_flags_supersession_exactly_as_the_abandonment_listing_does()
    {
        // The two listings share one query body precisely so this cannot drift. Resuming a superseded run
        // publishes a review of a commit pair a later run already replaced; a fast path that lost the flag
        // would deliver that stale comment SOONER, which is strictly worse than the delay it removes.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var older = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var newer = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-2" });
        foreach (var id in new[] { older.Id, newer.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromHours(1));
        }

        var fast = store.ListRetryPendingRuns(Now - RetryGrace, limit: 50);

        fast.Should().HaveCount(2);
        fast.Single(s => s.Run.Id == older.Id)
            .Superseded.Should()
            .BeTrue("run {0} reviewed a later head of the same PR", newer.Id);
        fast.Single(s => s.Run.Id == newer.Id).Superseded.Should().BeFalse();
    }

    [Fact]
    public async Task A_retry_pending_run_waits_the_fast_window_and_not_the_abandonment_one()
    {
        // The headline of #429, asserted against the REAL store because the claim is about the `updated_at`
        // predicate the fake listing never evaluates. Both directions are pinned: the run is NOT taken while it
        // is younger than the fast window (which is what stops the pass grabbing a run the poll is still
        // working, and what makes the window a real backoff on a path that resets the RetryGovernor), and it IS
        // taken as soon as it crosses that window — hours before the abandonment window it used to wait for.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        // The store stamps updated_at from the wall clock, so the fake clock starts beside it.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Backdate(db, run.Id, clock.GetUtcNow() - RetryGrace + TimeSpan.FromMinutes(5));
        var attempts = new List<long>();
        var reconciler = new StrandedRunReconciler(
            listStrandedRuns: store.ListStrandedRuns,
            getPrLifecycleAsync: (_, _) => Task.FromResult(PrLifecycle.Open),
            resumeAsync: (resuming, _) =>
            {
                attempts.Add(resuming.Id);
                return Task.FromResult(resuming);
            },
            updateRunState: store.UpdateReviewRunState,
            timeProvider: clock,
            grace: Grace,
            scanLimit: 50,
            maxResumesPerPass: 5,
            logger: new CapturingLogger<StrandedRunReconciler>([]),
            listRetryPendingRuns: store.ListRetryPendingRuns,
            retryPendingGrace: RetryGrace
        );

        await reconciler.SweepAsync(CancellationToken.None);

        attempts.Should().BeEmpty("the run has not yet waited its fast window");

        clock.Advance(TimeSpan.FromMinutes(6));
        await reconciler.SweepAsync(CancellationToken.None);

        attempts
            .Should()
            .Equal(
                [run.Id],
                "the run crossed the {0} fast window; before #429 it would have sat until the {1} abandonment one",
                RetryGrace,
                Grace
            );
        clock
            .GetUtcNow()
            .Should()
            .BeBefore(
                DateTimeOffset.UtcNow + Grace,
                "the whole point is that no part of this waited an abandonment window"
            );
    }

    [Fact]
    public async Task A_failed_resume_holds_the_run_out_of_the_backlog_for_one_grace_period_and_no_longer()
    {
        // Against the REAL store, because the fake harness's listing hands back whatever rows it was given and
        // never evaluates the `updated_at` predicate the whole backoff rests on — a claim asserted there is a
        // claim about the harness. What this pins is the contract in both directions: the claim write is what
        // keeps a permanently-failing run off the very next pass (resumes here run through
        // PrOrchestrator.ReconcileAsync, which resets the RetryGovernor by design, so grace is the only bound
        // left and a next-pass retry would be an unbounded loop of full reviews); and the grace period is a
        // DELAY, not a retirement, so the same run comes back on its own once it ages out again.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        // The store stamps updated_at from the wall clock, so the fake clock has to start beside it for
        // "claimed just now" and "aged out since" to mean anything to the query.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Backdate(db, run.Id, clock.GetUtcNow() - TimeSpan.FromDays(9));
        var attempts = new List<long>();
        var reconciler = new StrandedRunReconciler(
            listStrandedRuns: store.ListStrandedRuns,
            getPrLifecycleAsync: (_, _) => Task.FromResult(PrLifecycle.Open),
            resumeAsync: (resuming, _) =>
            {
                attempts.Add(resuming.Id);
                throw new TimeoutException("the review's remaining stages timed out");
            },
            updateRunState: store.UpdateReviewRunState,
            timeProvider: clock,
            grace: Grace,
            scanLimit: 50,
            maxResumesPerPass: 5,
            logger: new CapturingLogger<StrandedRunReconciler>([])
        );

        await reconciler.SweepAsync(CancellationToken.None);
        await reconciler.SweepAsync(CancellationToken.None);

        attempts
            .Should()
            .Equal(
                [run.Id],
                "the claim advanced updated_at, so the next pass's listing no longer sees the row — without it a "
                    + "run that fails forever is resumed on every cycle, at the cost of a lease, a clone and an LLM "
                    + "call each time"
            );
        clock.Advance(Grace + TimeSpan.FromMinutes(1));
        await reconciler.SweepAsync(CancellationToken.None);

        attempts
            .Should()
            .Equal(
                [run.Id, run.Id],
                "the claim delays the retry by one grace period; it must never remove the route back altogether"
            );
    }

    [Fact]
    public async Task An_unreachable_provider_holds_the_run_out_of_the_backlog_for_one_grace_period()
    {
        // Against the REAL store, for the same reason as the test above: the fake harness's listing hands back
        // whatever rows it was given and never evaluates the `updated_at` predicate the entire backoff rests on,
        // so the claim can only be made here. The defect this pins is a starvation one. A provider outage lasts
        // longer than a poll cycle, so without a write of its own every run behind it is re-listed, re-looked-up
        // and re-failed on every maintenance pass — each one eating a slice of the scan limit that a run the
        // daemon could actually settle would otherwise have had, indefinitely.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());
        var run = store.CreateOrGetReviewRun(SampleRun(repoId, "101"));
        // The store stamps updated_at from the wall clock, so the fake clock has to start beside it.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Backdate(db, run.Id, clock.GetUtcNow() - TimeSpan.FromDays(9));
        var lookups = new List<long>();
        var reconciler = new StrandedRunReconciler(
            listStrandedRuns: store.ListStrandedRuns,
            getPrLifecycleAsync: (row, _) =>
            {
                lookups.Add(row.Run.Id);
                throw new HttpRequestException("Bad gateway", null, HttpStatusCode.ServiceUnavailable);
            },
            resumeAsync: (resuming, _) => Task.FromResult(resuming),
            updateRunState: store.UpdateReviewRunState,
            timeProvider: clock,
            grace: Grace,
            scanLimit: 50,
            maxResumesPerPass: 5,
            logger: new CapturingLogger<StrandedRunReconciler>([])
        );

        await reconciler.SweepAsync(CancellationToken.None);
        await reconciler.SweepAsync(CancellationToken.None);

        lookups
            .Should()
            .Equal(
                [run.Id],
                "the backoff stamp advanced updated_at, so the second pass's listing no longer sees the row at all"
            );
        clock.Advance(Grace + TimeSpan.FromMinutes(1));
        await reconciler.SweepAsync(CancellationToken.None);

        lookups
            .Should()
            .Equal(
                [run.Id, run.Id],
                "the stamp delays the retry by one grace period; a provider blip must never cost a run its only "
                    + "route back, which is what a retirement here would do"
            );
        store
            .GetReviewRun(run.Id)!
            .WorkflowStatus.Should()
            .Be(WorkflowStatus.RetryPending, "the run is still open work — nothing about a 5xx retires it");
    }

    [Theory]
    [InlineData("security", "full")] // another variant reviews with its own prompt and its own output
    [InlineData("primary", "incremental")] // another kind reviews a different span of the PR
    public void The_store_does_not_let_a_different_reviews_later_run_supersede(string variantId, string kind)
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var unrelated = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with
            {
                HeadSha = "head-2",
                VariantId = variantId,
                ReviewKind = kind,
            }
        );
        foreach (var id in new[] { stranded.Id, unrelated.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store
            .ListStrandedRuns(Now - Grace, limit: 50)
            .Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should()
            .BeFalse(
                "run {0} never produced the review this run owes; retiring on it would drop that review "
                    + "silently and forever, because this listing is the run's only remaining route back",
                unrelated.Id
            );
    }

    [Fact]
    public void The_store_does_not_let_a_duplicate_row_at_the_same_head_supersede()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // The identity lookup is watermark-agnostic, so a duplicate at the same head is not reachable through
        // CreateOrGetReviewRun — it is the shape left behind by an earlier build that keyed identity on the
        // watermark, which FindReviewRunByIdentity still tolerates. The stranded listing meets those rows too.
        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var duplicateId = CloneRunAtSameHead(db, stranded.Id, watermark: "wm-2");
        foreach (var id in new[] { stranded.Id, duplicateId })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store
            .ListStrandedRuns(Now - Grace, limit: 50)
            .Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should()
            .BeFalse(
                "retirement is justified by a newer head making this diff stale, and run {0} sits at the same "
                    + "head — a higher row id on its own is not evidence that anything went stale",
                duplicateId
            );
    }

    [Fact]
    public void The_store_supersedes_when_a_later_run_reviewed_the_same_head_against_a_new_base()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        // A target-branch rebase moves base_sha under an unchanged head. The later run reviewed a genuinely
        // different diff — the PR as it now stands — while this one still owes findings about changes that have
        // since landed in the target branch. Keying supersession on the head alone would resume it and, on a
        // posting daemon, publish them. base_sha is part of the identity tuple for the same reason, so these are
        // two legitimately distinct runs and the later one is the current one.
        var stranded = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with
            {
                HeadSha = "head-1",
                BaseSha = "base-1",
            }
        );
        var newer = store.CreateOrGetReviewRun(
            SampleRun(repoId, "101") with
            {
                HeadSha = "head-1",
                BaseSha = "base-2",
            }
        );
        newer.Id.Should().NotBe(stranded.Id, "a moved base is a different identity, not the same run");
        foreach (var id in new[] { stranded.Id, newer.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store
            .ListStrandedRuns(Now - Grace, limit: 50)
            .Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should()
            .BeTrue(
                "run {0} reviewed the same head against the current base, so this run's diff is the stale one",
                newer.Id
            );
    }

    [Fact]
    public void The_store_still_supersedes_across_a_mode_change()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var stranded = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-1" });
        var newer = store.CreateOrGetReviewRun(SampleRun(repoId, "101") with { HeadSha = "head-2", Mode = "post" });
        foreach (var id in new[] { stranded.Id, newer.Id })
        {
            Backdate(db, id, Now - TimeSpan.FromDays(9));
        }

        store
            .ListStrandedRuns(Now - Grace, limit: 50)
            .Single(s => s.Run.Id == stranded.Id)
            .Superseded.Should()
            .BeTrue(
                "mode is an authorization decision made at post time, not part of what the review is (see "
                    + "CreateOrGetReviewRun) — toggling posting between the two runs does not make run {0}'s "
                    + "newer head any less of a replacement for this one's diff",
                newer.Id
            );
    }

    [Fact]
    public void The_store_caps_one_read_and_leaves_the_rest_for_the_next_pass()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = store.EnsureRepo(SampleRepo());

        var ids = new List<long>();
        foreach (var pr in new[] { "101", "102", "103" })
        {
            var run = store.CreateOrGetReviewRun(SampleRun(repoId, pr));
            Backdate(db, run.Id, Now - TimeSpan.FromDays(9));
            ids.Add(run.Id);
        }

        var stranded = store.ListStrandedRuns(Now - Grace, limit: 2);

        stranded
            .Select(s => s.Run.Id)
            .Should()
            .Equal(
                ids.Take(2),
                "the cap takes the oldest rows by id, in one query — never a second page by offset "
                    + "over a predicate the caller is mutating as it works"
            );
        stranded
            .Should()
            .AllSatisfy(
                s => s.Repo.RepoName.Should().Be(SampleRepo().RepoName),
                "the caller needs the repo identity to ask the provider what became of the PR"
            );
    }

    // ── harness ───────────────────────────────────────────────────────────────────────────────────

    private static void Backdate(TempSqliteDatabase db, long runId, DateTimeOffset updatedAt)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_run SET updated_at = $at WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$at", updatedAt.ToUniversalTime().ToString("O"));
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Stamps <c>parked_at</c> without touching <c>workflow_status</c>. Written directly rather than through
    /// <c>ReviewStore.TryMarkReviewRunParked</c>, which also writes <c>Failed</c> — a status that would
    /// independently exclude the row from the retry-pending listing and so hide whether the park conjunct is
    /// doing anything at all.
    /// </summary>
    private static void StampParkedAt(TempSqliteDatabase db, long runId, DateTimeOffset parkedAt)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_run SET parked_at = $at WHERE id = $id;";
        _ = command.Parameters.AddWithValue("$at", parkedAt.ToUniversalTime().ToString("O"));
        _ = command.Parameters.AddWithValue("$id", runId);
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Copies a run onto a second row at the same head, differing only by <c>trigger_watermark</c> — the
    /// duplicate an earlier build's identity key could produce, and the table's UNIQUE constraint still
    /// permits. Written directly because the store's own lookup is watermark-agnostic and would hand back
    /// the original. Returns the new row's id.
    /// </summary>
    private static long CloneRunAtSameHead(TempSqliteDatabase db, long runId, string watermark)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO review_run (
                repo_id, pr_id, head_sha, base_sha, trigger_watermark, review_kind, variant_id, mode,
                stage, workflow_status, pr_lifecycle_state, is_fork_pr, is_target_repo_public,
                created_at, updated_at)
            SELECT repo_id, pr_id, head_sha, base_sha, $watermark, review_kind, variant_id, mode,
                   stage, workflow_status, pr_lifecycle_state, is_fork_pr, is_target_repo_public,
                   created_at, updated_at
            FROM review_run WHERE id = $id
            RETURNING id;
            """;
        _ = command.Parameters.AddWithValue("$watermark", watermark);
        _ = command.Parameters.AddWithValue("$id", runId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static RepoIdentity SampleRepo() =>
        new()
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
        };

    private static ReviewRun SampleRun(long repoId, string prId) =>
        new()
        {
            RepoId = repoId,
            PrId = prId,
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.RetryPending,
            PrLifecycleState = PrLifecycleState.Open,
        };

    private static StrandedRunRow Row(
        long id,
        ReviewStage stage = ReviewStage.Discovered,
        bool superseded = false,
        PrLifecycleState lifecycle = PrLifecycleState.Open
    ) =>
        new(
            new ReviewRun
            {
                Id = id,
                RepoId = 1,
                PrId = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                HeadSha = "head-sha",
                BaseSha = "base-sha",
                TriggerWatermark = "wm-1",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "post",
                Stage = stage,
                WorkflowStatus = WorkflowStatus.RetryPending,
                PrLifecycleState = lifecycle,
            },
            SampleRepo(),
            superseded
        );

    private sealed class Harness
    {
        private StrandedRunRow[] _rows = [];
        private PrLifecycle _lifecycle = PrLifecycle.Open;
        private long? _throwFor;
        private Exception _failure = new InvalidOperationException("provider unreachable");
        private Action? _beforeLifecycleThrow;
        private long? _resolvesTo;
        private long? _resumeThrowsFor;
        private Exception _resumeFailure = new TimeoutException("the review's remaining stages timed out");
        private int _maxResumes = 10;
        private StrandedRunRow[] _retryRows = [];
        private bool _fastPath = true;

        public List<ReviewRun> Resumed { get; } = [];

        /// <summary>Every <c>review_run</c> state write the reconciler made, in order.</summary>
        public List<(long, ReviewStage, WorkflowStatus, PrLifecycleState)> StateWrites { get; } = [];

        /// <summary>
        /// The subset of <see cref="StateWrites"/> that retired a run. Retirement is the only write that marks a
        /// run <see cref="WorkflowStatus.Completed"/> — the claim stamp taken before a resume deliberately
        /// re-writes the status the row already had — so the status distinguishes the two without the harness
        /// having to guess which call was which.
        /// </summary>
        public IEnumerable<(long, ReviewStage, WorkflowStatus, PrLifecycleState)> Retired =>
            StateWrites.Where(w => w.Item3 == WorkflowStatus.Completed);

        public List<string> Log { get; } = [];

        /// <summary>
        /// Every state write and every resume, interleaved in the order they happened. The claim stamp is only
        /// worth anything if it lands BEFORE the resume, and two separate lists cannot show that.
        /// </summary>
        public List<string> Order { get; } = [];

        public int LifecycleLookups { get; private set; }

        public Harness WithRows(params StrandedRunRow[] rows)
        {
            _rows = rows;
            return this;
        }

        public Harness WithLifecycle(PrLifecycle lifecycle)
        {
            _lifecycle = lifecycle;
            return this;
        }

        public Harness WithLifecycleThrowingFor(long runId, Exception? failure = null, Action? before = null)
        {
            _throwFor = runId;
            _failure = failure ?? new InvalidOperationException("provider unreachable");
            _beforeLifecycleThrow = before;
            return this;
        }

        public Harness WithResumeResolvingTo(long runId)
        {
            _resolvesTo = runId;
            return this;
        }

        public Harness WithResumeThrowingFor(long runId, Exception? failure = null)
        {
            _resumeThrowsFor = runId;
            _resumeFailure = failure ?? new TimeoutException("the review's remaining stages timed out");
            return this;
        }

        public Harness WithMaxResumes(int max)
        {
            _maxResumes = max;
            return this;
        }

        /// <summary>Rows the retry-pending fast listing (#429) returns, distinct from <see cref="WithRows"/>.</summary>
        public Harness WithRetryPendingRows(params StrandedRunRow[] rows)
        {
            _retryRows = rows;
            return this;
        }

        /// <summary>Builds a reconciler with the fast path switched off, as a zeroed knob leaves it.</summary>
        public Harness WithoutFastPath()
        {
            _fastPath = false;
            return this;
        }

        public StrandedRunReconciler Reconciler()
        {
            Func<DateTimeOffset, int, IReadOnlyList<StrandedRunRow>>? fastListing = null;
            if (_fastPath)
            {
                fastListing = (staleBefore, limit) =>
                {
                    staleBefore
                        .Should()
                        .Be(
                            Now - RetryGrace,
                            "the fast window, not the abandonment one, decides which retry-pending rows are read"
                        );
                    return [.. _retryRows.Take(limit)];
                };
            }

            return new StrandedRunReconciler(
                listStrandedRuns: (staleBefore, limit) =>
                {
                    staleBefore.Should().Be(Now - Grace, "the grace period is subtracted from the current time");
                    return [.. _rows.Take(limit)];
                },
                getPrLifecycleAsync: (row, _) =>
                {
                    LifecycleLookups++;
                    if (row.Run.Id != _throwFor)
                    {
                        return Task.FromResult(_lifecycle);
                    }

                    _beforeLifecycleThrow?.Invoke();
                    throw _failure;
                },
                resumeAsync: (run, _) =>
                {
                    Order.Add($"resume:{run.Id}");
                    if (run.Id == _resumeThrowsFor)
                    {
                        throw _resumeFailure;
                    }

                    Resumed.Add(run);
                    return Task.FromResult(_resolvesTo is { } id ? run with { Id = id } : run);
                },
                updateRunState: (id, stage, status, state) =>
                {
                    Order.Add($"write:{id}");
                    StateWrites.Add((id, stage, status, state));
                },
                timeProvider: new FakeTimeProvider(Now),
                grace: Grace,
                scanLimit: 50,
                maxResumesPerPass: _maxResumes,
                logger: new CapturingLogger<StrandedRunReconciler>(Log),
                listRetryPendingRuns: fastListing,
                retryPendingGrace: _fastPath ? RetryGrace : default
            );
        }
    }

    /// <summary>Records the formatted message of every log entry so the deferral notices can be asserted.</summary>
    private sealed class CapturingLogger<T>(List<string> sink) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => sink.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose() { }
        }
    }
}
