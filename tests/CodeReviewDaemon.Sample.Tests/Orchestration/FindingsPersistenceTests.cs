using System.Text.Json;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// #543 — the round's findings must outlive the process that produced them.
/// <para>
/// <see cref="ReviewNotesArtifactBuilderTests"/> pins the SHAPE of the record; nothing there proves the
/// record is ever handed to the store, and a payload that is built perfectly and then dropped on the floor
/// looks identical in those tests. These drive the real
/// <see cref="DaemonReviewStageExecutor.ExecuteStageAsync"/> lifecycle through to the terminal stage against
/// the production <see cref="ReviewStore"/> on a real on-disk SQLite file, then CLOSE that store and open a
/// second one on the same file — the reader is a different connection with a different migration run, which
/// is what a daemon restart actually is. A findings row that only exists in the writing process's memory
/// cannot survive that.
/// </para>
/// </summary>
public sealed class FindingsPersistenceTests
{
    private const string ReviewBotRepoUrl = "https://github.com/acme/AchieveAiReviews.git";
    private const string SpecialistAgentId = "agent-architecture";
    private const string FindingLocation = "src/Workflow/WorkflowDistributedTasksModule.cs:42";

    /// <summary>What the specialist reviewer said, read back out of its transcript.</summary>
    private const string SpecialistTranscript =
        "#### [BLOCKER] High — DI coupling\n"
        + $"{FindingLocation} resolves the module from the container directly.\n"
        + "\n"
        + "#### [MEDIUM] — unchecked cast\n"
        + "src/Foo.cs:10 casts without a test.\n";

    /// <summary>The review that actually shipped — the text every finding is reconciled against.</summary>
    private const string ShippedReview =
        "## Review\n"
        + "#### [BLOCKER] High — DI coupling\n"
        + $"{FindingLocation} must be injected.\n"
        + "\n"
        + "#### [MEDIUM] — unchecked cast\n"
        + "src/Foo.cs:10 must be guarded.\n";

    [Fact]
    public async Task Findings_from_a_production_review_are_readable_after_the_store_is_reopened()
    {
        using var db = new TempSqliteDatabase();

        long runId;
        using (var writingStore = new ReviewStore(db.ConnectionString))
        {
            var run = SeedRun(writingStore);
            runId = run.Id;
            await RunReviewToPostedAsync(writingStore, run);

            // Sanity, in the writing process: the row exists at all. Asserted here and not only after the
            // reopen so a failure tells us WHICH half broke — never written, or written and not durable.
            ReadFindings(writingStore, runId).Should().NotBeNull(
                "the Posted stage writes the findings artifact through the production path");
        }

        // The restart. The writing store's connection is closed; this is a fresh handle over the same file,
        // re-running the migration chain exactly as a restarted daemon does.
        using var reopened = new ReviewStore(db.ConnectionString);
        var findings = ReadFindings(reopened, runId);

        findings.Should().NotBeNull("the findings artifact must survive the process that wrote it");
        findings!.Compared.Should().BeTrue("the shipped review body was available to reconcile against");
        findings.RecordedCount.Should().Be(2, "both of the specialist's findings reached the record");
        findings.ParsedCount.Should().Be(2);
        findings.Shortfall.Should().Be(0);

        // And the rows are still TYPED after the round trip through SQLite — the whole point of storing
        // data rather than prose. A row that reloads with an empty severity-token list is a row no query
        // can bucket, which is indistinguishable from not having stored it.
        var blocker = findings.Findings.Should().ContainSingle(f => f.Location == FindingLocation).Subject;
        blocker.Source.Should().Be("architecture");
        blocker.SeverityTokens.Should().Contain("Blocker");
        blocker.Outcome.Should().Be("kept");
        findings.DerivedFrom.Should().Be("reviewer-transcripts-via-reconciler");
    }

    [Fact]
    public async Task The_stored_findings_row_is_its_own_artifact_kind_at_its_own_schema_version()
    {
        // The row has to be FINDABLE, which means the kind and version are part of the contract and not
        // incidental. A findings payload appended under the 'review' kind would be picked up by the judge
        // and the eval corpus as a second review of the same run.
        using var db = new TempSqliteDatabase();
        using var store = new ReviewStore(db.ConnectionString);
        var run = SeedRun(store);

        await RunReviewToPostedAsync(store, run);

        var artifact = store
            .GetArtifacts(run.Id)
            .Should()
            .ContainSingle(a => a.ArtifactKind == DaemonReviewStageExecutor.FindingsArtifactKind)
            .Subject;

        artifact.ArtifactKind.Should().Be("review-findings");
        artifact.ArtifactSchemaVersion.Should().Be(DaemonReviewStageExecutor.FindingsArtifactSchemaVersion);
        artifact.Provider.Should().Be("github", "the row is attributed to the provider the review shipped on");
        artifact.Payload.Should().Contain(FindingLocation, "the payload is the findings, not a placeholder");
    }

    /// <summary>
    /// Drives the whole stage machine — the same entry point the orchestrator calls — against a host
    /// retention workspace, which is the branch that reaches <c>PublishToReviewBotAsync</c> and therefore
    /// the notes/findings gate.
    /// </summary>
    private static async Task RunReviewToPostedAsync(ReviewStore store, ReviewRun run)
    {
        var sandbox = new FakeSandboxCommandRunner()
            .OnArgvContains("rev-parse --is-inside-work-tree", new SandboxCommandResult(1, string.Empty, "not a git repo"))
            .OnArgvContains("diff", new SandboxCommandResult(0, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;", string.Empty));
        var host = new FakeSandboxCommandRunner()
            .OnArgvContains(
                "rev-parse review/lmdotnettools-118",
                new SandboxCommandResult(0, "f00dcafef00dcafe\n", string.Empty));
        var hostFileSystem = new FakeSandboxFileSystem()
            .Seed("/host/reviewbot/README.md", "# ReviewBot")
            .Seed("/host/reviewbot/PRs/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/.gitkeep", string.Empty)
            .Seed("/host/reviewbot/KnowledgeBase/_toc.md", "# Knowledge Base");

        var factory = new FakeReviewAgentLoopFactory { DefaultText = ShippedReview };
        var executor = new DaemonReviewStageExecutor(
            store,
            factory,
            sandbox,
            new FakeSandboxFileSystem(),
            new CodeReviewDaemonOptions { ReviewBotRepoUrl = ReviewBotRepoUrl },
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            hostRetention: new HostRetentionWorkspace(host, hostFileSystem, "/host/reviewbot"),
            completionSource: new ScriptedRoster(SpecialistNode()),
            transcriptSource: new ScriptedTranscripts(SpecialistTranscript));

        await executor.ExecuteStageAsync(ReviewStage.ContextReady, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Judged, run, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Posted, run, CancellationToken.None);
    }

    /// <summary>The stored findings payload for <paramref name="runId"/>, or null when no row was written.</summary>
    private static ReviewFindingsArtifactPayload? ReadFindings(ReviewStore store, long runId)
    {
        var artifact = store
            .GetArtifacts(runId)
            .SingleOrDefault(a => a.ArtifactKind == DaemonReviewStageExecutor.FindingsArtifactKind);
        return artifact is null
            ? null
            : JsonSerializer.Deserialize<ReviewFindingsArtifactPayload>(artifact.Payload);
    }

    private static ReviewSubAgentNode SpecialistNode() =>
        new()
        {
            AgentId = SpecialistAgentId,
            ThreadId = $"thread-{SpecialistAgentId}",
            ParentThreadId = "root",
            Depth = 1,
            Status = ReviewSubAgentStatus.Completed,
            Name = "architecture",
            Template = "reviewer",
        };

    private static ReviewRun SeedRun(ReviewStore store)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        return store.CreateOrGetReviewRun(new ReviewRun
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
        });
    }

    /// <summary>A settled roster of exactly the scripted nodes, returned on every poll.</summary>
    private sealed class ScriptedRoster(params ReviewSubAgentNode[] nodes) : IReviewSubAgentCompletionSource
    {
        private readonly ReviewSubAgentTreeSnapshot _snapshot = new(nodes);

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run,
            string parentThreadId,
            CancellationToken ct) => Task.FromResult(_snapshot);
    }

    /// <summary>Hands every sub-agent the same scripted transcript; the lead's root transcript is empty.</summary>
    private sealed class ScriptedTranscripts(string body) : IReviewAgentTranscriptSource
    {
        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetTranscriptAsync(
            string rootThreadId,
            string agentId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReviewAgentTranscriptEntry>>(
                [new("TextMessage", "assistant", FromAgent: null, TimestampUtc: null, Body: body)]);

        public Task<IReadOnlyList<ReviewAgentTranscriptEntry>> GetRootTranscriptAsync(
            string rootThreadId,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReviewAgentTranscriptEntry>>([]);
    }
}
