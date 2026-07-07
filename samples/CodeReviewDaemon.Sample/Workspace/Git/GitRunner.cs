using CodeReviewDaemon.Sample.Workspace.Sandbox;

namespace CodeReviewDaemon.Sample.Workspace.Git;

/// <summary>
/// Runs <c>git</c> inside the sandbox with the untrusted-code hardening flags applied to <b>every</b>
/// invocation (plan §3.6 / §4): local and ext transports are disabled and hooks are neutralized, so a
/// malicious PR or submodule can never trigger arbitrary command execution via
/// <c>file://</c>/<c>ext::</c> remotes, a checked-out <c>.git/hooks</c> script, or a crafted
/// <c>core.hooksPath</c>. Centralizing the flags here means no call site can forget them.
/// </summary>
internal sealed class GitRunner
{
    /// <summary>
    /// Hardening config prepended to every git command. <c>protocol.file.allow=never</c> and
    /// <c>protocol.ext.allow=never</c> block local/exec transports (the classic submodule RCE vectors);
    /// <c>core.hooksPath=/dev/null</c> ensures no hook from untrusted content can run.
    /// </summary>
    internal static readonly IReadOnlyList<string> HardeningArgs =
    [
        "-c",
        "protocol.file.allow=never",
        "-c",
        "protocol.ext.allow=never",
        "-c",
        "core.hooksPath=/dev/null",
    ];

    /// <summary>Default git commit identity <c>user.name</c> (<see cref="Configuration.CodeReviewDaemonOptions.BotName"/>'s default) when no bot name is passed.</summary>
    internal const string DefaultBotName = "Revobot";

    private readonly ISandboxCommandRunner _runner;

    /// <summary>
    /// Committer identity prepended to every git command. The sandbox git has no <c>user.name</c>/
    /// <c>user.email</c> configured, so a daemon <c>git commit</c> (retention publish, ReviewBot seed)
    /// fails "Author identity unknown" (exit 128) without it. Passed as per-command <c>-c</c> overrides
    /// so every commit is attributed to the review bot without mutating any global git config. The
    /// <c>user.name</c> is the constructor's <c>botName</c> (default <see cref="DefaultBotName"/> so
    /// <see cref="Configuration.CodeReviewDaemonOptions.BotName"/> can drive it); <c>user.email</c> is
    /// always the fixed <c>review-bot@achieveai.local</c> regardless of the configured bot name.
    /// </summary>
    private readonly IReadOnlyList<string> _identityArgs;

    public GitRunner(ISandboxCommandRunner runner, string botName = DefaultBotName)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _identityArgs =
        [
            "-c",
            $"user.name={botName}",
            "-c",
            "user.email=review-bot@achieveai.local",
        ];
    }

    /// <summary>
    /// Runs <c>git &lt;hardening&gt; &lt;identity&gt; &lt;gitArgs&gt;</c> in <paramref name="workingDirectory"/>.
    /// The arguments are an explicit vector (never a pre-joined string) so attacker-influenced tokens
    /// (branch names, paths, URLs) stay distinct and are safely quoted at the sandbox boundary.
    /// </summary>
    public Task<SandboxCommandResult> RunAsync(
        IReadOnlyList<string> gitArgs,
        string? workingDirectory,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(gitArgs);
        if (gitArgs.Count == 0)
        {
            throw new ArgumentException("At least one git argument is required.", nameof(gitArgs));
        }

        var argv = new List<string>(1 + HardeningArgs.Count + _identityArgs.Count + gitArgs.Count) { "git" };
        argv.AddRange(HardeningArgs);
        argv.AddRange(_identityArgs);
        argv.AddRange(gitArgs);

        return _runner.RunAsync(new SandboxCommand(argv, workingDirectory), cancellationToken);
    }
}
