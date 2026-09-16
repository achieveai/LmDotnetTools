using System.Text.Json;
using System.Text.Json.Nodes;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner;

/// <summary>
/// The task's own <c>meta.json</c> (tasks/README.md): what the runner needs to run and judge it.
/// Absent in the single-task layout, which predates the file.
/// </summary>
internal sealed record TaskMeta
{
    /// <summary>The family the task belongs to (<c>coding-py</c>, <c>research</c>, <c>steer</c>, ...).</summary>
    public string? Family { get; init; }

    /// <summary><c>dev</c> or <c>test</c>: which split the task may be tuned against.</summary>
    public string? Split { get; init; }

    /// <summary>
    /// Seed words substituted for <c>{SEED}</c>, one per repeat, so repeats of one task are not
    /// byte-identical. Seed <c>i</c> uses <c>Seeds[i % Seeds.Count]</c>, matching the topic axis.
    /// </summary>
    public IReadOnlyList<string> Seeds { get; init; } = [];

    /// <summary>
    /// Compactions a COMPACTING variant is expected to take on this task under the window clamp. A run
    /// below the floor never exercised the strategy under test, so J0 marks it invalid rather than
    /// scoring it. Null means the task states no expectation and the floor never applies.
    /// </summary>
    public int? MinCompactions { get; init; }

    /// <summary>The task's own wall-clock budget, overriding the sweep-wide one. Null = use the sweep's.</summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>
    /// Delay before the <c>## steer</c> correction is sent, measured from the first message. Null with
    /// a steer section present means "send it as soon as the run is under way" (zero delay).
    /// </summary>
    public int? SteerAfterSeconds { get; init; }

    /// <summary>Seed word for a zero-based repeat index, or null when the task declares no seeds.</summary>
    public string? SeedForIndex(int seedIndex) => Seeds.Count == 0 ? null : Seeds[seedIndex % Seeds.Count];
}

/// <summary>
/// One task in the sweep's task axis: where it lives, the user message template (with its
/// <c>{TOPIC}</c>/<c>{SEED}</c> placeholders), the optional mid-run correction, its <c>meta.json</c>,
/// and the board shape its runs are judged against.
/// </summary>
internal sealed record EvalTaskAsset
{
    /// <summary>The task id, or null for the single unnamed task of the <c>{EvalDir}/task.md</c> layout.</summary>
    public required string? Id { get; init; }

    /// <summary>The directory the task's assets live in: its fixtures, checker and hidden material.</summary>
    public required string Dir { get; init; }

    public required string Template { get; init; }

    /// <summary>
    /// The <c>## steer</c> correction sent as a SECOND message while the run is still active, or null
    /// when the task has no such section.
    /// </summary>
    public string? Steer { get; init; }

    /// <summary>The task's <c>meta.json</c>, or null in the single-task layout, which has none.</summary>
    public TaskMeta? Meta { get; init; }

    /// <summary>Null when the task ships no <c>expected-board.json</c>: the run has no board gate.</summary>
    public required BoardShapeExpectation? ExpectedBoard { get; init; }

    /// <summary>The fixture tree copied into every run's workspace, or null when the task ships none.</summary>
    public string? FixturesDir
    {
        get
        {
            var dir = Path.Combine(Dir, EvalAssets.FixturesDirName);
            return Directory.Exists(dir) ? dir : null;
        }
    }

    /// <summary>The task's J1 checker script, or null when the task ships none (then there is no J1 at all).</summary>
    public string? CheckScript
    {
        get
        {
            var path = Path.Combine(Dir, EvalAssets.CheckScriptName);
            return File.Exists(path) ? path : null;
        }
    }
}

/// <summary>
/// The eval asset set owned by the Testing Mode work item (#618): <c>mode.json</c> (a
/// ChatModeCreateUpdate payload), <c>task.md</c> (with a <c>{TOPIC}</c> placeholder) and
/// <c>expected-board.json</c> (the shape the final todo board must satisfy). The runner treats
/// <c>mode.json</c> as an opaque payload — it validates only the two fields it needs to key
/// create-or-update on, and posts the rest verbatim, so mode-side additions (new DTO fields) never
/// require a runner change.
/// </summary>
internal sealed class EvalAssets
{
    /// <summary>The subdirectory a multi-task eval keeps its per-task assets under.</summary>
    public const string TasksDirName = "tasks";

    /// <summary>The per-task tree copied into a run's workspace. Its sibling <c>hidden/</c> never is.</summary>
    public const string FixturesDirName = "fixtures";

    /// <summary>The task's deterministic J1 checker (tasks/README.md): <c>pwsh check.ps1 -Workspace -Out</c>.</summary>
    public const string CheckScriptName = "check.ps1";

    /// <summary>The default <c>mode.json</c> leaf, overridable through <c>EvalRunnerConfig.ModeFile</c>.</summary>
    public const string DefaultModeFileName = "mode.json";

    public required JsonObject ModePayload { get; init; }
    public required string ModeName { get; init; }

    /// <summary>
    /// The tasks this sweep runs: exactly one unnamed entry in the single-task layout, or one entry
    /// per configured task id in the <c>tasks/</c> layout.
    /// </summary>
    public required IReadOnlyList<EvalTaskAsset> Tasks { get; init; }

    public static EvalAssets Load(
        string evalDir,
        string expectedModeName,
        IReadOnlyList<string>? taskIds = null,
        string? modeFileName = null
    )
    {
        var modeFile = string.IsNullOrWhiteSpace(modeFileName) ? DefaultModeFileName : modeFileName;
        var modePath = Path.Combine(evalDir, modeFile);

        if (!File.Exists(modePath))
        {
            throw new FileNotFoundException(
                $"{modeFile} not found in eval dir '{evalDir}'. The eval asset set (mode.json, task.md, "
                    + "expected-board.json) is delivered by the todo-eval mode work item; point --eval-dir at it.",
                modePath
            );
        }

        var modePayload =
            JsonNode.Parse(File.ReadAllText(modePath), documentOptions: DocumentOptions) as JsonObject
            ?? throw new InvalidOperationException($"{modePath} did not parse to a JSON object.");

        var modeName = ReadRequiredString(modePayload, "name", modePath);
        _ = ReadRequiredString(modePayload, "systemPrompt", modePath);

        if (!string.Equals(modeName, expectedModeName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"mode.json declares name '{modeName}' but the runner is configured for mode '{expectedModeName}'. "
                    + "Refusing to create-or-update a mode the config does not name — that is how a typo would "
                    + "silently edit an unrelated mode."
            );
        }

        return new EvalAssets
        {
            ModePayload = modePayload,
            ModeName = modeName,
            Tasks = LoadTasks(evalDir, taskIds),
        };
    }

    /// <summary>
    /// The configured tasks, or the single unnamed task of the original layout when none is configured.
    /// A task's <c>task.md</c> is required (a typo'd id must not silently sweep nothing); its
    /// <c>expected-board.json</c> is optional and its absence means the run has NO board gate — the
    /// completion criterion is then reported as not measurable rather than failed.
    /// </summary>
    private static IReadOnlyList<EvalTaskAsset> LoadTasks(string evalDir, IReadOnlyList<string>? taskIds)
    {
        if (taskIds is not { Count: > 0 })
        {
            return [Read(id: null, evalDir, $"task.md not found in eval dir '{evalDir}'.")];
        }

        return
        [
            .. taskIds.Select(id =>
                Read(
                    id,
                    Path.Combine(evalDir, TasksDirName, id),
                    $"task.md not found for task '{id}': expected "
                        + $"'{Path.Combine(evalDir, TasksDirName, id, "task.md")}'."
                )
            ),
        ];

        static EvalTaskAsset Read(string? id, string dir, string missingMessage)
        {
            var taskPath = Path.Combine(dir, "task.md");
            if (!File.Exists(taskPath))
            {
                throw new FileNotFoundException(missingMessage, taskPath);
            }

            var boardPath = Path.Combine(dir, "expected-board.json");
            var metaPath = Path.Combine(dir, "meta.json");
            var (message, steer) = ExtractTaskMessage(File.ReadAllText(taskPath), taskPath);
            return new EvalTaskAsset
            {
                Id = id,
                Dir = dir,
                Template = message,
                Steer = steer,
                Meta = File.Exists(metaPath) ? LoadMeta(metaPath) : null,
                ExpectedBoard = File.Exists(boardPath) ? BoardShapeExpectation.Load(boardPath) : null,
            };
        }
    }

    private static TaskMeta LoadMeta(string path) =>
        JsonSerializer.Deserialize<TaskMeta>(File.ReadAllText(path), MetaOptions)
        ?? throw new InvalidOperationException($"{path} parsed to null.");

    private static readonly JsonSerializerOptions MetaOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The heading that begins the mid-run correction, per tasks/README.md.</summary>
    private const string SteerHeading = "## steer";

    /// <summary>
    /// task.md is header documentation, a <c>---</c> marker line, then the user message VERBATIM
    /// (task.md's own contract). Only the part below the first marker line is sent; a file with no
    /// marker is used whole, so a plain-message task file still works.
    /// <para>
    /// A <c>## steer</c> heading below the marker ENDS the first message and begins the correction the
    /// runner sends as a second message while the run is active. Everything above the heading is the
    /// task; everything below it is the steer. A task with no such heading returns a null steer.
    /// </para>
    /// </summary>
    internal static (string Message, string? Steer) ExtractTaskMessage(string taskFileText, string path)
    {
        var lines = taskFileText.Split('\n');
        var body = lines;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Normalize(lines[i]) == "---")
            {
                body = lines[(i + 1)..];
                break;
            }
        }

        var steerAt = Array.FindIndex(
            body,
            line => Normalize(line).StartsWith(SteerHeading, StringComparison.OrdinalIgnoreCase)
        );
        var message = string.Join('\n', steerAt < 0 ? body : body[..steerAt]).Trim();
        if (message.Length == 0)
        {
            throw new InvalidOperationException(
                $"{path} has nothing below its '---' marker (and above any '{SteerHeading}' heading) to "
                    + "send as the user message."
            );
        }

        var steer = steerAt < 0 ? null : string.Join('\n', body[(steerAt + 1)..]).Trim();
        if (steer is { Length: 0 })
        {
            throw new InvalidOperationException($"{path} has a '{SteerHeading}' heading but nothing below it.");
        }

        return (message, steer);
    }

    private static string Normalize(string line) => line.TrimEnd('\r').Trim();

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string ReadRequiredString(JsonObject payload, string camelCaseName, string path)
    {
        // mode.json is authored by hand; accept either camelCase (the wire casing) or PascalCase.
        var pascal = char.ToUpperInvariant(camelCaseName[0]) + camelCaseName[1..];
        var node = payload[camelCaseName] ?? payload[pascal];
        var value = node?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{path} is missing required string property '{camelCaseName}'.");
        }

        return value;
    }
}
