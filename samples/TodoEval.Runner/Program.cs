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
        var assets = EvalAssets.Load(evalDir, config.ModeName, config.Tasks);
        foreach (var task in assets.Tasks.Where(t => t.ExpectedBoard is null))
        {
            log.WriteLine(
                $"[warn] no expected-board.json for task '{task.Id ?? "(single)"}'; completion will be reported "
                    + "as n/a for its runs — the criterion is unproven, never failed."
            );
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
        log.WriteLine($"[sweep] results: {sweepDir}");

        var manifestPath = Path.Combine(sweepDir, "runs-manifest.jsonl");
        var archivedConversations = Path.Combine(sweepDir, "conversations");
        var manifest = new List<RunManifestEntry>();

        // Frozen BEFORE the first run: these name the corpus and the measurement contract the models
        // actually faced, and nothing later in this method may recompute them.
        var ranUnder = FingerprintSet.Compute(evalDir);
        var startedUtc = DateTimeOffset.UtcNow;
        var startupWork = new HostStartupWork();

        // One ISOLATED host per variant. Every compaction knob binds onto a DI singleton resolved at
        // the host's boot, so an option-set can only be varied by launching another host; the cells
        // within one variant share theirs, which is what keeps maxParallelRuns meaningful.
        foreach (var variant in config.Variants)
        {
            var instanceDir = Path.Combine(Path.GetTempPath(), $"todo-eval-host-{timestamp}-{variant.Name}");
            log.WriteLine($"[sweep] variant '{variant.Name}': {Describe(variant)}");

            await using (
                var host = await EvalHostProcess.StartAsync(
                    config.Host.For(variant),
                    repoRoot,
                    instanceDir,
                    sweepDir,
                    log,
                    ct
                )
            )
            {
                startupWork = new HostStartupWork
                {
                    HostPublishMs = startupWork.HostPublishMs + host.PublishMs,
                    HostReadyMs = startupWork.HostReadyMs + host.ReadyMs,
                };
                using var http = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromMinutes(2) };
                var client = new EvalHostClient(http);

                // Each host is fresh, so the mode and workspace are created per host, not per sweep.
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
                    variant,
                    assets.Tasks,
                    log
                );
                manifest.AddRange(await runner.RunSweepAsync(manifestPath, ct));
            } // DisposeAsync waits the shutdown grace, then kills the host — this store is now quiescent.

            // Archive this host's whole conversation store next to the reports: the store IS this
            // variant's data (the host was fresh), and the archived copy is what makes a committed
            // baseline re-extractable offline. Every variant's threads merge into ONE conversations/
            // directory: thread ids are host-minted and the manifest's threadId is the only join key,
            // so the extractor, --extract-only and the comparison all keep working unchanged.
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
        }

        new SweepManifest
        {
            GitSha = Fingerprints.GitSha(repoRoot),
            RunnerVersion = RunnerVersion,
            RanUnder = ranUnder,
            ExtractedUnder = FingerprintSet.Compute(evalDir),
            Models = config.Models,
            Variants = config.Variants,
            Tasks = [.. assets.Tasks.Select(t => t.Id).OfType<string>()],
            Seeds = config.Seeds,
            PerRunTimeoutMinutes = config.PerRunTimeoutMinutes,
            StartupWork = startupWork,
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

        var metrics = MetricsExtractor.Extract(
            conversationsDir,
            manifest,
            ExpectedBoardResolver(evalDir),
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

    /// <summary>
    /// Resolves each run's board expectation from the eval corpus on disk, by the task the manifest
    /// row names: <c>{evalDir}/expected-board.json</c> for the single-task layout, and
    /// <c>{evalDir}/tasks/{id}/expected-board.json</c> for a named task. A task with no such file has
    /// NO board gate, which the completion criterion reports as not measurable rather than failed.
    /// </summary>
    /// <remarks>
    /// Loaded lazily and cached per task, because a sweep has one row per (task x model x seed) and
    /// re-reading one fixture for every one of them is pure work. Reading from disk at EXTRACTION time
    /// (rather than reusing what the sweep loaded) is deliberate and unchanged: <c>--extract-only</c>
    /// must resolve the same way with no sweep in sight, and <c>extractedUnder</c> is what records
    /// which corpus a re-extraction actually read.
    /// </remarks>
    private static Func<RunManifestEntry, BoardShapeExpectation?> ExpectedBoardResolver(string evalDir)
    {
        var cache = new Dictionary<string, BoardShapeExpectation?>(StringComparer.Ordinal);
        return entry =>
        {
            var key = entry.Task ?? "";
            if (!cache.TryGetValue(key, out var board))
            {
                var dir = entry.Task is { } id ? Path.Combine(evalDir, EvalAssets.TasksDirName, id) : evalDir;
                var path = Path.Combine(dir, "expected-board.json");
                board = File.Exists(path) ? BoardShapeExpectation.Load(path) : null;
                cache[key] = board;
            }

            return board;
        };
    }

    /// <summary>A variant's option-set for the sweep log, so a run's archive names what produced it.</summary>
    private static string Describe(VariantConfig variant) =>
        variant.IsDefault ? "the host's own configuration, unmodified" : variant.Signature();

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
