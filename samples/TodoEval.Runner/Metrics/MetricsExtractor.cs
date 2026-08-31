using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Metrics;

/// <summary>Per-tool call/error tallies while merging threads.</summary>
internal sealed record ToolStats
{
    public int Calls { get; init; }
    public int Errors { get; init; }
}

/// <summary>One <c>perTool</c> row of the score object: calls, errors, errorRate rounded to 4dp.</summary>
internal sealed record PerToolScore
{
    public int Calls { get; init; }
    public int Errors { get; init; }
    public double ErrorRate { get; init; }
}

/// <summary>The score object's validity block (metrics-spec.md, "Validity preconditions").</summary>
internal sealed record RunValidity
{
    /// <summary>The spec's zero-threads reason, verbatim — a mis-pointed store must never score.</summary>
    public const string NoThreadsReason = "no conversation threads found - harness misconfiguration";

    public required bool Valid { get; init; }
    public required IReadOnlyList<string> Reasons { get; init; }
    public required int SubAgentThreads { get; init; }
    public required IReadOnlyList<string> SubAgentsWithoutTaskToolCalls { get; init; }
    public required IReadOnlyList<string> FabricatedComplianceSuspects { get; init; }

    /// <summary>Zero sub-agent threads is VALID (model behavior under test, not harness breakage).</summary>
    public static RunValidity From(
        int threadCount,
        int subAgentThreads,
        IReadOnlyList<string> withoutTaskToolCalls,
        IReadOnlyList<string> fabricatedSuspects
    )
    {
        var reasons = new List<string>();
        if (threadCount == 0)
        {
            reasons.Add(NoThreadsReason);
        }

        if (withoutTaskToolCalls.Count > 0)
        {
            reasons.Add($"sub-agent thread(s) with zero task-tool calls: {string.Join(", ", withoutTaskToolCalls)}");
        }

        return new RunValidity
        {
            Valid = reasons.Count == 0,
            Reasons = reasons,
            SubAgentThreads = subAgentThreads,
            SubAgentsWithoutTaskToolCalls = withoutTaskToolCalls,
            FabricatedComplianceSuspects = fabricatedSuspects,
        };
    }
}

/// <summary>
/// One line of <c>runs.jsonl</c>: the sweep's own facts about the run (model, seed, outcome,
/// duration) plus the full <c>todo-eval/score@1</c> object metrics-spec.md defines. Field names
/// and semantics of the score portion match the reference oracle one-for-one so a run scored by
/// both implementations diffs clean.
/// </summary>
internal sealed record RunMetrics
{
    public string Schema { get; init; } = "todo-eval/score@1";

    // --- sweep facts -------------------------------------------------------------------
    public required string RunKey { get; init; }
    public required string Model { get; init; }
    public required int SeedIndex { get; init; }
    public required string Topic { get; init; }
    public required string Status { get; init; }
    public string? ThreadId { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }

    // --- score object (metrics-spec.md) --------------------------------------------------
    /// <summary>Threads contributing to this run: the root conversation plus its sub-agent descendants.</summary>
    public int Threads { get; init; }

    /// <summary>Top-level duplicate of <c>validity.subAgentThreads</c> (spec: sweep-table segmentation).</summary>
    public int SubAgentCount { get; init; }

    public int TotalToolCalls { get; init; }
    public int TaskToolCalls { get; init; }
    public int TaskToolErrors { get; init; }
    public int UnpairedToolCalls { get; init; }

    /// <summary>All 15 task tools, zero-call rows included, in the spec's tool order.</summary>
    public IReadOnlyDictionary<string, PerToolScore> PerTool { get; init; } =
        new Dictionary<string, PerToolScore>(StringComparer.Ordinal);

    public int RetryStormCount { get; init; }
    public IReadOnlyList<RetryStorm> RetryStorms { get; init; } = [];

    /// <summary>Board-loss watch (#621 Part B): see <see cref="BoardIdVanishedReport" />.</summary>
    public BoardIdVanishedReport BoardIdVanished { get; init; } = BoardIdVanishedReport.From([]);

    public bool BlockRecorded { get; init; }
    public bool BlockExplicitlyCleared { get; init; }
    public bool BlockCleared { get; init; }

    /// <summary>Null only when no expected-board fixture was supplied to the extractor.</summary>
    public bool? Completion { get; init; }

    public IReadOnlyList<string> CompletionFailures { get; init; } = [];

    public int Turns { get; init; }
    public int PrimaryTurns { get; init; }

    public RunValidity Validity { get; init; } = RunValidity.From(0, 0, [], []);
}

/// <summary>
/// The score object's <c>boardIdVanished</c> block (metrics-spec.md, "Board id vanished"): rows a
/// run's threads minted, never deleted, and later got a not-found for.
/// </summary>
/// <remarks>
/// The <see cref="Note" /> travels with the number on purpose. A reader who skips the spec sees a
/// zero here and concludes the board held; the count is a one-directional LOWER bound, and the
/// authoritative signal is the server-side Warning, so the field that carries the number also
/// carries the instruction to grep the host log for the event name.
/// </remarks>
internal sealed record BoardIdVanishedReport
{
    public const string LowerBoundNote =
        "Transcript-derived LOWER BOUND (#621 Part B). The authoritative signal is the server-side "
        + "Warning whose event name is TodoBoardIdVanished; also grep the host structured logs for that "
        + "name, because losses the transcript cannot see (ids minted in a process whose transcript is "
        + "not in this directory, or bulk-initialize subtask ids in transcripts recorded before that "
        + "tool began echoing them) "
        + "reach the log and not this count.";

    public required int Count { get; init; }
    public required IReadOnlyList<BoardIdVanish> Events { get; init; }
    public string Note { get; init; } = LowerBoundNote;

    public static BoardIdVanishedReport From(IReadOnlyList<BoardIdVanish> events) =>
        new() { Count = events.Count, Events = events };
}

/// <summary>
/// A conversation thread unreachable from every manifest run via the <c>sample.subAgentOf</c> chain
/// (the link is missing or points at a thread the store does not contain). Likeliest on hard-timeout
/// kills, where the host's debounced metadata write dies with the run — exactly the failure the
/// metrics exist to measure, so these threads are surfaced with their activity, never dropped.
/// </summary>
internal sealed record UnattributedThread
{
    public required string ThreadId { get; init; }
    public required bool IsSubAgentThread { get; init; }
    public required int TotalToolCalls { get; init; }
    public required int TaskToolCalls { get; init; }
    public required int TaskToolErrors { get; init; }
    public required bool FabricatedComplianceSuspect { get; init; }
}

/// <summary>The whole extraction: one row per manifest run, plus every thread no run can claim.</summary>
internal sealed record SweepMetrics
{
    public required IReadOnlyList<RunMetrics> Runs { get; init; }
    public required IReadOnlyList<UnattributedThread> UnattributedThreads { get; init; }
}

/// <summary>
/// The C# production twin of the eval's reference oracle (score.ps1): reads the isolated host's
/// conversation store (or an archived copy) and turns each sweep run into a <see cref="RunMetrics"/>
/// row. Everything here is offline — a committed baseline sweep re-extracts bit-identically. The
/// one addition over the oracle is run scoping: one host serves the whole N x M sweep, so each
/// run's threads are selected via the <c>sample.subAgentOf</c> parent links before scoring.
/// </summary>
internal static class MetricsExtractor
{
    public static SweepMetrics Extract(
        string conversationsDir,
        IReadOnlyList<RunManifestEntry> manifest,
        BoardShapeExpectation? expectedBoard
    )
    {
        var threads = ConversationStoreReader.LoadAllThreads(conversationsDir);
        var groups = ConversationStoreReader.GroupByRootThread(threads);

        // F-003: a thread whose sample.subAgentOf link is missing/unresolvable becomes its own
        // group root, which no manifest entry names — before this diff it silently vanished from
        // every metric and validity check. Every group no run claims is surfaced instead.
        var claimedRoots = new HashSet<string>(
            manifest.Where(e => e.ThreadId is not null).Select(e => e.ThreadId!),
            StringComparer.Ordinal
        );
        var unattributed = groups
            .Where(kvp => !claimedRoots.Contains(kvp.Key))
            .SelectMany(kvp => kvp.Value)
            .OrderBy(t => t.ThreadId, StringComparer.Ordinal)
            .Select(t => new UnattributedThread
            {
                ThreadId = t.ThreadId,
                IsSubAgentThread = t.IsSubAgentThread,
                TotalToolCalls = t.TotalToolCalls,
                TaskToolCalls = t.TaskToolCallCount,
                TaskToolErrors = t.PerTaskTool.Values.Sum(s => s.Errors),
                FabricatedComplianceSuspect = t.FabricatedComplianceSuspect,
            })
            .ToList();

        return new SweepMetrics
        {
            Runs = [.. manifest.Select(entry => ExtractRun(entry, groups, expectedBoard))],
            UnattributedThreads = unattributed,
        };
    }

    private static RunMetrics ExtractRun(
        RunManifestEntry entry,
        IReadOnlyDictionary<string, IReadOnlyList<ConversationStoreReader.ThreadData>> groups,
        BoardShapeExpectation? expectedBoard
    )
    {
        var baseMetrics = new RunMetrics
        {
            RunKey = entry.RunKey,
            Model = entry.Model,
            SeedIndex = entry.SeedIndex,
            Topic = entry.Topic,
            Status = entry.Status,
            ThreadId = entry.ThreadId,
            DurationMs = entry.DurationMs,
            Error = entry.Error,
            PerTool = BuildPerTool(new Dictionary<string, ToolStats>(StringComparer.Ordinal)),
        };

        if (entry.ThreadId is null || !groups.TryGetValue(entry.ThreadId, out var group))
        {
            // The run never produced a conversation (provision failed) or the store lacks it
            // (harvest raced a crash). The row still exists so completion rates keep the run in
            // their denominator.
            return baseMetrics with
            {
                Completion = expectedBoard is null ? null : false,
                CompletionFailures = expectedBoard is null
                    ? []
                    : [$"conversation '{entry.ThreadId ?? "(none)"}' not found in store"],
            };
        }

        // Merge per-tool tallies across the run's threads; all 15 tools present, zero rows too.
        var merged = new Dictionary<string, ToolStats>(StringComparer.Ordinal);
        foreach (var thread in group)
        {
            foreach (var (tool, stats) in thread.PerTaskTool)
            {
                var current = merged.TryGetValue(tool, out var existing) ? existing : new ToolStats();
                merged[tool] = new ToolStats
                {
                    Calls = current.Calls + stats.Calls,
                    Errors = current.Errors + stats.Errors,
                };
            }
        }

        var perTool = BuildPerTool(merged);
        var storms = group.SelectMany(t => t.RetryStorms).ToList();
        var vanishes = group.SelectMany(t => t.BoardIdVanishes).ToList();
        var blockRecorded = group.Any(t => t.BlockRecorded);
        var blockExplicitlyCleared = group.Any(t => t.BlockExplicitlyCleared);

        var subAgents = group.Where(t => t.IsSubAgentThread).ToList();
        var withoutTaskTools = subAgents.Where(t => t.TaskToolCallCount == 0).Select(t => t.ThreadId).ToList();
        var suspects = subAgents.Where(t => t.FabricatedComplianceSuspect).Select(t => t.ThreadId).ToList();

        var rootThread = group.First(t => string.Equals(t.ThreadId, entry.ThreadId, StringComparison.Ordinal));
        var board = rootThread.TodoBoardJson is { } boardJson ? BoardSnapshot.Parse(boardJson) : null;
        var flat = board?.Flatten() ?? [];
        var blockCleared = blockRecorded && board is not null && BoardShapeExpectation.CountBlocked(flat) == 0;

        IReadOnlyList<string> completionFailures = [];
        if (expectedBoard is not null)
        {
            completionFailures = board is null
                ? ["no todo board snapshot was persisted for the run"]
                : expectedBoard.Evaluate(flat, blockRecorded, blockCleared);
        }

        return baseMetrics with
        {
            Threads = group.Count,
            SubAgentCount = subAgents.Count,
            TotalToolCalls = group.Sum(t => t.TotalToolCalls),
            TaskToolCalls = perTool.Values.Sum(s => s.Calls),
            TaskToolErrors = perTool.Values.Sum(s => s.Errors),
            UnpairedToolCalls = group.Sum(t => t.UnpairedToolCalls),
            PerTool = perTool,
            RetryStormCount = storms.Count,
            RetryStorms = storms,
            BoardIdVanished = BoardIdVanishedReport.From(vanishes),
            BlockRecorded = blockRecorded,
            BlockExplicitlyCleared = blockExplicitlyCleared,
            BlockCleared = blockCleared,
            Completion = expectedBoard is null ? null : completionFailures.Count == 0,
            CompletionFailures = completionFailures,
            Turns = group.Sum(t => t.TurnCount),
            PrimaryTurns = group.Where(t => !t.IsSubAgentThread).Sum(t => t.TurnCount),
            Validity = RunValidity.From(group.Count, subAgents.Count, withoutTaskTools, suspects),
        };
    }

    /// <summary>
    /// Every task tool gets a row — zero-call tools included — in the spec's declared tool order,
    /// with errorRate rounded to 4 decimal places (0 when the tool was never called).
    /// </summary>
    private static IReadOnlyDictionary<string, PerToolScore> BuildPerTool(
        IReadOnlyDictionary<string, ToolStats> tallies
    )
    {
        var result = new Dictionary<string, PerToolScore>(StringComparer.Ordinal);
        foreach (var tool in TaskTools.All)
        {
            var stats = tallies.TryGetValue(tool, out var found) ? found : new ToolStats();
            result[tool] = new PerToolScore
            {
                Calls = stats.Calls,
                Errors = stats.Errors,
                ErrorRate = stats.Calls > 0 ? Math.Round((double)stats.Errors / stats.Calls, 4) : 0,
            };
        }

        return result;
    }
}
