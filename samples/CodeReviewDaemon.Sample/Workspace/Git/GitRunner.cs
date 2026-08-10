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

    /// <summary>
    /// Committer name used when a call site supplies none. Matches the
    /// <c>CodeReviewDaemon:BotName</c> default so an unthreaded runner and a configured one agree.
    /// </summary>
    internal const string DefaultBotName = "Revobot";

    /// <summary>
    /// Committer address on every daemon commit, whatever the bot is called. Deliberately fixed and
    /// deliberately synthetic: the daemon commits as itself, not as a mailbox anyone can reach, and a
    /// stable address keeps one operator's rename from splitting the store's history across two
    /// identities.
    /// </summary>
    internal const string CommitterEmail = "revobot@revobot.local";

    /// <summary>
    /// Committer identity prepended to every git command. The sandbox git has no <c>user.name</c>/
    /// <c>user.email</c> configured, so a daemon <c>git commit</c> (retention publish, ReviewBot seed)
    /// fails "Author identity unknown" (exit 128) without it. Passed as per-command <c>-c</c> overrides
    /// so every commit is attributed to the review bot without mutating any global git config.
    /// </summary>
    /// <remarks>
    /// The name is <c>CodeReviewDaemon:BotName</c>, which is what that setting always claimed to be —
    /// it was documented as the retention commits' <c>user.name</c> while this list was a hardcoded
    /// constant, so every commit in every store landed as "AchieveAi Review Bot" no matter what the
    /// operator configured. A blank or whitespace name falls back to <see cref="DefaultBotName"/>
    /// rather than producing a nameless commit.
    /// </remarks>
    internal static IReadOnlyList<string> IdentityArgsFor(string? botName) =>
    [
        "-c",
        $"user.name={(string.IsNullOrWhiteSpace(botName) ? DefaultBotName : botName.Trim())}",
        "-c",
        $"user.email={CommitterEmail}",
    ];

    private readonly ISandboxCommandRunner _runner;
    private readonly IReadOnlyList<string> _identityArgs;

    internal ISandboxCommandRunner CommandRunner => _runner;

    /// <param name="runner">Where the composed command is executed.</param>
    /// <param name="botName">
    /// <c>CodeReviewDaemon:BotName</c>. Optional so the many read-only call sites — which never commit —
    /// need not thread configuration they do not use; they get <see cref="DefaultBotName"/>.
    /// </param>
    public GitRunner(ISandboxCommandRunner runner, string? botName = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _identityArgs = IdentityArgsFor(botName);
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
