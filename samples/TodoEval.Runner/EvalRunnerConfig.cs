using System.Text.Json;
using System.Text.Json.Serialization;

namespace TodoEval.Runner;

/// <summary>
/// Configuration for one eval sweep. Loaded from an optional JSON file (camelCase, comments
/// allowed) and then overridden by command-line switches; every knob has a default so
/// <c>dotnet run --project samples/TodoEval.Runner</c> works with no file at all.
/// </summary>
internal sealed record EvalRunnerConfig
{
    /// <summary>Directory holding the eval assets: <c>mode.json</c>, <c>task.md</c>, <c>expected-board.json</c>.</summary>
    public string EvalDir { get; init; } = Path.Combine("evals", "todo-eval");

    /// <summary>Where sweep output lands. Defaults to <c>{EvalDir}/results</c>.</summary>
    public string? ResultsDir { get; init; }

    /// <summary>Model ids swept, passed per conversation as the provision-time provider id (#565's per-call model channel on this host).</summary>
    public IReadOnlyList<string> Models { get; init; } = ["deepseek-v4-flash", "gpt-5.6-luna"];

    /// <summary>
    /// Topics substituted for <c>{TOPIC}</c>; seed <c>i</c> uses <c>Topics[i % Topics.Count]</c> so
    /// the seed axis stays meaningful when the two counts differ.
    /// </summary>
    public IReadOnlyList<string> Topics { get; init; } =
    [
        "planning a two-day team offsite",
        "migrating a blog from WordPress to a static site generator",
        "launching the beta of a note-taking mobile app",
        "setting up a small home-lab server rack",
        "organizing a 200-person charity 5K run",
    ];

    /// <summary>Seeds per model (N in the N x M sweep).</summary>
    public int Seeds { get; init; } = 5;

    /// <summary>Hard per-run wall-clock budget; a run that exceeds it is recorded as <c>TimedOut</c>.</summary>
    public int PerRunTimeoutMinutes { get; init; } = 20;

    /// <summary>1 = sequential (default). Higher values run that many conversations concurrently against the one isolated host.</summary>
    public int MaxParallelRuns { get; init; } = 1;

    /// <summary>Display name identifying the eval mode among the host's chat modes (create-or-update key; never a system mode).</summary>
    public string ModeName { get; init; } = "todo-eval";

    /// <summary>Name of the workspace the runner creates (or reuses) on the isolated host.</summary>
    public string WorkspaceName { get; init; } = "todo-eval";

    public HostConfig Host { get; init; } = new();

    public PollConfig Poll { get; init; } = new();

    /// <summary>
    /// When true, models the host does not report as available are skipped with a warning instead of
    /// failing the sweep. Default false: a silent skip would archive a "baseline" missing a model.
    /// </summary>
    public bool AllowMissingModels { get; init; }

    /// <summary>
    /// When true the archived <c>conversations/</c> tree is a verbatim copy. Default false: the
    /// committed archive is metric-preserving-redacted (metrics-spec.md, "Redaction").
    /// </summary>
    public bool ArchiveRaw { get; init; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static EvalRunnerConfig Load(string? configPath)
    {
        var config = new EvalRunnerConfig();
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            if (!File.Exists(configPath))
            {
                throw new FileNotFoundException($"Eval runner config not found: {configPath}", configPath);
            }

            config =
                JsonSerializer.Deserialize<EvalRunnerConfig>(File.ReadAllText(configPath), ReadOptions)
                ?? throw new InvalidOperationException($"Eval runner config parsed to null: {configPath}");
        }

        config.Validate();
        return config;
    }

    public void Validate()
    {
        if (Seeds < 1)
        {
            throw new InvalidOperationException($"seeds must be >= 1 (got {Seeds}).");
        }

        if (Models.Count == 0 || Models.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("models must be a non-empty list of non-blank model ids.");
        }

        if (Models.Distinct(StringComparer.Ordinal).Count() != Models.Count)
        {
            throw new InvalidOperationException("models contains duplicates; each model is one sweep axis entry.");
        }

        if (Topics.Count == 0 || Topics.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("topics must be a non-empty list of non-blank strings.");
        }

        if (PerRunTimeoutMinutes < 1)
        {
            throw new InvalidOperationException($"perRunTimeoutMinutes must be >= 1 (got {PerRunTimeoutMinutes}).");
        }

        if (MaxParallelRuns < 1)
        {
            throw new InvalidOperationException($"maxParallelRuns must be >= 1 (got {MaxParallelRuns}).");
        }

        if (string.IsNullOrWhiteSpace(ModeName))
        {
            throw new InvalidOperationException("modeName must be non-blank.");
        }
    }

    /// <summary>Topic for a given zero-based seed index.</summary>
    public string TopicForSeed(int seedIndex) => Topics[seedIndex % Topics.Count];

    public string ResolveResultsDir() => ResultsDir ?? Path.Combine(EvalDir, "results");
}

/// <summary>How the isolated LmStreaming.Sample host instance is obtained and launched.</summary>
internal sealed record HostConfig
{
    /// <summary>
    /// Directory of already-published LmStreaming.Sample binaries to copy into the isolated instance
    /// dir. When null, the runner publishes <see cref="ProjectPath"/> itself. NEVER point this at a
    /// live deployment's runtime dir expecting shared state — the copy is the isolation.
    /// </summary>
    public string? PublishDir { get; init; }

    /// <summary>Path (relative to repo root or absolute) of the host project to publish when <see cref="PublishDir"/> is null.</summary>
    public string ProjectPath { get; init; } = Path.Combine("samples", "LmStreaming.Sample");

    public string Configuration { get; init; } = "Release";

    /// <summary>0 picks a free ephemeral port.</summary>
    public int Port { get; init; }

    /// <summary>
    /// Explicit env file handed to the host as <c>LMSTREAMING_ENV_FILE</c>. Required for real
    /// providers because the host's own .env walk-up starts at its (temp) binary dir and finds
    /// nothing there. Null = no env file (fine for the keyless <c>test</c> provider).
    /// </summary>
    public string? EnvFile { get; init; }

    public string Environment { get; init; } = "Production";

    public int ReadinessTimeoutSeconds { get; init; } = 120;

    /// <summary>
    /// Grace between the last run finishing and the host process being killed, so debounced
    /// persistence (notably the todo-board metadata writer) flushes to disk first.
    /// </summary>
    public int ShutdownGraceSeconds { get; init; } = 10;

    /// <summary>Extra <c>--Section:Key=value</c> style command-line arguments for the host.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Extra environment variables for the host process.</summary>
    public IReadOnlyDictionary<string, string> ExtraEnv { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Status-polling cadence, mirroring the review daemon's poll-to-terminal settings.</summary>
internal sealed record PollConfig
{
    public int InitialIntervalSeconds { get; init; } = 2;
    public int MaxIntervalSeconds { get; init; } = 15;

    /// <summary>
    /// How long an <c>Interrupted</c> reading is re-polled before it is believed. The host records an
    /// accepted input before draining it into a run, so the first poll after send can legitimately
    /// read <c>Interrupted</c> for a run that is about to start.
    /// </summary>
    public int InterruptedGraceSeconds { get; init; } = 45;

    public int InterruptedConfirmDelaySeconds { get; init; } = 5;

    [JsonIgnore]
    public TimeSpan InitialInterval => TimeSpan.FromSeconds(InitialIntervalSeconds);

    [JsonIgnore]
    public TimeSpan MaxInterval => TimeSpan.FromSeconds(MaxIntervalSeconds);

    [JsonIgnore]
    public TimeSpan InterruptedGrace => TimeSpan.FromSeconds(InterruptedGraceSeconds);

    [JsonIgnore]
    public TimeSpan InterruptedConfirmDelay => TimeSpan.FromSeconds(InterruptedConfirmDelaySeconds);
}
