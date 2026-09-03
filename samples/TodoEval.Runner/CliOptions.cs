namespace TodoEval.Runner;

/// <summary>Parsed command line. Parsing is separated from <c>Program</c> so it is unit-testable.</summary>
internal sealed record CliOptions
{
    public string? ConfigPath { get; init; }
    public string? EvalDir { get; init; }
    public string? ResultsDir { get; init; }
    public IReadOnlyList<string>? Models { get; init; }
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
          --seeds <n>                Seeds per model (default: 5)
          --parallel <n>             Max concurrent runs (default: 1 = sequential)
          --timeout-min <n>          Hard per-run timeout in minutes (default: 20)
          --host-publish-dir <dir>   Pre-published LmStreaming.Sample binaries to copy instead of publishing
          --env-file <path>          .env handed to the host (LMSTREAMING_ENV_FILE) for provider keys
          --allow-missing-models     Skip models the host does not offer instead of failing the sweep
          --archive-raw              Archive transcripts verbatim (NOT metric-preserving-redacted).
                                     The result carries model prose - keep it off-repo.
          --extract-only <sweepDir>  Re-run metrics extraction over an archived sweep (no host, no runs)
          --help                     This text

        Exit codes:
          0  the sweep produced at least one Completed run and no run hit a harness error
             (extract-only: extraction succeeded)
          1  a run failed with a harness error, or the sweep itself failed to run
          2  invalid command line
          3  no run completed - every run timed out, errored, or was interrupted; the archive
             is written but must not gate anything as a successful baseline
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
                    options = options with
                    {
                        Models = TakeValue(args, ref i)
                            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    };
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
