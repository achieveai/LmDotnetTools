using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using CodeReviewDaemon.Sample.Configuration;
using CodeReviewDaemon.Sample.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodeReviewDaemon.Sample.Tests.Orchestration;

public class ReviewSessionProvisionerTests
{
    [Fact]
    public async Task Runs_and_bootstrap_share_one_workspace_session_for_all_repositories()
    {
        var source = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            source,
            new CodeReviewDaemonOptions(),
            NullLoggerFactory.Instance
        );
        var first = await provisioner.GetOrCreateSharedAsync(default);
        var second = await provisioner.GetOrCreateSharedAsync(default);
        var bootstrap = await provisioner.GetOrCreateSharedAsync(default);
        second.Should().BeSameAs(first);
        bootstrap.Should().BeSameAs(first);
        source.References.Should().HaveCount(3);
        source.References[0].Id.Should().Be("nova-reviews");
        source.References[0].DirectoryRelPath.Should().Be("nova-reviews");
    }

    [Fact]
    public async Task Disposal_closes_all_adapter_generations_and_rejects_acquisition()
    {
        var source = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            source,
            new CodeReviewDaemonOptions(),
            NullLoggerFactory.Instance
        );
        var first = await provisioner.GetOrCreateSharedAsync(default);
        source.SessionId = "replacement";
        var second = await provisioner.GetOrCreateSharedAsync(default);
        await provisioner.DisposeAsync();
        await provisioner.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => provisioner.GetOrCreateSharedAsync(default));
        foreach (var session in new[] { first, second })
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                session.CommandRunner.RunAsync(
                    new CodeReviewDaemon.Sample.Workspace.Sandbox.SandboxCommand(["git", "status"], "/workspace"),
                    default
                )
            );
    }

    [Fact]
    public async Task Configured_shared_leaf_is_used_without_host_storage_paths()
    {
        var source = new FakeSessionSource();
        var provisioner = new ReviewSessionProvisioner(
            source,
            new CodeReviewDaemonOptions(),
            NullLoggerFactory.Instance,
            workspaceId: "shared-reviews"
        );
        await provisioner.GetOrCreateSharedAsync(default);
        source.References.Single().DirectoryRelPath.Should().Be("shared-reviews");
    }

    [Fact]
    public async Task Evicted_gateway_session_is_revalidated_and_replaced_without_destroying_old_borrowers()
    {
        var source = new FakeSessionSource();
        await using var provisioner = new ReviewSessionProvisioner(
            source,
            new CodeReviewDaemonOptions(),
            NullLoggerFactory.Instance
        );
        var old = await provisioner.GetOrCreateSharedAsync(default);
        source.SessionId = "replacement-after-eviction";
        var replacement = await provisioner.GetOrCreateSharedAsync(default);
        replacement!.SessionId.Should().Be(source.SessionId);
        replacement.Should().NotBeSameAs(old);
        old!.SessionId.Should().Be("shared-session");
        // No command is replayed, and the old session remains a valid transport for its existing borrower.
        var disposed = typeof(CodeReviewDaemon.Sample.Workspace.Sandbox.SandboxSessionAdapter).GetField(
            "_disposed",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance
        );
        disposed.Should().NotBeNull();
        disposed!.GetValue(old.CommandRunner).Should().Be(false);
        (await provisioner.GetOrCreateSharedAsync(default)).Should().BeSameAs(replacement);
        source.References.Should().HaveCount(3);
    }

    [Fact]
    public async Task Registry_probe_failure_never_silently_returns_stale_adapter()
    {
        var source = new FakeSessionSource();
        await using var provisioner = new ReviewSessionProvisioner(
            source,
            new CodeReviewDaemonOptions(),
            NullLoggerFactory.Instance
        );
        await provisioner.GetOrCreateSharedAsync(default);
        source.Failure = new IOException("gateway unavailable");
        await Assert.ThrowsAsync<IOException>(() => provisioner.GetOrCreateSharedAsync(default));
        source.Failure = null;
        source.SessionId = "after-restart";
        (await provisioner.GetOrCreateSharedAsync(default))!.SessionId.Should().Be("after-restart");
    }

    private sealed class FakeSessionSource : ISandboxSessionSource
    {
        public string SessionId { get; set; } = "shared-session";
        public Exception? Failure { get; set; }
        public List<WorkspaceRef> References { get; } = [];

        public Task<SandboxSession> GetOrCreateLiveSessionAsync(WorkspaceRef workspaceRef, CancellationToken ct)
        {
            References.Add(workspaceRef);
            if (Failure is not null)
                throw Failure;
            return Task.FromResult(
                new SandboxSession(workspaceRef.Id, SessionId, workspaceRef.Id, "/remote/not-readable")
            );
        }
    }
}
