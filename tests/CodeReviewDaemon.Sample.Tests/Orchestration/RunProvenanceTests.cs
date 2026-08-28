using AchieveAi.LmDotnetTools.LmCore.Prompts;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <c>review_run.prompt_template_hash</c> has existed since v1 of the schema and was NULL on every row the
/// daemon had ever written, so no prompt change it shipped could be attributed to the reviews that change
/// altered — and the eval corpus, which already carries the column, carried an empty string for every
/// candidate.
/// <para>
/// The obvious fix is the wrong one, and these tests exist mostly to pin why. The INSERT in
/// <c>CreateOrGetReviewRun</c> is executed by the POLLER at discovery, before any prompt is rendered — and on
/// an identity match it returns the existing row without updating it. A run discovered under one prompt and
/// dispatched under another after a deploy (the ordinary fate of everything left in
/// <see cref="WorkflowStatus.RetryPending"/>) would keep the first prompt's hash and be filed under a prompt
/// it never ran. The write therefore belongs at REVIEW DISPATCH, which is both where the prompt in force is
/// known and where the review of record is actually produced.
/// </para>
/// </summary>
public sealed class RunProvenanceTests
{
    /// <summary>
    /// What a row created under an earlier build's prompt carries. Any value the current build cannot
    /// produce works; this one is obviously not a live digest.
    /// </summary>
    private const string PromptHashFromAnEarlierBuild = "0000stale0000000";

    /// <summary>
    /// The same embedded resource <c>DaemonAgentFactory</c> reads, opened independently so the templates the
    /// digest claims to cover can be named here rather than taken on the factory's word.
    /// </summary>
    private static readonly IPromptReader Prompts = new PromptReader(
        typeof(DaemonAgentFactory).Assembly,
        "CodeReviewDaemon.Sample.Prompts.daemon-prompts.yaml"
    );

    [Fact]
    public async Task Dispatching_a_review_records_the_prompt_template_hash_on_the_persisted_run()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store);
        var run = SeedRun(store, promptTemplateHash: null);

        run.PromptTemplateHash.Should()
            .BeNull("discovery runs in the poller, before a prompt is rendered — there is nothing true to write yet");

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var recorded = store.GetReviewRun(run.Id)!;
        recorded
            .PromptTemplateHash.Should()
            .NotBeNull("the production dispatch path is the producer for this column; without it the row stays NULL");
        recorded
            .PromptTemplateHash.Should()
            .Be(
                DaemonAgentFactory.ReviewPromptTemplateHash,
                "the review was dispatched under this build's review+synthesis templates"
            );
    }

    /// <summary>
    /// The case that rules out writing the hash at creation. The run was discovered AND reviewed under an
    /// earlier build's prompt, left <see cref="WorkflowStatus.RetryPending"/> across a deploy, and is now
    /// resumed. The review of record is the one TODAY'S prompt produces, so that is what the row must say —
    /// a hash naming the prompt that wrote the review it replaced is worse than no hash, because it reads as
    /// evidence.
    /// </summary>
    [Fact]
    public async Task A_run_resumed_under_a_different_prompt_records_the_new_hash_not_the_stale_one()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store);
        var run = SeedRun(store, promptTemplateHash: null);

        // The earlier build's dispatch, replayed: this is the state the row is in when the deploy lands.
        store.RecordRunProvenance(run.Id, PromptHashFromAnEarlierBuild);
        store.GetReviewRun(run.Id)!.PromptTemplateHash.Should().Be(PromptHashFromAnEarlierBuild);

        // Why creation cannot own this write, stated as an assertion rather than as a comment: re-polling the
        // same identity with today's hash in the seed leaves the row exactly as it was. Creation is an
        // "insert or return the existing row", and the existing row is the one being resumed.
        store
            .CreateOrGetReviewRun(SeedFor(run.RepoId, DaemonAgentFactory.ReviewPromptTemplateHash))
            .PromptTemplateHash.Should()
            .Be(
                PromptHashFromAnEarlierBuild,
                "an identity match returns the stored row untouched, so a seed can never correct it"
            );

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var recorded = store.GetReviewRun(run.Id)!;
        recorded
            .PromptTemplateHash.Should()
            .NotBe(
                PromptHashFromAnEarlierBuild,
                "the review of record was produced by the prompt this process is running, not the one the row was "
                    + "created under"
            );
        recorded.PromptTemplateHash.Should().Be(DaemonAgentFactory.ReviewPromptTemplateHash);
    }

    /// <summary>
    /// A caller with nothing to say must not erase what a dispatch already established — the COALESCE is the
    /// difference between "this run's prompt is unknown" and "this run's prompt was forgotten".
    /// </summary>
    [Fact]
    public void A_null_hash_leaves_a_previously_recorded_one_standing()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store, promptTemplateHash: null);

        store.RecordRunProvenance(run.Id, DaemonAgentFactory.ReviewPromptTemplateHash);
        store.RecordRunProvenance(run.Id, null);

        store.GetReviewRun(run.Id)!.PromptTemplateHash.Should().Be(DaemonAgentFactory.ReviewPromptTemplateHash);
    }

    /// <summary>
    /// The hash must be a stable label for a build, not a per-run value: a column that gives every run its own
    /// value cannot group the runs a prompt change affected, which is the only thing it is for.
    /// </summary>
    [Fact]
    public void The_prompt_template_hash_is_a_stable_short_digest_of_this_build()
    {
        var hash = DaemonAgentFactory.ReviewPromptTemplateHash;

        hash.Should()
            .Be(DaemonAgentFactory.ReviewPromptTemplateHash, "it identifies the build's templates, not a call");
        hash.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]{16}$", "lowercase hex, matching the daemon's short-digest idiom");
    }

    /// <summary>
    /// Both directions of the only property the column has to have, pinned against the templates ACTUALLY in
    /// force. Stability alone is satisfied by a constant, and sensitivity alone by a per-call random value;
    /// neither half is worth anything without the other.
    /// </summary>
    [Fact]
    public void The_hash_covers_the_live_templates_and_moves_when_either_of_them_changes()
    {
        var review = Prompts.GetPrompt("review").Value;
        var synthesis = Prompts.GetPrompt("synthesis").Value;

        DaemonAgentFactory
            .ReviewPromptTemplateHash.Should()
            .Be(
                DaemonAgentFactory.ComputeTemplateHash(review, synthesis),
                "the published value is the digest of the review+synthesis templates this build actually ships, "
                    + "not of some other input that merely happens to be constant"
            );

        DaemonAgentFactory
            .ComputeTemplateHash(review, synthesis)
            .Should()
            .Be(
                DaemonAgentFactory.ComputeTemplateHash(review, synthesis),
                "an unchanged pair of templates keeps its label, or two runs of one build cannot be grouped"
            );

        DaemonAgentFactory
            .ComputeTemplateHash(review + "\nAlso: never say 'delve'.", synthesis)
            .Should()
            .NotBe(
                DaemonAgentFactory.ReviewPromptTemplateHash,
                "editing the review template changes what the first turn was asked"
            );

        DaemonAgentFactory
            .ComputeTemplateHash(review, synthesis + "\nAlso: never say 'delve'.")
            .Should()
            .NotBe(
                DaemonAgentFactory.ReviewPromptTemplateHash,
                "editing the synthesis template changes what the turn that writes the review was asked"
            );
    }

    /// <summary>
    /// The distinguishing case for the length-prefixed framing, which is the whole reason it is there. The two
    /// pairs below CONCATENATE to the same bytes and are different prompt sets: the first tells the review
    /// turn to deliver and leaves the synthesis turn nothing, the second tells the synthesis turn to. A digest
    /// taken over the joined text cannot tell them apart, so the boundary has to be inside the subject.
    /// <para>
    /// The case has to be a boundary AMBIGUITY, not merely an edit that moves text — move a line and its
    /// newline together and the concatenations already differ, which a naive digest also catches, so such a
    /// case pins nothing about the framing.
    /// </para>
    /// </summary>
    [Fact]
    public void Two_template_pairs_that_concatenate_alike_are_still_different_digests()
    {
        const string ReviewOwnsIt = "Report blockers.\nDeliver the answer.";
        const string SynthesisOwnsIt = "Deliver the answer.";

        (ReviewOwnsIt + string.Empty)
            .Should()
            .Be(
                "Report blockers.\n" + SynthesisOwnsIt,
                "the case is only worth anything if the joined text is byte-identical"
            );

        DaemonAgentFactory
            .ComputeTemplateHash(ReviewOwnsIt, string.Empty)
            .Should()
            .NotBe(
                DaemonAgentFactory.ComputeTemplateHash("Report blockers.\n", SynthesisOwnsIt),
                "which turn was told to deliver is a real difference, and the digest has to carry it"
            );
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static DaemonReviewStageExecutor Executor(ReviewStore store)
    {
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree",
                new SandboxCommandResult(1, string.Empty, "not a git repo")
            )
            .OnArgvContains(
                "diff",
                new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty)
            );

        return new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions(),
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance
        );
    }

    private static ReviewRun SeedRun(ReviewStore store, string? promptTemplateHash)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
                RepoStableId = "repo-stable-1",
            }
        );
        return store.CreateOrGetReviewRun(SeedFor(repoId, promptTemplateHash));
    }

    private static ReviewRun SeedFor(long repoId, string? promptTemplateHash) =>
        new()
        {
            RepoId = repoId,
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "2026-06-29T12:34:56Z",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
            PromptTemplateHash = promptTemplateHash,
        };
}
