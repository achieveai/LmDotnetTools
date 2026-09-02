using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>The knobs the cut rules read (spec 679 §2.4, §5.4 hypotheses as defaults).</summary>
internal sealed record CutSelectorOptions
{
    /// <summary>R3: the least of the current run the tail keeps, in estimated tokens.</summary>
    public long MinTailTokens { get; init; } = 8_000;

    /// <summary>R7: the tail size the policy prefers to stay under. A preference, not a rule.</summary>
    public long MaxTailTokens { get; init; } = 24_000;

    /// <summary>R4: how many of the most recent runs are protected from being split when they carry a correction.</summary>
    public int CorrectionLookbackRuns { get; init; } = 3;

    /// <summary>The estimator every size test uses.</summary>
    public Func<IMessage, long> Estimator { get; init; } = CompactionTokenEstimate.Default;
}

/// <summary>
///     Cut-blocking state the loop knows and the rows do not show (spec 679 §2.6): continuations owed to
///     a resolved deferral and a turn interrupted mid-stream. Deferred placeholders and parked
///     <c>Wait</c> results are rows, and the selector reads them from the rows — the coordinator that
///     mirrors them is itself rebuilt from those rows on restart, so the rows are the source of truth.
/// </summary>
internal sealed record CutBlockingState(int OwedContinuations = 0, int InterruptedTurns = 0)
{
    /// <summary>Nothing blocking.</summary>
    public static readonly CutBlockingState Clean = new();
}

/// <summary>What the selector is asked.</summary>
/// <param name="Rows">Every canonical row of the thread, ascending by <c>Seq</c>.</param>
/// <param name="CandidateSeq">The policy's proposed cut; the selector only moves it earlier.</param>
/// <param name="LoopState">Cut-blocking state outside the rows.</param>
/// <param name="Runs">The run ledger, for R4's errored/interrupted-predecessor test. May be empty.</param>
/// <param name="ActiveBoundarySeq">The active checkpoint's boundary; a cut at or before it changes nothing.</param>
/// <param name="Options">The knobs.</param>
internal sealed record CutRequest(
    IReadOnlyList<SequencedMessage> Rows,
    long CandidateSeq,
    CutBlockingState LoopState,
    IReadOnlyList<RunLedgerEntry> Runs,
    long? ActiveBoundarySeq,
    CutSelectorOptions Options
);

/// <summary>The selector's answer: a legal cut, or a typed refusal.</summary>
internal abstract record CutDecision
{
    /// <summary>What blocked, or all zero for a legal cut; recorded on the manifest for audit.</summary>
    public required RecoveryStateAtCut Recovery { get; init; }

    /// <summary>A cut every rule accepts.</summary>
    public sealed record Cut : CutDecision
    {
        /// <summary>The last row the checkpoint will cover.</summary>
        public required long Seq { get; init; }

        /// <summary>The candidate the policy proposed, for the record.</summary>
        public required long CandidateSeq { get; init; }

        /// <summary>The run of the last human row, or null when the thread has none.</summary>
        public string? CurrentRunId { get; init; }

        /// <summary>
        ///     The human rows of the current run at or before the cut, in <c>seq</c> order: what the
        ///     envelope quotes whole as <c>Current instruction</c> (R2, V3). Empty when the cut precedes
        ///     the run.
        /// </summary>
        public required IReadOnlyList<SequencedMessage> CurrentInstruction { get; init; }

        /// <summary>Estimated tokens of the tail after the cut, checkpoint rows excluded.</summary>
        public required long TailTokens { get; init; }

        /// <summary>R7: the tail is larger than the policy prefers; R1–R6 won.</summary>
        public required bool ExceedsMaxTail { get; init; }
    }

    /// <summary>No legal cut; <see cref="Reason" /> is one of <see cref="CompactionReasons" />.</summary>
    public sealed record Skipped : CutDecision
    {
        /// <summary>The typed reason.</summary>
        public required string Reason { get; init; }
    }
}

/// <summary>
///     Applies the protected-tail rules R1–R7 (spec 679 §2.4) to a proposed cut, moving it earlier
///     until every rule holds or refusing it with a typed reason.
/// </summary>
/// <remarks>
///     <para>
///         R6 has two halves. State the rows cannot show — an owed continuation, an interrupted turn —
///         refuses the cut outright (<c>unsafe_state</c>): nothing in the rows says where it would be safe.
///         State the rows do show — a deferred placeholder, a parked <c>Wait</c> — is an obstacle row: the
///         cut moves before the generation that holds it, so the placeholder a later resolution replaces
///         in place stays in the tail the model sees, never behind the boundary (§12.2's R2+R6 fixtures).
///         The policy's own guard on the live coordinator (§5.3 row 3) is what makes corpus (g) and (h)
///         skip; this selector answers what a legal cut would be.
///     </para>
///     <para>
///         Walking the candidate earlier one row at a time: R1 requires a completed generation boundary
///         with no tool call/result pair split across it; R3 requires the tail to keep at least
///         <see cref="CutSelectorOptions.MinTailTokens" /> of the current run, or the whole run when it is
///         shorter; R4 refuses to split a recent run that carries a correction. R2 is the absence of a
///         rule — a cut inside the current run is fine — and the human rows it leaves behind travel
///         verbatim as <see cref="CutDecision.Cut.CurrentInstruction" />. R5 belongs to validation. R7 is
///         reported, never enforced.
///     </para>
/// </remarks>
internal static class CutSelector
{
    public static CutDecision Select(CutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rows = request.Rows;
        var options = request.Options;

        if (request.LoopState.OwedContinuations > 0 || request.LoopState.InterruptedTurns > 0)
        {
            return new CutDecision.Skipped
            {
                Reason = CompactionReasons.UnsafeState,
                Recovery = Observe(rows, request.LoopState, upToSeq: long.MaxValue),
            };
        }

        if (rows.Count == 0)
        {
            return new CutDecision.Skipped
            {
                Reason = CompactionReasons.NoSafeBoundary,
                Recovery = Observe(rows, request.LoopState, upToSeq: long.MaxValue),
            };
        }

        // R6, the row half: nothing deferred or parked may end up behind the boundary.
        var firstObstacle = rows.Where(r => IsObstacle(r.Message))
            .Select(r => r.Seq)
            .DefaultIfEmpty(long.MaxValue)
            .Min();

        var lastHuman = rows.LastOrDefault(r => r.IsHumanRow);
        var currentRunId = lastHuman?.EffectiveRunId;
        var currentRun = currentRunId is null ? [] : RowsOfRun(rows, currentRunId);
        var currentRunTokens = currentRun.Sum(r => options.Estimator(r.Message));
        var protectedRuns = ProtectedRuns(rows, request.Runs, options.CorrectionLookbackRuns);
        var floor = request.ActiveBoundarySeq ?? 0;

        var pairing = new ToolPairing(rows);
        var start = Math.Min(request.CandidateSeq, rows[^1].Seq);

        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            var cut = row.Seq;
            if (cut > start)
            {
                continue;
            }

            if (cut <= floor)
            {
                break;
            }

            if (cut >= firstObstacle)
            {
                continue; // R6
            }

            if (!IsGenerationBoundary(rows, i) || pairing.Splits(cut))
            {
                continue; // R1
            }

            if (!SatisfiesTailFloor(currentRun, currentRunTokens, cut, options))
            {
                continue; // R3
            }

            if (protectedRuns.Any(run => run.First <= cut && cut < run.Last))
            {
                continue; // R4
            }

            var tailTokens = rows.Where(r => r.Seq > cut && !r.IsCheckpointRow).Sum(r => options.Estimator(r.Message));
            return new CutDecision.Cut
            {
                Seq = cut,
                CandidateSeq = request.CandidateSeq,
                CurrentRunId = currentRunId,
                CurrentInstruction = [.. currentRun.Where(r => r.IsHumanRow && r.Seq <= cut)],
                TailTokens = tailTokens,
                ExceedsMaxTail = tailTokens > options.MaxTailTokens,
                Recovery = Observe(rows, request.LoopState, upToSeq: cut),
            };
        }

        return new CutDecision.Skipped
        {
            Reason = CompactionReasons.NoSafeBoundary,
            Recovery = Observe(rows, request.LoopState, upToSeq: long.MaxValue),
        };
    }

    /// <summary>
    ///     The human rows of the run that owns the last human row, at or before <paramref name="cutSeq" />:
    ///     the definition V3 recomputes rather than trusts (spec 679 §2.3, §3.4).
    /// </summary>
    public static IReadOnlyList<SequencedMessage> CurrentInstructionRows(
        IReadOnlyList<SequencedMessage> rows,
        long cutSeq
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        var currentRunId = rows.LastOrDefault(r => r.IsHumanRow)?.EffectiveRunId;
        return currentRunId is null
            ? []
            : [.. RowsOfRun(rows, currentRunId).Where(r => r.IsHumanRow && r.Seq <= cutSeq)];
    }

    /// <summary>
    ///     R6 as recorded on the manifest: the deferred placeholders and parked <c>Wait</c> results at or
    ///     before <paramref name="upToSeq" /> — the ones a cut there would hide — plus what the loop knows.
    ///     All zero for the cut <see cref="Select" /> returns.
    /// </summary>
    public static RecoveryStateAtCut Observe(
        IReadOnlyList<SequencedMessage> rows,
        CutBlockingState loopState,
        long upToSeq
    )
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(loopState);

        var deferred = 0;
        var parkedWaits = 0;
        foreach (var row in rows.Where(r => r.Seq <= upToSeq))
        {
            switch (row.Message)
            {
                case ToolCallResultMessage { IsDeferred: true } single:
                    if (IsWait(single.ToolName))
                    {
                        parkedWaits++;
                    }
                    else
                    {
                        deferred++;
                    }

                    break;
                case ToolsCallResultMessage many:
                    foreach (var result in many.ToolCallResults.Where(r => r.IsDeferred))
                    {
                        if (IsWait(result.ToolName))
                        {
                            parkedWaits++;
                        }
                        else
                        {
                            deferred++;
                        }
                    }

                    break;
                default:
                    break;
            }
        }

        return new RecoveryStateAtCut
        {
            DeferredToolCalls = deferred,
            ParkedWaits = parkedWaits,
            OwedContinuations = loopState.OwedContinuations,
            InterruptedTurns = loopState.InterruptedTurns,
        };
    }

    private static bool IsObstacle(IMessage message) =>
        message switch
        {
            ToolCallResultMessage { IsDeferred: true } => true,
            ToolsCallResultMessage many => many.ToolCallResults.Any(r => r.IsDeferred),
            _ => false,
        };

    private static bool IsWait(string? toolName) =>
        string.Equals(toolName, WaitToolProvider.WaitToolName, StringComparison.Ordinal);

    private static List<SequencedMessage> RowsOfRun(IReadOnlyList<SequencedMessage> rows, string runId) =>
        [.. rows.Where(r => string.Equals(r.EffectiveRunId, runId, StringComparison.Ordinal))];

    /// <summary>
    ///     R1's turn-boundary half: row <paramref name="index" /> closes a generation when it is the last row
    ///     or the next row belongs to a different generation. Rows nobody stamped with a generation are
    ///     their own boundary.
    /// </summary>
    private static bool IsGenerationBoundary(IReadOnlyList<SequencedMessage> rows, int index)
    {
        if (index == rows.Count - 1)
        {
            return true;
        }

        var here = rows[index].Message.GenerationId;
        var next = rows[index + 1].Message.GenerationId;
        return here is null || next is null || !string.Equals(here, next, StringComparison.Ordinal);
    }

    /// <summary>
    ///     R3: the tail keeps at least <see cref="CutSelectorOptions.MinTailTokens" /> of the current run;
    ///     a run shorter than that stays whole, which means the cut lies before its first row.
    /// </summary>
    private static bool SatisfiesTailFloor(
        List<SequencedMessage> currentRun,
        long currentRunTokens,
        long cut,
        CutSelectorOptions options
    )
    {
        if (currentRun.Count == 0)
        {
            return true;
        }

        if (currentRunTokens < options.MinTailTokens)
        {
            return cut < currentRun[0].Seq;
        }

        var kept = currentRun.Where(r => r.Seq > cut).Sum(r => options.Estimator(r.Message));
        return kept >= options.MinTailTokens;
    }

    /// <summary>
    ///     R4: among the last <paramref name="lookback" /> runs (by row order), those that received a
    ///     mid-run injection — a human row after an assistant row inside the same run — or that started
    ///     while the previous run ended Errored or Interrupted. Each is returned as its first and last
    ///     <c>Seq</c>; a cut strictly inside the span splits it.
    /// </summary>
    private static List<(long First, long Last)> ProtectedRuns(
        IReadOnlyList<SequencedMessage> rows,
        IReadOnlyList<RunLedgerEntry> ledger,
        int lookback
    )
    {
        var spans = new List<(string RunId, long First, long Last)>();
        foreach (var row in rows)
        {
            var runId = row.EffectiveRunId;
            if (runId is null)
            {
                continue;
            }

            var idx = spans.FindIndex(s => string.Equals(s.RunId, runId, StringComparison.Ordinal));
            if (idx < 0)
            {
                spans.Add((runId, row.Seq, row.Seq));
            }
            else
            {
                spans[idx] = (runId, spans[idx].First, row.Seq);
            }
        }

        var statusByRun = ledger
            .GroupBy(e => e.RunId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Status, StringComparer.Ordinal);

        var protectedSpans = new List<(long First, long Last)>();
        var firstProtected = Math.Max(0, spans.Count - lookback);
        for (var i = firstProtected; i < spans.Count; i++)
        {
            var (runId, first, last) = spans[i];
            var runRows = RowsOfRun(rows, runId);
            var sawAssistant = false;
            var injected = false;
            foreach (var row in runRows)
            {
                if (row.Message.Role == Role.Assistant)
                {
                    sawAssistant = true;
                }
                else if (row.IsHumanRow && sawAssistant)
                {
                    injected = true;
                    break;
                }
            }

            var afterFailure =
                i > 0
                && statusByRun.TryGetValue(spans[i - 1].RunId, out var previous)
                && previous is RunStatus.Errored or RunStatus.Interrupted;

            if (injected || afterFailure)
            {
                protectedSpans.Add((first, last));
            }
        }

        return protectedSpans;
    }

    /// <summary>R1's pairing half, with the same id extractors the restore-time sweep uses.</summary>
    private sealed class ToolPairing
    {
        private readonly List<(long CallSeq, long ResultSeq)> _pairs = [];
        private readonly HashSet<long> _callRows = [];

        public ToolPairing(IReadOnlyList<SequencedMessage> rows)
        {
            var callSeqById = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var callIds = CallIds(row.Message).ToList();
                if (callIds.Count > 0)
                {
                    _ = _callRows.Add(row.Seq);
                    foreach (var id in callIds)
                    {
                        callSeqById[id] = row.Seq;
                    }
                }

                foreach (var id in ResultIds(row.Message))
                {
                    if (callSeqById.TryGetValue(id, out var callSeq))
                    {
                        _pairs.Add((callSeq, row.Seq));
                    }
                }
            }
        }

        /// <summary>True when the last row at or before <paramref name="cut" /> is a call, or any pair straddles it.</summary>
        public bool Splits(long cut) =>
            _callRows.Contains(cut) || _pairs.Any(p => p.CallSeq <= cut && p.ResultSeq > cut);

        private static IEnumerable<string> CallIds(IMessage message) =>
            message switch
            {
                ToolCallMessage single => Usable([single.ToolCallId]),
                ICanGetToolCalls many => Usable((many.GetToolCalls() ?? []).Select(tc => tc.ToolCallId)),
                _ => [],
            };

        private static IEnumerable<string> ResultIds(IMessage message) =>
            message switch
            {
                ToolCallResultMessage single => Usable([single.ToolCallId]),
                ToolsCallResultMessage many => Usable(many.ToolCallResults.Select(r => r.ToolCallId)),
                _ => [],
            };

        private static IEnumerable<string> Usable(IEnumerable<string?> ids) =>
            ids.Where(id => !string.IsNullOrWhiteSpace(id)).Select(id => id!);
    }
}
