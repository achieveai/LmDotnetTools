using System.Globalization;
using System.Reflection;
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
        return EvalProgram.ExtractOnly(sweepDir, config, options.CompareBaselineDir, Console.Out);
    }

    return await EvalProgram.RunSweepAsync(config, options.CompareBaselineDir, Console.Out, CancellationToken.None);
}
catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

/// <summary>Top-level flow: sweep + extract, or extract-only over an archived sweep.</summary>
internal static class EvalProgram
{
    public static async Task<int> RunSweepAsync(
        EvalRunnerConfig config,
        string? compareBaselineDir,
        TextWriter log,
        CancellationToken ct
    )
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

        // Frozen BEFORE the first run: these name the corpus and the measurement contract the models
        // actually faced, and nothing later in this method may recompute them.
        var ranUnder = FingerprintSet.Compute(evalDir);
        var startedUtc = DateTimeOffset.UtcNow;
        long publishMs;
        long readyMs;

        await using (var host = await EvalHostProcess.StartAsync(config.Host, repoRoot, instanceDir, sweepDir, log, ct))
        {
            publishMs = host.PublishMs;
            readyMs = host.ReadyMs;
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
        var liveConversations = Path.Combine(instanceDir, "conversations");
        if (config.ArchiveRaw)
        {
            log.WriteLine("[warn] --archive-raw: the archived transcripts carry model prose. Keep them off-repo.");
            CopyTree(liveConversations, archivedConversations);
        }
        else
        {
            TranscriptRedactor.CopyRedacted(liveConversations, archivedConversations);
        }

        TryDeleteTree(instanceDir, log);

        new SweepManifest
        {
            GitSha = Fingerprints.GitSha(repoRoot),
            RunnerVersion = RunnerVersion,
            RanUnder = ranUnder,
            ExtractedUnder = FingerprintSet.Compute(evalDir),
            Models = config.Models,
            Seeds = config.Seeds,
            PerRunTimeoutMinutes = config.PerRunTimeoutMinutes,
            StartupWork = new HostStartupWork { HostPublishMs = publishMs, HostReadyMs = readyMs },
            StartedUtc = startedUtc,
            FinishedUtc = DateTimeOffset.UtcNow,
            ConversationsRedacted = !config.ArchiveRaw,
        }.Write(sweepDir);
        log.WriteLine($"[sweep] wrote {Path.Combine(sweepDir, SweepManifest.FileName)}");

        var metrics = Extract(sweepDir, archivedConversations, manifest, config, log);
        var comparison = CompareAndReport(sweepDir, compareBaselineDir, metrics, log);
        return ComputeExitCode(manifest, comparison);
    }

    /// <summary>
    /// Exit code for a finished sweep (documented in <see cref="CliOptions.HelpText"/>): the
    /// archived baseline gates a merge through this value, so "nothing completed" must be loud.
    /// </summary>
    internal static int ComputeExitCode(IReadOnlyList<RunManifestEntry> manifest, ComparisonReport? comparison = null)
    {
        if (manifest.Any(e => e.Status == RunOutcomes.HarnessError))
        {
            return 1;
        }

        // F-004: a sweep in which every run timed out / errored / was interrupted used to exit 0 —
        // a wrapper gating on the exit code would archive a fully failed sweep as a "baseline".
        // The sweep's own outcome outranks the comparison: a broken sweep's comparison means nothing.
        return manifest.Any(e => e.Status == RunOutcomes.Completed) ? ComparisonExitCode(comparison) : 3;
    }

    /// <summary>
    /// The comparison's own exit codes; 0 when none was requested. A refusal is 5 and never 0 — a
    /// wrapper that reads only the exit code must not mistake "these sweeps are not comparable" for
    /// "the fix worked". An unmeasurable gate is neither: it cannot fail a run, and the report says
    /// the criterion it covers is unproven.
    /// </summary>
    internal static int ComparisonExitCode(ComparisonReport? comparison) =>
        comparison is null ? 0
        : !comparison.Compared ? 5
        : comparison.HasGateFailure ? 4
        : 0;

    public static int ExtractOnly(string sweepDir, EvalRunnerConfig config, string? compareBaselineDir, TextWriter log)
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
        var metrics = Extract(sweepDir, Path.Combine(sweepDir, "conversations"), manifest, config, log);

        // Re-extraction stamps what it ran UNDER without touching what the sweep ran under: a later
        // reader compares the two and sees whether the corpus or a measurement constant has moved.
        // This happens BEFORE the comparison, which reads extractedUnder to decide comparability.
        if (SweepManifest.Read(sweepDir) is { } archived)
        {
            var evalDir = ResolvePath(config.EvalDir, repoRoot);
            (archived with { ExtractedUnder = FingerprintSet.Compute(evalDir) }).Write(sweepDir);
        }
        else
        {
            log.WriteLine(
                $"[warn] '{sweepDir}' has no {SweepManifest.FileName}: it predates the fingerprint "
                    + "manifest, so nothing pins the corpus and contract its numbers were produced under."
            );
        }

        return ComparisonExitCode(CompareAndReport(sweepDir, compareBaselineDir, metrics, log));
    }

    /// <summary>
    /// Compares this sweep against an archived baseline and writes the reports, then returns the
    /// verdict (null when no comparison was asked for).
    /// </summary>
    /// <remarks>
    /// The summary is written HERE rather than in <see cref="Extract"/> because it carries the
    /// before/after and gate sections: writing it earlier would publish a report whose verdict is
    /// missing, and a reader cannot tell that from a comparison that found nothing to say.
    /// </remarks>
    private static ComparisonReport? CompareAndReport(
        string sweepDir,
        string? compareBaselineDir,
        SweepMetrics metrics,
        TextWriter log
    )
    {
        ComparisonReport? comparison = null;
        if (compareBaselineDir is not null)
        {
            var baselineDir = ResolvePath(compareBaselineDir, FindRepoRoot());
            comparison = SweepComparison.Compare(
                SweepSnapshot.Load(baselineDir),
                SweepSnapshot.WithRuns(sweepDir, metrics.Runs)
            );
            comparison.Write(sweepDir);
            log.WriteLine($"[sweep] wrote {Path.Combine(sweepDir, ComparisonReport.FileName)}");
            log.WriteLine(
                comparison.Compared
                    ? $"[compare] accepted against '{baselineDir}'; "
                        + $"{comparison.Gates?.Count(g => g.Outcome == GateOutcome.Passed)} gate(s) passed, "
                        + $"{comparison.Gates?.Count(g => g.Outcome == GateOutcome.Failed)} failed, "
                        + $"{comparison.Gates?.Count(g => g.Outcome == GateOutcome.NotMeasurable)} not measurable."
                    : $"[compare] REFUSED ({comparison.Refusal}): {comparison.Reason}"
            );
        }

        var summaryPath = Path.Combine(sweepDir, ResultsWriter.SummaryFileName);
        ResultsWriter.WriteSummaryMarkdown(summaryPath, metrics.Runs, metrics.UnattributedThreads, comparison);
        log.WriteLine($"[sweep] wrote {summaryPath}");
        return comparison;
    }

    private static SweepMetrics Extract(
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

        var metrics = MetricsExtractor.Extract(
            conversationsDir,
            manifest,
            expectedBoard,
            FingerprintSet.Compute(evalDir)
        );
        var runsPath = Path.Combine(sweepDir, ResultsWriter.RunsFileName);
        ResultsWriter.WriteRunsJsonl(runsPath, metrics.Runs);
        log.WriteLine($"[sweep] wrote {runsPath}");
        if (metrics.UnattributedThreads.Count > 0)
        {
            log.WriteLine(
                $"[warn] {metrics.UnattributedThreads.Count} conversation thread(s) are unreachable from any run "
                    + "(missing/unresolvable sample.subAgentOf link — likeliest a hard-timeout kill before the "
                    + "debounced metadata write); their activity is in summary.md's 'Unattributed threads' section, "
                    + $"NOT in the per-run rows: {string.Join(", ", metrics.UnattributedThreads.Select(t => t.ThreadId))}"
            );
        }

        return metrics;
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

    /// <summary>The Runner build's own version, recorded as provenance and NEVER hashed (#670).</summary>
    private static string RunnerVersion =>
        typeof(EvalProgram)
            .Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

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
