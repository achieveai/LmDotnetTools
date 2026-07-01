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
        Commands.Add(command);
        foreach (var (match, next) in _rules)
        {
            if (match(command))
            {
                return Task.FromResult(next());
            }
        }

        return Task.FromResult(Default);
    }

    private static bool ArgvContains(SandboxCommand command, string argvSubstring) =>
        string.Join(' ', command.Argv).Contains(argvSubstring, StringComparison.Ordinal);
}
