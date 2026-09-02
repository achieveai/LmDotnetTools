using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>The caps the validator enforces (spec 679 §3.4 V7, V9).</summary>
internal sealed record CheckpointValidationOptions
{
    /// <summary>V7: the most the narrative may be, in estimated tokens.</summary>
    public long NarrativeTokenCap { get; init; } = 2_000;

    /// <summary>V9: the most the rendered envelope may be, in estimated tokens.</summary>
    public long CheckpointTokenCap { get; init; } = 6_000;

    /// <summary>How text is sized for V7 and V9.</summary>
    public Func<string?, long> TextEstimator { get; init; } = CompactionTokenEstimate.EstimateText;

    /// <summary>How the envelope is rendered for V9; must match what the projection dispatches.</summary>
    public CheckpointRenderOptions Render { get; init; } = CheckpointRenderOptions.Default;
}

/// <summary>What a checkpoint is validated against: the rows it stands in for and the state it mirrors.</summary>
/// <param name="Rows">Every canonical row of the thread, store-derived so each carries its persisted id (V2).</param>
/// <param name="Board">The todo board when the loop has one; null means no board exists.</param>
/// <param name="KnownAgentIds">
///     Every agent id the roster can resolve: the live roster plus legacy ids in the persisted roster (V5).
/// </param>
internal sealed record CheckpointValidationContext(
    IReadOnlyList<SequencedMessage> Rows,
    TodoBoardSnapshot? Board,
    IReadOnlyCollection<string> KnownAgentIds
);

/// <summary>The validator's verdict: valid, or the first rule that failed and why.</summary>
internal sealed record CheckpointValidationResult
{
    public static readonly CheckpointValidationResult Valid = new();

    public bool IsValid => Rule is null;

    /// <summary>The rule that failed, <c>V1</c>…<c>V9</c>, or null.</summary>
    public string? Rule { get; init; }

    /// <summary>What was found, for the log; never shown to the model.</summary>
    public string? Detail { get; init; }

    /// <summary>The typed reason recorded in <c>compaction.state</c>, or null when valid.</summary>
    public string? Reason => Rule is null ? null : CompactionReasons.ValidationFailed(Rule);

    public static CheckpointValidationResult Fail(string rule, string detail) => new() { Rule = rule, Detail = detail };
}

/// <summary>
///     The gate before commit (spec 679 §3.4): nine rules, checked in order, first failure wins. A
///     checkpoint that fails is rejected with <c>validation_failed:Vn</c>, no row is appended, and the
///     view the model sees does not change.
/// </summary>
/// <remarks>
///     V3 is where R5 (human rows are never summarised) is enforced: every quote must be a substring of
///     the row it cites, and <c>CurrentInstruction</c> is recomputed from the rows and compared whole,
///     never trusted from the summarizer.
/// </remarks>
internal static class CheckpointValidator
{
    public static CheckpointValidationResult Validate(
        CompactionCheckpointMessage checkpoint,
        CheckpointValidationContext context,
        CheckpointValidationOptions? options = null
    )
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(context);
        options ??= new CheckpointValidationOptions();

        var boundary = checkpoint.Boundary.Seq;
        var manifest = checkpoint.Manifest;
        var bySeq = context.Rows.ToDictionary(r => r.Seq);

        // V1: schema version known.
        if (checkpoint.SchemaVersion != CompactionCheckpointMessage.CurrentSchemaVersion)
        {
            return CheckpointValidationResult.Fail("V1", $"schema version {checkpoint.SchemaVersion} is unknown");
        }

        // V2: the boundary names the row that is there.
        if (!bySeq.TryGetValue(boundary, out var boundaryRow))
        {
            return CheckpointValidationResult.Fail("V2", $"no row at seq {boundary}");
        }

        if (boundaryRow.MessageId is null)
        {
            return CheckpointValidationResult.Fail("V2", $"row at seq {boundary} has no persisted id to match");
        }

        if (!string.Equals(boundaryRow.MessageId, checkpoint.Boundary.MessageId, StringComparison.Ordinal))
        {
            return CheckpointValidationResult.Fail(
                "V2",
                $"row at seq {boundary} is '{boundaryRow.MessageId}', boundary says '{checkpoint.Boundary.MessageId}'"
            );
        }

        // V3: every quote cites a row at or before the boundary and is verbatim; CurrentInstruction is exact.
        foreach (
            var (item, section) in manifest
                .Instructions.Select(q => (q, "Instructions"))
                .Concat(manifest.Decisions.Select(q => (q, "Decisions")))
        )
        {
            var failure = CheckSubstringQuote(item, section, boundary, bySeq);
            if (failure is not null)
            {
                return failure;
            }
        }

        var expected = CutSelector.CurrentInstructionRows(context.Rows, boundary);
        if (manifest.CurrentInstruction.Count != expected.Count)
        {
            return CheckpointValidationResult.Fail(
                "V3",
                $"CurrentInstruction has {manifest.CurrentInstruction.Count} rows; the current run has {expected.Count} human rows at or before seq {boundary}"
            );
        }

        for (var i = 0; i < expected.Count; i++)
        {
            var quoted = manifest.CurrentInstruction[i];
            if (quoted.Seq != expected[i].Seq)
            {
                return CheckpointValidationResult.Fail(
                    "V3",
                    $"CurrentInstruction[{i}] cites seq {quoted.Seq}; expected seq {expected[i].Seq}"
                );
            }

            if (!string.Equals(quoted.Quote, expected[i].Text, StringComparison.Ordinal))
            {
                return CheckpointValidationResult.Fail(
                    "V3",
                    $"CurrentInstruction[{i}] is not the whole text of seq {quoted.Seq}"
                );
            }
        }

        // V4: task ids resolve to the board.
        foreach (var task in manifest.Tasks.Where(t => t.Id is not null))
        {
            if (context.Board is null)
            {
                return CheckpointValidationResult.Fail(
                    "V4",
                    $"task '{task.Id}' carries a board id but no board exists"
                );
            }

            if (!Contains(context.Board.Tasks, task.Id!))
            {
                return CheckpointValidationResult.Fail("V4", $"task '{task.Id}' is not on the board");
            }
        }

        // V5: agent ids resolve to the roster.
        foreach (var agent in manifest.Agents)
        {
            if (!context.KnownAgentIds.Contains(agent.AgentId))
            {
                return CheckpointValidationResult.Fail("V5", $"agent '{agent.AgentId}' is not in the roster");
            }
        }

        // V6: the index covers 1..boundary with no gaps.
        var indexFailure = CheckIndex(manifest.Index, boundary);
        if (indexFailure is not null)
        {
            return indexFailure;
        }

        // V7: narrative within its cap.
        var narrativeTokens = options.TextEstimator(checkpoint.Narrative);
        if (narrativeTokens > options.NarrativeTokenCap)
        {
            return CheckpointValidationResult.Fail(
                "V7",
                $"narrative is {narrativeTokens} tokens; cap is {options.NarrativeTokenCap}"
            );
        }

        // V8: nothing cut-blocking at the cut.
        if (!manifest.Recovery.IsClean)
        {
            return CheckpointValidationResult.Fail("V8", "recovery state reports cut-blocking items");
        }

        // V9: the envelope within its cap.
        var envelopeTokens = options.TextEstimator(checkpoint.RenderEnvelope(options.Render));
        if (envelopeTokens > options.CheckpointTokenCap)
        {
            return CheckpointValidationResult.Fail(
                "V9",
                $"envelope is {envelopeTokens} tokens; cap is {options.CheckpointTokenCap}"
            );
        }

        return CheckpointValidationResult.Valid;
    }

    private static CheckpointValidationResult? CheckSubstringQuote(
        QuotedItem item,
        string section,
        long boundary,
        Dictionary<long, SequencedMessage> bySeq
    )
    {
        if (item.Seq > boundary)
        {
            return CheckpointValidationResult.Fail(
                "V3",
                $"{section} quote cites seq {item.Seq}, past the boundary {boundary}"
            );
        }

        if (!bySeq.TryGetValue(item.Seq, out var row))
        {
            return CheckpointValidationResult.Fail("V3", $"{section} quote cites seq {item.Seq}, which has no row");
        }

        if (string.IsNullOrEmpty(item.Quote))
        {
            return CheckpointValidationResult.Fail("V3", $"{section} quote of seq {item.Seq} is empty");
        }

        if (row.Text is null || !row.Text.Contains(item.Quote, StringComparison.Ordinal))
        {
            return CheckpointValidationResult.Fail("V3", $"{section} quote is not a substring of seq {item.Seq}");
        }

        return null;
    }

    private static CheckpointValidationResult? CheckIndex(IReadOnlyList<IndexEntry> index, long boundary)
    {
        if (index.Count == 0)
        {
            return boundary < 1 ? null : CheckpointValidationResult.Fail("V6", "index is empty");
        }

        var expectedFrom = 1L;
        foreach (var entry in index)
        {
            if (entry.FromSeq != expectedFrom)
            {
                return CheckpointValidationResult.Fail(
                    "V6",
                    $"index entry starts at seq {entry.FromSeq}; expected {expectedFrom}"
                );
            }

            if (entry.ToSeq < entry.FromSeq)
            {
                return CheckpointValidationResult.Fail("V6", $"index entry {entry.FromSeq}-{entry.ToSeq} is inverted");
            }

            expectedFrom = entry.ToSeq + 1;
        }

        return expectedFrom == boundary + 1
            ? null
            : CheckpointValidationResult.Fail("V6", $"index ends at seq {expectedFrom - 1}; boundary is {boundary}");
    }

    private static bool Contains(IReadOnlyList<TodoTaskNode> nodes, string id) =>
        nodes.Any(n => string.Equals(n.Id, id, StringComparison.Ordinal) || Contains(n.SubTasks, id));
}
