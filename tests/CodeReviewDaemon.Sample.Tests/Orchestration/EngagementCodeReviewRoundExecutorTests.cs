using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class EngagementCodeReviewRoundExecutorTests
{
    [Fact]
    public async Task Runs_the_review_linked_to_the_admitted_code_round()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                "118",
                PrLifecycleState.Open,
                "head-1",
                "base-1",
                null,
                new ProviderActivityWatermark("github", DateTimeOffset.UnixEpoch, "poll:1"),
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UnixEpoch
            )
        );
        var round = store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.CodeReview,
                EngagementRoundStatus.Pending,
                "head-1",
                "base-1",
                null,
                engagement.LatestActivity,
                0,
                null,
                0,
                null,
                null,
                null,
                null,
                null
            )
        )!;
        var run = store.CreateOrGetReviewRun(
            new ReviewRun
            {
                RepoId = repoId,
                PrId = "118",
                HeadSha = "head-1",
                BaseSha = "base-1",
                TriggerWatermark = "poll:1",
                ReviewKind = "full",
                VariantId = "primary",
                Mode = "collect-only",
                EngagementRoundId = round.Id,
                Stage = ReviewStage.Discovered,
                WorkflowStatus = WorkflowStatus.Pending,
                PrLifecycleState = PrLifecycleState.Open,
            }
        );
        var orchestrator = new PrOrchestrator(store, new RecordingStageExecutor(), NullLogger<PrOrchestrator>.Instance);
        var executor = new EngagementCodeReviewRoundExecutor(store, orchestrator);

        var result = await executor.ExecuteAsync(store.GetEngagementRound(round.Id)!, CancellationToken.None);

        result.Should().Be(EngagementRoundStatus.Completed);
        store.GetReviewRun(run.Id)!.Stage.Should().Be(ReviewStage.Posted);
    }
}
