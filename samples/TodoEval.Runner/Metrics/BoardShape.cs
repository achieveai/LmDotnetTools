using System.Text.Json;

namespace TodoEval.Runner.Metrics;

/// <summary>
/// The final todo board of a run, deserialized from the host's persisted snapshot (the
/// <c>todo.board</c> property in the conversation's <c>metadata.json</c>, or the
/// GET /api/conversations/{id}/todos payload). Reads are case-insensitive on purpose: the snapshot
/// root is unpinned (PascalCase in practice) while task rows are pinned camelCase, and pre-#583
/// blobs used PascalCase rows.
/// </summary>
internal sealed record BoardSnapshot
{
    public IReadOnlyList<BoardTask> Tasks { get; init; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static BoardSnapshot Parse(string json) =>
        JsonSerializer.Deserialize<BoardSnapshot>(json, ReadOptions)
        ?? throw new InvalidOperationException("todo board snapshot parsed to null.");

    /// <summary>
    /// Flattens the task tree into the spec's per-task rows (depth is 1-based: top-level = 1).
    /// Every completion check in metrics-spec.md is defined over these four facts.
    /// </summary>
    public IReadOnlyList<FlatTask> Flatten()
    {
        var rows = new List<FlatTask>();
        Walk(Tasks, depth: 1, rows);
        return rows;
    }

    private static void Walk(IReadOnlyList<BoardTask> tasks, int depth, List<FlatTask> rows)
    {
        foreach (var task in tasks)
        {
            rows.Add(new FlatTask(depth, task.Status, task.Notes.Count, task.SubTasks.Count));
            Walk(task.SubTasks, depth + 1, rows);
        }
    }
}

internal sealed record BoardTask
{
    public string Status { get; init; } = "";
    public IReadOnlyList<BoardTask> SubTasks { get; init; } = [];

    /// <summary>Note shape is irrelevant to the metrics — only the count is scored.</summary>
    public IReadOnlyList<JsonElement> Notes { get; init; } = [];
}

/// <summary>One flattened board row: everything the completion checks look at.</summary>
internal readonly record struct FlatTask(int Depth, string Status, int NoteCount, int ChildCount);

/// <summary>
/// The committed <c>evals/todo-eval/expected-board.json</c> fixture
/// (schema <c>todo-eval/expected-board@1</c>, owned by the #618 asset set — consumed verbatim,
/// never authored here). Shape-based on purpose: it pins counts, depths, statuses and note
/// minimums, never free text, so any topic word and any task phrasing can pass.
/// Every check is optional — omitted fixture fields are not evaluated, exactly like the oracle.
/// </summary>
internal sealed record BoardShapeExpectation
{
    public string? Schema { get; init; }
    public BoardChecks? Board { get; init; }
    public ConversationChecks? Conversation { get; init; }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BoardShapeExpectation Load(string path) =>
        JsonSerializer.Deserialize<BoardShapeExpectation>(File.ReadAllText(path), ReadOptions)
        ?? throw new InvalidOperationException($"{path} parsed to null.");

    /// <summary>
    /// Evaluates the flattened board plus the conversation-derived block flags. An empty list means
    /// completion. Failure strings mirror the reference oracle's wording so a run scored by both
    /// implementations diffs clean.
    /// </summary>
    public IReadOnlyList<string> Evaluate(IReadOnlyList<FlatTask> flat, bool blockRecorded, bool blockCleared)
    {
        var failures = new List<string>();
        var board = Board;

        if (board?.TopLevelTaskCount is { } expectedTop)
        {
            var actualTop = flat.Count(t => t.Depth == 1);
            if (actualTop != expectedTop)
            {
                failures.Add($"topLevelTaskCount: expected {expectedTop}, found {actualTop}");
            }
        }

        if (board?.SubtaskCountsSorted is { } expectedCounts)
        {
            var actual = flat.Where(t => t.Depth == 1).Select(t => t.ChildCount).Order().ToArray();
            var expected = expectedCounts.Order().ToArray();
            if (!actual.SequenceEqual(expected))
            {
                failures.Add(
                    $"subtaskCountsSorted: expected [{string.Join(",", expected)}], "
                        + $"found [{string.Join(",", actual)}]"
                );
            }
        }

        if (board?.Level3 is { } level3)
        {
            var parents = flat.Count(t => t.Depth == 2 && t.ChildCount >= level3.MinChildrenPerParent);
            if (parents < level3.MinParents)
            {
                failures.Add(
                    $"level3: expected >= {level3.MinParents} depth-2 task(s) with "
                        + $">= {level3.MinChildrenPerParent} children, found {parents}"
                );
            }
        }

        if (board?.AllTasksCompleted == true)
        {
            var notCompleted = flat.Count(t => !StatusIs(t.Status, "Completed"));
            if (notCompleted > 0)
            {
                failures.Add($"allTasksCompleted: {notCompleted} task(s) not Completed");
            }
        }

        if (board?.MinNotesPerSubtask is { } minNotes)
        {
            var shortOnNotes = flat.Count(t => t.Depth >= 2 && t.NoteCount < minNotes);
            if (shortOnNotes > 0)
            {
                failures.Add($"minNotesPerSubtask: {shortOnNotes} subtask(s) have fewer than {minNotes} note(s)");
            }
        }

        if (board?.MaxBlockedTasks is { } maxBlocked)
        {
            var blocked = CountBlocked(flat);
            if (blocked > maxBlocked)
            {
                failures.Add($"maxBlockedTasks: expected <= {maxBlocked}, found {blocked}");
            }
        }

        if (Conversation?.RequireBlockRecorded == true && !blockRecorded)
        {
            failures.Add("requireBlockRecorded: no successful block-task call with a non-empty blockedBy was found");
        }

        if (Conversation?.RequireBlockCleared == true && !blockCleared)
        {
            failures.Add("requireBlockCleared: the recorded block was never cleared (or was never recorded)");
        }

        return failures;
    }

    /// <summary>Blocked rows on the final board; feeds <c>blockCleared = blockRecorded AND blocked == 0</c>.</summary>
    public static int CountBlocked(IReadOnlyList<FlatTask> flat) => flat.Count(t => StatusIs(t.Status, "Blocked"));

    private static bool StatusIs(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
}

internal sealed record BoardChecks
{
    public int? TopLevelTaskCount { get; init; }
    public IReadOnlyList<int>? SubtaskCountsSorted { get; init; }
    public Level3Check? Level3 { get; init; }
    public bool? AllTasksCompleted { get; init; }
    public int? MinNotesPerSubtask { get; init; }
    public int? MaxBlockedTasks { get; init; }
}

internal sealed record Level3Check
{
    public int MinParents { get; init; }
    public int MinChildrenPerParent { get; init; }
}

internal sealed record ConversationChecks
{
    public bool? RequireBlockRecorded { get; init; }
    public bool? RequireBlockCleared { get; init; }
}
