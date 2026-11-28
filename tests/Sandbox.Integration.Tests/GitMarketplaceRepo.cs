using System.Diagnostics;

namespace AchieveAi.LmDotnetTools.Sandbox.Integration.Tests;

/// <summary>
/// A local git work tree holding a one-plugin marketplace (<c>probe</c>) whose skill file carries a marker per commit,
/// so a test can tell which commit a sandbox mounted. Git runs with <c>safe.directory=*</c> because the contract job
/// hands the repository to the gateway's user (uid 1000), and git refuses a repository another user owns.
/// </summary>
internal sealed class GitMarketplaceRepo
{
    /// <summary>The marker file, relative to the repository root (and to <c>/marketplaces/&lt;alias&gt;/</c>).</summary>
    public const string MarkerPath = "probe/skills/marker/SKILL.md";

    public GitMarketplaceRepo(string path) => Path = path;

    public string Path { get; }

    /// <summary>Creates a fresh repository on branch <c>main</c>.</summary>
    public static async Task<GitMarketplaceRepo> InitAsync(string path)
    {
        _ = Directory.CreateDirectory(path);
        var repo = new GitMarketplaceRepo(path);
        _ = await repo.GitAsync("init", "-q", "-b", "main");
        return repo;
    }

    /// <summary>Writes the marketplace with <paramref name="marker"/> in the skill file, commits it and returns HEAD.</summary>
    public async Task<string> CommitVersionAsync(string marker)
    {
        Write("marketplace.json", """{"name":"lmprobe","plugins":[{"name":"probe","source":"./probe"}]}""");
        Write("probe/.claude-plugin/plugin.json", """{"name":"probe"}""");
        Write(MarkerPath, $"---\nname: marker\ndescription: {marker}\n---\n{marker}\n");
        _ = await GitAsync("add", "-A");
        _ = await GitAsync("commit", "-q", "-m", marker);
        return await GitAsync("rev-parse", "HEAD");
    }

    /// <summary>Refreshes the files a dumb-HTTP client reads (<c>info/refs</c>, <c>objects/info/packs</c>).</summary>
    public async Task UpdateServerInfoAsync() => _ = await GitAsync("update-server-info");

    public async Task<string> GitAsync(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (
            var arg in new[]
            {
                "-c",
                "safe.directory=*",
                "-c",
                "user.name=lmdt-contract",
                "-c",
                "user.email=lmdt-contract@example.invalid",
                "-c",
                "commit.gpgsign=false",
            }.Concat(args)
        )
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start git.");
        // Both pipes are drained concurrently so neither can fill and stall git while the other is read.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
        return process.ExitCode == 0
            ? (await stdout).Trim()
            : throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {(await stderr).Trim()}"
            );
    }

    private void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
