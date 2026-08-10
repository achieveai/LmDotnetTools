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
/// <c>review_run.prompt_template_hash</c> and <c>review_run.model_provider</c> have existed since v1 of the
/// schema and are NULL on every row the daemon has ever written — 283 of 283 in <c>.run/nova-review.db</c> —
/// so no prompt change it has shipped can be attributed to the reviews that change altered.
/// <para>
/// The obvious fix is the wrong one, and these tests exist mostly to pin why. The INSERT in
/// <c>CreateOrGetReviewRun</c> is executed by the POLLER at discovery, before any prompt is rendered or
/// provider chosen — and on an identity match it returns the existing row without updating it. A run
/// discovered under one prompt and dispatched under another after a deploy (the ordinary fate of everything
/// left in <see cref="WorkflowStatus.RetryPending"/>) would keep the first prompt's hash and be filed under a
/// prompt it never ran. The write therefore belongs at REVIEW DISPATCH, which is both where the prompt in
/// force is known and where the review of record is actually produced.
/// </para>
/// </summary>
public sealed class RunProvenanceTests
{
    /// <summary>What a row created under an earlier build's prompt carries. Any value the current build
    /// cannot produce works; this one is obviously not a live digest.</summary>
    private const string PromptHashFromAnEarlierBuild = "0000stale0000000";

    private const string ProviderId = "gpt-5.6-luna";

    [Fact]
    public async Task Provenance_is_recorded_at_review_dispatch_and_not_at_discovery()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store);
        var run = SeedRun(store, promptTemplateHash: null);

        run.PromptTemplateHash.Should().BeNull(
            "discovery runs in the poller, before a prompt is rendered — there is nothing true to write yet");
        run.ModelProvider.Should().BeNull();

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var recorded = store.GetReviewRun(run.Id)!;
        recorded.PromptTemplateHash.Should().Be(
            DaemonAgentFactory.ReviewPromptTemplateHash,
            "the review was dispatched under this build's review+synthesis templates");
        recorded.ModelProvider.Should().Be(
            ProviderId,
            "on the S2S path the model id is not forwarded at all — the provider is what actually served it");
    }

    /// <summary>
    /// The case that rules out writing the hash at creation. The run was discovered AND reviewed under an
    /// earlier build's prompt, left <see cref="WorkflowStatus.RetryPending"/> across a deploy, and is now
    /// resumed. The review of record is the one TODAY'S prompt produces, so that is what the row must say —
    /// a hash naming the prompt that wrote the review it replaced is worse than no hash, because it reads
    /// as evidence.
    /// </summary>
    [Fact]
    public async Task A_run_resumed_under_a_different_prompt_records_the_new_hash_not_the_stale_one()
    {
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var executor = Executor(store);
        var run = SeedRun(store, promptTemplateHash: null);

        // The earlier build's dispatch, replayed: this is the state the row is in when the deploy lands.
        store.RecordRunProvenance(run.Id, PromptHashFromAnEarlierBuild, "provider-of-that-build");
        store.GetReviewRun(run.Id)!.PromptTemplateHash.Should().Be(PromptHashFromAnEarlierBuild);

        // Why creation cannot own this write, stated as an assertion rather than as a comment: re-polling the
        // same identity with today's hash in the seed leaves the row exactly as it was. Creation is an
        // "insert or return the existing row", and the existing row is the one being resumed.
        store.CreateOrGetReviewRun(SeedFor(run.RepoId, DaemonAgentFactory.ReviewPromptTemplateHash))
            .PromptTemplateHash.Should().Be(
                PromptHashFromAnEarlierBuild,
                "an identity match returns the stored row untouched, so a seed can never correct it");

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var recorded = store.GetReviewRun(run.Id)!;
        recorded.PromptTemplateHash.Should().NotBe(
            PromptHashFromAnEarlierBuild,
            "the review of record was produced by the prompt this process is running, not the one the row was "
                + "created under");
        recorded.PromptTemplateHash.Should().Be(DaemonAgentFactory.ReviewPromptTemplateHash);
        recorded.ModelProvider.Should().Be(ProviderId, "the resumed dispatch is the one that served it");
    }

    /// <summary>
    /// The hash must be a stable label for a build, not a per-run value: a column that gives every run its own
    /// value cannot group the runs a prompt change affected, which is the only thing it is for.
    /// </summary>
    [Fact]
    public void The_prompt_template_hash_is_a_stable_short_digest_of_this_build()
    {
        var hash = DaemonAgentFactory.ReviewPromptTemplateHash;

        hash.Should().Be(
            DaemonAgentFactory.ReviewPromptTemplateHash, "it identifies the build's templates, not a call");
        hash.Should().HaveLength(16);
        hash.Should().MatchRegex("^[0-9a-f]{16}$", "lowercase hex, matching the daemon's short-digest idiom");
    }

    // ── fixtures ──────────────────────────────────────────────────────────────────────────────────

    private static DaemonReviewStageExecutor Executor(ReviewStore store)
    {
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
            .OnArgvContains(
                "diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty));

        // Resumable because these options are S2S: RunReviewAttemptAsync throws when UseS2SReviewAgent is on
        // and the loop exposes no IResumableReviewTurn. Arrangement, not behaviour — see HostRetentionTests.
        return new DaemonReviewStageExecutor(
            store,
            new FakeReviewAgentLoopFactory { Resumable = true },
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions { UseS2SReviewAgent = true, LmStreamingProviderId = ProviderId },
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance);
    }

    private static ReviewRun SeedRun(ReviewStore store, string? promptTemplateHash)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        return store.CreateOrGetReviewRun(SeedFor(repoId, promptTemplateHash));
    }

    private static ReviewRun SeedFor(long repoId, string? promptTemplateHash) => new()
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
