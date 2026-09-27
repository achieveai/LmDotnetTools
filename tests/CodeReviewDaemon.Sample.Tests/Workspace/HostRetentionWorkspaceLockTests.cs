using System.Diagnostics;
using CodeReviewDaemon.Sample.Workspace;

namespace CodeReviewDaemon.Sample.Tests.Workspace;

public sealed class HostRetentionWorkspaceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retention-lock-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Configured_store_destinations_have_distinct_roots()
    {
        HostRetentionWorkspace
            .ResolveRoot(_root, "https://github.com/acme/reviews")
            .Should()
            .NotBe(HostRetentionWorkspace.ResolveRoot(_root, "https://github.com/other/reviews"));
    }

    [Fact]
    public void Relative_retention_root_uses_the_application_base_directory()
    {
        var resolved = HostRetentionWorkspace.ResolveRoot("relative/workspaces", "https://github.com/acme/reviews");
        Path.GetDirectoryName(resolved).Should().Be(Path.GetFullPath("relative/workspaces", AppContext.BaseDirectory));
        Path.GetFileName(resolved).Should().StartWith("retention-").And.HaveLength("retention-".Length + 64);
    }

    [Fact]
    public async Task Exclusive_handle_blocks_an_independent_process_and_release_allows_it()
    {
        var repoRoot = Path.Combine(_root, "store");
        var lease = await HostRetentionWorkspace.AcquireRepositoryLockAsync(repoRoot, default);
        try
        {
            Directory.Exists(repoRoot).Should().BeFalse("the lock must work before cloning the repository");
            (await ProbeFromOtherProcessAsync(lease.Name)).Should().Be("blocked");
        }
        finally
        {
            await lease.DisposeAsync();
        }
        (await ProbeFromOtherProcessAsync(lease.Name)).Should().Be("acquired");
    }

    [Fact]
    public async Task Waiting_for_the_same_repository_is_cancelable_and_does_not_release_its_owner()
    {
        var repoRoot = Path.Combine(_root, "store");
        await using var owner = await HostRetentionWorkspace.AcquireRepositoryLockAsync(repoRoot, default);
        using var canceled = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Func<Task> wait = async () =>
        {
            await using var unexpected = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
                repoRoot + Path.DirectorySeparatorChar,
                canceled.Token
            );
        };
        await wait.Should().ThrowAsync<OperationCanceledException>();
        (await ProbeFromOtherProcessAsync(owner.Name)).Should().Be("blocked");
    }

    [Fact]
    public async Task Different_repositories_have_independent_leases()
    {
        await using var first = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
            Path.Combine(_root, "first"),
            default
        );
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var second = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
            Path.Combine(_root, "second"),
            timeout.Token
        );
        first.Name.Should().NotBe(second.Name);
    }

    [Fact]
    public async Task Invalid_parent_is_reported_without_retrying_it_as_contention()
    {
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "file");
        await File.WriteAllTextAsync(file, "not a directory");
        Func<Task> acquire = async () =>
        {
            await using var unexpected = await HostRetentionWorkspace.AcquireRepositoryLockAsync(
                Path.Combine(file, "store"),
                default
            );
        };
        await acquire.Should().ThrowAsync<IOException>();
    }

    private async Task<string> ProbeFromOtherProcessAsync(string lockPath)
    {
        var script = Path.Combine(_root, "probe.ps1");
        await File.WriteAllTextAsync(
            script,
            """
            param([string] $LockPath)
            try {
                $lease = [System.IO.File]::Open($LockPath, [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
                $lease.Dispose()
                [Console]::Write('acquired')
            } catch [System.IO.IOException] {
                [Console]::Write('blocked')
            }
            """
        );
        var start = new ProcessStartInfo("pwsh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, lockPath })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        process.ExitCode.Should().Be(0, await error);
        return await output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
