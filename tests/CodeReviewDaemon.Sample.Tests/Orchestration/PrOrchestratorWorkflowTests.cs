using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class PrOrchestratorWorkflowTests
{
    [Fact]
    public async Task Determinate_failed_workflow_attempts_consume_the_durable_budget_and_park()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var seed = Seed(store);
        var runner = new StatusRunner(WorkflowInvocationStatus.Failed);
        var orchestrator = new PrOrchestrator(
            store,
            runner,
            NullLogger<PrOrchestrator>.Instance,
            maxDurableRetryAttempts: 2
        );

        var first = await orchestrator.RunAsync(seed, [], default);
        var second = await orchestrator.ReconcileAsync(first, default);

        first.GovernedFailureCount.Should().Be(1);
        first.ParkedAt.Should().BeNull();
        second.GovernedFailureCount.Should().Be(2);
        second.ParkedAt.Should().NotBeNull();
        second.WorkflowStatus.Should().Be(WorkflowStatus.Failed);
        runner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Completed_cleanup_receipt_does_not_start_another_generation()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var seed = Seed(store);
        var run = store.CreateOrGetReviewRun(seed);
        store.UpdateReviewRunState(run.Id, ReviewStage.Posted, WorkflowStatus.Completed, PrLifecycleState.Open);
        store.AddArtifact(
            new ReviewArtifact
            {
                ReviewRunId = run.Id,
                ArtifactKind = "workflow-attempt-reset",
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = new JsonObject { ["status"] = "reset" }.ToJsonString(),
            }
        );
        var runner = new StatusRunner(WorkflowInvocationStatus.Unknown);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var repeat = await orchestrator.RunAsync(seed, [], default);

        repeat.Id.Should().Be(run.Id);
        repeat.Generation.Should().Be(0);
        repeat.WorkflowStatus.Should().Be(WorkflowStatus.Completed);
        runner.Attempts.Should().Be(0);
    }

    [Fact]
    public async Task Unknown_workflow_attempt_stays_reconcilable_without_consuming_the_failure_budget()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var seed = Seed(store);
        var runner = new StatusRunner(WorkflowInvocationStatus.Unknown);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);

        var result = await orchestrator.RunAsync(seed, [], default);

        result.WorkflowStatus.Should().Be(WorkflowStatus.RetryPending);
        result.GovernedFailureCount.Should().Be(0);
        result.ParkedAt.Should().BeNull();
    }

    [Fact]
    public async Task Accepted_reset_starts_one_fresh_generation_and_repeat_poll_preserves_original_failure()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var seed = Seed(store);
        var prior = store.CreateOrGetReviewRun(seed);
        store.UpdateReviewRunState(prior.Id, ReviewStage.Discovered, WorkflowStatus.Failed, PrLifecycleState.Open);
        var owner = $"review-run-{prior.Id}";
        store.AddArtifact(
            new()
            {
                ReviewRunId = prior.Id,
                ArtifactKind = WorkflowWorkspace.AssignmentArtifactKind,
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = System.Text.Json.JsonSerializer.Serialize(
                    new WorkflowWorkspaceAssignment(new ReviewSlot("widgets", 0), false, "assignment-1", owner)
                ),
            }
        );
        var reset = new JsonObject
        {
            ["status"] = "reset",
            ["identity"] = new JsonObject
            {
                ["runId"] = prior.Id,
                ["assignmentId"] = "assignment-1",
                ["workflowInstanceId"] = owner,
                ["head"] = prior.HeadSha,
                ["base"] = prior.BaseSha,
            },
        };
        store.AddArtifact(
            new()
            {
                ReviewRunId = prior.Id,
                ArtifactKind = "workflow-attempt-reset",
                ArtifactSchemaVersion = 1,
                Provider = "workflow",
                Payload = reset.ToJsonString(),
            }
        );
        var runner = new StatusRunner(WorkflowInvocationStatus.Unknown);
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);
        var fresh = await orchestrator.RunAsync(seed, [], default);
        fresh.Id.Should().NotBe(prior.Id);
        fresh.Generation.Should().Be(prior.Generation + 1);
        fresh.TriggerWatermark.Should().Be($"fresh:{prior.Id}");
        fresh.VariantId.Should().Be(prior.VariantId);
        store.GetReviewRun(prior.Id)!.WorkflowStatus.Should().Be(WorkflowStatus.Failed);
        store.TryGetLatestArtifact(prior.Id, "workflow-attempt-reset")!.Payload.Should().Be(reset.ToJsonString());
        var repeated = await orchestrator.RunAsync(seed, [], default);
        repeated.Id.Should().Be(fresh.Id);
        repeated.Generation.Should().Be(fresh.Generation);
        runner.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task Same_pr_different_head_waits_while_an_independent_pr_runs()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var seed = Seed(store);
        var runner = new BlockingRunner();
        var orchestrator = new PrOrchestrator(store, runner, NullLogger<PrOrchestrator>.Instance);
        var first = orchestrator.RunAsync(seed, [], default);
        await runner.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var second = orchestrator.RunAsync(seed with { HeadSha = "next-head" }, [], default);
        try
        {
            await orchestrator
                .RunAsync(seed with { PrId = "8", HeadSha = "independent" }, [], default)
                .WaitAsync(TimeSpan.FromSeconds(3));
            second.IsCompleted.Should().BeFalse();
            runner.Seen.Should().NotContain(run => run.PrId == seed.PrId && run.HeadSha == "next-head");
            runner.Seen.Should().Contain(run => run.PrId == "8");
        }
        finally
        {
            runner.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        }
        runner.Seen.Where(run => run.PrId == seed.PrId).Select(run => run.Id).Distinct().Should().HaveCount(2);
        runner.Seen.Where(run => run.PrId == seed.PrId).Select(run => run.HeadSha).Should().Equal("head", "next-head");
    }

    [Fact]
    public async Task Pending_owner_recovery_rotates_one_attempt_per_cycle_and_backs_off_failures()
    {
        using var database = new TempSqliteDatabase();
        using var store = new ReviewStore(database.ConnectionString);
        var owners = Enumerable
            .Range(7, 3)
            .Select(pr => store.CreateOrGetReviewRun(Seed(store) with { PrId = pr.ToString() }))
            .ToArray();
        foreach (var run in owners)
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = WorkflowWorkspace.AssignmentArtifactKind,
                    ArtifactSchemaVersion = 1,
                    Provider = "workflow",
                    Payload = System.Text.Json.JsonSerializer.Serialize(
                        new WorkflowWorkspaceAssignment(
                            new ReviewSlot("widgets", (int)run.Id - 1),
                            true,
                            $"assignment-{run.Id}",
                            $"review-run-{run.Id}"
                        )
                    ),
                }
            );
        var now = DateTimeOffset.UtcNow;
        var governor = new RetryGovernor(
            10,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(10),
            () => now,
            NullLogger<RetryGovernor>.Instance
        );
        var runner = new StatusRunner(WorkflowInvocationStatus.Unknown);
        var seen = new List<long>();
        var throwOnRecovery = false;
        runner.RecoverOwner = run =>
        {
            seen.Add(run.Id);
            if (throwOnRecovery)
                throw new InvalidDataException("Recovery boundary failed.");
            store.AddArtifact(
                new ReviewArtifact
                {
                    ReviewRunId = run.Id,
                    ArtifactKind = "workflow-recovery-pending",
                    ArtifactSchemaVersion = 1,
                    Provider = "workflow",
                    Payload = "{}",
                }
            );
        };
        var orchestrator = new PrOrchestrator(
            store,
            runner,
            NullLogger<PrOrchestrator>.Instance,
            retryGovernor: governor
        );

        await orchestrator.RecoverPendingOwnersAsync(default);
        seen.Should().Equal(owners[0].Id);
        governor.ShouldAttempt(owners[0].Id).Should().BeFalse();
        await orchestrator.RecoverPendingOwnersAsync(default);
        await orchestrator.RecoverPendingOwnersAsync(default);
        seen.Should().Equal(owners.Select(run => run.Id));
        await orchestrator.RecoverPendingOwnersAsync(default);
        seen.Should().HaveCount(3);
        now += TimeSpan.FromMinutes(1);
        throwOnRecovery = true;
        await orchestrator.RecoverPendingOwnersAsync(default);
        seen.Should().Equal(owners.Select(run => run.Id).Append(owners[0].Id));
        throwOnRecovery = false;
        await orchestrator.RecoverPendingOwnersAsync(default);
        seen.Should().Equal(owners.Select(run => run.Id).Concat(owners.Take(2).Select(run => run.Id)));
        store.ListActiveWorkflowWorkspaceRuns().Should().HaveCount(3);
        runner.Attempts.Should().Be(0);
    }

    private sealed class BlockingRunner : IReviewWorkflowRunner
    {
        public List<ReviewRun> Seen { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject context,
            CancellationToken ct
        )
        {
            Seen.Add(run);
            if (run.PrId == "7" && run.HeadSha == "head")
            {
                Entered.TrySetResult();
                await Release.Task.WaitAsync(ct);
            }
            return WorkflowInvocationStatus.Unknown;
        }

        public Task<WorkflowInvocationStatus> RunOrResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject context,
            CancellationToken ct
        ) => RunAsync(run, round, context, ct);

        public Task<WorkflowInvocationStatus> ResumeAsync(ReviewRun run, WorkflowRound? round, CancellationToken ct) =>
            throw new InvalidOperationException("Unexpected resume");

        public Task<JsonObject> ReadFrozenContextAsync(ReviewRun run, WorkflowRound? round, CancellationToken ct) =>
            Task.FromResult(new JsonObject());

        public Task RecoverActiveWorkspacesAsync(CancellationToken ct) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => true;
    }

    private static ReviewRun Seed(ReviewStore store)
    {
        var repoId = store.EnsureRepo(
            new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "example",
                RepoName = "widgets",
                RepoStableId = "repo-1",
            }
        );
        return new ReviewRun
        {
            RepoId = repoId,
            PrId = "7",
            HeadSha = "head",
            BaseSha = "base",
            TriggerWatermark = "window",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Pending,
            PrLifecycleState = PrLifecycleState.Open,
        };
    }

    private sealed class StatusRunner(WorkflowInvocationStatus status) : IReviewWorkflowRunner
    {
        public int Attempts { get; private set; }
        public Action<ReviewRun>? RecoverOwner { get; set; }

        public Task RecoverActiveOwnerAsync(ReviewRun run, CancellationToken ct)
        {
            RecoverOwner?.Invoke(run);
            return Task.CompletedTask;
        }

        public Task<WorkflowInvocationStatus> RunAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject frozenContext,
            CancellationToken cancellationToken
        )
        {
            Attempts++;
            return Task.FromResult(status);
        }

        public Task<WorkflowInvocationStatus> ResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        )
        {
            Attempts++;
            return Task.FromResult(status);
        }

        public Task<WorkflowInvocationStatus> RunOrResumeAsync(
            ReviewRun run,
            WorkflowRound? round,
            JsonObject currentContext,
            CancellationToken cancellationToken
        ) => RunAsync(run, round, currentContext, cancellationToken);

        public Task<JsonObject> ReadFrozenContextAsync(
            ReviewRun run,
            WorkflowRound? round,
            CancellationToken cancellationToken
        ) => Task.FromResult(new JsonObject());

        public Task RecoverActiveWorkspacesAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public bool HasFrozenContext(ReviewRun run, WorkflowRound? round) => true;
    }
}
