using System.Diagnostics;
using System.Text.Json;

namespace TodoEval.Runner.Sweep;

/// <summary>
/// The J1 layer for one run: what the task's own deterministic checker made of the workspace the run
/// left behind. Never an LLM verdict — <c>check.ps1</c> runs hidden tests and answer keys.
/// </summary>
internal sealed record J1Result
{
    /// <summary>The outcome of a checker that could not judge: a crash, a bad exit, or no parseable score.</summary>
    public const string Unjudged = "unjudged";

    /// <summary>The exit code tasks/README.md reserves for "I could not judge this".</summary>
    public const int CouldNotJudgeExitCode = 2;

    /// <summary><c>pass</c>, <c>partial</c>, <c>fail</c>, or <see cref="Unjudged"/>.</summary>
    public required string Outcome { get; init; }

    /// <summary>Passed checks over total checks, or null when the run was not judged.</summary>
    public double? Score { get; init; }

    /// <summary>The names of the checks that did not pass, in the checker's own order.</summary>
    public IReadOnlyList<string> FailedChecks { get; init; } = [];

    /// <summary>Why the checker could not judge. Null on every judged outcome.</summary>
    public string? Error { get; init; }

    /// <summary>True only for a checker that ran and produced a score.</summary>
    public bool Judged => !string.Equals(Outcome, Unjudged, StringComparison.Ordinal);

    public static J1Result CouldNotJudge(string error) => new() { Outcome = Unjudged, Error = error };

    /// <summary>
    /// Maps one checker invocation onto a result. A checker that exits non-zero could not judge (2 is
    /// the contract's own "could not judge"; any other non-zero is a crash, which is the same thing
    /// from here), and so does one whose score file is missing or unreadable. Only a clean exit with a
    /// parseable score becomes a verdict — a checker must never be able to fail a run by failing itself.
    /// </summary>
    public static J1Result From(int exitCode, string scorePath, string? diagnostics = null)
    {
        var detail = string.IsNullOrWhiteSpace(diagnostics) ? "" : $" {diagnostics.Trim()}";
        if (exitCode != 0)
        {
            return CouldNotJudge(
                exitCode == CouldNotJudgeExitCode
                    ? $"the checker reported it could not judge this run (exit {CouldNotJudgeExitCode}).{detail}"
                    : $"the checker exited {exitCode}.{detail}"
            );
        }

        if (!File.Exists(scorePath))
        {
            return CouldNotJudge($"the checker exited 0 but wrote no score file at '{scorePath}'.{detail}");
        }

        try
        {
            return Parse(File.ReadAllText(scorePath));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return CouldNotJudge($"'{scorePath}' is not a readable score file: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the <c>compaction-eval/score@1</c> shape: an outcome, a score, and one row per check.
    /// A missing outcome or score is unjudged rather than a zero — an absent number is not a failure.
    /// </summary>
    private static J1Result Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (
            !root.TryGetProperty("outcome", out var outcome)
            || outcome.ValueKind != JsonValueKind.String
            || outcome.GetString() is not { Length: > 0 } outcomeText
        )
        {
            return CouldNotJudge("the score file carries no 'outcome' string.");
        }

        var failed = new List<string>();
        if (root.TryGetProperty("checks", out var checks) && checks.ValueKind == JsonValueKind.Array)
        {
            foreach (var check in checks.EnumerateArray())
            {
                var passed = check.TryGetProperty("pass", out var pass) && pass.ValueKind == JsonValueKind.True;
                if (!passed)
                {
                    failed.Add(
                        check.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                            ? name.GetString()!
                            : "(unnamed check)"
                    );
                }
            }
        }

        return new J1Result
        {
            Outcome = outcomeText,
            Score =
                root.TryGetProperty("score", out var score) && score.ValueKind == JsonValueKind.Number
                    ? score.GetDouble()
                    : null,
            FailedChecks = failed,
        };
    }
}

/// <summary>Judges the workspace a finished run left behind. A seam so a sweep test needs no pwsh.</summary>
internal interface ITaskChecker
{
    /// <summary>
    /// Runs the task's checker against <paramref name="workspacePath"/>, writing its score file under
    /// <paramref name="outputDir"/>. Returns null when the task ships no checker — which is "there is
    /// no J1 for this task", quite unlike an unjudged run.
    /// </summary>
    Task<J1Result?> JudgeAsync(EvalTaskAsset task, string workspacePath, string outputDir, CancellationToken ct);
}

/// <summary>
/// Runs a task's <c>check.ps1</c> through PowerShell, per tasks/README.md:
/// <c>pwsh check.ps1 -Workspace &lt;dir&gt; -Out &lt;score.json&gt;</c>.
/// </summary>
internal sealed class PwshTaskChecker(TextWriter log, int timeoutMinutes = 10) : ITaskChecker
{
    /// <summary>The score file every checker writes, inside the run's own output directory.</summary>
    public const string ScoreFileName = "score.json";

    /// <summary>How long a killed checker tree is given to actually die before the wait gives up.</summary>
    private static readonly TimeSpan TerminationGrace = TimeSpan.FromSeconds(30);

    public async Task<J1Result?> JudgeAsync(
        EvalTaskAsset task,
        string workspacePath,
        string outputDir,
        CancellationToken ct
    )
    {
        if (task.CheckScript is not { } script)
        {
            return null;
        }

        _ = Directory.CreateDirectory(outputDir);
        var scorePath = Path.Combine(outputDir, ScoreFileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("-Workspace");
        startInfo.ArgumentList.Add(workspacePath);
        startInfo.ArgumentList.Add("-Out");
        startInfo.ArgumentList.Add(scorePath);

        try
        {
            using var process =
                Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned no process for 'pwsh'.");

            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            // Both cancellations land here, and both must kill the tree before this method returns.
            // A checker holds the run's workspace open, and the sweep deletes that workspace as soon
            // as the run is over; an orphaned pwsh outlives the sweep that started it.
            catch (OperationCanceledException)
            {
                await TerminateAsync(process, stdout, stderr);
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                return J1Result.CouldNotJudge($"the checker did not finish within {timeoutMinutes} minute(s).");
            }

            var diagnostics = (await stderr).Trim();
            _ = await stdout;
            return J1Result.From(process.ExitCode, scorePath, diagnostics);
        }
        // A missing pwsh, or one that cannot be launched, must not fault the sweep: an unjudged run
        // still carries its cost and compaction numbers, which are the measurements under study.
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.WriteLine($"[warn] could not run '{script}': {ex.Message}");
            return J1Result.CouldNotJudge($"could not launch the checker: {ex.Message}");
        }
    }

    /// <summary>
    /// Kills <paramref name="process"/> and everything it started, then waits for the kill to land so
    /// the caller returns to a reaped tree rather than a signalled one. The two redirected reads are
    /// awaited to completion for the same reason: an abandoned read holds the pipe, and its fault
    /// would otherwise surface later on the finalizer thread.
    /// </summary>
    private static async Task TerminateAsync(Process process, Task<string> stdout, Task<string> stderr)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone; nothing to kill. Still reap below, so the wait sees the exit either way.
        }

        // Rooted at None on purpose: this runs because a token already fired, so a token-bound wait
        // here would return before the tree is gone. The grace bounds a kill the OS never completes.
        using var grace = new CancellationTokenSource(TerminationGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            // Unkillable tree. Nothing further this process can do; the caller still unwinds.
        }

        await Observe(stdout);
        await Observe(stderr);
    }

    /// <summary>Awaits <paramref name="read"/> for its completion only, discarding value and fault.</summary>
    private static async Task Observe(Task<string> read)
    {
        try
        {
            _ = await read;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The pipe died with the process it belonged to. Expected on this path.
        }
    }
}
