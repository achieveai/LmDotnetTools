using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using CodeReviewDaemon.Sample.Persistence.Models;
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
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance);

        var a = await provisioner.GetOrCreateAsync(Run(), default);
        var b = await provisioner.GetOrCreateAsync(Run(), default);

        a.SessionId.Should().Be(b.SessionId);
        fake.CreateCount.Should().Be(1);
    }

    [Fact]
    public async Task DestroyAsync_TearsDownTheRunSession()
    {
        var fake = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(fake, new CodeReviewDaemonOptions(), NullLoggerFactory.Instance);

        _ = await provisioner.GetOrCreateAsync(Run(), default);
        await provisioner.DestroyAsync(Run(), default);

        fake.DestroyedWorkspaceIds.Should().Contain("review-run-7");
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

        public Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct)
        {
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
