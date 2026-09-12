using System.Text.Json.Nodes;
using AchieveAi.LmDotnetTools.LmWorkflow.Runtime;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
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
