namespace CodeReviewDaemon.Sample.Orchestration;

/// <summary>Which of the four scoped, one-shot CLI surfaces (if any) <see cref="ScopedCommandLine.Parse"/>
/// recognized.</summary>
internal enum ScopedCommandKind
{
    /// <summary>Not a scoped command at all — falls through to normal daemon startup
    /// (<c>ReviewProfileArgs.Extract</c> + <c>app.Run()</c>).</summary>
    None,

    /// <summary><c>--workflow-operation &lt;name&gt;</c>.</summary>
    WorkflowOperation,

    /// <summary><c>--list-candidate-prs</c>.</summary>
    ListCandidatePrs,

    /// <summary><c>--run-pr &lt;repoKey&gt; &lt;prId&gt; &lt;approvedHead&gt; &lt;approvedBase&gt;</c>.</summary>
    RunPr,

    /// <summary>Bounded exact-PR requests supplied as JSON lines on stdin.</summary>
    RunPrStream,

    /// <summary>Retain missing task notes for an exact completed run without rerunning its review.</summary>
    RetainReviewNotes,

    /// <summary>Inspect or explicitly reset one incomplete review's occupied slot.</summary>
    ResetReviewRun,

    /// <summary>
    /// <c>--redo-artifact-branch &lt;repoKey&gt; &lt;prId&gt;</c> (task #82, requirement 4) — the explicit,
    /// operator-only, deletion-only redo. Parsed here rather than by its own <c>args is [...]</c> match so a
    /// fat-fingered redo is a usage error on the same path as every other scoped command instead of falling
    /// through into normal daemon startup.
    /// </summary>
    RedoArtifactBranch,

    /// <summary>
    /// The first argument names a scoped command, but the rest of the argument list does not match its
    /// required arity. This must be a usage failure, not a silent fall-through into
    /// <c>ReviewProfileArgs.Extract</c>/normal daemon startup — an operator who fat-fingers <c>--run-pr</c>'s
    /// four arguments must be told so, not hand the daemon an unrelated set of "review args" and have it boot
    /// the poller instead.
    /// </summary>
    Malformed,
}

/// <summary>
/// Parses <c>Program.Main</c>'s raw <c>args</c> into exactly one of: not-a-scoped-command, one of the four
/// well-formed scoped commands (task #81 plan, plus task #82's redo), or malformed. Pure and DI-free by design — <c>Program.cs</c>
/// must decide (and, on <see cref="ScopedCommandKind.Malformed"/>, exit) before it builds any configuration,
/// host, or provider, so this parse has to be checkable on its own, with no host required to exercise it.
/// </summary>
internal static class ScopedCommandLine
{
    private static readonly string[] KnownPrefixes =
    [
        "--workflow-operation",
        "--list-candidate-prs",
        "--run-pr",
        "--run-pr-stream",
        "--retain-review-notes",
        "--reset-review-run",
        "--redo-artifact-branch",
    ];

    public static ScopedCommandLineResult Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args is ["--workflow-operation", var operation])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.WorkflowOperation,
                WorkflowOperation = operation,
            };
        }

        if (args is ["--list-candidate-prs"])
        {
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.ListCandidatePrs };
        }

        if (args is ["--retain-review-notes", var notesRunId] && long.TryParse(notesRunId, out var runId) && runId > 0)
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.RetainReviewNotes, NotesRunId = runId };

        var confirmReset = args is ["--reset-review-run", _, "--confirm"];
        var resetArgs = confirmReset ? args[..^1] : args;
        if (
            resetArgs is ["--reset-review-run", var resetId]
            && long.TryParse(resetId, out var resetRunId)
            && resetRunId > 0
        )
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.ResetReviewRun,
                ResetRunId = resetRunId,
                ConfirmReset = confirmReset,
            };

        var fresh = args is ["--run-pr", _, _, _, _, "--fresh"];
        var runArgs = fresh ? args[..^1] : args;
        if (runArgs is ["--run-pr", var repoKey, var prId, var headSha, var baseSha])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.RunPr,
                RunPrRepoKey = repoKey,
                RunPrId = prId,
                RunPrHeadSha = headSha,
                RunPrBaseSha = baseSha,
                Fresh = fresh,
            };
        }

        if (
            args is ["--run-pr-stream", var concurrencyText]
            && int.TryParse(concurrencyText, out var concurrency)
            && concurrency is >= 1 and <= 6
        )
        {
            return new ScopedCommandLineResult { Kind = ScopedCommandKind.RunPrStream, RunPrConcurrency = concurrency };
        }

        if (args is ["--redo-artifact-branch", var redoRepoKey, var redoPrId])
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.RedoArtifactBranch,
                RedoRepoKey = redoRepoKey,
                RedoPrId = redoPrId,
            };
        }

        if (args.Length > 0 && Array.IndexOf(KnownPrefixes, args[0]) >= 0)
        {
            return new ScopedCommandLineResult
            {
                Kind = ScopedCommandKind.Malformed,
                Error =
                    $"'{args[0]}' does not take {args.Length - 1} argument(s). Usage: "
                    + "--workflow-operation <name> | --list-candidate-prs | "
                    + "--run-pr <repoKey> <prId> <approvedHeadSha> <approvedBaseSha> [--fresh] | "
                    + "--run-pr-stream <concurrency:1-6> | --redo-artifact-branch <repoKey> <prId> | "
                    + "--reset-review-run <positive-id> [--confirm]",
            };
        }

        return new ScopedCommandLineResult { Kind = ScopedCommandKind.None };
    }
}

/// <summary>One <see cref="ScopedCommandLine.Parse"/> outcome.</summary>
internal sealed record ScopedCommandLineResult
{
    public required ScopedCommandKind Kind { get; init; }

    public string? WorkflowOperation { get; init; }

    public int? RunPrConcurrency { get; init; }

    public bool Fresh { get; init; }

    public long? NotesRunId { get; init; }

    public long? ResetRunId { get; init; }

    public bool ConfirmReset { get; init; }

    public string? RunPrRepoKey { get; init; }

    public string? RunPrId { get; init; }

    public string? RunPrHeadSha { get; init; }

    public string? RunPrBaseSha { get; init; }

    /// <summary>The repository key of a <see cref="ScopedCommandKind.RedoArtifactBranch"/> command.</summary>
    public string? RedoRepoKey { get; init; }

    /// <summary>The PR id of a <see cref="ScopedCommandKind.RedoArtifactBranch"/> command.</summary>
    public string? RedoPrId { get; init; }

    /// <summary>Set only when <see cref="Kind"/> is <see cref="ScopedCommandKind.Malformed"/> — the usage
    /// message to print to stderr before exiting.</summary>
    public string? Error { get; init; }
}
