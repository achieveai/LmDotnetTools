using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Scenarios;

/// <summary>
/// Issue #537 — <c>CodeReviewDaemon:MaxPagesPerPoll</c> was declared and documented with <b>zero readers</b>:
/// both PR providers ignored it in favour of a private <c>const int MaxPages = 10</c>, so an operator who set
/// it would have changed nothing. (No shipped profile does set it — the knob was documented, not used.)
/// Measured consequence: ~101 of 711 active PRs enumerated per poll.
/// <para>
/// These tests pin the half a provider-level test cannot: that the operator's configured value actually
/// reaches the provider instance the host registers. The provider tests prove the bound changes how many
/// pages are fetched; deleting the argument from <c>Program.cs</c> leaves every one of them green, because
/// they construct the provider themselves. Only booting the real <c>Program</c> graph here turns that
/// mutation red — which is exactly how the knob came to have zero readers in the first place.
/// </para>
/// </summary>
public sealed class DaemonPrPollingCoverageWiringTests
{
    [Fact]
    public void The_registered_github_provider_carries_the_configured_poll_bounds()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", "25");
            builder.UseSetting("CodeReviewDaemon:MaxPrsPerPage", "40");
        });

        var provider = configured
            .Services.GetServices<IPrProvider>()
            .OfType<GitHubPrProvider>()
            .Should()
            .ContainSingle("GitHub is always registered")
            .Subject;

        provider
            .MaxPagesPerPoll.Should()
            .Be(25, "the operator's CodeReviewDaemon:MaxPagesPerPoll must reach the provider Program.cs builds");
        provider.PageSize.Should().Be(40, "the operator's CodeReviewDaemon:MaxPrsPerPage must reach it too");
    }

    [Fact]
    public void The_registered_ado_provider_carries_the_configured_poll_bounds()
    {
        using var factory = new DaemonWebAppFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CodeReviewDaemon:EnableAdoProvider", "true");
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", "25");
            builder.UseSetting("CodeReviewDaemon:MaxPrsPerPage", "40");
        });

        var provider = configured
            .Services.GetServices<IPrProvider>()
            .OfType<AdoPrProvider>()
            .Should()
            .ContainSingle("EnableAdoProvider registers the ADO provider")
            .Subject;

        provider.MaxPagesPerPoll.Should().Be(25);
        provider.PageSize.Should().Be(40);
    }

    /// <summary>
    /// The default path. With no key configured the provider must land on the documented default rather
    /// than on whatever a provider-local constant happens to say — the two agreeing today is precisely what
    /// let them disagree unnoticed.
    /// </summary>
    [Fact]
    public void An_unconfigured_host_falls_back_to_the_documented_default()
    {
        using var factory = new DaemonWebAppFactory();

        var provider = factory
            .Services.GetServices<IPrProvider>()
            .OfType<GitHubPrProvider>()
            .Should()
            .ContainSingle()
            .Subject;

        provider.MaxPagesPerPoll.Should().Be(CodeReviewDaemonOptions.DefaultMaxPagesPerPoll);
        provider.MaxPagesPerPoll.Should().Be(10, "the documented default is 10 pages per poll");
    }

    /// <summary>
    /// A configured <c>0</c> is neither "fetch nothing" nor "no limit". Zero pages would make every repo
    /// read as permanently empty — indistinguishable from a repo with no open PRs — and unbounded is the
    /// failure the knob exists to prevent, so it degrades to the documented default in both providers.
    /// </summary>
    [Fact]
    public async Task Startup_quarantines_every_unresolved_slot_claim_before_the_pool_can_lease()
    {
        using var database = new TempSqliteDatabase();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "crd-startup-quarantine-" + Guid.NewGuid().ToString("N"));
        var unsafePath = Path.Combine(workspaceRoot, "review-slot-0");
        using (var store = new ReviewStore(database.ConnectionString))
        {
            var repoId = store.EnsureRepo(
                new RepoIdentity
                {
                    Provider = "github",
                    OrgOrOwner = "achieveai",
                    RepoName = "LmDotnetTools",
                }
            );
            var run = store.CreateOrGetReviewRun(
                new ReviewRun
                {
                    RepoId = repoId,
                    PrId = "startup-quarantine",
                    HeadSha = "head",
                    BaseSha = "base",
                    TriggerWatermark = "watermark",
                    ReviewKind = "full",
                    VariantId = "primary",
                    Mode = "collect-only",
                    Stage = ReviewStage.ContextReady,
                    WorkflowStatus = WorkflowStatus.Running,
                    PrLifecycleState = PrLifecycleState.Open,
                }
            );
            _ = store.AppendReviewSlotClaim(run.Id, unsafePath);
            _ = store.AppendReviewSlotClaim(run.Id, Path.Combine(workspaceRoot, "review-slot-1"));
        }

        try
        {
            using var factory = new DaemonWebAppFactory();
            using var host = factory.WithWebHostBuilder(builder =>
            {
                builder.UseSetting("CodeReviewDaemon:DatabasePath", database.Path);
                builder.UseSetting("CodeReviewDaemon:EnableToolAssistedReview", "true");
                builder.UseSetting("CodeReviewDaemon:EnableReviewerWrites", "true");
                builder.UseSetting("CodeReviewDaemon:CrossRepoStoreUrl", "https://example.invalid/review-store.git");
                builder.UseSetting("CodeReviewDaemon:ReviewPoolSize", "1");
                builder.UseSetting("SandboxGateway:WorkspaceBasePath", workspaceRoot);
            });

            var slots = host.Services.GetRequiredService<ReviewSlotWorkspace>();
            var leased = await slots.Pool.LeaseAsync(CancellationToken.None);

            leased.Index.Should().Be(2, "startup must reconstruct every quarantine before the first lease");
            leased.HostPath.Should().Be(Path.Combine(workspaceRoot, "review-slot-2"));
        }
        finally
        {
            try
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Fact]
    public async Task Restart_quarantines_a_host_accepted_unknown_attempt_and_every_other_unresolved_address()
    {
        using var database = new TempSqliteDatabase();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "crd-host-accepted-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        try
        {
            string[] quarantinedPaths;
            using (var processAStore = new ReviewStore(database.ConnectionString))
            {
                var repoId = processAStore.EnsureRepo(
                    new RepoIdentity
                    {
                        Provider = "github",
                        OrgOrOwner = "achieveai",
                        RepoName = "LmDotnetTools",
                    }
                );
                var run = processAStore.CreateOrGetReviewRun(
                    new ReviewRun
                    {
                        RepoId = repoId,
                        PrId = "host-accepted-crash",
                        HeadSha = "head",
                        BaseSha = "base",
                        TriggerWatermark = "watermark",
                        ReviewKind = "full",
                        VariantId = "primary",
                        Mode = "collect-only",
                        Stage = ReviewStage.ContextReady,
                        WorkflowStatus = WorkflowStatus.Running,
                        PrLifecycleState = PrLifecycleState.Open,
                    }
                );

                var processAPool = new ReviewSlotPool(
                    2,
                    workspaceRoot,
                    "scratch",
                    NullLogger<ReviewSlotPool>.Instance,
                    slotDirPrefix: "review-slot-"
                );
                var first = await processAPool.LeaseAsync(CancellationToken.None);
                var second = await processAPool.LeaseAsync(CancellationToken.None);
                var firstClaim = processAStore.AppendReviewSlotClaim(run.Id, first.HostPath);
                var knownAttempt = processAStore.AppendReviewProvisionIntent(firstClaim, run.Id);
                processAStore.AssociateReviewProvisionIntent(knownAttempt, "thread-known");
                _ = processAStore.AppendReviewProvisionIntent(firstClaim, run.Id); // The host accepted, then process A died before association.
                _ = processAStore.AppendReviewSlotClaim(run.Id, second.HostPath);

                quarantinedPaths =
                [
                    .. processAStore.ListUnresolvedReviewSlotClaims().Select(claim => claim.SlotHostPath),
                ];

                var firstClaimAfterCrash = processAStore
                    .ListUnresolvedReviewSlotClaims()
                    .Single(claim => claim.Id == firstClaim);
                firstClaimAfterCrash.Intents.Should().HaveCount(2, "both real provision attempts must survive restart");
                firstClaimAfterCrash.Intents.Should().Contain(intent => intent.ThreadId == "thread-known");
                firstClaimAfterCrash.Intents.Should().Contain(intent => intent.ThreadId == null);
            }

            var unsafeControl = new ReviewSlotPool(
                1,
                workspaceRoot,
                "scratch",
                NullLogger<ReviewSlotPool>.Instance,
                slotDirPrefix: "review-slot-"
            );
            var controlLease = await unsafeControl.LeaseAsync(CancellationToken.None);
            controlLease.Index.Should().Be(0, "without reconstructed quarantine process B would reuse the live mount");
            await unsafeControl.RetireAsync(controlLease, CancellationToken.None);

            var processBPool = new ReviewSlotPool(
                1,
                workspaceRoot,
                "scratch",
                NullLogger<ReviewSlotPool>.Instance,
                slotDirPrefix: "review-slot-",
                quarantinedHostPaths: quarantinedPaths
            );
            var unrelatedRun = await processBPool.LeaseAsync(CancellationToken.None);

            unrelatedRun.Index.Should().Be(2, "the known subset cannot hide a later unknown mount or another claim");
        }
        finally
        {
            try
            {
                Directory.Delete(workspaceRoot, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void A_nonsensical_page_bound_degrades_to_the_bounded_default(string configured)
    {
        using var factory = new DaemonWebAppFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("CodeReviewDaemon:MaxPagesPerPoll", configured)
        );

        var provider = host
            .Services.GetServices<IPrProvider>()
            .OfType<GitHubPrProvider>()
            .Should()
            .ContainSingle()
            .Subject;

        provider
            .MaxPagesPerPoll.Should()
            .Be(
                CodeReviewDaemonOptions.DefaultMaxPagesPerPoll,
                "a value that cannot be a page count is treated as unset, never as zero pages and never as unbounded"
            );
    }
}
