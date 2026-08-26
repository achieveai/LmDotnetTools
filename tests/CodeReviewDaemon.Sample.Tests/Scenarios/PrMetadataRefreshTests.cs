using System.Globalization;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Refreshing WHAT THE PR SAYS IT DOES — author, title, description, target branch — on an existing run from
/// the current poll.
/// <para>
/// The sibling of <see cref="TrustSignalRefreshTests"/>, and the same root cause: these four are written by
/// <c>CreateOrGetReviewRun</c>'s INSERT and no <c>UPDATE review_run</c> in the codebase touched them again.
/// The poller builds a fresh seed carrying the current values on EVERY poll and the orchestrator discarded
/// them, so the brief's "Stated intent" block quotes the claim as it stood at DISCOVERY. Measured on
/// <c>.run/nova-review.db</c> over 2026-08-06 → 2026-08-10: PR titles do get rewritten mid-life (PR 5505154
/// was captured as "[WIP] Remove all references to the enableEmployeeDescriptiveAsPH flight…" by run 154 and,
/// 2.5 h later, without the [WIP] by run 169), and across the 251 runs that produced a review of record the
/// creation-to-review window ran median 9.6 min, p90 155.8, max 2,095 (34.9 h). A run that went stale
/// mid-flight left no trace of it — the frozen column is why — so the fix is also what makes the question
/// observable.
/// </para>
/// <para>
/// The refresh is NOT the trust-signal refresh with different columns, and the second test is where the two
/// part company. The trust signals can be adopted unconditionally because <c>PrPollingService</c> already
/// collapsed "the provider could not tell" into a fail-closed bool. These have no such collapse upstream: a
/// payload that omitted the description arrives as null, indistinguishable from an author who deleted it,
/// and only one of the two readings is recoverable afterwards.
/// </para>
/// </summary>
public sealed class PrMetadataRefreshTests
{
    private const string WipTitle = "[WIP] Remove all references to the enableEmployeeDescriptiveAsPH flight";

    private const string RetitledTitle =
        "Remove EnableEmployeeDescriptiveAsPH flight references from Data Ingress docs";

    private const string CapturedDescription =
        "Removes the flight gate and the four doc sections that referenced it. No behaviour change.";

    private static long Repo(ReviewStore store) =>
        store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "azure-devops",
                OrgOrOwner = "o365exchange",
                Project = "Weve_DA",
                RepoName = "Nova",
            }
        );

    private static ReviewRun Seed(
        long repoId,
        string? title,
        string? description,
        string? targetBranch = "main",
        string? author = "dev@contoso.com"
    )
    {
        return new ReviewRun
        {
            RepoId = repoId,
            PrId = "5505154",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "head-sha",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "post",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
            PrAuthor = author,
            PrTitle = title,
            PrDescription = description,
            PrTargetBranch = targetBranch,
        };
    }

    private static PrOrchestrator Orchestrator(ReviewStore store) =>
        new(
            store,
            new RecordingStageExecutor(),
            NullLogger<PrOrchestrator>.Instance,
            providers: [new ReadyPrProvider()]
        );

    /// <summary>
    /// PR 5505154's edit, replayed inside one run's lifetime. The run was created while the PR still carried
    /// its <c>[WIP]</c> title; by the time a later poll reaches it the author has rewritten title, body and
    /// target. The run must adopt them, or the review is graded against intent the PR has retracted.
    /// </summary>
    [Fact]
    public async Task A_re_poll_adopts_an_edited_title_and_description()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var created = store.CreateOrGetReviewRun(
            Seed(repoId, WipTitle, "Draft — do not review yet.", targetBranch: "main")
        );

        // Same identity tuple, so this re-polls the SAME row — now with the PR as its author left it.
        var run = await Orchestrator(store)
            .RunAsync(
                Seed(repoId, RetitledTitle, CapturedDescription, targetBranch: "release/2026.08"),
                CancellationToken.None
            );

        run.Id.Should().Be(created.Id, "the identity tuple is unchanged, so this must be the same run");
        var refreshed = store.GetReviewRun(created.Id)!;
        refreshed.PrTitle.Should().Be(RetitledTitle);
        refreshed
            .PrDescription.Should()
            .Be(
                CapturedDescription,
                "the body is what the reviewer weighs the diff against; a stale one asks the wrong question"
            );
        refreshed
            .PrTargetBranch.Should()
            .Be("release/2026.08", "risk is judged by destination, and this PR was retargeted");

        // The returned run, not only the row: the orchestrator hands this object to the stage executor, which
        // renders the brief from it. A row that is right while the in-memory run is stale reviews the stale one.
        run.PrTitle.Should().Be(RetitledTitle);
        run.PrDescription.Should().Be(CapturedDescription);
    }

    /// <summary>
    /// The direction a refresh could get irrecoverably wrong. A poll whose payload carried no description —
    /// a provider that returned the PR without its body, a permissions-trimmed listing — must not erase one
    /// the daemon already captured. There is no second chance at it: the at-close path runs against a PR that
    /// is by then closed, and nothing else in the daemon re-reads a description.
    /// </summary>
    [Fact]
    public async Task A_poll_that_carries_no_description_does_not_erase_the_one_already_captured()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        var created = store.CreateOrGetReviewRun(Seed(repoId, WipTitle, CapturedDescription));

        _ = await Orchestrator(store).RunAsync(Seed(repoId, RetitledTitle, description: null), CancellationToken.None);

        var refreshed = store.GetReviewRun(created.Id)!;
        refreshed
            .PrDescription.Should()
            .Be(
                CapturedDescription,
                "a poll that could not read the body says nothing about the body; adopting its silence would "
                    + "destroy the only copy the daemon will ever have"
            );

        // Same poll, same block, opposite outcome — proving the guard is about ABSENCE and not about refusing
        // to write. Without this the first assertion would also pass on a refresh that had simply stopped.
        refreshed
            .PrTitle.Should()
            .Be(RetitledTitle, "this poll DID carry a title, so there is nothing to protect and it must be adopted");
    }

    /// <summary>
    /// The title and description are the author's own words — EUII — so the line that reports a mid-run
    /// change may name their LENGTHS and nothing else, the same rule the review-brief inventory line in
    /// <c>DaemonReviewStageExecutor</c> keeps. This is a live guard, not a vacuous one: the orchestrator holds
    /// both strings at the point it logs, so emitting them is one edit away.
    /// </summary>
    [Fact]
    public async Task A_changed_intent_is_reported_by_length_and_never_by_text()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var repoId = Repo(store);
        _ = store.CreateOrGetReviewRun(Seed(repoId, WipTitle, "Draft — do not review yet."));

        using var loggerFactory = new CapturingLoggerFactory();
        var orchestrator = new PrOrchestrator(
            store,
            new RecordingStageExecutor(),
            loggerFactory.CreateLogger<PrOrchestrator>(),
            providers: [new ReadyPrProvider()]
        );
        _ = await orchestrator.RunAsync(Seed(repoId, RetitledTitle, CapturedDescription), CancellationToken.None);

        var reported = loggerFactory
            .Capturing.MessagesAtLevel(LogLevel.Information)
            .Where(m => m.Contains("stated intent", StringComparison.Ordinal))
            .ToList();
        reported
            .Should()
            .ContainSingle(
                "a review read against a superseded intent is otherwise indistinguishable from one read against "
                    + "the current intent, in every artifact the run leaves behind"
            );
        reported[0].Should().Contain(RetitledTitle.Length.ToString(CultureInfo.InvariantCulture));
        reported[0].Should().NotContain(RetitledTitle);
        reported[0].Should().NotContain(CapturedDescription);
        reported[0].Should().NotContain(WipTitle);
    }
}
