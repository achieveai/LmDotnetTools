using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using CodeReviewDaemon.Sample.Agents;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Tests.Infrastructure;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 9 — the executor builds a <see cref="ReviewToolContext"/> for the primary review only when
/// <see cref="CodeReviewDaemonOptions.EnableToolAssistedReview"/> is on, and DEGRADES to a null context
/// (diff-only) rather than failing the stage when the per-run session cannot be provisioned.
///
/// Task 12 — once the session resolves, sub-agent discovery is a further, INDEPENDENT degrade tier: a
/// discovered <c>code-reviewer:*</c> item populates <see cref="ReviewToolContext.SubAgentOptions"/>; no
/// discovery (or a discovery failure) leaves it null — a skill-only tool context — without dropping all
/// the way back to diff-only (the context itself, and its <c>SessionId</c>, stay populated).
/// </summary>
public sealed class ReviewToolContextBuildTests
{
    [Fact]
    public async Task Reviewed_ProvisionerThrows_DegradesToDiffOnly()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new ThrowingProvisioner();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner);
        var run = SeedRunWithContext(store);

        // The primary review still runs (diff-only) rather than throwing out of the stage.
        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // A 'review' artifact was persisted — the run degraded instead of failing.
        store.GetArtifacts(run.Id)
            .Should().Contain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);

        // ...and the loop factory received a null tool context — proving the run actually took the
        // degrade path, not merely that it happened to succeed for an unrelated reason.
        factory.ToolContexts.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Reviewed_ProvisionerSucceeds_PassesAPopulatedToolContextToTheLoopFactory()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var options = new CodeReviewDaemonOptions { EnableToolAssistedReview = true };
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var executor = BuildExecutor(store, factory, options, provisioner);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var toolContext = factory.ToolContexts.Should().ContainSingle().Subject;
        toolContext.Should().NotBeNull();
        toolContext!.SessionId.Should().Be("session-abc");
        toolContext.ReadOnlyToolAllowList.Should().BeEquivalentTo(options.ReadOnlyToolAllowList);
        toolContext.SubAgentOptions.Should().BeNull("sub-agents are attached in a later task");

        // A non-pooled (no leased slot) tool-assisted run resolves the session via the per-run mount, never
        // the slot mount — GetOrCreateForSlotAsync is reserved for a run that leased a pooled slot.
        provisioner.GetOrCreateCalls.Should().Be(1);
        provisioner.GetOrCreateForSlotCalls.Should().Be(0);
    }

    [Fact]
    public async Task Reviewed_ToolAssistedReviewDisabled_NeverConsultsTheProvisioner()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new ThrowingProvisioner();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = false }, provisioner);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        factory.ToolContexts.Should().ContainSingle().Which.Should().BeNull();
    }

    [Fact]
    public async Task Reviewed_WithDiscoveredCodeReviewerSubAgent_PopulatesSubAgentOptions()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var discovery = new FakeDiscoveredItemsSource(
        [
            new SandboxSessionRegistry.DiscoveredItem(
                "subagent", "architecture-review", "arch", "/marketplaces/gb-plugins/agents/a.md",
                Content: SubAgentBody, QualifiedName: "code-reviewer:architecture-review"),
            new SandboxSessionRegistry.DiscoveredItem(
                "subagent", "other", "x", "/marketplaces/other/agents/o.md",
                Content: SubAgentBody, QualifiedName: "other-plugin:thing"),
        ]);
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner, discovery);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var toolContext = factory.ToolContexts.Should().ContainSingle().Subject;
        toolContext.Should().NotBeNull();
        toolContext!.SubAgentOptions.Should().NotBeNull();
        toolContext.SubAgentOptions!.Templates.Should().ContainKey("code-reviewer:architecture-review");
        toolContext.SubAgentOptions.Templates.Should().NotContainKey("other-plugin:thing");
    }

    /// <summary>
    /// Task 5 (fix round 1) — the live tool-assisted path hands the executor a DECORATED loop
    /// (<c>ToolScopedReviewLoop</c>), not the loop itself. Resolving the sub-agent surface must see past the
    /// decorator, so the barrier polls the loop's OWN completion source and the synthesis turn runs with
    /// spawning suppressed. Before the capability seam this went through a concrete type switch, so any
    /// wrapper it did not know about silently skipped BOTH.
    /// </summary>
    [Fact]
    public async Task Reviewed_SpawnCapableLoopBehindADecorator_StillWaitsOnItsBarrierAndSuppressesSpawning()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var completionSource = new RecordingCompletionSource(SettledChild);
        var suppression = new SpawnSuppressionRecorder();
        factory.DecorateCreatedAgent = agent =>
        {
            agent.CompletionSource = completionSource;
            agent.SuppressSpawning = suppression.Open;
            return new WrappingLoop(agent);
        };
        var executor = BuildExecutor(
            store,
            factory,
            new CodeReviewDaemonOptions { EnableToolAssistedReview = true, ReviewSubAgentBarrierQuietSeconds = 1 },
            new FakeReviewSessionProvisioner("session-abc"),
            DiscoveryWithCodeReviewerSubAgent);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The barrier polled the WRAPPED loop's source: at least the candidate plus its confirmation.
        completionSource.Calls.Should().BeGreaterThanOrEqualTo(2);

        // ...and the settled roster reached the synthesis turn, which is the second turn of the same agent.
        var agent = factory.CreatedAgents.Should().ContainSingle().Subject;
        agent.ReceivedInputs.Should().HaveCount(2);
        agent.ReceivedInputs[1].Messages.OfType<TextMessage>().Single().Text.Should()
            .Contain("- arch (code-reviewer:architecture-review): Completed");

        // ...which ran inside exactly one spawn-suppression scope, opened and closed.
        suppression.Opened.Should().Be(1);
        suppression.Closed.Should().Be(1);
    }

    /// <summary>
    /// Task 5 (fix round 1) — a run configured to spawn sub-agents on a loop whose spawn surface cannot be
    /// resolved must FAIL, not quietly review without the barrier: skipping it would synthesize while
    /// children are still writing and let the synthesis turn fan out again.
    /// </summary>
    [Fact]
    public async Task Reviewed_SpawnCapableRunOnAnUnknownLoop_FailsFastInsteadOfSkippingTheBarrier()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory { DecorateCreatedAgent = agent => new OpaqueLoop(agent) };
        var executor = BuildExecutor(
            store,
            factory,
            new CodeReviewDaemonOptions { EnableToolAssistedReview = true },
            new FakeReviewSessionProvisioner("session-abc"),
            DiscoveryWithCodeReviewerSubAgent);
        var run = SeedRunWithContext(store);

        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*IReviewLoopSubAgentSurface*");

        // Not even the provisional turn ran: the refusal happens before the conversation starts.
        factory.CreatedAgents.Should().ContainSingle().Which.ReceivedInputs.Should().BeEmpty();
        store.GetArtifacts(run.Id).Should()
            .NotContain(a => a.ArtifactKind == DaemonReviewStageExecutor.ReviewArtifactKind);
    }

    [Fact]
    public async Task Reviewed_NoSubAgentsDiscovered_LeavesSubAgentOptionsNullButToolContextStillPopulated()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var discovery = new FakeDiscoveredItemsSource([]);
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner, discovery);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        var toolContext = factory.ToolContexts.Should().ContainSingle().Subject;
        toolContext.Should().NotBeNull("the session still resolved — this is a skill-only degrade, not diff-only");
        toolContext!.SessionId.Should().Be("session-abc");
        toolContext.SubAgentOptions.Should().BeNull();
    }

    [Fact]
    public async Task Reviewed_DiscoveryThrows_DegradesToSkillOnly_NotAllTheWayToDiffOnly()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var discovery = new ThrowingDiscoveredItemsSource();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner, discovery);
        var run = SeedRunWithContext(store);

        await executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        // The session resolved fine; only discovery failed, so the tool context degrades to skill-only —
        // it must NOT collapse to the full diff-only (null) tier.
        var toolContext = factory.ToolContexts.Should().ContainSingle().Subject;
        toolContext.Should().NotBeNull();
        toolContext!.SessionId.Should().Be("session-abc");
        toolContext.SubAgentOptions.Should().BeNull();
    }

    [Fact]
    public async Task Reviewed_ProvisionerThrowsOperationCanceled_Propagates()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new CancelingProvisioner();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner);
        var run = SeedRunWithContext(store);

        // Cancellation is not a capability gap — it must surface (the catch filter excludes
        // OperationCanceledException), not be swallowed into a silent diff-only degrade.
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.ToolContexts.Should().BeEmpty("cancellation propagated before any review loop was built");
    }

    [Fact]
    public async Task Reviewed_DiscoveryThrowsOperationCanceled_Propagates()
    {
        using var db = new TempSqliteDatabase();
        var store = new ReviewStore(db.ConnectionString);
        var factory = new FakeReviewAgentLoopFactory();
        var provisioner = new FakeReviewSessionProvisioner("session-abc");
        var discovery = new CancelingDiscoveredItemsSource();
        var executor = BuildExecutor(
            store, factory, new CodeReviewDaemonOptions { EnableToolAssistedReview = true }, provisioner, discovery);
        var run = SeedRunWithContext(store);

        // A cancellation raised while discovering sub-agents must propagate too — it is not the same as a
        // discovery failure (which degrades to skill-only); the sub-agent catch filter excludes it.
        var act = () => executor.ExecuteStageAsync(ReviewStage.Reviewed, run, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        factory.ToolContexts.Should().BeEmpty("cancellation propagated before any review loop was built");
    }

    private static DaemonReviewStageExecutor BuildExecutor(
        ReviewStore store,
        FakeReviewAgentLoopFactory factory,
        CodeReviewDaemonOptions options,
        IReviewSessionProvisioner provisioner,
        IDiscoveredItemsSource? discoveredItemsSource = null) =>
        new(
            store,
            factory,
            new FakeSandboxCommandRunner(),
            new FakeSandboxFileSystem(),
            options,
            [new FakeReviewCommentPublisher("github")],
            NullLoggerFactory.Instance,
            provisioner,
            discoveredItemsSource,
            discoveredItemsSource is null
                ? null
                : new DiscoveredSubAgentTemplateBuilder(NullLogger<DiscoveredSubAgentTemplateBuilder>.Instance),
            discoveredItemsSource is null ? null : StubAgentFactory);

    private static readonly Func<IStreamingAgent> StubAgentFactory = () => new Mock<IStreamingAgent>().Object;

    private const string SubAgentBody = """
        ---
        name: architecture-review
        description: Reviews architecture.
        ---
        You review architecture across the connected repos.
        """;

    /// <summary>
    /// Seeds a run + the 'review-context' artifact the Reviewed stage reads, so the test can drive
    /// <see cref="ReviewStage.Reviewed"/> directly without first running <see cref="ReviewStage.ContextReady"/>
    /// (which — on the tool-assisted path — would itself consult the provisioner for the checkout git).
    /// </summary>
    private static ReviewRun SeedRunWithContext(ReviewStore store)
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
            PrId = "118",
            HeadSha = "head-sha",
            BaseSha = "base-sha",
            TriggerWatermark = "wm-1",
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

    /// <summary>Simulates an unreachable sandbox gateway/session (design §7 capability gap).</summary>
    private sealed class ThrowingProvisioner : IReviewSessionProvisioner
    {
        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            throw new InvalidOperationException("sandbox gateway unreachable");

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct) =>
            throw new InvalidOperationException("sandbox gateway unreachable");

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;

        public Task DestroyAsync(long runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Simulates provisioning being cancelled — must propagate, never degrade to diff-only.</summary>
    private sealed class CancelingProvisioner : IReviewSessionProvisioner
    {
        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct) =>
            throw new OperationCanceledException("provisioning cancelled");

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct) =>
            throw new OperationCanceledException("provisioning cancelled");

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;

        public Task DestroyAsync(long runId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeReviewSessionProvisioner(string sessionId) : IReviewSessionProvisioner
    {
        public int GetOrCreateCalls { get; private set; }
        public int GetOrCreateForSlotCalls { get; private set; }

        public Task<ReviewRunSession?> GetOrCreateAsync(ReviewRun run, CancellationToken ct)
        {
            GetOrCreateCalls++;
            return Task.FromResult<ReviewRunSession?>(new ReviewRunSession(
                sessionId, $"/workspace/{sessionId}", new FakeSandboxCommandRunner(), new FakeSandboxFileSystem()));
        }

        public Task<ReviewRunSession?> GetOrCreateForSlotAsync(ReviewRun run, ReviewSlot slot, CancellationToken ct)
        {
            GetOrCreateForSlotCalls++;
            return GetOrCreateAsync(run, ct);
        }

        public Task DestroyAsync(ReviewRun run, CancellationToken ct) => Task.CompletedTask;

        public Task DestroyAsync(long runId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Scripted discovery results for the sub-agent degrade-tier tests (Task 12).</summary>
    private sealed class FakeDiscoveredItemsSource(IReadOnlyList<SandboxSessionRegistry.DiscoveredItem> items)
        : IDiscoveredItemsSource
    {
        public Task<IReadOnlyList<SandboxSessionRegistry.DiscoveredItem>> ListDiscoveredAsync(
            string sessionId, CancellationToken ct) => Task.FromResult(items);
    }

    /// <summary>Simulates a discovery-only failure (gateway `ListDiscoveredAsync` unreachable/erroring).</summary>
    private sealed class ThrowingDiscoveredItemsSource : IDiscoveredItemsSource
    {
        public Task<IReadOnlyList<SandboxSessionRegistry.DiscoveredItem>> ListDiscoveredAsync(
            string sessionId, CancellationToken ct) => throw new InvalidOperationException("discovery unreachable");
    }

    /// <summary>Simulates discovery being cancelled — must propagate, never degrade to skill-only.</summary>
    private sealed class CancelingDiscoveredItemsSource : IDiscoveredItemsSource
    {
        public Task<IReadOnlyList<SandboxSessionRegistry.DiscoveredItem>> ListDiscoveredAsync(
            string sessionId, CancellationToken ct) => throw new OperationCanceledException("discovery cancelled");
    }

    /// <summary>Discovery yielding ONE code-reviewer sub-agent, so the built tool context carries
    /// <c>SubAgentOptions</c> — i.e. the run is spawn-capable, which is what makes the completion barrier (and
    /// the fail-fast when the loop's spawn surface is unknown) apply.</summary>
    private static readonly FakeDiscoveredItemsSource DiscoveryWithCodeReviewerSubAgent = new(
    [
        new SandboxSessionRegistry.DiscoveredItem(
            "subagent", "architecture-review", "arch", "/marketplaces/gb-plugins/agents/a.md",
            Content: SubAgentBody, QualifiedName: "code-reviewer:architecture-review"),
    ]);

    /// <summary>A roster of one already-terminal child: all-terminal on the first poll, so the barrier only
    /// pays the (test-shortened) quiet period before confirming it.</summary>
    private static readonly ReviewSubAgentTreeSnapshot SettledChild = new(
    [
        new ReviewSubAgentNode
        {
            AgentId = "sub-1",
            ThreadId = "thread-sub-1",
            ParentThreadId = "fake-thread",
            Depth = 1,
            Status = ReviewSubAgentStatus.Completed,
            Name = "arch",
            Template = "code-reviewer:architecture-review",
        },
    ]);

    /// <summary>Counts how often the barrier polled, proving WHICH source it resolved off the loop.</summary>
    private sealed class RecordingCompletionSource(ReviewSubAgentTreeSnapshot snapshot)
        : IReviewSubAgentCompletionSource
    {
        public int Calls { get; private set; }

        public Task<ReviewSubAgentTreeSnapshot> GetSnapshotAsync(
            ReviewRun run, string parentThreadId, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }
    }

    /// <summary>Records the synthesis turn's spawn-suppression scope being opened and closed.</summary>
    private sealed class SpawnSuppressionRecorder : IDisposable
    {
        public int Opened { get; private set; }
        public int Closed { get; private set; }

        public IDisposable Open()
        {
            Opened++;
            return this;
        }

        public void Dispose() => Closed++;
    }
}
