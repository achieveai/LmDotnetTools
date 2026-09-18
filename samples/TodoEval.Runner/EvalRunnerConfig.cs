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

    /// <summary>
    /// Host option-sets swept, one ISOLATED host process each. Every compaction knob is host-level
    /// (<c>CompactionHostSetup</c> binds the <c>Compaction</c> section onto a DI singleton), so two
    /// strategies can only be compared by launching two hosts — this is that axis. The default is the
    /// single empty <c>default</c> variant, which reproduces the pre-variant sweep exactly.
    /// </summary>
    public IReadOnlyList<VariantConfig> Variants { get; init; } = [VariantConfig.Default];

    /// <summary>
    /// Task ids swept, each resolving to <c>{EvalDir}/tasks/{id}/task.md</c> with an optional
    /// <c>{EvalDir}/tasks/{id}/expected-board.json</c>. Null or empty keeps the single-task layout:
    /// <c>{EvalDir}/task.md</c> and <c>{EvalDir}/expected-board.json</c>.
    /// </summary>
    public IReadOnlyList<string>? Tasks { get; init; }

    /// <summary>Seeds per model (N in the N x M sweep).</summary>
    public int Seeds { get; init; } = 5;

    /// <summary>Hard per-run wall-clock budget; a run that exceeds it is recorded as <c>TimedOut</c>.</summary>
    public int PerRunTimeoutMinutes { get; init; } = 20;

    /// <summary>1 = sequential (default). Higher values run that many conversations concurrently against the one isolated host.</summary>
    public int MaxParallelRuns { get; init; } = 1;

    /// <summary>Display name identifying the eval mode among the host's chat modes (create-or-update key; never a system mode).</summary>
    public string ModeName { get; init; } = "todo-eval";

    /// <summary>
    /// The chat-mode payload file inside <see cref="EvalDir"/>, posted verbatim to
    /// <c>/api/chat-modes</c> once per host. Its <c>name</c> must equal <see cref="ModeName"/>.
    /// </summary>
    public string ModeFile { get; init; } = EvalAssets.DefaultModeFileName;

    /// <summary>Name of the workspace the runner creates (or reuses) on the isolated host.</summary>
    public string WorkspaceName { get; init; } = "todo-eval";

    /// <summary>
    /// Where a per-run workspace directory is created, for tasks that ship a <c>fixtures/</c> tree.
    /// Each run gets <c>{WorkspacesRoot}/{leaf}</c>, the leaf derived from its run key.
    /// <para>
    /// This MUST be the same directory the host resolves a workspace leaf under — its
    /// <c>SandboxGateway:WorkspaceBasePath</c> — or the agent would work in one tree while the J1
    /// checker judges another. A task with no fixtures ignores this entirely and keeps using the one
    /// shared <see cref="WorkspaceName"/> workspace, which is what the todo-eval layout does.
    /// </para>
    /// </summary>
    public string WorkspacesRoot { get; init; } = @"B:\sandbox-workspaces\workspaces";

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

        // Anchor relative paths to the checkout the config file lives in, not to the current directory.
        // Path.GetFullPath(string) resolves against the process working directory, so without this the
        // same config expands differently depending on where the runner was invoked from.
        config = config.ResolvePathTokens(
            string.IsNullOrWhiteSpace(configPath) ? null : Path.GetDirectoryName(Path.GetFullPath(configPath))
        );
        config.Validate();
        return config;
    }

    /// <summary>
    ///     Expands <c>{evalDir}</c> and <c>{repoRoot}</c> in the forwarded host arguments and environment to
    ///     absolute paths, once, at load.
    /// </summary>
    /// <remarks>
    ///     The host runs with its working directory set to a scratch instance directory, not the checkout, so a
    ///     relative path in <see cref="HostConfig.ExtraArgs"/> resolves against somewhere the repository is not
    ///     and the host's own <c>File.ReadAllText</c> fails. Writing the absolute path into the committed config
    ///     instead makes it work only in the worktree it was written in. Expanding a token here keeps the config
    ///     portable and still hands the child an absolute path.
    /// </remarks>
    /// <param name="anchor">
    ///     Directory a relative <see cref="EvalDir"/> resolves against; null uses the working directory. The
    ///     loader passes the config file's own directory so a checked-in config expands to the checkout it
    ///     belongs to however the runner was invoked.
    /// </param>
    public EvalRunnerConfig ResolvePathTokens(string? anchor = null)
    {
        var root = FindRepoRoot(anchor ?? Path.GetFullPath(".")) ?? anchor ?? Path.GetFullPath(".");
        var evalDir = Path.GetFullPath(EvalDir, root);
        var repoRoot = FindRepoRoot(evalDir) ?? root;
        var workspacesRoot = Path.GetFullPath(
            WorkspacesRoot.Replace("{repoRoot}", repoRoot, StringComparison.OrdinalIgnoreCase),
            root
        );

        string Expand(string value) =>
            value
                .Replace("{evalDir}", evalDir, StringComparison.OrdinalIgnoreCase)
                .Replace("{repoRoot}", repoRoot, StringComparison.OrdinalIgnoreCase);

        IReadOnlyList<string> ExpandArgs(IReadOnlyList<string> args) => [.. args.Select(Expand)];

        IReadOnlyDictionary<string, string> ExpandEnv(IReadOnlyDictionary<string, string> env) =>
            env.ToDictionary(kvp => kvp.Key, kvp => Expand(kvp.Value), StringComparer.OrdinalIgnoreCase);

        return this with
        {
            WorkspacesRoot = workspacesRoot,
            Host = Host with { ExtraArgs = ExpandArgs(Host.ExtraArgs), ExtraEnv = ExpandEnv(Host.ExtraEnv) },
            Variants =
            [
                .. Variants.Select(v =>
                    v with
                    {
                        ExtraArgs = ExpandArgs(v.ExtraArgs),
                        ExtraEnv = ExpandEnv(v.ExtraEnv),
                    }
                ),
            ],
        };
    }

    /// <summary>The nearest ancestor of <paramref name="start"/> holding a <c>.git</c> entry, or null.</summary>
    private static string? FindRepoRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
        }

        return null;
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

        ValidateVariants();
        ValidateTasks();
    }

    private void ValidateVariants()
    {
        if (Variants.Count == 0 || Variants.Any(v => string.IsNullOrWhiteSpace(v.Name)))
        {
            throw new InvalidOperationException("variants must be a non-empty list of entries with a non-blank name.");
        }

        // A duplicate name would make two option-sets share a run key and a manifest row, so the
        // archive could no longer say which host produced which run.
        if (Variants.Select(v => v.Name).Distinct(StringComparer.Ordinal).Count() != Variants.Count)
        {
            throw new InvalidOperationException(
                "variants contains duplicate names; each variant is one host option-set."
            );
        }
    }

    private void ValidateTasks()
    {
        if (Tasks is not { Count: > 0 } tasks)
        {
            return;
        }

        if (tasks.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("tasks must contain only non-blank task ids.");
        }

        if (tasks.Distinct(StringComparer.Ordinal).Count() != tasks.Count)
        {
            throw new InvalidOperationException("tasks contains duplicates; each task id is one sweep axis entry.");
        }

        // A task id becomes a path segment under {EvalDir}/tasks/, so a separator or a '..' would read
        // assets from outside the eval corpus the fingerprints pin.
        if (tasks.Any(id => id.Contains('/') || id.Contains('\\') || id.Contains("..", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "a task id must be a single path segment: '/', '\\' and '..' are rejected because the id "
                    + "resolves under {evalDir}/tasks/ and must not escape the eval corpus."
            );
        }
    }

    /// <summary>Topic for a given zero-based seed index.</summary>
    public string TopicForSeed(int seedIndex) => Topics[seedIndex % Topics.Count];

    public string ResolveResultsDir() => ResultsDir ?? Path.Combine(EvalDir, "results");
}

/// <summary>
/// One host option-set in the variant axis: a name plus the extra command-line arguments and
/// environment variables the isolated host for this cell is launched with.
/// </summary>
/// <remarks>
/// The arguments are appended AFTER <see cref="HostConfig.ExtraArgs"/>, and the environment entries
/// overwrite <see cref="HostConfig.ExtraEnv"/>, so a variant always wins over the sweep-wide host
/// configuration it specialises. That is the whole point of the axis: the shared block carries what
/// every host needs (gateway paths, workspace), the variant carries the one thing under test.
/// </remarks>
internal sealed record VariantConfig
{
    /// <summary>The name of the variant every sweep has when none is configured.</summary>
    public const string DefaultName = "default";

    /// <summary>The empty variant: no extra arguments, no extra environment, no behaviour change.</summary>
    public static readonly VariantConfig Default = new();

    public string Name { get; init; } = DefaultName;

    /// <summary>Extra <c>--Section:Key=value</c> host arguments, e.g. <c>--Compaction:Mode=Compact</c>.</summary>
    public IReadOnlyList<string> ExtraArgs { get; init; } = [];

    /// <summary>Extra environment variables for this variant's host process.</summary>
    public IReadOnlyDictionary<string, string> ExtraEnv { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this option-set is expected to COMPACT. Only then does a task's
    /// <c>minCompactions</c> floor apply, and a run below it is J0-invalid because it never exercised
    /// the strategy under test. Declared rather than inferred from the arguments: reading intent out
    /// of an argument string would silently mislabel every knob spelling the parser did not expect.
    /// </summary>
    public bool Compacts { get; init; }

    /// <summary>True for the untouched default variant — the shape a pre-variant sweep had.</summary>
    public bool IsDefault =>
        string.Equals(Name, DefaultName, StringComparison.Ordinal) && ExtraArgs.Count == 0 && ExtraEnv.Count == 0;

    /// <summary>
    /// The variant's identity for a comparison: name, then its arguments in order (order decides which
    /// wins), then its environment sorted by key (a dictionary has no order to preserve), and finally
    /// <see cref="Compacts"/> — which changes which of its runs count as valid, so two sweeps that
    /// disagree about it are not judging the same thing even when they ran the same arguments.
    /// </summary>
    public string Signature() =>
        $"{Name}[{string.Join(" ", ExtraArgs)}]"
        + $"{{{string.Join(" ", ExtraEnv.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value}"))}}}"
        + (Compacts ? "+compacts" : "");
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

    /// <summary>
    /// Whether the host may stand up its sandbox gateway. False (the default) pins
    /// <c>--SandboxGateway:AutoSpawn=false</c>, which is right for a pure todo-board eval: spawning is
    /// non-fatal-but-noisy and the task needs no file or shell tool. True omits that argument, leaving
    /// the decision to the host's own configuration — so a CODING eval supplies the gateway and agent
    /// binaries through <see cref="ExtraArgs"/> and gets a real sandbox.
    /// </summary>
    public bool Sandbox { get; init; }

    /// <summary>
    /// This host configuration specialised for one variant: the variant's arguments appended after the
    /// shared ones and its environment overlaid on the shared one, so the variant wins both times.
    /// </summary>
    public HostConfig For(VariantConfig variant)
    {
        var env = new Dictionary<string, string>(ExtraEnv, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in variant.ExtraEnv)
        {
            env[key] = value;
        }

        return this with
        {
            ExtraArgs = [.. ExtraArgs, .. variant.ExtraArgs],
            ExtraEnv = env,
        };
    }
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
