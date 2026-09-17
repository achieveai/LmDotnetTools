using System.Text.Json;

namespace TodoEval.Runner.Metrics;

/// <summary>One token bucket: the five counters plus the record count that produced them.</summary>
internal sealed record UsageTotals
{
    public int Records { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long TotalTokens { get; init; }

    public UsageTotals Add(UsageRecordRow row) =>
        new()
        {
            Records = Records + 1,
            InputTokens = InputTokens + row.InputTokens,
            OutputTokens = OutputTokens + row.OutputTokens,
            CacheReadTokens = CacheReadTokens + row.CacheReadTokens,
            CacheWriteTokens = CacheWriteTokens + row.CacheWriteTokens,
            ReasoningTokens = ReasoningTokens + row.ReasoningTokens,
            TotalTokens = TotalTokens + row.TotalTokens,
        };
}

/// <summary>
/// One persisted <c>UsageRecord</c>, reduced to the fields the eval attributes tokens with. A
/// deliberately partial mirror: the eval must keep reading archives written by older hosts, so it
/// reads field by field and never fails on an unknown or missing one.
/// </summary>
internal sealed record UsageRecordRow
{
    /// <summary>The <c>UsageExecutionKind</c> naming a compaction summarization pass.</summary>
    public const string CompactionKind = "Compaction";

    /// <summary>The generation-id prefix a summary pass mints (<c>CompactionRuntime</c>).</summary>
    public const string CompactionAttemptPrefix = "compaction-";

    public required string ProviderAttemptId { get; init; }
    public required string ExecutionKind { get; init; }
    public string? ParentExecutionId { get; init; }
    public string? RootConversationId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long TotalTokens { get; init; }

    /// <summary>
    /// The record's revision. One attempt is relayed into more than one bag, and the copies are not
    /// the same age: the sub-agent's bag holds the revision written before the pricing resolver ran,
    /// the root bag the resolved one. Higher is more resolved. 0 when the archive predates the field,
    /// which keeps every such copy equal and leaves the first one standing.
    /// </summary>
    public int Revision { get; init; }

    /// <summary>
    /// What this attempt cost in micro-units, or null when no cost could be resolved. Mirrors
    /// <c>UsageRecord.PreferredCostMicros</c>: the provider's own figure when it reported one, else
    /// the public-pricing estimate. Read defensively — an archive written before costs were persisted
    /// carries none of the three fields, and null there means UNKNOWN, never free.
    /// </summary>
    public long? CostMicros { get; init; }

    /// <summary>The checkpoint a compaction summary pass produced; null for every other kind.</summary>
    public string? CompactionCheckpointId { get; init; }

    /// <summary>
    /// True when this row is a compaction summary pass. Three independent marks, any one of which is
    /// enough: the execution kind, the checkpoint id, or the <c>compaction-</c> generation-id prefix
    /// the summary pass mints. An archive written by a host that stamps only one of them still
    /// attributes correctly.
    /// </summary>
    public bool IsCompactionSummary =>
        string.Equals(ExecutionKind, CompactionKind, StringComparison.Ordinal)
        || CompactionCheckpointId is not null
        || AttemptKey.StartsWith(CompactionAttemptPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Billed input tokens that were NOT served from cache. <c>UsageRecord</c> declares
    /// <c>CacheReadTokens ⊆ InputTokens</c> as normative and carries no accounting-mode field, so the
    /// subtraction is the definition; the clamp only guards a provider row that violates the subset
    /// rule, which must not turn into a negative token count.
    /// </summary>
    public long InputTokensUncached => Math.Max(0, InputTokens - CacheReadTokens);

    /// <summary>
    /// The emitting execution: the sub-agent's own thread for a sub-agent record
    /// (<c>ParentExecutionId</c> holds the child's <c>subagent-*</c> id, not the parent's), the root
    /// conversation for a primary one.
    /// </summary>
    public string AgentId => ParentExecutionId ?? RootConversationId ?? "(unknown)";

    /// <summary>
    /// The attempt key <c>UsageRecordMapper</c> appended to the owner id — the generation id when the
    /// provider gave one, else a synthetic <c>derived:...</c> key that can never join to a turn.
    /// </summary>
    public string AttemptKey
    {
        get
        {
            var separator = ProviderAttemptId.IndexOf(':', StringComparison.Ordinal);
            return separator < 0 ? ProviderAttemptId : ProviderAttemptId[(separator + 1)..];
        }
    }
}

/// <summary>
/// One run's cache-aware cost. Compaction is only worth its price where the alternative is failure —
/// on a conversation that fits the window it rewrites the cached prefix and bills every summary — so
/// a strategy comparison has to read the cache split and the money, not the token total alone.
/// </summary>
/// <remarks>
/// Every field is derived from the SAME deduplicated walk as <see cref="UsageReport.Totals"/>: one
/// provider attempt is relayed into more than one metadata bag by design, and two independent
/// reductions of that bag are exactly how a token count and the cost computed from it end up
/// disagreeing in one published row.
/// </remarks>
internal sealed record RunCost
{
    /// <summary>The cost block of a run whose threads persisted no usage record at all.</summary>
    public static readonly RunCost Absent = new();

    public long InputTokens { get; init; }

    /// <summary>Billed input tokens not served from cache: <c>inputTokens - cacheReadTokens</c>.</summary>
    public long InputTokensUncached { get; init; }

    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long OutputTokens { get; init; }

    /// <summary>Cached share of billed input, 0 when no input token was billed.</summary>
    public double CacheHitRatio { get; init; }

    /// <summary>
    /// Resolved cost in micro-units over <see cref="RecordsWithCost"/>, or null when NO record carried
    /// one. Null means unpriced, never free — and a figure covering only some of the records is a
    /// lower bound, which is why the two counts travel with it.
    /// </summary>
    public long? CostMicros { get; init; }

    public int RecordsWithCost { get; init; }

    /// <summary>Deduplicated usage records behind every number here; zero means ABSENT, not zero spend.</summary>
    public int Records { get; init; }

    /// <summary>Records marked as a compaction summary pass (<see cref="UsageRecordRow.IsCompactionSummary"/>).</summary>
    public int CompactionSummaryRecords { get; init; }

    /// <summary>
    /// Tokens the summary passes themselves billed — what compaction charges before any saving is
    /// counted. Null when <see cref="CompactionSummaryRecords"/> is 0, because a 0 there would read as
    /// "measured, the summaries were free" when the truth is that nothing attributed them.
    /// </summary>
    public long? CompactionSummaryTokens { get; init; }
}

/// <summary>The score object's <c>usage</c> block: rollups plus the limits that bound them.</summary>
internal sealed record UsageReport
{
    /// <summary>
    /// Execution kinds the ledger declares but this build never emits. They are LISTED rather than
    /// reported as zero rows, because a zero would read as "measured, none happened" when the truth
    /// is "this build has no code path that produces one".
    /// </summary>
    public static readonly IReadOnlyList<string> KindsNotEmittedByThisBuild =
    [
        "WorkflowController",
        "WorkflowTask",
        "Continuation",
    ];

    public const string TurnJoinNote =
        "Turn attribution is a HEURISTIC: UsageRecord carries no generation id, so a record is joined "
        + "to a turn by stripping the owner-execution prefix from ProviderAttemptId and matching the "
        + "remainder against the thread's generation ids. Records whose attempt key is synthetic "
        + "(derived:...) can never join and are counted in unattributedTurnTokens.";

    public const string ToolFamilyNote =
        "Tokens are NOT attributable to a tool family. A turn's prompt carries every tool's schema at "
        + "once, so no per-family split exists in the data; none is invented here.";

    public UsageTotals Totals { get; init; } = new();

    /// <summary>The cache-aware cost of the same deduplicated records (compaction strategy eval, §6).</summary>
    public RunCost Cost { get; init; } = RunCost.Absent;

    public int DuplicateAttemptIds { get; init; }
    public IReadOnlyDictionary<string, UsageTotals> ByExecutionKind { get; init; } =
        new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, UsageTotals> ByAgent { get; init; } =
        new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
    public IReadOnlyList<string> KindsNotEmitted { get; init; } = KindsNotEmittedByThisBuild;
    public long AttributedTurnTokens { get; init; }
    public long UnattributedTurnTokens { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [TurnJoinNote, ToolFamilyNote];
}

/// <summary>
/// Offline parser for the <c>usage.records</c> property the host persists into each thread's
/// <c>metadata.json</c> (<c>ConversationUsageProjection.RecordsPropertyKey</c>).
/// </summary>
/// <remarks>
/// Two shape facts the host's serializer fixes and this reader must honour: the records payload is
/// written with the DEFAULT serializer options, so its property names are PascalCase and its enums
/// are NUMBERS — unlike the camelCase <c>metadata.json</c> envelope around it. Reads here are
/// case-insensitive and accept an enum as either a number or its name, so an archive written by a
/// host that later adds a string-enum converter keeps parsing.
/// </remarks>
internal static class UsageReader
{
    public const string RecordsPropertyKey = "usage.records";

    /// <summary>
    /// <c>UsageExecutionKind</c> by ordinal — the form the default serializer writes. The list must
    /// stay in the enum's declaration order and cover every member: a kind missing from the end reads
    /// as <c>(unknown)</c>, which is how a whole compaction pass would silently leave the by-kind
    /// rollup it belongs in.
    /// </summary>
    private static readonly string[] KindNames =
    [
        "Primary",
        "SubAgent",
        "WorkflowController",
        "WorkflowTask",
        "Continuation",
        UsageRecordRow.CompactionKind,
    ];

    /// <summary>Parses the records array out of the raw <c>usage.records</c> property value.</summary>
    public static IReadOnlyList<UsageRecordRow> ParseRecords(string? recordsJson)
    {
        if (string.IsNullOrWhiteSpace(recordsJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(recordsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return [.. doc.RootElement.EnumerateArray().Select(ReadRow).OfType<UsageRecordRow>()];
        }
        catch (JsonException)
        {
            // A corrupt usage bag must not sink the whole extraction; the run still has transcripts.
            return [];
        }
    }

    /// <summary>
    /// Rolls a run's records up. <paramref name="records"/> may span the root thread and every
    /// sub-agent bag, so rows are DEDUPED by <c>ProviderAttemptId</c> first: the same attempt is
    /// relayed into more than one bag by design, and counting it twice would double the run's tokens.
    /// The survivor is the copy with the highest <see cref="UsageRecordRow.Revision"/>, NOT whichever
    /// bag was read first: the copies differ in how resolved they are, and the sub-agent's earlier
    /// one is written before the pricing resolver fills the cost in. Keeping the first dropped the
    /// price of every summariser call in the compaction eval while still counting its tokens, so the
    /// run's cost read as a total when it was a lower bound.
    /// <paramref name="generationIdsByAgent"/> supplies the turn ids each thread actually recorded,
    /// which is what the best-effort turn join matches against.
    /// </summary>
    public static UsageReport Rollup(
        IReadOnlyList<UsageRecordRow> records,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> generationIdsByAgent
    )
    {
        // Two passes, because the survivor of a duplicate is not knowable until every copy is seen.
        // The first pass picks it; the second walks the ORIGINAL order filtered to survivors, so the
        // per-kind and per-agent groupings keep the order the archives were read in.
        var survivors = new Dictionary<string, UsageRecordRow>(StringComparer.Ordinal);
        foreach (var row in records)
        {
            if (!survivors.TryGetValue(row.ProviderAttemptId, out var held) || row.Revision > held.Revision)
            {
                survivors[row.ProviderAttemptId] = row;
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var totals = new UsageTotals();
        var byKind = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        var byAgent = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        long attributed = 0;
        long unattributed = 0;
        var cost = new CostAccumulator();

        foreach (var row in records)
        {
            if (!ReferenceEquals(survivors[row.ProviderAttemptId], row) || !seen.Add(row.ProviderAttemptId))
            {
                duplicates++;
                continue;
            }

            totals = totals.Add(row);
            cost.Add(row);
            byKind[row.ExecutionKind] = (byKind.TryGetValue(row.ExecutionKind, out var k) ? k : new()).Add(row);
            byAgent[row.AgentId] = (byAgent.TryGetValue(row.AgentId, out var a) ? a : new()).Add(row);

            var joined =
                generationIdsByAgent.TryGetValue(row.AgentId, out var turnIds) && turnIds.Contains(row.AttemptKey);
            if (joined)
            {
                attributed += row.TotalTokens;
            }
            else
            {
                unattributed += row.TotalTokens;
            }
        }

        return new UsageReport
        {
            Totals = totals,
            Cost = cost.Build(),
            DuplicateAttemptIds = duplicates,
            ByExecutionKind = byKind,
            ByAgent = byAgent,
            AttributedTurnTokens = attributed,
            UnattributedTurnTokens = unattributed,
        };
    }

    /// <summary>
    /// Folds the deduplicated rows into a <see cref="RunCost"/>. A mutable accumulator rather than a
    /// second pass, so the cost block can never be computed over a different row set than the totals
    /// beside it.
    /// </summary>
    private sealed class CostAccumulator
    {
        private int _records;
        private long _input;
        private long _inputUncached;
        private long _cacheRead;
        private long _cacheWrite;
        private long _output;
        private long _cost;
        private int _recordsWithCost;
        private int _compactionRecords;
        private long _compactionTokens;

        public void Add(UsageRecordRow row)
        {
            _records++;
            _input += row.InputTokens;
            _inputUncached += row.InputTokensUncached;
            _cacheRead += row.CacheReadTokens;
            _cacheWrite += row.CacheWriteTokens;
            _output += row.OutputTokens;
            if (row.CostMicros is { } micros)
            {
                _cost += micros;
                _recordsWithCost++;
            }

            if (row.IsCompactionSummary)
            {
                _compactionRecords++;
                _compactionTokens += row.TotalTokens;
            }
        }

        public RunCost Build() =>
            _records == 0
                ? RunCost.Absent
                : new RunCost
                {
                    Records = _records,
                    InputTokens = _input,
                    InputTokensUncached = _inputUncached,
                    CacheReadTokens = _cacheRead,
                    CacheWriteTokens = _cacheWrite,
                    OutputTokens = _output,
                    CacheHitRatio = _input == 0 ? 0 : (double)_cacheRead / _input,
                    CostMicros = _recordsWithCost == 0 ? null : _cost,
                    RecordsWithCost = _recordsWithCost,
                    CompactionSummaryRecords = _compactionRecords,
                    CompactionSummaryTokens = _compactionRecords == 0 ? null : _compactionTokens,
                };
    }

    private static UsageRecordRow? ReadRow(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var attemptId = GetString(element, "ProviderAttemptId") ?? GetString(element, "LogicalCallId");
        if (attemptId is null)
        {
            // Without an attempt id the row cannot be deduped, so counting it risks double-counting
            // the very tokens this reader exists to attribute. Dropping it is the safe direction.
            return null;
        }

        return new UsageRecordRow
        {
            ProviderAttemptId = attemptId,
            ExecutionKind = GetEnumName(element, "ExecutionKind"),
            ParentExecutionId = GetString(element, "ParentExecutionId"),
            RootConversationId = GetString(element, "RootConversationId"),
            InputTokens = GetLong(element, "InputTokens"),
            OutputTokens = GetLong(element, "OutputTokens"),
            CacheReadTokens = GetLong(element, "CacheReadTokens"),
            CacheWriteTokens = GetLong(element, "CacheWriteTokens"),
            ReasoningTokens = GetLong(element, "ReasoningTokens"),
            TotalTokens = GetLong(element, "TotalTokens"),
            Revision = (int)GetLong(element, "Revision"),
            CostMicros = ReadCost(element),
            CompactionCheckpointId = GetString(element, "CompactionCheckpointId"),
        };
    }

    /// <summary>
    /// The record's preferred cost: the serialized computed property when the archive carries it,
    /// else the same rule recomputed from the two underlying figures — provider-reported first,
    /// because it is what was actually billed. Null when the archive carries none of the three.
    /// </summary>
    private static long? ReadCost(JsonElement element) =>
        GetNullableLong(element, "PreferredCostMicros")
        ?? GetNullableLong(element, "ProviderReportedCostMicros")
        ?? GetNullableLong(element, "EstimatedPublicCostMicros");

    /// <summary>Case-insensitive property lookup — the payload's casing is not this reader's to pin.</summary>
    private static bool TryGet(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A long that distinguishes "absent or null" from zero. A cost of 0 is a real measurement (a free
    /// model), so it must never collapse into the same value as an unpriced record.
    /// </summary>
    private static long? GetNullableLong(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : null;

    private static long GetLong(JsonElement element, string name) =>
        TryGet(element, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : 0;

    /// <summary>
    /// The enum's NAME, whether the archive stored the number (what the default serializer writes
    /// today) or the name (what a host with a string-enum converter would write).
    /// </summary>
    private static string GetEnumName(JsonElement element, string name)
    {
        if (!TryGet(element, name, out var value))
        {
            return "(unknown)";
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? "(unknown)";
        }

        return
            value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var ordinal)
            && ordinal >= 0
            && ordinal < KindNames.Length
            ? KindNames[ordinal]
            : "(unknown)";
    }
}
