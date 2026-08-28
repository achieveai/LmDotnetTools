using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ISandboxCommandRunner"/> that records every command (in order) and returns
/// scripted results matched by predicate, so the deterministic git orchestration can be verified
/// without a live gateway. Each rule yields its result via a factory, which lets a single rule walk a
/// sequence (e.g. push fails twice then succeeds). Unmatched commands return <see cref="Default"/>.
/// </summary>
internal sealed class FakeSandboxCommandRunner : ISandboxCommandRunner
{
    private readonly List<(Func<SandboxCommand, bool> Match, Func<SandboxCommandResult> Next)> _rules = [];
    private readonly Lock _commandsGate = new();

    /// <summary>Every command the runner was asked to execute, in invocation order.</summary>
    public List<SandboxCommand> Commands { get; } = [];

    /// <summary>Result returned when no rule matches.</summary>
    public SandboxCommandResult Default { get; set; } = new(0, string.Empty, string.Empty);

    /// <summary>Scripts a result for commands whose argv satisfies <paramref name="match"/>.</summary>
    public FakeSandboxCommandRunner On(Func<SandboxCommand, bool> match, SandboxCommandResult result)
    {
        _rules.Add((match, () => result));
        return this;
    }

    /// <summary>Scripts a result for commands whose joined argv contains <paramref name="argvSubstring"/>.</summary>
    public FakeSandboxCommandRunner OnArgvContains(string argvSubstring, SandboxCommandResult result) =>
        On(c => ArgvContains(c, argvSubstring), result);

    /// <summary>
    /// Same as <see cref="OnArgvContains"/> but registers the rule AHEAD of every rule added so far. Rules
    /// are first-match-wins, so a narrow rule ("diff --name-only") added after a fixture's broad one
    /// ("diff") would never fire; this lets a test refine a fixture-provided rule without rebuilding it.
    /// </summary>
    public FakeSandboxCommandRunner OnArgvContainsFirst(string argvSubstring, SandboxCommandResult result)
    {
        _rules.Insert(0, (c => ArgvContains(c, argvSubstring), () => result));
        return this;
    }

    /// <summary>
    /// Scripts results for successive matches of <paramref name="argvSubstring"/>: the first match
    /// returns <paramref name="results"/>[0], the next [1], and so on, repeating the last entry once
    /// exhausted. Used to exercise rebase-retry (fail, fail, succeed) paths.
    /// </summary>
    public FakeSandboxCommandRunner OnArgvContainsSequence(
        string argvSubstring,
        params SandboxCommandResult[] results
    )
    {
        if (results.Length == 0)
        {
            throw new ArgumentException("At least one result is required.", nameof(results));
        }

        var index = 0;
        SandboxCommandResult Next()
        {
            var result = results[Math.Min(index, results.Length - 1)];
            index++;
            return result;
        }

        _rules.Add((c => ArgvContains(c, argvSubstring), Next));
        return this;
    }

    public Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken)
    {
        // Guarded because the isolation tests drive two reviews through ONE host runner concurrently; an
        // unsynchronized List.Add there can drop or duplicate an entry and turn a real isolation regression
        // into a flake. Readers inspect Commands only after the concurrent phase has been awaited.
        lock (_commandsGate)
        {
            Commands.Add(command);
        }

        foreach (var (match, next) in _rules)
        {
            if (match(command))
            {
                return Task.FromResult(next());
            }
        }

        // Local-filesystem fidelity for workspace orchestration tests. Production executes these exact argv
        // vectors through SandboxClient; temp-dir tests need the same observable effects without a gateway.
        if (TryApplyLocalFileCommand(command, out var localResult))
        {
            return Task.FromResult(localResult);
        }

        if (IsGitDirectoryProbe(command, out var storePath)
            && !Directory.Exists(Path.Combine(storePath, ".git"))
            && !File.Exists(Path.Combine(storePath, ".git")))
        {
            return Task.FromResult(new SandboxCommandResult(128, string.Empty, "fatal: not a git repository"));
        }

        // The blob-identity pair behind the cleanliness gate's normalization check. Both print a sha on
        // success, so answering them with the empty-stdout Default would silently mean "these bytes are NOT
        // the recorded blob" — a decision about whether a review is refused, made by a fake that was never
        // told anything. Fail loudly instead, so a test that reaches the check has to say what it means.
        if (IsBlobIdentityProbe(command.Argv))
        {
            return Task.FromResult(new SandboxCommandResult(
                128,
                string.Empty,
                "fatal: this fake was not told what blob to report. Script the `hash-object` / "
                    + "`rev-parse :<path>` pair if this test means to reach the normalization check."));
        }

        // `git status --porcelain -b` ALWAYS emits a branch header, and the gate reads its absence as "the
        // probe did not answer". A fake that answered the empty string would put every unscripted test on
        // that path by accident, and would let the header-skip in the parser go untested because nothing
        // ever produced a header. Answer as the daemon's own DETACHED checkout does, so no-answer stays a
        // state a test has to script on purpose.
        if (IsBranchHeaderStatusProbe(command.Argv))
        {
            return Task.FromResult(new SandboxCommandResult(0, DetachedBranchHeader, string.Empty));
        }

        return Task.FromResult(Default);
    }

    /// <summary>What a clean detached checkout answers <c>status --porcelain -b -z</c> with (git 2.53.0).</summary>
    internal const string DetachedBranchHeader = "## HEAD (no branch)\0";

    /// <summary>True for <c>status --porcelain</c> carrying <c>-b</c>, whose output must start with a header.</summary>
    private static bool IsBranchHeaderStatusProbe(IReadOnlyList<string> argv) =>
        argv.Contains("status", StringComparer.Ordinal)
        && argv.Contains("--porcelain", StringComparer.Ordinal)
        && argv.Contains("-b", StringComparer.Ordinal);

    /// <summary>
    /// True for <c>hash-object</c> and for <c>rev-parse :&lt;path&gt;</c>, the lookup that reads the blob the
    /// index records for a path.
    /// </summary>
    private static bool IsBlobIdentityProbe(IReadOnlyList<string> argv)
    {
        if (argv.Contains("hash-object", StringComparer.Ordinal))
        {
            return true;
        }

        if (!argv.Contains("rev-parse", StringComparer.Ordinal))
        {
            return false;
        }

        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i].Length > 1 && argv[i][0] == ':')
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsGitDirectoryProbe(SandboxCommand command, out string storePath)
    {
        var argv = command.Argv;
        var cIndex = -1;
        for (var i = 0; i < argv.Count; i++)
        {
            if (argv[i] == "-C")
            {
                cIndex = i;
                break;
            }
        }

        storePath = cIndex >= 0 && cIndex + 1 < argv.Count ? argv[cIndex + 1] : string.Empty;
        return cIndex >= 0
            && argv.Contains("rev-parse", StringComparer.Ordinal)
            && argv.Contains("--git-dir", StringComparer.Ordinal);
    }

    private static bool TryApplyLocalFileCommand(
        SandboxCommand command,
        out SandboxCommandResult result)
    {
        result = new SandboxCommandResult(0, string.Empty, string.Empty);
        var argv = command.Argv;
        if (argv.Count >= 4
            && argv[0] == "rm"
            && argv[1] == "-rf"
            && argv[2] == "--")
        {
            var path = argv[3];
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }

        if (argv.Count >= 4
            && argv[0] == "mkdir"
            && argv[1] == "-p"
            && argv[2] == "--")
        {
            Directory.CreateDirectory(argv[3]);
            return true;
        }

        if (argv.Count >= 3 && argv[0] == "find")
        {
            var root = argv[1];
            if (!Directory.Exists(root))
            {
                return true;
            }

            if (argv.Contains("-delete", StringComparer.Ordinal))
            {
                var names = new[] { "*.lock", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD" };
                foreach (var name in names)
                {
                    foreach (var file in Directory.EnumerateFiles(root, name, SearchOption.AllDirectories))
                    {
                        File.Delete(file);
                    }
                }

                return true;
            }

            if (argv.Contains("-prune", StringComparer.Ordinal))
            {
                foreach (var name in new[] { "rebase-merge", "rebase-apply" })
                {
                    foreach (var directory in Directory.EnumerateDirectories(root, name, SearchOption.AllDirectories)
                                 .OrderByDescending(path => path.Length))
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }

                return true;
            }
        }

        return false;
    }

    private static bool ArgvContains(SandboxCommand command, string argvSubstring) =>
        string.Join(' ', command.Argv).Contains(argvSubstring, StringComparison.Ordinal);
}
