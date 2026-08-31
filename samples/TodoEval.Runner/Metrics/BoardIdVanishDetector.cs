using System.Text.RegularExpressions;

namespace TodoEval.Runner.Metrics;

/// <summary>One reported board-row loss: a not-found naming an id its own thread minted and never deleted.</summary>
internal sealed record BoardIdVanish
{
    public required string ThreadId { get; init; }
    public required string TaskId { get; init; }
    public required string Tool { get; init; }
}

/// <summary>
/// The transcript-side mirror of the task tool's <c>TodoBoardIdVanished</c> detector (#621 Part B),
/// per metrics-spec.md "Board id vanished". One instance per thread: it accumulates the ids that
/// thread minted and has not deleted, and answers whether a given not-found names one of them.
/// </summary>
/// <remarks>
/// <para>
/// The count this feeds is a LOWER BOUND, one-directionally. Two gaps remain, both documented in the
/// spec and repeated in the emitted note: a row minted in a process whose transcript is not in this
/// store is invisible, and a thread that starts from a rehydrated board knows nothing of ids that
/// predate its own transcript. The server-side detector has neither, which is why the note tells the
/// reader to grep the host log for the event name too.
/// </para>
/// <para>
/// A third gap — <c>bulk-initialize</c> naming only its main tasks, never the subtask ids it minted —
/// closed when that tool started echoing every row it creates (#634 R1). The subtask rows are
/// indented deeper but carry the same <c>- Task &lt;id&gt;: </c> shape, so <see cref="BulkAdded" />
/// picks them up unchanged. A transcript recorded BEFORE that change still parses; it simply
/// contributes no subtask ids, exactly as it did then.
/// </para>
/// </remarks>
internal sealed class BoardIdLedger
{
    private static readonly Regex AddedTask = new(@"^Added task ([0-9. ]+):", RegexOptions.Compiled);
    private static readonly Regex BulkCleared = new(
        @"^Cleared existing tasks\.",
        RegexOptions.Compiled | RegexOptions.Multiline
    );
    private static readonly Regex BulkAdded = new(
        @"^\s*-\s*Task ([0-9.]+):",
        RegexOptions.Compiled | RegexOptions.Multiline
    );
    private static readonly Regex DeletedTask = new(@"^Deleted task (\S+) and all subtasks:", RegexOptions.Compiled);
    private static readonly Regex DeletedSubtask = new(
        @"^Deleted subtask (\d+) from task (\S+):",
        RegexOptions.Compiled
    );

    private static readonly Regex NoSuchSubtask = new(
        @"^Error: Task '([^']+)' has no subtask (\d+)\.",
        RegexOptions.Compiled
    );

    // Quoted forms are tested BEFORE the bare one: the bare pattern also matches them and captures
    // the quotes, which then fail canonicalization and silently lose the event.
    private static readonly Regex QuotedNotFound = new(
        @"^Error: (?:Task|Parent task|Blocking task) '([^']+)' not found\.",
        RegexOptions.Compiled
    );

    private static readonly Regex BareNotFound = new(
        @"^Error: (?:Task|Parent task) (\S+) not found\.",
        RegexOptions.Compiled
    );

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);

    /// <summary>
    /// Every dotted segment parsed as an integer and re-emitted, so <c>" 01"</c>, <c>"+1"</c> and
    /// <c>"1"</c> collapse to one key — <c>FindTaskByStringId</c> resolves them all to the same row,
    /// and a ledger keyed on the raw text would miss a loss purely over spelling. Null when a segment
    /// is not an integer: that is an invalid-id error, not a not-found.
    /// </summary>
    public static string? Canonicalize(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var segments = new List<string>();
        foreach (var part in id.Split('.'))
        {
            if (!int.TryParse(part.Trim(), out var value))
            {
                return null;
            }

            segments.Add(value.ToString());
        }

        return string.Join('.', segments);
    }

    /// <summary>The canonical id a not-found error names, or null when the text is not a not-found.</summary>
    public static string? NotFoundTaskId(string resultText)
    {
        if (NoSuchSubtask.Match(resultText) is { Success: true } subtask)
        {
            return Canonicalize($"{subtask.Groups[1].Value}.{subtask.Groups[2].Value}");
        }

        if (QuotedNotFound.Match(resultText) is { Success: true } quoted)
        {
            return Canonicalize(quoted.Groups[1].Value);
        }

        return BareNotFound.Match(resultText) is { Success: true } bare ? Canonicalize(bare.Groups[1].Value) : null;
    }

    /// <summary>Folds one SUCCESSFUL task-tool result into the ledger.</summary>
    public void RecordSuccess(string tool, string resultText)
    {
        switch (tool)
        {
            case "add-task":
                if (AddedTask.Match(resultText) is { Success: true } added)
                {
                    Add(added.Groups[1].Value);
                }

                break;

            case "bulk-initialize":
                // clearExisting is a requested reset that also renumbers from 1, so keeping the old
                // ids would report every re-used id as vanished.
                if (BulkCleared.IsMatch(resultText))
                {
                    _ids.Clear();
                }

                foreach (Match match in BulkAdded.Matches(resultText))
                {
                    Add(match.Groups[1].Value);
                }

                break;

            case "delete-task":
                if (DeletedTask.Match(resultText) is { Success: true } deleted)
                {
                    Forget(deleted.Groups[1].Value);
                }
                else if (DeletedSubtask.Match(resultText) is { Success: true } deletedSub)
                {
                    Forget($"{deletedSub.Groups[2].Value}.{deletedSub.Groups[1].Value}");
                }

                break;

            default:
                // Every other task tool mints and deletes nothing, so it moves the ledger not at all.
                break;
        }
    }

    public bool Owns(string canonicalId) => _ids.Contains(canonicalId);

    private void Add(string id)
    {
        if (Canonicalize(id) is { } canonical)
        {
            _ = _ids.Add(canonical);
        }
    }

    /// <summary>
    /// Drops the id and every dotted descendant, because a delete takes the whole subtree with it.
    /// Forgetting only the row itself would leave each child to be reported as lost.
    /// </summary>
    private void Forget(string id)
    {
        if (Canonicalize(id) is not { } canonical)
        {
            return;
        }

        _ = _ids.RemoveWhere(key =>
            string.Equals(key, canonical, StringComparison.Ordinal)
            || key.StartsWith(canonical + ".", StringComparison.Ordinal)
        );
    }
}
