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
    public static GitMarketplaceRepo Init(string path)
    {
        _ = Directory.CreateDirectory(path);
        var repo = new GitMarketplaceRepo(path);
        _ = repo.Git("init", "-q", "-b", "main");
        return repo;
    }

    /// <summary>Writes the marketplace with <paramref name="marker"/> in the skill file, commits it and returns HEAD.</summary>
    public string CommitVersion(string marker)
    {
        Write("marketplace.json", """{"name":"lmprobe","plugins":[{"name":"probe","source":"./probe"}]}""");
        Write("probe/.claude-plugin/plugin.json", """{"name":"probe"}""");
        Write(MarkerPath, $"---\nname: marker\ndescription: {marker}\n---\n{marker}\n");
        _ = Git("add", "-A");
        _ = Git("commit", "-q", "-m", marker);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>Refreshes the files a dumb-HTTP client reads (<c>info/refs</c>, <c>objects/info/packs</c>).</summary>
    public void UpdateServerInfo() => _ = Git("update-server-info");

    public string Git(params string[] args)
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
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0
            ? stdout.Result.Trim()
            : throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed with exit code {process.ExitCode}: {stderr.Trim()}"
            );
    }

    private void Write(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative);
        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }
}
