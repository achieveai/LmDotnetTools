namespace TodoEval.Runner;

/// <summary>Parsed command line. Parsing is separated from <c>Program</c> so it is unit-testable.</summary>
internal sealed record CliOptions
{
    public string? ConfigPath { get; init; }
    public string? EvalDir { get; init; }
    public string? ResultsDir { get; init; }
    public IReadOnlyList<string>? Models { get; init; }

    /// <summary>Names of the configured variants to run; null runs every configured variant.</summary>
    public IReadOnlyList<string>? Variants { get; init; }

    /// <summary>Task ids to run; null runs every configured task.</summary>
    public IReadOnlyList<string>? Tasks { get; init; }

    public int? Seeds { get; init; }
    public int? MaxParallelRuns { get; init; }
    public int? PerRunTimeoutMinutes { get; init; }
    public string? HostPublishDir { get; init; }
    public string? EnvFile { get; init; }
    public bool AllowMissingModels { get; init; }

    /// <summary>
    /// Archive the conversation store VERBATIM instead of redacting it. The raw archive carries
    /// model prose, so it belongs in an off-repo instance directory and never in a commit.
    /// </summary>
    public bool ArchiveRaw { get; init; }

    /// <summary>When set, no sweep runs: metrics are re-extracted from this archived sweep directory.</summary>
    public string? ExtractOnlyDir { get; init; }

    /// <summary>
    /// Archived baseline sweep to compare this run against (#677). Usable with a live sweep or with
    /// <see cref="ExtractOnlyDir"/>; the comparison refuses before it publishes any number the two
    /// sweeps are not entitled to share.
    /// </summary>
    public string? CompareBaselineDir { get; init; }

    public bool ShowHelp { get; init; }

    public const string HelpText = """
        TodoEval.Runner — S2S eval sweep harness for the todo-eval Testing Mode (#619).

        Usage:
          dotnet run --project samples/TodoEval.Runner [options]

        Options:
          --config <path>            JSON config file (all knobs; CLI switches override it)
          --eval-dir <dir>           Eval asset dir with mode.json/task.md/expected-board.json
                                     (default: evals/todo-eval)
          --results-dir <dir>        Output root (default: <eval-dir>/results)
          --models <a,b,...>         Comma-separated model ids (default: deepseek-v4-flash,gpt-5.6-luna)
          --variants <a,b,...>       Run only these of the config's variants (one isolated host each).
                                     Every name must be configured; a name that is not is an error,
                                     because a typo would silently sweep the wrong option-sets.
          --tasks <t1,t2,...>        Task ids, each resolving to <eval-dir>/tasks/<id>/task.md. Filters
                                     the config's tasks when it has some, and sets them when it has
                                     none. Unset keeps the single <eval-dir>/task.md layout. A task
                                     that also ships meta.json/fixtures/check.ps1 gets a per-run
                                     workspace (config: workspacesRoot) and a J1 score.
          --seeds <n>                Seeds per model (default: 5)
          --parallel <n>             Max concurrent runs (default: 1 = sequential)
          --timeout-min <n>          Hard per-run timeout in minutes (default: 20; a task's own
                                     meta.timeoutMinutes wins where it sets one)
          --host-publish-dir <dir>   Pre-published LmStreaming.Sample binaries to copy instead of publishing
          --env-file <path>          .env handed to the host (LMSTREAMING_ENV_FILE) for provider keys
          --allow-missing-models     Skip models the host does not offer instead of failing the sweep
          --archive-raw              Archive transcripts verbatim (NOT metric-preserving-redacted).
                                     The result carries model prose - keep it off-repo.
          --extract-only <sweepDir>  Re-run metrics extraction over an archived sweep (no host, no runs)
          --compare <baselineDir>    Compare this sweep against an archived baseline sweep and write
                                     comparison.json plus a Before/after section in summary.md.
                                     Works with a live sweep or with --extract-only.
          --help                     This text

        A sweep writes runs-manifest.jsonl, runs.jsonl, summary.md and summary.json (one row per
        variant x task: valid runs, mean J1 score, pass rate, tokens, cost and compactions, averaged
        over the VALID runs only). --extract-only regenerates all but the manifest.
        Per-variant host diagnostics land in hosts/<variant>/: host-publish.log, host-stdout.log,
        host-stderr.log, and instance-logs/ (the host's own Serilog files, copied out before its
        temp instance dir is deleted).

        Exit codes:
          0  the sweep produced at least one Completed run and no run hit a harness error
             (extract-only: extraction succeeded); with --compare, the comparison was accepted
             and no deterministic gate failed
          1  a run failed with a harness error, or the sweep itself failed to run
          2  invalid command line
          3  no run completed - every run timed out, errored, or was interrupted; the archive
             is written but must not gate anything as a successful baseline
          4  --compare: a deterministic gate failed
          5  --compare: the comparison was REFUSED - the two sweeps do not share a corpus, their
             variant/task axes, a metrics-spec revision or an evaluator, or one of them is too thin
             or too faulty to compare. A refusal is never reported as a pass.

        The sweep's own outcome outranks the comparison: a sweep that broke (1) or completed
        nothing (3) reports that, because its comparison would mean nothing either way.
        """;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--help" or "-h" or "-?":
                    options = options with { ShowHelp = true };
                    break;
                case "--allow-missing-models":
                    options = options with { AllowMissingModels = true };
                    break;
                case "--archive-raw":
                    options = options with { ArchiveRaw = true };
                    break;
                case "--config":
                    options = options with { ConfigPath = TakeValue(args, ref i) };
                    break;
                case "--eval-dir":
                    options = options with { EvalDir = TakeValue(args, ref i) };
                    break;
                case "--results-dir":
                    options = options with { ResultsDir = TakeValue(args, ref i) };
                    break;
                case "--models":
                    options = options with { Models = TakeList(args, ref i) };
                    break;
                case "--variants":
                    options = options with { Variants = TakeList(args, ref i) };
                    break;
                case "--tasks":
                    options = options with { Tasks = TakeList(args, ref i) };
                    break;
                case "--seeds":
                    options = options with { Seeds = TakeInt(args, ref i) };
                    break;
                case "--parallel":
                    options = options with { MaxParallelRuns = TakeInt(args, ref i) };
                    break;
                case "--timeout-min":
                    options = options with { PerRunTimeoutMinutes = TakeInt(args, ref i) };
                    break;
                case "--host-publish-dir":
                    options = options with { HostPublishDir = TakeValue(args, ref i) };
                    break;
                case "--env-file":
                    options = options with { EnvFile = TakeValue(args, ref i) };
                    break;
                case "--extract-only":
                    options = options with { ExtractOnlyDir = TakeValue(args, ref i) };
                    break;
                case "--compare":
                    options = options with { CompareBaselineDir = TakeValue(args, ref i) };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[i]}'. Try --help.");
            }
        }

        return options;
    }

    /// <summary>Overlays these CLI switches onto a loaded config.</summary>
    public EvalRunnerConfig ApplyTo(EvalRunnerConfig config)
    {
        var host = config.Host;
        if (HostPublishDir is not null)
        {
            host = host with { PublishDir = HostPublishDir };
        }

        if (EnvFile is not null)
        {
            host = host with { EnvFile = EnvFile };
        }

        var merged = config with
        {
            EvalDir = EvalDir ?? config.EvalDir,
            ResultsDir = ResultsDir ?? config.ResultsDir,
            Models = Models ?? config.Models,
            Variants = SelectVariants(config.Variants),
            Tasks = SelectTasks(config.Tasks),
            Seeds = Seeds ?? config.Seeds,
            MaxParallelRuns = MaxParallelRuns ?? config.MaxParallelRuns,
            PerRunTimeoutMinutes = PerRunTimeoutMinutes ?? config.PerRunTimeoutMinutes,
            AllowMissingModels = AllowMissingModels || config.AllowMissingModels,
            ArchiveRaw = ArchiveRaw || config.ArchiveRaw,
            Host = host,
        };
        merged.Validate();
        return merged;
    }

    /// <summary>
    /// The configured variants narrowed to <c>--variants</c>. A variant carries the arguments that
    /// DEFINE it, so the switch can only select among what the config declares — and a name that
    /// selects nothing is an error, because a typo would otherwise sweep a different experiment than
    /// the operator asked for and still exit 0.
    /// </summary>
    private IReadOnlyList<VariantConfig> SelectVariants(IReadOnlyList<VariantConfig> configured)
    {
        if (Variants is not { Count: > 0 } wanted)
        {
            return configured;
        }

        EnsureAllKnown("--variants", wanted, [.. configured.Select(v => v.Name)]);
        return [.. configured.Where(v => wanted.Contains(v.Name, StringComparer.Ordinal))];
    }

    /// <summary>
    /// The task axis from <c>--tasks</c>: a filter over the configured ids, or the axis itself when
    /// the config declares none. Unlike a variant a task id needs no configuration to be meaningful —
    /// it resolves straight to <c>{evalDir}/tasks/{id}/</c> — so setting it is the useful reading.
    /// </summary>
    private IReadOnlyList<string>? SelectTasks(IReadOnlyList<string>? configured)
    {
        if (Tasks is not { Count: > 0 } wanted)
        {
            return configured;
        }

        if (configured is not { Count: > 0 })
        {
            return wanted;
        }

        EnsureAllKnown("--tasks", wanted, configured);
        return [.. configured.Where(id => wanted.Contains(id, StringComparer.Ordinal))];
    }

    private static void EnsureAllKnown(string switchName, IReadOnlyList<string> wanted, IReadOnlyList<string> known)
    {
        var unknown = wanted.Where(name => !known.Contains(name, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            // InvalidOperationException, not ArgumentException: this is discovered while merging the
            // switches ONTO a loaded config, which is the same failure class as an out-of-range knob
            // and is reported through the same exit code, not as a parse error.
            throw new InvalidOperationException(
                $"{switchName} names {string.Join(", ", unknown)}, which the config does not declare. "
                    + $"Configured: {string.Join(", ", known)}."
            );
        }
    }

    private static string[] TakeList(string[] args, ref int i) =>
        TakeValue(args, ref i).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string TakeValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new ArgumentException($"Argument '{args[i]}' expects a value.");
        }

        return args[++i];
    }

    private static int TakeInt(string[] args, ref int i)
    {
        var name = args[i];
        var raw = TakeValue(args, ref i);
        return int.TryParse(raw, out var value)
            ? value
            : throw new ArgumentException($"Argument '{name}' expects an integer, got '{raw}'.");
    }
}
