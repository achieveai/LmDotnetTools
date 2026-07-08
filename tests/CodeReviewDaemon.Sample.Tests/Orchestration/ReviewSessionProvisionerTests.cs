using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
using CodeReviewDaemon.Sample.Workspace;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

/// <summary>
/// Task 6 — the provisioner keys a sandbox session by a stable per-run workspace id
/// (<c>review-run-{Id}</c>), so repeated calls for the same run reuse one session instead of creating a
/// new one every stage, and <c>DestroyAsync</c> tears the run's session down at end-of-run.
/// </summary>
public class ReviewSessionProvisionerTests
{
    private static ReviewRun Run(long id = 7) =>
        new()
        {
            Id = id,
            RepoId = 1,
            PrId = "42",
            HeadSha = "abc1234",
            BaseSha = "def5678",
            TriggerWatermark = "w",
            ReviewKind = "full",
            VariantId = "primary",
            Mode = "auto",
            Stage = ReviewStage.ContextReady,
            WorkflowStatus = WorkflowStatus.Running,
            PrLifecycleState = PrLifecycleState.Open,
        };

    [Fact]
    public async Task GetOrCreateAsync_SameRun_ReusesOneSession()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws");

        var a = await provisioner.GetOrCreateAsync(Run(), default);
        var b = await provisioner.GetOrCreateAsync(Run(), default);

        a.Should().NotBeNull();
        b.Should().NotBeNull();
        a!.SessionId.Should().Be(b!.SessionId);
        fake.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task DestroyAsync_TearsDownTheRunSession()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws");

        _ = await provisioner.GetOrCreateAsync(Run(), default);
        await provisioner.DestroyAsync(Run(), default);

        fake.DestroyedWorkspaceIds.Should().Contain("review-run-7");
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_MountsTheSlotRelativeToTheWorkspaceBase()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws");
        var slot = new ReviewSlot(
            0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        fake.LastRef.Should().NotBeNull();
        // The session is still keyed by the per-run workspace id, but the MOUNTED directory is the slot's
        // host path relative to the base, forward-slashed — so /workspace becomes the slot itself.
        fake.LastRef!.Id.Should().Be("review-run-7");
        fake.LastRef.DirectoryRelPath.Should().Be("review-pool/slot-0");
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_FallsBackToPerRunMount_WhenNoWorkspaceBaseConfigured()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: null);
        var slot = new ReviewSlot(
            0, "/ws/review-pool/slot-0", "/ws/review-pool/slot-0/store", "/ws/review-pool/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        // No base configured → the slot cannot be expressed under it, so it degrades to the per-run mount:
        // DirectoryRelPath is the review-run-{id} id, NOT the slot leaf.
        fake.LastRef!.DirectoryRelPath.Should().Be("review-run-7");
    }

    [Fact]
    public async Task GetOrCreateForSlotAsync_FallsBackToPerRunMount_WhenSlotEscapesTheWorkspaceBase()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance, workspaceBasePath: "/ws");
        // The slot lives OUTSIDE the configured base, so mounting it at /workspace would escape the base —
        // the provisioner refuses and degrades to the per-run mount rather than throwing.
        var slot = new ReviewSlot(0, "/other/slot-0", "/other/slot-0/store", "/other/slot-0/scratch");

        var session = await provisioner.GetOrCreateForSlotAsync(Run(), slot, default);

        session.Should().NotBeNull();
        fake.LastRef!.DirectoryRelPath.Should().Be("review-run-7");
    }

    /// <summary>
    /// In-memory <see cref="ISandboxSessionSource"/> that mimics <see cref="SandboxSessionRegistry"/>'s own
    /// per-workspace-id session caching, so the provisioner's behavior is verifiable without a live
    /// gateway: a second request for the same workspace id returns the same <see cref="SandboxSession"/>
    /// (same <c>SessionId</c>) and does not bump <see cref="CreateCount"/>.
    /// </summary>
    private sealed class FakeSessionSource : ISandboxSessionSource
    {
        private readonly Dictionary<string, SandboxSession> _sessions = new(StringComparer.Ordinal);

        public int CreateCount { get; private set; }

        public List<string> DestroyedWorkspaceIds { get; } = [];

        /// <summary>The most recent <see cref="WorkspaceRef"/> the provisioner asked to mount — lets a test
        /// assert the session key (<c>Id</c>) and the mounted directory (<c>DirectoryRelPath</c>).</summary>
        public WorkspaceRef? LastRef { get; private set; }

        public Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct)
        {
            LastRef = workspaceRef;
            if (!_sessions.TryGetValue(workspaceRef.Id, out var session))
            {
                CreateCount++;
                session = new SandboxSession(
                    workspaceRef.Id,
                    $"session-{workspaceRef.Id}",
                    workspaceRef.Id,
                    $"/workspace/{workspaceRef.Id}");
                _sessions[workspaceRef.Id] = session;
            }

            return Task.FromResult(session);
        }

        public Task DestroyWorkspaceSessionAsync(string workspaceId, CancellationToken ct)
        {
            DestroyedWorkspaceIds.Add(workspaceId);
            _ = _sessions.Remove(workspaceId);
            return Task.CompletedTask;
        }
    }
}
