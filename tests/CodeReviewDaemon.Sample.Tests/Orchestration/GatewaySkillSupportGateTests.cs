using System.Text.Json;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace.Sandbox;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// <c>RequireSkillSupport</c> on the S2S path. Revobot must not review without the skills/agents it reviews
/// WITH: a review that runs anyway does not fail loudly — it quietly posts a shallow review that LOOKS like a
/// real one, which is worse than no review because a human trusts it.
///
/// The in-process path asserts this against the daemon's OWN sandbox session. On S2S there is no such session
/// (the review runs inside a conversation the review host provisions), so the equivalent check reads the
/// gateway's marketplace catalog. These tests pin the three outcomes that distinction creates — supported ⇒
/// proceed, unsupported ⇒ stop the daemon, UNREADABLE ⇒ warn and retry next run — plus the gating that keeps
/// the in-process path unchanged.
/// </summary>
public sealed class GatewaySkillSupportGateTests
{
    [Fact]
    public async Task S2S_GatewayLacksTheReviewSkill_AbortsTheRunAndStopsTheDaemon()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(
            HasReviewSkill: false, ReviewerAgentCount: 16, MarketplaceErrors: []));
        var lifetime = new RecordingHostLifetime();
        var executor = BuildExecutor(store, factory, S2SOptions(), probe, lifetime);
        var run = SeedRunWithContext(store, prId: "118");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Aborting the RUN is only half of it — a daemon that kept polling would fail every subsequent PR the
        // same way, so the operator has to be told to fix the marketplace rather than left with a churn of
        // empty runs.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("RequireSkillSupport").And.Contain("MISSING");
        lifetime.StopCalls.Should().Be(1);
        factory.ToolContexts.Should().BeEmpty("no review loop may be built once the prerequisite check fails");
        store.GetArtifacts(run.Id)
            .Should().NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task S2S_GatewayHasTheSkillButNoReviewerSubAgents_AlsoAborts()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(
            HasReviewSkill: true, ReviewerAgentCount: 0, MarketplaceErrors: []));
        var lifetime = new RecordingHostLifetime();
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), S2SOptions(), probe, lifetime);
        var run = SeedRunWithContext(store, prId: "118");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The mirror of the case above: the review procedure is there but there is nothing to dispatch the
        // deep passes to, so the review collapses to one agent reading a diff.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("sub-agents=0");
        lifetime.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task S2S_MarketplaceLoadError_IsCarriedIntoTheAbortMessage()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(
            HasReviewSkill: false,
            ReviewerAgentCount: 0,
            MarketplaceErrors: ["gb-plugins: clone failed: authentication required"]));
        var executor = BuildExecutor(
            store, new FakeReviewAgentLoopFactory(), S2SOptions(), probe, new RecordingHostLifetime());
        var run = SeedRunWithContext(store, prId: "118");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // "The plugin is not installed" and "the marketplace would not clone" demand completely different
        // fixes, so the gateway's own reason has to reach the operator rather than being flattened into the
        // former.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("authentication required");
    }

    [Fact]
    public async Task S2S_GatewaySupported_ReviewProceeds_AndTheCatalogIsProbedOnlyOnce()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(
            HasReviewSkill: true, ReviewerAgentCount: 16, MarketplaceErrors: []));
        var lifetime = new RecordingHostLifetime();
        var executor = BuildExecutor(store, factory, S2SOptions(), probe, lifetime);
        var first = SeedRunWithContext(store, prId: "118");
        var second = SeedRunWithContext(store, prId: "119");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, first, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, second, CancellationToken.None);

        lifetime.StopCalls.Should().Be(0);
        store.GetArtifacts(first.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
        factory.ToolContexts.Should().HaveCount(2).And.OnlyContain(c => c == null,
            "S2S builds no daemon-side tool context — the tools live in the hosted conversation");
        // The catalog is process-lifetime gateway configuration, so re-reading it per review would add a
        // blocking gateway round-trip to every run for an answer that cannot have changed.
        probe.Calls.Should().Be(1);
    }

    [Fact]
    public async Task S2S_CatalogUnreadable_WarnsAndProceeds_AndRetriesOnTheNextRun()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var probe = new ThrowingGatewaySkillProbe();
        var lifetime = new RecordingHostLifetime();
        var executor = BuildExecutor(store, factory, S2SOptions(), probe, lifetime);
        var first = SeedRunWithContext(store, prId: "118");
        var second = SeedRunWithContext(store, prId: "119");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, first, CancellationToken.None);
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, second, CancellationToken.None);

        // An unreachable gateway is a DIFFERENT finding from an unsupported one. Reporting it as "skills
        // absent" would stop the daemon over a momentary blip and demand a marketplace fix that was never
        // wrong — and a genuinely down gateway fails the run loudly anyway, when the review host tries to
        // provision its session. So: no stop, and the verdict stays uncached so the next run re-probes.
        lifetime.StopCalls.Should().Be(0);
        probe.Calls.Should().Be(2, "a read failure must not be cached as a verdict");
        store.GetArtifacts(first.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task S2S_ProbeCancelled_Propagates_RatherThanBeingTreatedAsAnUnreadableCatalog()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var probe = new CancelingGatewaySkillProbe();
        var executor = BuildExecutor(store, factory, S2SOptions(), probe, new RecordingHostLifetime());
        var run = SeedRunWithContext(store, prId: "118");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Shutdown is not a capability gap: swallowing it as "catalog unverified, carry on" would run a whole
        // review during a cancel.
        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.ToolContexts.Should().BeEmpty();
    }

    [Fact]
    public async Task S2S_RequireSkillSupportOff_NeverConsultsTheGateway()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(false, 0, []));
        var executor = BuildExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            S2SOptions(requireSkillSupport: false),
            probe,
            new RecordingHostLifetime());
        var run = SeedRunWithContext(store, prId: "118");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // One knob, one meaning on both paths: off ⇒ degrade quietly, and the daemon does not pay for a probe
        // whose answer it would ignore.
        probe.Calls.Should().Be(0);
    }

    [Fact]
    public async Task S2S_ToolAssistedReviewOff_StillEnforcesTheGatewayPrerequisite()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(false, 0, []));
        var lifetime = new RecordingHostLifetime();
        var executor = BuildExecutor(
            store,
            new FakeReviewAgentLoopFactory(),
            S2SOptions(enableToolAssistedReview: false),
            probe,
            lifetime);
        var run = SeedRunWithContext(store, prId: "118");

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // Regression guard for the guard ORDER. On S2S, EnableToolAssistedReview governs daemon-side
        // provisioning this path does not use, so if the prerequisite check sat below that guard, turning an
        // unrelated flag off would silently switch the fail-fast off too.
        await act.Should().ThrowAsync<InvalidOperationException>();
        probe.Calls.Should().Be(1);
        lifetime.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task InProcessPath_NeverConsultsTheGatewayProbe()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var probe = new FakeGatewaySkillProbe(new GatewaySkillSupport(false, 0, []));
        var lifetime = new RecordingHostLifetime();
        var options = new CodeReviewDaemonOptions { RequireSkillSupport = true };
        var executor = BuildExecutor(store, new FakeReviewAgentLoopFactory(), options, probe, lifetime);
        var run = SeedRunWithContext(store, prId: "118");

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The in-process path enforces the same flag against its own session, not the catalog. Probing here
        // would double-enforce and could stop the daemon over a marketplace the in-process review never uses.
        probe.Calls.Should().Be(0);
        lifetime.StopCalls.Should().Be(0);
    }

    private static CodeReviewDaemonOptions S2SOptions(
        bool requireSkillSupport = true, bool enableToolAssistedReview = true) =>
        new()
        {
            UseS2SReviewAgent = true,
            LmStreamingBaseUrl = "http://localhost:5051",
            RequireSkillSupport = requireSkillSupport,
            EnableToolAssistedReview = enableToolAssistedReview,
            SubAgentMarketplaces = ["gb-plugins"],
        };

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        CodeReviewDaemonOptions options,
        IGatewaySkillProbe probe,
        IHostApplicationLifetime lifetime)
    {
        // Only the HOSTED path's turns are durable, and the executor refuses an S2S review whose loop cannot
        // checkpoint them — so the double is resumable on exactly the path production is, and no other.
        factory.Resumable = options.UseS2SReviewAgent;
        return new DaemonReviewStageExecutor(
            store,
            factory,
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            appLifetime: lifetime,
            skillProbe: probe);
    }

    /// <summary>
    /// Seeds a run plus the 'review-context' artifact the Reviewed stage reads, so a test can drive that stage
    /// directly without first running ContextReady.
    /// </summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store, string prId)
    {
        var repoId = store.EnsureRepo(new RepoIdentity
        {
            Provider = "github",
            OrgOrOwner = "achieveai",
            RepoName = "LmDotnetTools",
            RepoStableId = "repo-stable-1",
        });
        var run = store.CreateOrGetReviewRun(new ReviewRun
        {
            RepoId = repoId,
            PrId = prId,
            HeadSha = $"head-{prId}",
            BaseSha = $"base-{prId}",
            TriggerWatermark = $"wm-{prId}",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "collect-only",
            Stage = ReviewStage.Discovered,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        });

        _ = store.AddArtifact(new ReviewArtifact
        {
            ReviewRunId = run.Id,
            ArtifactSchemaVersion = DaemonReviewStageExecutor.ContextArtifactSchemaVersion,
            ArtifactKind = DaemonReviewStageExecutor.ContextArtifactKind,
            Provider = "github",
            Payload = JsonSerializer.Serialize(new ContextArtifactPayload(
                run.PrId, run.BaseSha, run.HeadSha, "diff --git a/Foo.cs b/Foo.cs\n+ var x = bar;")),
        });

        return run;
    }

    private sealed class FakeGatewaySkillProbe(GatewaySkillSupport support) : IGatewaySkillProbe
    {
        public int Calls { get; private set; }

        public Task<GatewaySkillSupport> ProbeAsync(IReadOnlyList<string> marketplaces, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(support);
        }
    }

    /// <summary>An unreachable/erroring gateway — NOT the same thing as a catalog without the skills.</summary>
    private sealed class ThrowingGatewaySkillProbe : IGatewaySkillProbe
    {
        public int Calls { get; private set; }

        public Task<GatewaySkillSupport> ProbeAsync(IReadOnlyList<string> marketplaces, CancellationToken ct)
        {
            Calls++;
            throw new InvalidOperationException("gateway unreachable");
        }
    }

    private sealed class CancelingGatewaySkillProbe : IGatewaySkillProbe
    {
        public Task<GatewaySkillSupport> ProbeAsync(IReadOnlyList<string> marketplaces, CancellationToken ct) =>
            throw new OperationCanceledException("probe cancelled");
    }

    private sealed class RecordingHostLifetime : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopCalls++;
    }
}
