using System.Text.Json;
using System.Text.Json.Nodes;
using TodoEval.Runner.Metrics;

namespace TodoEval.Runner;

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
    public required JsonObject ModePayload { get; init; }
    public required string ModeName { get; init; }
    public required string TaskTemplate { get; init; }
    public required BoardShapeExpectation? ExpectedBoard { get; init; }

    public static EvalAssets Load(string evalDir, string expectedModeName)
    {
        var modePath = Path.Combine(evalDir, "mode.json");
        var taskPath = Path.Combine(evalDir, "task.md");
        var expectedBoardPath = Path.Combine(evalDir, "expected-board.json");

        if (!File.Exists(modePath))
        {
            throw new FileNotFoundException(
                $"mode.json not found in eval dir '{evalDir}'. The eval asset set (mode.json, task.md, "
                    + "expected-board.json) is delivered by the todo-eval mode work item; point --eval-dir at it.",
                modePath
            );
        }

        if (!File.Exists(taskPath))
        {
            throw new FileNotFoundException($"task.md not found in eval dir '{evalDir}'.", taskPath);
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

        BoardShapeExpectation? expectedBoard = null;
        if (File.Exists(expectedBoardPath))
        {
            expectedBoard = BoardShapeExpectation.Load(expectedBoardPath);
        }

        return new EvalAssets
        {
            ModePayload = modePayload,
            ModeName = modeName,
            TaskTemplate = ExtractTaskMessage(File.ReadAllText(taskPath), taskPath),
            ExpectedBoard = expectedBoard,
        };
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
