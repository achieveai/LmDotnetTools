using System.Globalization;
using TodoEval.Runner;
using TodoEval.Runner.Metrics;
using TodoEval.Runner.Sweep;

CliOptions options;
try
{
    options = CliOptions.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

if (options.ShowHelp)
{
    Console.WriteLine(CliOptions.HelpText);
    return 0;
}

try
{
    var config = options.ApplyTo(EvalRunnerConfig.Load(options.ConfigPath));

    if (options.ExtractOnlyDir is { } sweepDir)
    {
        return EvalProgram.ExtractOnly(sweepDir, config, Console.Out);
    }

    return await EvalProgram.RunSweepAsync(config, Console.Out, CancellationToken.None);
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

/// <summary>Top-level flow: sweep + extract, or extract-only over an archived sweep.</summary>
internal static class EvalProgram
{
    public static async Task<int> RunSweepAsync(EvalRunnerConfig config, TextWriter log, CancellationToken ct)
    {
        var repoRoot = FindRepoRoot();
        var evalDir = ResolvePath(config.EvalDir, repoRoot);
        var assets = EvalAssets.Load(evalDir, config.ModeName);
        if (assets.ExpectedBoard is null)
        {
            log.WriteLine("[warn] expected-board.json not found; completion will be reported as n/a for this sweep.");
        }

        // F-007: a GUID suffix keeps same-second invocations from sharing one sweep/instance dir.
        var timestamp =
            DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")[..8];
        var resultsRoot = ResolvePath(
            config.ResultsDir is null ? Path.Combine(evalDir, "results") : config.ResultsDir,
            repoRoot
        );
        var sweepDir = Path.Combine(resultsRoot, timestamp);
        Directory.CreateDirectory(sweepDir);
        var instanceDir = Path.Combine(Path.GetTempPath(), $"todo-eval-host-{timestamp}");
        log.WriteLine($"[sweep] results: {sweepDir}");

        var manifestPath = Path.Combine(sweepDir, "runs-manifest.jsonl");
        IReadOnlyList<RunManifestEntry> manifest;

        await using (var host = await EvalHostProcess.StartAsync(config.Host, repoRoot, instanceDir, sweepDir, log, ct))
        {
            using var http = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
            var client = new EvalHostClient(http);

            var models = await CheckModelsAsync(client, config, log, ct);
            var modeId = await client.EnsureModeAsync(assets.ModeName, assets.ModePayload, ct);
            var workspaceId = await client.EnsureWorkspaceAsync(config.WorkspaceName, ct);
            log.WriteLine($"[sweep] mode '{assets.ModeName}' => {modeId}; workspace => {workspaceId}");

            var runner = new SweepRunner(
                client,
                config with
                {
                    Models = models,
                },
                workspaceId,
                modeId,
                assets.TaskTemplate,
                log
            );
            manifest = await runner.RunSweepAsync(manifestPath, ct);
        } // DisposeAsync waits the shutdown grace, then kills the host — the store is quiescent below.

        // Archive the whole conversation store next to the reports: the store IS this sweep's data
        // (the host was fresh), and the archived copy is what makes a committed baseline
        // re-extractable offline.
        var archivedConversations = Path.Combine(sweepDir, "conversations");
        CopyTree(Path.Combine(instanceDir, "conversations"), archivedConversations);
        TryDeleteTree(instanceDir, log);

        Extract(sweepDir, archivedConversations, manifest, config, log);
        return ComputeExitCode(manifest);
    }

    /// <summary>
    /// Exit code for a finished sweep (documented in <see cref="CliOptions.HelpText"/>): the
    /// archived baseline gates a merge through this value, so "nothing completed" must be loud.
    /// </summary>
    internal static int ComputeExitCode(IReadOnlyList<RunManifestEntry> manifest)
    {
        if (manifest.Any(e => e.Status == RunOutcomes.HarnessError))
        {
            return 1;
        }

        // F-004: a sweep in which every run timed out / errored / was interrupted used to exit 0 —
        // a wrapper gating on the exit code would archive a fully failed sweep as a "baseline".
        return manifest.Any(e => e.Status == RunOutcomes.Completed) ? 0 : 3;
    }

    public static int ExtractOnly(string sweepDir, EvalRunnerConfig config, TextWriter log)
    {
        var repoRoot = FindRepoRoot();
        sweepDir = ResolvePath(sweepDir, repoRoot);
        var manifestPath = Path.Combine(sweepDir, "runs-manifest.jsonl");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException(
                $"'{sweepDir}' does not look like an archived sweep (no runs-manifest.jsonl).",
                manifestPath
            );
        }

        var manifest = RunManifestEntry.ReadJsonl(manifestPath);
        Extract(sweepDir, Path.Combine(sweepDir, "conversations"), manifest, config, log);
        return 0;
    }

    private static void Extract(
        string sweepDir,
        string conversationsDir,
        IReadOnlyList<RunManifestEntry> manifest,
        EvalRunnerConfig config,
        TextWriter log
    )
    {
        var repoRoot = FindRepoRoot();
        var evalDir = ResolvePath(config.EvalDir, repoRoot);
        var expectedBoardPath = Path.Combine(evalDir, "expected-board.json");
        var expectedBoard = File.Exists(expectedBoardPath) ? BoardShapeExpectation.Load(expectedBoardPath) : null;

        var metrics = MetricsExtractor.Extract(conversationsDir, manifest, expectedBoard);
        var runsPath = Path.Combine(sweepDir, "runs.jsonl");
        var summaryPath = Path.Combine(sweepDir, "summary.md");
        ResultsWriter.WriteRunsJsonl(runsPath, metrics.Runs);
        ResultsWriter.WriteSummaryMarkdown(summaryPath, metrics.Runs, metrics.UnattributedThreads);
        log.WriteLine($"[sweep] wrote {runsPath}");
        log.WriteLine($"[sweep] wrote {summaryPath}");
        if (metrics.UnattributedThreads.Count > 0)
        {
            log.WriteLine(
                $"[warn] {metrics.UnattributedThreads.Count} conversation thread(s) are unreachable from any run "
                    + "(missing/unresolvable sample.subAgentOf link — likeliest a hard-timeout kill before the "
                    + "debounced metadata write); their activity is in summary.md's 'Unattributed threads' section, "
                    + $"NOT in the per-run rows: {string.Join(", ", metrics.UnattributedThreads.Select(t => t.ThreadId))}"
            );
        }
    }

    private static async Task<IReadOnlyList<string>> CheckModelsAsync(
        EvalHostClient client,
        EvalRunnerConfig config,
        TextWriter log,
        CancellationToken ct
    )
    {
        var available = await client.ListAvailableProviderIdsAsync(ct);
        var missing = config.Models.Where(m => !available.Contains(m, StringComparer.Ordinal)).ToList();
        if (missing.Count == 0)
        {
            return config.Models;
        }

        var message =
            $"The host does not offer: {string.Join(", ", missing)}. Available ids: {string.Join(", ", available)}. "
            + "deepseek-v4-flash needs the DEEPSEEK_* env vars in the host's env file; gpt-5.6-luna needs a "
            + "resolvable Copilot/gh token in the host's environment.";
        if (!config.AllowMissingModels)
        {
            throw new InvalidOperationException(
                message + " Pass --allow-missing-models to sweep the remaining models anyway."
            );
        }

        log.WriteLine($"[warn] {message} Skipping them.");
        var remaining = config.Models.Where(m => available.Contains(m, StringComparer.Ordinal)).ToList();
        return remaining.Count > 0
            ? remaining
            : throw new InvalidOperationException("No configured model is available on the host; nothing to sweep.");
    }

    /// <summary>
    /// Repo root for resolving relative paths (eval dir, host project): the nearest ancestor of the
    /// current directory containing a .git dir or .sln file, else the current directory itself.
    /// </summary>
    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var probe = dir; probe is not null; probe = probe.Parent)
        {
            if (Directory.Exists(Path.Combine(probe.FullName, ".git")) || probe.EnumerateFiles("*.sln").Any())
            {
                return probe.FullName;
            }
        }

        return dir.FullName;
    }

    private static string ResolvePath(string path, string repoRoot)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        // Prefer the path as seen from the current directory; fall back to repo-root-relative so
        // `dotnet run --project samples/TodoEval.Runner` works from anywhere inside the repo.
        var fromCwd = Path.GetFullPath(path);
        return Directory.Exists(fromCwd) || File.Exists(fromCwd)
            ? fromCwd
            : Path.GetFullPath(Path.Combine(repoRoot, path));
    }

    private static void CopyTree(string sourceDir, string destinationDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        Directory.CreateDirectory(destinationDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetRelativePath(sourceDir, file)), overwrite: true);
        }
    }

    private static void TryDeleteTree(string dir, TextWriter log)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"[warn] could not delete the temp host instance dir '{dir}': {ex.Message}");
        }
    }
}
