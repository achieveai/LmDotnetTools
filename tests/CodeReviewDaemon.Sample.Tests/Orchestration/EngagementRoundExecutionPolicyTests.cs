using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public sealed class EngagementRoundExecutionPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 2, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Repeated_shadow_observations_reuse_one_pending_close_without_executing_it()
    {
        using var fixture = new Fixture(
            new CodeReviewDaemonOptions
            {
                EnableEngagementCoordinator = true,
                EnableEngagementShadowMode = true,
                EnableEngagementEligibility = true,
            },
            seedComplete: true
        );

        await fixture.Policy.RunAsync(fixture.Decision, null, CancellationToken.None);
        var repeated = await fixture.ObserveMergedAsync();
        await fixture.Policy.RunAsync(repeated, null, CancellationToken.None);

        fixture.Executor.Calls.Should().Be(0);
        repeated.RoundId.Should().Be(fixture.Round.Id);
        fixture.Store.GetEngagementRound(fixture.Round.Id)!.Status.Should().Be(EngagementRoundStatus.Pending);
        fixture.Store.ListEngagementRounds(fixture.Round.PrEngagementId).Should().ContainSingle();
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Any_disabled_rollout_gate_leaves_close_pending(
        bool coordinatorEnabled,
        bool eligibilityEnabled,
        bool seedComplete
    )
    {
        using var fixture = new Fixture(
            new CodeReviewDaemonOptions
            {
                EnableEngagementCoordinator = coordinatorEnabled,
                EnableEngagementEligibility = eligibilityEnabled,
            },
            seedComplete
        );

        await fixture.Policy.RunAsync(fixture.Decision, null, CancellationToken.None);

        fixture.Executor.Calls.Should().Be(0);
        fixture.Store.GetEngagementRound(fixture.Round.Id)!.Status.Should().Be(EngagementRoundStatus.Pending);
    }

    [Fact]
    public async Task All_rollout_gates_execute_the_admitted_close()
    {
        using var fixture = new Fixture(
            new CodeReviewDaemonOptions { EnableEngagementCoordinator = true, EnableEngagementEligibility = true },
            seedComplete: true
        );

        await fixture.Policy.RunAsync(fixture.Decision, null, CancellationToken.None);

        fixture.Executor.Calls.Should().Be(1);
        fixture.Store.GetEngagementRound(fixture.Round.Id)!.Status.Should().Be(EngagementRoundStatus.Completed);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempSqliteDatabase _database = new();
        private readonly FakeTimeProvider _time = new(Start);
        private readonly RepoIdentity _repo;
        private readonly long _repoId;

        public Fixture(CodeReviewDaemonOptions options, bool seedComplete)
        {
            Store = new ReviewStore(_database.ConnectionString, _time);
            _repo = new RepoIdentity
            {
                Provider = "github",
                OrgOrOwner = "achieveai",
                RepoName = "LmDotnetTools",
            };
            _repoId = Store.EnsureRepo(_repo);
            var watermark = new ProviderActivityWatermark("github", Start, "merge:118");
            var engagement = Store.CreateOrGetEngagement(
                new PrEngagement(
                    0,
                    _repoId,
                    "github",
                    "118",
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
                    Start
                )
            );
            Round = Store.TryAdmitRound(
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
            Executor = new RecordingExecutor();
            var runner = new EngagementRoundRunner(
                Store,
                [Executor],
                options,
                _time,
                new LoggerFactory().CreateLogger<EngagementRoundRunner>()
            );
            var seeder = new EngagementCutoverSeeder(Store, [], _time);
            if (seedComplete)
            {
                Store.SaveCursor(
                    new OpaqueCursor
                    {
                        Provider = "review-engagement",
                        Scope = "global-cutover-seed",
                        CursorVersion = 1,
                        CursorPayload = "{\"complete\":true}",
                        HighWaterMark = Start.ToString("O"),
                    }
                );
            }

            Policy = new EngagementRoundExecutionPolicy(options, seeder, runner);
            Decision = new EngagementDecision(EngagementDecisionKind.AdmitMergedClose, Round.Id, "test");
        }

        public ReviewStore Store { get; }
        public EngagementRound Round { get; }
        public RecordingExecutor Executor { get; }
        public EngagementRoundExecutionPolicy Policy { get; }
        public EngagementDecision Decision { get; }

        public Task<EngagementDecision> ObserveMergedAsync()
        {
            var watermark = new ProviderActivityWatermark("github", Start, "merge:118");
            var snapshot = ProviderEngagementSnapshot.Create(
                PrLifecycleState.Merged,
                "head-1",
                "base-1",
                watermark,
                []
            );
            return new PrEngagementCoordinator(Store, _time).ObserveAsync(
                _repoId,
                _repo,
                new PullRequestDescriptor
                {
                    PrId = "118",
                    HeadSha = "head-1",
                    BaseSha = "base-1",
                    TriggerWatermark = watermark.StableObjectId,
                    LifecycleState = PrLifecycleState.Merged,
                },
                snapshot,
                CancellationToken.None
            );
        }

        public void Dispose()
        {
            Store.Dispose();
            _database.Dispose();
        }
    }

    private sealed class RecordingExecutor : IEngagementRoundExecutor
    {
        public EngagementRoundIntent Intent => EngagementRoundIntent.MergedClose;
        public int Calls { get; private set; }

        public Task<EngagementRoundStatus> ExecuteAsync(EngagementRound round, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(EngagementRoundStatus.Completed);
        }
    }
}
