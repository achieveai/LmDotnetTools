namespace CodeReviewDaemon.Sample.Workspace.Sandbox;

/// <summary>
/// A single command to run inside the sandbox, expressed as an explicit argument vector. Argv (not a
/// pre-joined shell string) is the contract on purpose: branch names, paths, and submodule URLs are
/// attacker-influenced, so they must travel as distinct tokens and never be concatenated into a shell
/// command by a caller. The runner is responsible for safe quoting at the sandbox boundary.
/// </summary>
/// <param name="Argv">The executable and its arguments (e.g. <c>["git", "clone", url]</c>). Must be
/// non-empty.</param>
/// <param name="WorkingDirectory">Optional absolute sandbox path to run in.</param>
internal sealed record SandboxCommand(
    IReadOnlyList<string> Argv,
    string? WorkingDirectory = null);

/// <summary>The captured outcome of a <see cref="SandboxCommand"/>.</summary>
/// <param name="ExitCode">Process exit code (0 = success).</param>
/// <param name="Stdout">Captured standard output.</param>
/// <param name="Stderr">Captured standard error.</param>
internal sealed record SandboxCommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>
/// Runs deterministic git/filesystem commands inside the sandbox. The production implementation
/// (<see cref="SandboxSessionAdapter"/>) delegates to the typed <c>SandboxClient</c> SDK; tests use a
/// fake that records the argv and returns scripted results, so all git-orchestration logic is verifiable
/// without a live gateway.
/// </summary>
internal interface ISandboxCommandRunner
{
    Task<SandboxCommandResult> RunAsync(SandboxCommand command, CancellationToken cancellationToken);
}
