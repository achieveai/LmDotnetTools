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

    private static readonly string[] KindNames =
    [
        "Primary",
        "SubAgent",
        "WorkflowController",
        "WorkflowTask",
        "Continuation",
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
    /// <paramref name="generationIdsByAgent"/> supplies the turn ids each thread actually recorded,
    /// which is what the best-effort turn join matches against.
    /// </summary>
    public static UsageReport Rollup(
        IReadOnlyList<UsageRecordRow> records,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> generationIdsByAgent
    )
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = 0;
        var totals = new UsageTotals();
        var byKind = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        var byAgent = new Dictionary<string, UsageTotals>(StringComparer.Ordinal);
        long attributed = 0;
        long unattributed = 0;

        foreach (var row in records)
        {
            if (!seen.Add(row.ProviderAttemptId))
            {
                duplicates++;
                continue;
            }

            totals = totals.Add(row);
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
            DuplicateAttemptIds = duplicates,
            ByExecutionKind = byKind,
            ByAgent = byAgent,
            AttributedTurnTokens = attributed,
            UnattributedTurnTokens = unattributed,
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
        };
    }

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
