using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// In-memory <see cref="ISandboxCommandRunner"/> that records every command (in order) and returns
/// scripted results matched by predicate, so the deterministic git orchestration can be verified
/// without a live gateway. Each rule yields its result via a factory, which lets a single rule walk a
/// sequence (e.g. push fails twice then succeeds).
///
/// An unmatched command is NOT answered with a blanket default. Commands that really do succeed
/// silently get <see cref="SilentSuccess"/>; commands whose success implies output are answered from
/// the checkout state this fake tracks (see <see cref="TryAnswerStateReadingCommand"/>). That split is
/// deliberate — see the remarks on <see cref="SilentSuccess"/>.
/// </summary>
internal sealed class FakeSandboxCommandRunner : ISandboxCommandRunner
{
    private readonly List<(Func<SandboxCommand, bool> Match, Func<SandboxCommandResult> Next)> _rules = [];
    private readonly Lock _commandsGate = new();
    private readonly Lock _headGate = new();

    /// <summary>
    /// The commit each directory was last successfully checked out to, and whether that checkout was
    /// detached. This is what lets an unscripted <c>rev-parse HEAD</c> be answered honestly instead of
    /// guessed: production checks the sha out (<c>ReviewSlotPreparer</c> <c>--detach run.HeadSha</c> and
    /// <c>checkout --force run.HeadSha</c>) one command BEFORE it probes HEAD, so the fake has already
    /// been told the answer. Keyed by directory, so a branch checked out into the store root cannot
    /// answer for the reviewed target dir.
    /// </summary>
    private readonly Dictionary<string, (string Sha, bool Detached)> _headByDir = new(StringComparer.Ordinal);

    /// <summary>Every command the runner was asked to execute, in invocation order.</summary>
    public List<SandboxCommand> Commands { get; } = [];

    /// <summary>
    /// The answer for commands that legitimately succeed without output — <c>checkout</c>, <c>fetch</c>,
    /// <c>clean</c>. Production reads their exit code and never their stdout, so empty is an answer here
    /// rather than a guess.
    /// </summary>
    /// <remarks>
    /// There is deliberately no public settable default. "Exit 0 with empty stdout" as the answer to a
    /// command whose success implies OUTPUT is byte-identical to the capture race in #104, and it is how
    /// 44 of the preparer tests came to pass: they drove the head-verification gate down its fail-open
    /// branch and returned before verifying anything, while reading as coverage of the verified path. A
    /// blanket default would re-arm that for the next test, so the ability to reach it is removed rather
    /// than documented against.
    /// </remarks>
    private static readonly SandboxCommandResult SilentSuccess = new(0, string.Empty, string.Empty);

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

        var result = Resolve(command);

        // After resolution, so a rule that scripts a checkout FAILURE is honoured: a failed checkout must
        // not move HEAD, or a test exercising that failure would still satisfy the head gate.
        ObserveCheckout(command, result);
        return Task.FromResult(result);
    }

    private SandboxCommandResult Resolve(SandboxCommand command)
    {
        foreach (var (match, next) in _rules)
        {
            if (match(command))
            {
                return next();
            }
        }

        // Local-filesystem fidelity for workspace orchestration tests. Production executes these exact argv
        // vectors through SandboxClient; temp-dir tests need the same observable effects without a gateway.
        if (TryApplyLocalFileCommand(command, out var localResult))
        {
            return localResult;
        }

        if (IsGitDirectoryProbe(command, out var storePath)
            && !Directory.Exists(Path.Combine(storePath, ".git"))
            && !File.Exists(Path.Combine(storePath, ".git")))
        {
            return new SandboxCommandResult(128, string.Empty, "fatal: not a git repository");
        }

        if (TryAnswerStateReadingCommand(command, out var stateResult))
        {
            return stateResult;
        }

        return SilentSuccess;
    }

    /// <summary>
    /// Answers the git commands whose success implies output, so that none of them can fall through to
    /// an empty-stdout success. Rules are consulted first, so a test can still script any of these.
    /// </summary>
    private bool TryAnswerStateReadingCommand(SandboxCommand command, out SandboxCommandResult result)
    {
        result = SilentSuccess;
        var argv = command.Argv;
        var directory = DirectoryArg(argv);
        if (directory.Length == 0)
        {
            return false;
        }

        if (Contains(argv, "rev-parse") && Contains(argv, "HEAD"))
        {
            (string Sha, bool Detached) head;
            bool known;
            lock (_headGate)
            {
                known = _headByDir.TryGetValue(directory, out head);
            }

            // Unknown HEAD is reported as a failure, never as an empty success: the caller's fail-open
            // branch is then entered deliberately and says why, instead of being reached by a default that
            // looks exactly like a lost read.
            result = known
                ? new SandboxCommandResult(0, head.Sha + "\n", string.Empty)
                : new SandboxCommandResult(
                    128,
                    string.Empty,
                    $"fatal: no checkout was recorded for '{directory}', so HEAD is unknown. Script the "
                        + "checkout (or the rev-parse itself) if this test means to reach the head gate.");
            return true;
        }

        // `git status --porcelain --branch` always prints a `##` header, clean or dirty, which is what
        // makes an empty read distinguishable from a clean tree. Slots run detached, and that header shape
        // (verified against git 2.53.0) is the case a plain `## <branch>` stub would not cover.
        if (Contains(argv, "status") && Contains(argv, "--branch"))
        {
            bool detached;
            lock (_headGate)
            {
                detached = _headByDir.TryGetValue(directory, out var head) && head.Detached;
            }

            result = new SandboxCommandResult(
                0, detached ? "## HEAD (no branch)\n" : "## master\n", string.Empty);
            return true;
        }

        return false;
    }

    /// <summary>Mirrors real git: a successful checkout or worktree-add moves HEAD, so record where to.</summary>
    private void ObserveCheckout(SandboxCommand command, SandboxCommandResult result)
    {
        if (result.ExitCode != 0
            || !TryReadCheckout(command, out var directory, out var sha, out var detached))
        {
            return;
        }

        lock (_headGate)
        {
            _headByDir[directory] = (sha, detached);
        }
    }

    /// <summary>
    /// Extracts the tree and the commit a checkout lands on, for the argv shapes production actually
    /// sends. Returns false when the command moves no HEAD — notably a bare <c>checkout --detach</c>,
    /// which detaches in place and leaves the commit alone.
    /// </summary>
    private static bool TryReadCheckout(
        SandboxCommand command,
        out string directory,
        out string sha,
        out bool detached)
    {
        directory = string.Empty;
        sha = string.Empty;
        var argv = command.Argv;
        var detachIndex = IndexOf(argv, "--detach");
        detached = detachIndex >= 0;

        // `worktree add [flags] <path> --detach <sha>`: HEAD moves in the NEW tree, so the path argument
        // is the key, not the owner repo named by -C.
        var addIndex = Contains(argv, "worktree") ? IndexOf(argv, "add") : -1;
        if (addIndex > 0)
        {
            var path = FirstNonFlagAfter(argv, addIndex);
            var target = detached ? ValueAfter(argv, detachIndex) : LastNonFlagAfter(argv, addIndex);
            if (path.Length == 0 || target.Length == 0 || string.Equals(path, target, StringComparison.Ordinal))
            {
                return false;
            }

            directory = path;
            sha = target;
            return true;
        }

        var checkoutIndex = IndexOf(argv, "checkout");
        if (checkoutIndex < 0)
        {
            return false;
        }

        var workingDirectory = DirectoryArg(argv);
        if (workingDirectory.Length == 0)
        {
            return false;
        }

        // `checkout -B <branch> <start-point>` leaves HEAD at the start point.
        var branchIndex = IndexOf(argv, "-B");
        var landsOn = branchIndex >= 0
            ? ValueAfter(argv, branchIndex + 1)
            : detached
                ? ValueAfter(argv, detachIndex)
                : LastNonFlagAfter(argv, checkoutIndex);
        if (landsOn.Length == 0)
        {
            return false;
        }

        directory = workingDirectory;
        sha = landsOn;
        return true;
    }

    private static bool IsGitDirectoryProbe(SandboxCommand command, out string storePath)
    {
        storePath = DirectoryArg(command.Argv);
        return storePath.Length > 0
            && Contains(command.Argv, "rev-parse")
            && Contains(command.Argv, "--git-dir");
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

    private static bool Contains(IReadOnlyList<string> argv, string value) => IndexOf(argv, value) >= 0;

    private static int IndexOf(IReadOnlyList<string> argv, string value)
    {
        for (var i = 0; i < argv.Count; i++)
        {
            if (string.Equals(argv[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The argument following <paramref name="index"/>, or empty when there is none.</summary>
    private static string ValueAfter(IReadOnlyList<string> argv, int index) =>
        index >= 0 && index + 1 < argv.Count ? argv[index + 1] : string.Empty;

    /// <summary>The directory named by <c>-C</c>, which is how every git command here selects its tree.</summary>
    private static string DirectoryArg(IReadOnlyList<string> argv) => ValueAfter(argv, IndexOf(argv, "-C"));

    private static string FirstNonFlagAfter(IReadOnlyList<string> argv, int index)
    {
        for (var i = index + 1; i < argv.Count; i++)
        {
            if (!argv[i].StartsWith('-'))
            {
                return argv[i];
            }
        }

        return string.Empty;
    }

    private static string LastNonFlagAfter(IReadOnlyList<string> argv, int index)
    {
        var found = string.Empty;
        for (var i = index + 1; i < argv.Count; i++)
        {
            if (!argv[i].StartsWith('-'))
            {
                found = argv[i];
            }
        }

        return found;
    }
}
