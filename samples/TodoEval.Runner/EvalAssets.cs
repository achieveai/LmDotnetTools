using System.Text.Json;
using System.Text.Json.Nodes;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner;

/// <summary>
/// One task in the sweep's task axis: the user message template (with its <c>{TOPIC}</c> placeholder)
/// and the board shape its runs are judged against.
/// </summary>
internal sealed record EvalTaskAsset
{
    /// <summary>The task id, or null for the single unnamed task of the <c>{EvalDir}/task.md</c> layout.</summary>
    public required string? Id { get; init; }

    public required string Template { get; init; }

    /// <summary>Null when the task ships no <c>expected-board.json</c>: the run has no board gate.</summary>
    public required BoardShapeExpectation? ExpectedBoard { get; init; }
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

    public required JsonObject ModePayload { get; init; }
    public required string ModeName { get; init; }

    /// <summary>
    /// The tasks this sweep runs: exactly one unnamed entry in the single-task layout, or one entry
    /// per configured task id in the <c>tasks/</c> layout.
    /// </summary>
    public required IReadOnlyList<EvalTaskAsset> Tasks { get; init; }

    public static EvalAssets Load(string evalDir, string expectedModeName, IReadOnlyList<string>? taskIds = null)
    {
        var modePath = Path.Combine(evalDir, "mode.json");

        if (!File.Exists(modePath))
        {
            throw new FileNotFoundException(
                $"mode.json not found in eval dir '{evalDir}'. The eval asset set (mode.json, task.md, "
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
            return new EvalTaskAsset
            {
                Id = id,
                Template = ExtractTaskMessage(File.ReadAllText(taskPath), taskPath),
                ExpectedBoard = File.Exists(boardPath) ? BoardShapeExpectation.Load(boardPath) : null,
            };
        }
    }

    /// <summary>
    /// task.md is header documentation, a <c>---</c> marker line, then the user message VERBATIM
    /// (task.md's own contract). Only the part below the first marker line is sent; a file with no
    /// marker is used whole, so a plain-message task file still works.
    /// </summary>
    internal static string ExtractTaskMessage(string taskFileText, string path)
    {
        var lines = taskFileText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---")
            {
                var message = string.Join('\n', lines[(i + 1)..]).Trim();
                if (message.Length == 0)
                {
                    throw new InvalidOperationException($"{path} has a '---' marker but nothing below it.");
                }

                return message;
            }
        }

        return taskFileText.Trim();
    }

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
