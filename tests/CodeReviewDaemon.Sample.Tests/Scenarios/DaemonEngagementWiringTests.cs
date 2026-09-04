using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

public sealed class DaemonEngagementWiringTests
{
    [Fact]
    public void Enabled_merged_close_extraction_is_resolvable_before_the_executor_is_created()
    {
        using var factory = new DaemonWebAppFactory(
            settings: new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:EnableToolAssistedReview"] = "true",
                ["CodeReviewDaemon:EnableReviewerWrites"] = "true",
                ["CodeReviewDaemon:ReviewBotRepoUrl"] = "https://example.invalid/reviews.git",
                ["CodeReviewDaemon:EnableKnowledgeAgent"] = "true",
            }
        );

        factory.Services.GetRequiredService<MergedCloseKnowledgeExtraction>().Should().NotBeNull();
        factory
            .Services.GetServices<IEngagementRoundExecutor>()
            .Should()
            .ContainSingle(executor => executor is MergedCloseRoundExecutor);
    }

    [Fact]
    public async Task Enabled_merged_close_does_not_checkpoint_or_archive_unimplemented_required_outputs()
    {
        using var factory = new DaemonWebAppFactory(
            settings: new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:EnableToolAssistedReview"] = "true",
                ["CodeReviewDaemon:EnableReviewerWrites"] = "true",
                ["CodeReviewDaemon:ReviewBotRepoUrl"] = "https://example.invalid/reviews.git",
                ["CodeReviewDaemon:EnableKnowledgeAgent"] = "true",
                ["CodeReviewDaemon:MergeNotesBranchOnClose"] = "false",
            }
        );
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var round = SeedMergedCloseRound(store, "118");
        var executor = factory
            .Services.GetServices<IEngagementRoundExecutor>()
            .OfType<MergedCloseRoundExecutor>()
            .Single();

        var status = await executor.ExecuteAsync(round, CancellationToken.None);

        status.Should().Be(EngagementRoundStatus.RetryPending);
        store
            .ListAuditRecordsForRound(round.Id)
            .Should()
            .NotContain(record => record.RecordType == MergedCloseRoundExecutor.SubstepRecordType);
    }

    [Fact]
    public async Task Disabled_merged_close_executor_preserves_pending_work_without_charging_failures()
    {
        using var factory = new DaemonWebAppFactory(
            settings: new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:EnableToolAssistedReview"] = "true",
                ["CodeReviewDaemon:EnableReviewerWrites"] = "true",
                ["CodeReviewDaemon:ReviewBotRepoUrl"] = "https://example.invalid/reviews.git",
                ["CodeReviewDaemon:EnableKnowledgeAgent"] = "false",
                ["CodeReviewDaemon:MaxDurableRetryAttempts"] = "2",
            }
        );
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var round = SeedMergedCloseRound(store, "unavailable-close");
        var executor = factory
            .Services.GetServices<IEngagementRoundExecutor>()
            .Single(candidate => candidate.Intent == EngagementRoundIntent.MergedClose);
        var runner = factory.Services.GetRequiredService<EngagementRoundRunner>();
        var decision = new EngagementDecision(EngagementDecisionKind.AdmitMergedClose, round.Id, "test");

        executor.Should().BeOfType<DisabledMergedCloseRoundExecutor>();
        executor.IsAvailable.Should().BeFalse();
        await runner.RunAsync(decision, null, CancellationToken.None);
        await runner.RunAsync(decision, null, CancellationToken.None);

        var pending = store.GetEngagementRound(round.Id)!;
        pending.Status.Should().Be(EngagementRoundStatus.Pending);
        pending.GovernedFailureCount.Should().Be(0);
        pending.ParkedAt.Should().BeNull();
        store.ListEngagementRounds(pending.PrEngagementId).Should().ContainSingle();
    }

    [Fact]
    public async Task Production_execution_policy_honors_rollout_gates_for_merged_close_rounds()
    {
        using var factory = new DaemonWebAppFactory(
            settings: new Dictionary<string, string?>
            {
                ["CodeReviewDaemon:EnableEngagementCoordinator"] = "true",
                ["CodeReviewDaemon:EnableEngagementEligibility"] = "true",
                ["CodeReviewDaemon:EnableEngagementShadowMode"] = "true",
            }
        );
        var store = factory.Services.GetRequiredService<ReviewStore>();
        var round = SeedMergedCloseRound(store, "shadow-policy");
        var policy = factory.Services.GetRequiredService<EngagementRoundExecutionPolicy>();

        await policy.RunAsync(
            new EngagementDecision(EngagementDecisionKind.AdmitMergedClose, round.Id, "test"),
            null,
            CancellationToken.None
        );

        store.GetEngagementRound(round.Id)!.Status.Should().Be(EngagementRoundStatus.Pending);
        store.ListAuditRecordsForRound(round.Id).Should().BeEmpty();
    }

    [Fact]
    public void Default_host_registers_the_coordinator_seed_and_code_review_executor_with_rollout_off()
    {
        using var factory = new DaemonWebAppFactory();

        var options = factory.Services.GetRequiredService<CodeReviewDaemonOptions>();
        options.EnableEngagementCoordinator.Should().BeFalse();
        options.EnableEngagementEligibility.Should().BeFalse();
        options.EnableEngagementShadowMode.Should().BeFalse();
        factory.Services.GetRequiredService<PrEngagementCoordinator>().Should().NotBeNull();
        factory.Services.GetRequiredService<EngagementCutoverSeeder>().Should().NotBeNull();
        factory.Services.GetRequiredService<EngagementRoundRunner>().Should().NotBeNull();
        factory.Services.GetRequiredService<EngagementRoundExecutionPolicy>().Should().NotBeNull();
        var executors = factory.Services.GetServices<IEngagementRoundExecutor>().ToArray();
        executors.Should().ContainSingle(executor => executor.Intent == EngagementRoundIntent.CodeReview);
        var discussion = executors
            .Should()
            .ContainSingle(executor => executor.Intent == EngagementRoundIntent.DiscussionFollowUp)
            .Subject;
        discussion.Should().BeOfType<DisabledDiscussionRoundExecutor>();
        discussion.IsAvailable.Should().BeFalse();
        factory
            .Services.GetRequiredService<IRoundObservationSink>()
            .Should()
            .BeOfType<ReviewStoreRoundObservationSink>();
    }

    private static EngagementRound SeedMergedCloseRound(ReviewStore store, string prId)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            }
        );
        var watermark = new ProviderActivityWatermark("github", DateTimeOffset.UtcNow, $"merge:{prId}");
        var engagement = store.CreateOrGetEngagement(
            new PrEngagement(
                0,
                repoId,
                "github",
                prId,
                PrLifecycleState.Merged,
                "head-1",
                "base-1",
                "head-1",
                watermark,
                watermark,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow
            )
        );
        return store.TryAdmitRound(
            new EngagementRound(
                0,
                engagement.Id,
                EngagementRoundIntent.MergedClose,
                EngagementRoundStatus.Pending,
                "head-1",
                "base-1",
                watermark,
                watermark,
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
    }
}
