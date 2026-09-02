using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>What caused a compaction checkpoint to be built (spec 679 §3.1).</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompactionTrigger
{
    /// <summary>The policy compacted ahead of the window filling up.</summary>
    Preemptive,

    /// <summary>The provider reported an overflow and the loop compacted to retry.</summary>
    Reactive,

    /// <summary>An operator asked for a compaction.</summary>
    Manual,

    /// <summary>Built and validated for measurement only; never activated.</summary>
    Shadow,
}

/// <summary>A verbatim quote of one canonical row, addressed by its <c>Seq</c> (spec 679 §3.1, R5).</summary>
public sealed record QuotedItem
{
    /// <summary>The <c>Seq</c> of the row the quote was taken from.</summary>
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    /// <summary>The quoted text. Must be a substring of the row's text (V3).</summary>
    [JsonPropertyName("quote")]
    public required string Quote { get; init; }
}

/// <summary>A todo-board task carried into the manifest (spec 679 §3.3).</summary>
public sealed record TaskRef
{
    /// <summary>Board task id; null when the task was model-extracted because no board exists.</summary>
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    /// <summary>The task's title.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary>The task's status as the board (or the model) reported it.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}

/// <summary>A path or id the conversation produced or depends on, with its hash when known (§3.3).</summary>
public sealed record ArtifactRef
{
    /// <summary>The path or id of the artifact.</summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>Content hash when the sandbox file API could supply one.</summary>
    [JsonPropertyName("hash")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hash { get; init; }

    /// <summary>The <c>Seq</c> of the row that produced or first named the artifact.</summary>
    [JsonPropertyName("origin_seq")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OriginSeq { get; init; }
}

/// <summary>One sub-agent of the compacted conversation, by ordinal id (§3.3, §10).</summary>
public sealed record AgentRef
{
    /// <summary>The <c>agent-N</c> id.</summary>
    [JsonPropertyName("agent_id")]
    public required string AgentId { get; init; }

    /// <summary>The template the agent was spawned from.</summary>
    [JsonPropertyName("template")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Template { get; init; }

    /// <summary>What the agent was asked to do.</summary>
    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Task { get; init; }

    /// <summary>Roster status at the cut.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    /// <summary>The agent's reported outcome, when terminal.</summary>
    [JsonPropertyName("outcome")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Outcome { get; init; }

    /// <summary>The agent's own conversation thread.</summary>
    [JsonPropertyName("thread_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }
}

/// <summary>One line of the compacted-history index: a seq range and its headline (§3.1, §6.4).</summary>
public sealed record IndexEntry
{
    /// <summary>First <c>Seq</c> of the range, inclusive.</summary>
    [JsonPropertyName("from_seq")]
    public required long FromSeq { get; init; }

    /// <summary>Last <c>Seq</c> of the range, inclusive.</summary>
    [JsonPropertyName("to_seq")]
    public required long ToSeq { get; init; }

    /// <summary>The run the range belongs to.</summary>
    [JsonPropertyName("run_id")]
    public required string RunId { get; init; }

    /// <summary>A one-line headline for the range.</summary>
    [JsonPropertyName("headline")]
    public required string Headline { get; init; }
}

/// <summary>
///     Counts of cut-blocking state observed at the cut (spec 679 §2.6, R6). All zero for a legal cut;
///     recorded for audit so a rejected checkpoint can say what blocked it.
/// </summary>
public sealed record RecoveryStateAtCut
{
    /// <summary>Deferred tool calls still awaiting resolution.</summary>
    [JsonPropertyName("deferred_tool_calls")]
    public int DeferredToolCalls { get; init; }

    /// <summary>Parked <c>Wait</c> triggers.</summary>
    [JsonPropertyName("parked_waits")]
    public int ParkedWaits { get; init; }

    /// <summary>Owed continuations not yet resumed.</summary>
    [JsonPropertyName("owed_continuations")]
    public int OwedContinuations { get; init; }

    /// <summary>Interrupted turns.</summary>
    [JsonPropertyName("interrupted_turns")]
    public int InterruptedTurns { get; init; }

    /// <summary>True when no cut-blocking state was observed.</summary>
    [JsonIgnore]
    public bool IsClean =>
        DeferredToolCalls == 0 && ParkedWaits == 0 && OwedContinuations == 0 && InterruptedTurns == 0;
}

/// <summary>The last canonical row a checkpoint covers, as <c>(Seq, MessageId)</c> (spec 679 §2.2).</summary>
public sealed record CheckpointBoundary
{
    /// <summary>The <c>Seq</c> of the last covered row.</summary>
    [JsonPropertyName("seq")]
    public required long Seq { get; init; }

    /// <summary>
    ///     The persisted id of that row. The tie-check: a row at <see cref="Seq" /> with a different id
    ///     means the boundary no longer points where it was cut, and the checkpoint is rejected.
    /// </summary>
    [JsonPropertyName("message_id")]
    public required string MessageId { get; init; }
}

/// <summary>Size and provenance figures recorded with a checkpoint (spec 679 §3.1, §8.2).</summary>
public sealed record CheckpointStats
{
    /// <summary>How many canonical rows the checkpoint stands in for.</summary>
    [JsonPropertyName("rows_covered")]
    public long RowsCovered { get; init; }

    /// <summary>Estimated tokens of the execution view before the cut.</summary>
    [JsonPropertyName("estimated_tokens_before")]
    public long EstimatedTokensBefore { get; init; }

    /// <summary>Estimated tokens of the execution view after the cut.</summary>
    [JsonPropertyName("estimated_tokens_after")]
    public long EstimatedTokensAfter { get; init; }

    /// <summary>The summarization call's usage attempt id, for cost attribution (§3.2).</summary>
    [JsonPropertyName("summary_usage_attempt_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SummaryUsageAttemptId { get; init; }

    /// <summary>Wall-clock latency of the summarization pass, in milliseconds.</summary>
    [JsonPropertyName("summary_latency_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? SummaryLatencyMs { get; init; }
}

/// <summary>
///     The structured half of a semantic context checkpoint (spec 679 §3.1). Every section is a list of
///     verbatim quotes or structural copies whose sources are named in §3.3; the free-text half is the
///     checkpoint's <see cref="CompactionCheckpointMessage.Narrative" />.
/// </summary>
/// <remarks>
///     Sections default to empty rather than being <c>required</c>, deliberately: a manifest is a durable
///     row read back by later builds, and a section a later build no longer writes must read as empty, not
///     as an unreadable row. Whether a section MAY be empty is the validator's call (#683), not the
///     serializer's.
/// </remarks>
public sealed record ContextManifest
{
    /// <summary>
    ///     The most recent human instruction, quoted verbatim. Listed first so the model reads what it is
    ///     currently asked to do before the standing instructions that qualify it (R3).
    /// </summary>
    [JsonPropertyName("current_instruction")]
    public IReadOnlyList<QuotedItem> CurrentInstruction { get; init; } = [];

    /// <summary>Standing instructions, oldest first, verbatim with seq refs.</summary>
    [JsonPropertyName("instructions")]
    public IReadOnlyList<QuotedItem> Instructions { get; init; } = [];

    /// <summary>Goal and acceptance criteria, free text, capped.</summary>
    [JsonPropertyName("goals")]
    public IReadOnlyList<string> Goals { get; init; } = [];

    /// <summary>Decisions, approvals, constraints and prohibitions, verbatim with seq refs.</summary>
    [JsonPropertyName("decisions")]
    public IReadOnlyList<QuotedItem> Decisions { get; init; } = [];

    /// <summary>Open work, from the todo board when one exists.</summary>
    [JsonPropertyName("tasks")]
    public IReadOnlyList<TaskRef> Tasks { get; init; } = [];

    /// <summary>Artifacts and evidence: paths or ids, hashes when known, seq of origin.</summary>
    [JsonPropertyName("artifacts")]
    public IReadOnlyList<ArtifactRef> Artifacts { get; init; } = [];

    /// <summary>Every sub-agent of the conversation with its status and outcome.</summary>
    [JsonPropertyName("agents")]
    public IReadOnlyList<AgentRef> Agents { get; init; } = [];

    /// <summary>Index of compacted history: seq ranges to headlines, for the recall tool.</summary>
    [JsonPropertyName("index")]
    public IReadOnlyList<IndexEntry> Index { get; init; } = [];

    /// <summary>Cut-blocking state observed at the cut; all zero by R6.</summary>
    [JsonPropertyName("recovery")]
    public RecoveryStateAtCut Recovery { get; init; } = new();
}

/// <summary>
///     The durable row a compaction commit appends (spec 679 §3.1). It gives the UI its divider position,
///     the workspace mirror a line, the recall tool its index, and an older binary a row it skips as an
///     unknown <c>$type</c> — which is the rollback contract (§8.3).
/// </summary>
/// <remarks>
///     <para>
///         Never dispatched to a provider as-is: the agent projection renders it into a synthetic user
///         turn (§2.3, #683). <see cref="Role" /> is <see cref="Messages.Role.User" /> so that, should the
///         row ever reach a provider mapping unrendered, it lands on the side every provider accepts.
///     </para>
///     <para>
///         <see cref="Text" /> is a computed rendering of the structured fields (the same discipline as
///         <see cref="NotifyMessage.Text" />): it is written for search and the mirror and skipped on read,
///         so it can never drift from the manifest it renders. The exact envelope prose is owned by the
///         renderer in #683; this rendering is the durable, deterministic minimum.
///     </para>
/// </remarks>
public sealed record CompactionCheckpointMessage : IMessage, ICanGetText
{
    /// <summary>The <c>$type</c> discriminator the JSON converter writes for this row.</summary>
    public const string TypeDiscriminator = "compaction_checkpoint";

    /// <summary>The persisted schema version this build writes.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Checkpoint id, <c>cp-{thread-short}-{n}</c>. The key of the state machine entry.</summary>
    [JsonPropertyName("checkpoint_id")]
    public required string CheckpointId { get; init; }

    /// <summary>Schema version of this row.</summary>
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The last canonical row this checkpoint covers.</summary>
    [JsonPropertyName("boundary")]
    public required CheckpointBoundary Boundary { get; init; }

    /// <summary>The checkpoint this one chains from, when any (§2.5).</summary>
    [JsonPropertyName("supersedes_checkpoint_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SupersedesCheckpointId { get; init; }

    /// <summary>What caused the compaction.</summary>
    [JsonPropertyName("trigger")]
    public required CompactionTrigger Trigger { get; init; }

    /// <summary>The structured manifest.</summary>
    [JsonPropertyName("manifest")]
    public required ContextManifest Manifest { get; init; }

    /// <summary>The narrative: what happened, bounded by the narrative token cap.</summary>
    [JsonPropertyName("narrative")]
    public required string Narrative { get; init; }

    /// <summary>Size and provenance figures.</summary>
    [JsonPropertyName("stats")]
    public CheckpointStats Stats { get; init; } = new();

    /// <summary>When the checkpoint was built.</summary>
    [JsonPropertyName("created_at_utc")]
    public DateTimeOffset CreatedAtUtc { get; init; }

    private string? _cachedText;

    /// <summary>The rendered envelope, for search and the mirror. Computed; never set.</summary>
    [JsonPropertyName("text")]
    public string Text => _cachedText ??= Render();

    /// <inheritdoc />
    public string? GetText() => Text;

    /// <summary>Always <see cref="Messages.Role.User" /> — see the type remarks.</summary>
    [JsonPropertyName("role")]
    public Role Role => Role.User;

    /// <summary>The agent whose history was compacted: <c>agent-N</c>, or null for the root.</summary>
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("parentRunId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ParentRunId { get; init; }

    /// <inheritdoc />
    [JsonPropertyName("messageOrderIdx")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MessageOrderIdx { get; init; }

    /// <summary>Not persisted on checkpoint rows.</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    private string Render()
    {
        var sb = new StringBuilder();
        _ = sb.Append(
                CultureInfo.InvariantCulture,
                $"<context-checkpoint version=\"{SchemaVersion}\" id=\"{CheckpointId}\""
            )
            .Append(CultureInfo.InvariantCulture, $" covers_seq=\"1-{Boundary.Seq}\"")
            .Append(CultureInfo.InvariantCulture, $" created_at=\"{CreatedAtUtc.ToUniversalTime():O}\">")
            .Append('\n');

        AppendQuoted(sb, "Current instruction (verbatim)", Manifest.CurrentInstruction);
        AppendQuoted(sb, "Standing instructions (verbatim, oldest first)", Manifest.Instructions);
        AppendLines(sb, "Goal and acceptance criteria", Manifest.Goals);
        AppendQuoted(sb, "Decisions and approvals", Manifest.Decisions);
        AppendLines(
            sb,
            "Open work",
            Manifest.Tasks.Select(t => t.Id is null ? $"[{t.Status}] {t.Title}" : $"[{t.Status}] {t.Id}: {t.Title}")
        );
        AppendLines(
            sb,
            "Artifacts and evidence",
            Manifest.Artifacts.Select(a =>
                a.Path
                + (a.Hash is null ? string.Empty : $" ({a.Hash})")
                + (a.OriginSeq is null ? string.Empty : $" [seq {a.OriginSeq.Value}]")
            )
        );
        AppendLines(
            sb,
            "Agents",
            Manifest.Agents.Select(a =>
                $"{a.AgentId}: {a.Template ?? "-"}; {a.Task ?? "-"}; {a.Status}"
                + (a.Outcome is null ? string.Empty : $"; {a.Outcome}")
            )
        );

        _ = sb.Append("## What happened\n").Append(Narrative).Append('\n');

        AppendLines(
            sb,
            "Index of compacted history",
            Manifest.Index.Select(i =>
                string.Create(CultureInfo.InvariantCulture, $"seq {i.FromSeq}-{i.ToSeq} ({i.RunId}): {i.Headline}")
            )
        );

        _ = sb.Append("Use RecallConversation to read any compacted range verbatim.\n</context-checkpoint>");
        return sb.ToString();
    }

    private static void AppendQuoted(StringBuilder sb, string heading, IReadOnlyList<QuotedItem> items) =>
        AppendLines(
            sb,
            heading,
            items.Select(q => string.Create(CultureInfo.InvariantCulture, $"[seq {q.Seq}] {q.Quote}"))
        );

    private static void AppendLines(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        var any = false;
        foreach (var line in lines)
        {
            if (!any)
            {
                _ = sb.Append("## ").Append(heading).Append('\n');
                any = true;
            }

            _ = sb.Append("- ").Append(line).Append('\n');
        }
    }
}
