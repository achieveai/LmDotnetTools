using TodoEval.Runner.Sweep;

namespace TodoEval.Runner.Metrics;

/// <summary>Per-tool call/error tallies while merging threads, plus the failures' error codes.</summary>
internal sealed record ToolStats
{
    public int Calls { get; init; }
    public int Errors { get; init; }

    /// <summary>Failure count per stable error code; <c>unclassified</c> when the result named none.</summary>
    public IReadOnlyDictionary<string, int> ErrorCodes { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public ToolStats Merge(ToolStats other)
    {
        var codes = new Dictionary<string, int>(ErrorCodes, StringComparer.Ordinal);
        foreach (var (code, count) in other.ErrorCodes)
        {
            codes[code] = codes.TryGetValue(code, out var seen) ? seen + count : count;
        }

        return new ToolStats
        {
            Calls = Calls + other.Calls,
            Errors = Errors + other.Errors,
            ErrorCodes = codes,
        };
    }
}

/// <summary>
/// One <c>perTool</c> row of the score object: calls, errors, errorRate rounded to 4dp, the tool's
/// family, and the failures broken down by error code.
/// </summary>
internal sealed record PerToolScore
{
    public int Calls { get; init; }
    public int Errors { get; init; }
    public double ErrorRate { get; init; }

    /// <summary><c>task</c> or <c>coordination</c> - the only two families that get a row.</summary>
    public required string Family { get; init; }

    public IReadOnlyDictionary<string, int> ErrorCodes { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);
}

/// <summary>
/// What the host's own startup and directory work cost, stamped onto the root conversation by the
/// host (<c>sample.startupWork</c>) and read back offline here. Every field is a COUNT, not a
/// verdict: #670 measures this cost so #671-#676 can be judged against it, and measuring it does
/// not pre-judge any of it as a defect.
/// </summary>
internal sealed record StartupWork
{
    public int FunctionRegistryBuilds { get; init; }
    public int DescriptorCacheHits { get; init; }
    public int DescriptorCacheMisses { get; init; }
    public int RestartRebuilds { get; init; }
    public int GetAgentsCalls { get; init; }
    public int GetAgentsEntries { get; init; }
    public long GetAgentsBytes { get; init; }
}

/// <summary>
/// One sub-agent spawn's phase timings, as the host's <c>OnSpawnTimed</c> seam recorded them and
/// <c>SubAgentProvenance</c> stamped them into the child's <c>metadata.json</c>.
/// </summary>
internal sealed record SpawnTiming
{
    public string AgentId { get; init; } = "";
    public string Template { get; init; } = "";
    public long QueuedMs { get; init; }
    public long ToolRegistryMs { get; init; }
    public long ContextFanOutMs { get; init; }
    public long TotalMs { get; init; }
    public int ToolCatalogBytes { get; init; }
}

/// <summary>
/// The score object's <c>openObligations</c> block. The number is read from a field coordination
/// results do not carry yet (#673 adds it), so the count of results that DID carry one travels with
/// it: without that, a reader cannot tell "no obligations were open" from "nothing reported any".
/// </summary>
internal sealed record OpenObligationsReport
{
    public const string NotYetEmittedNote =
        "No coordination result carried an openObligations field: this build does not emit one yet "
        + "(#673). The zero means NOT REPORTED, not 'none were open'.";

    public required int LastObserved { get; init; }
    public required int ResultsCarryingField { get; init; }
    public string? Note { get; init; }

    public static OpenObligationsReport From(int lastObserved, int resultsCarryingField) =>
        new()
        {
            LastObserved = lastObserved,
            ResultsCarryingField = resultsCarryingField,
            Note = resultsCarryingField == 0 ? NotYetEmittedNote : null,
        };
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
    public string Schema { get; init; } = TodoEval.Runner.Metrics.Fingerprints.Schema;

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

    /// <summary>The coordination twins of <c>taskToolCalls</c>/<c>taskToolErrors</c>.</summary>
    public int CoordinationToolCalls { get; init; }

    public int CoordinationToolErrors { get; init; }
    public int UnpairedToolCalls { get; init; }

    /// <summary>
    /// All 22 rows - the 15 task tools then the 7 coordination tools, zero-call rows included, in
    /// the spec's declared order.
    /// </summary>
    public IReadOnlyDictionary<string, PerToolScore> PerTool { get; init; } =
        new Dictionary<string, PerToolScore>(StringComparer.Ordinal);

    /// <summary>Every failure in the run rolled up by error code, across both families.</summary>
    public IReadOnlyDictionary<string, int> ErrorCodes { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>How the run's wait calls ended, keyed by result status or error code.</summary>
    public IReadOnlyDictionary<string, int> WaitOutcomes { get; init; } =
        new Dictionary<string, int>(StringComparer.Ordinal);

    public OpenObligationsReport OpenObligations { get; init; } = OpenObligationsReport.From(0, 0);

    /// <summary>Token attribution from the persisted usage records (metrics-spec.md, "Usage").</summary>
    public UsageReport Usage { get; init; } = new();

    /// <summary>Per-spawn cost the host measured, one row per sub-agent the run spawned.</summary>
    public IReadOnlyList<SpawnTiming> SpawnTimings { get; init; } = [];

    /// <summary>Host-side startup and directory work, or null when the host stamped none.</summary>
    public StartupWork? StartupWork { get; init; }

    /// <summary>Corpus / spec / evaluator hashes this row was EXTRACTED under.</summary>
    public FingerprintSet? Fingerprints { get; init; }

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
        + "not in this directory, or bulk-initialize subtask ids its result text never named, which is "
        + "every one before #634 R1 and any past its row cap since) "
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
        BoardShapeExpectation? expectedBoard,
        FingerprintSet? fingerprints = null
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
                TaskToolErrors = t
                    .PerTool.Where(kvp => ToolFamilies.Classify(kvp.Key) == ToolFamily.Task)
                    .Sum(kvp => kvp.Value.Errors),
                FabricatedComplianceSuspect = t.FabricatedComplianceSuspect,
            })
            .ToList();

        return new SweepMetrics
        {
            Runs = [.. manifest.Select(entry => ExtractRun(entry, groups, expectedBoard, fingerprints))],
            UnattributedThreads = unattributed,
        };
    }

    private static RunMetrics ExtractRun(
        RunManifestEntry entry,
        IReadOnlyDictionary<string, IReadOnlyList<ConversationStoreReader.ThreadData>> groups,
        BoardShapeExpectation? expectedBoard,
        FingerprintSet? fingerprints
    )
    {
        var baseMetrics = new RunMetrics
        {
            Fingerprints = fingerprints,
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

        // Merge per-tool tallies across the run's threads; all 22 rows present, zero rows too.
        var merged = new Dictionary<string, ToolStats>(StringComparer.Ordinal);
        foreach (var thread in group)
        {
            foreach (var (tool, stats) in thread.PerTool)
            {
                merged[tool] = (merged.TryGetValue(tool, out var current) ? current : new ToolStats()).Merge(stats);
            }
        }

        var perTool = BuildPerTool(merged);
        var taskRows = FamilyRows(perTool, ToolFamily.Task);
        var coordinationRows = FamilyRows(perTool, ToolFamily.Coordination);
        var usage = UsageReader.Rollup(
            [.. group.SelectMany(t => UsageReader.ParseRecords(t.UsageRecordsJson))],
            group.ToDictionary(t => t.ThreadId, t => t.GenerationIds, StringComparer.Ordinal)
        );
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
            TaskToolCalls = taskRows.Sum(r => r.Calls),
            TaskToolErrors = taskRows.Sum(r => r.Errors),
            CoordinationToolCalls = coordinationRows.Sum(r => r.Calls),
            CoordinationToolErrors = coordinationRows.Sum(r => r.Errors),
            UnpairedToolCalls = group.Sum(t => t.UnpairedToolCalls),
            PerTool = perTool,
            ErrorCodes = MergeCounts(perTool.Values.Select(r => r.ErrorCodes)),
            WaitOutcomes = MergeCounts(group.Select(t => t.WaitOutcomes)),
            OpenObligations = OpenObligationsReport.From(
                group.Select(t => t.OpenObligationsLastObserved).LastOrDefault(),
                group.Sum(t => t.OpenObligationResults)
            ),
            Usage = usage,
            SpawnTimings = [.. group.SelectMany(t => SpawnTimingsOf(t))],
            StartupWork = group.Select(t => StartupWorkOf(t)).FirstOrDefault(w => w is not null),
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
    /// Every task tool and every coordination tool gets a row - zero-call tools included - in the
    /// spec's declared order, with errorRate rounded to 4 decimal places (0 when the tool was never
    /// called).
    /// </summary>
    private static IReadOnlyDictionary<string, PerToolScore> BuildPerTool(
        IReadOnlyDictionary<string, ToolStats> tallies
    )
    {
        var result = new Dictionary<string, PerToolScore>(StringComparer.Ordinal);
        foreach (var tool in ToolFamilies.RowOrder)
        {
            var stats = tallies.TryGetValue(tool, out var found) ? found : new ToolStats();
            result[tool] = new PerToolScore
            {
                Calls = stats.Calls,
                Errors = stats.Errors,
                ErrorRate = stats.Calls > 0 ? Math.Round((double)stats.Errors / stats.Calls, 4) : 0,
                Family = ToolFamilies.Name(ToolFamilies.Classify(tool)),
                ErrorCodes = stats.ErrorCodes,
            };
        }

        return result;
    }

    private static IReadOnlyList<PerToolScore> FamilyRows(
        IReadOnlyDictionary<string, PerToolScore> perTool,
        ToolFamily family
    ) => [.. perTool.Where(kvp => ToolFamilies.Classify(kvp.Key) == family).Select(kvp => kvp.Value)];

    private static IReadOnlyDictionary<string, int> MergeCounts(IEnumerable<IReadOnlyDictionary<string, int>> sources)
    {
        var merged = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, count) in sources.SelectMany(source => source))
        {
            merged[key] = merged.TryGetValue(key, out var seen) ? seen + count : count;
        }

        return merged;
    }

    private static IReadOnlyList<SpawnTiming> SpawnTimingsOf(ConversationStoreReader.ThreadData thread) =>
        ReadStamp<List<SpawnTiming>>(thread.SpawnTimingsJson) ?? [];

    private static StartupWork? StartupWorkOf(ConversationStoreReader.ThreadData thread) =>
        ReadStamp<StartupWork>(thread.StartupWorkJson);

    /// <summary>
    /// Deserializes one host stamp. Both are written by the host as JSON STRINGS inside
    /// <c>metadata.json</c>'s properties bag, and a malformed or older-shaped stamp must cost the
    /// run its timings, never its whole extraction.
    /// </summary>
    private static T? ReadStamp<T>(string? json)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, StampOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions StampOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
