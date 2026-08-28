using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Persistence;

/// <summary>
/// The DURABLE parent→child link for spawned sub-agents: the property keys stamped onto a child's
/// persisted thread metadata (write side) and the projection back to a <see cref="SubAgentSummary"/>
/// (read side). Both sides live here so the key names cannot drift apart.
/// </summary>
/// <remarks>
/// <para>
/// A sub-agent's TRANSCRIPT already survives the run — children persist under the reserved
/// <c>subagent-{agentId}</c> thread convention — but the roster that names them does not:
/// <c>SubAgentManager</c> holds the parent→child mapping in memory only, so
/// <c>GET /api/conversations/{threadId}/subagents</c> could answer solely for a parent still in the
/// agent pool. Once a run ends and the parent leaves the pool (or the host restarts), the child
/// threads are still on disk but nothing says whose children they are — the panel a deep-link exists
/// to show comes back empty (or 404s). Stamping the parent id, and the identity the manager knows
/// only while it is alive, makes the roster reconstructible from the store alone.
/// </para>
/// <para>
/// Keys are namespaced <c>sample.*</c> like the other sample-owned metadata properties, so they can
/// never collide with a library-owned key in the shared property bag.
/// </para>
/// </remarks>
public static class SubAgentProvenance
{
    /// <summary>
    /// Reserved thread-id prefix for sub-agent conversations (<c>subagent-{agentId}</c>), the
    /// convention <c>SubAgentManager</c> mints child threads under.
    /// </summary>
    public const string ThreadIdPrefix = "subagent-";

    /// <summary>Thread id of the parent conversation that spawned the sub-agent.</summary>
    public const string ParentThreadIdKey = "sample.subAgentOf";

    /// <summary>Caller-supplied display name of the sub-agent, when the spawn provided one.</summary>
    public const string NameKey = "sample.subAgentName";

    /// <summary>Name of the template the sub-agent was spawned from (e.g. <c>code-reviewer:*</c>).</summary>
    public const string TemplateKey = "sample.subAgentTemplate";

    /// <summary>The task prompt the sub-agent was dispatched with.</summary>
    public const string TaskKey = "sample.subAgentTask";

    /// <summary>
    /// Exact lifecycle status of the child at the moment it was stamped (e.g. <c>running</c>,
    /// <c>completed</c>, <c>error</c>, <c>stopped</c>), lower-cased to match the live listing
    /// projection. Stamped whenever a live snapshot is available — including while still
    /// <c>Running</c>, so a reconstructed roster can distinguish "still going" from "never
    /// resolved" — and pushed causally by the manager at the terminal transition (Task 1), so it
    /// survives even if the child never writes metadata again.
    /// </summary>
    public const string StatusKey = "sample.subAgentStatus";

    /// <summary>
    /// Unix milliseconds the child reached a terminal status. Present only once <see cref="StatusKey"/>
    /// is terminal; while <c>Running</c>, this key holds <see cref="RemovalMarker"/> instead of being
    /// omitted, so a stale value from a PRIOR terminal transition (e.g. before a restart) is actually
    /// cleared from persisted metadata rather than silently surviving it. Captured once at the
    /// transition (<see cref="SubAgentSnapshot.TerminalAtUtc"/>) rather than recomputed on every stamp,
    /// so a later idempotent refresh (the child's own post-run save) never shifts it forward.
    /// </summary>
    public const string TerminalAtKey = "sample.subAgentTerminalAt";

    /// <summary>
    /// The concrete model the child's provider was built with, after spawn/tier/conversation-default/
    /// template/parent precedence resolved (<see cref="SubAgentSnapshot.EffectiveModelId"/>).
    /// </summary>
    /// <remarks>
    /// Not a <see cref="RemovalMarker"/> key, unlike <see cref="TerminalAtKey"/>. A child's effective model
    /// is decided once, when its provider is constructed, and does not go stale the way a terminal instant
    /// does across a restart — so omitting on absence is correct and explicitly clearing is not. Absent means
    /// "no model was recorded for this child", which is a fact in its own right (a queued spawn has not been
    /// routed yet) and must never be filled in from the run-level model downstream.
    /// </remarks>
    public const string ModelKey = "sample.subAgentModel";

    /// <summary>
    /// The intelligence tier that selected <see cref="ModelKey"/>, when selection was tier-based. Null for
    /// an explicit model override, the operator's conversation-wide default, or plain parent inheritance —
    /// in none of those was a tier consulted.
    /// </summary>
    public const string ModelIntelligenceKey = "sample.subAgentModelIntelligence";

    /// <summary>
    /// Which input won the model-routing precedence — the labels <c>SubAgentManager.BuildRouting</c>
    /// produces: <c>spawn-model</c>, <c>spawn-tier</c>, <c>conversation-default</c>, <c>template-model</c>,
    /// <c>template-tier</c>, <c>parent</c>, or <c>pending</c> for a spawn that has not been routed yet.
    /// This is what makes the ladder legible: the model alone cannot tell a tier that resolved to a model
    /// from a caller that named the same model outright, nor an operator's configured sub-agent model
    /// (#529) from a child that simply inherited its parent's.
    /// </summary>
    public const string ModelSelectionSourceKey = "sample.subAgentModelSource";

    /// <summary>
    /// Sentinel value for <see cref="TerminalAtKey"/> meaning "remove this key" rather than "set this
    /// value". <see cref="ThreadMetadata.Properties"/> is a non-nullable-value dictionary, so a null
    /// cannot be used as a tombstone; <see cref="NonOwningConversationStore"/>'s additive metadata merge
    /// recognizes this exact reference (via <see cref="object.ReferenceEquals(object?, object?)"/>) and
    /// removes the key instead of writing it. Internal: only <see cref="Build"/> produces it and only
    /// the sample's store decorator interprets it — never a value a caller should compare directly.
    /// </summary>
    internal static readonly object RemovalMarker = new();

    /// <summary>
    /// Status reported for a child whose metadata predates this stamp (legacy data) or that was
    /// never registered with the live manager. Lifecycle status is in-memory state that dies with
    /// the manager, so a child with no stamped status gets this honest marker rather than a guessed
    /// <c>completed</c>/<c>failed</c>.
    /// </summary>
    public const string UnknownStatus = "unknown";

    /// <summary>
    /// Template reported for a persisted child whose identity was never stamped (it wrote metadata
    /// only after leaving the manager's registry). The DTO requires a template, and inventing a
    /// plausible one would misreport which reviewer ran.
    /// </summary>
    public const string UnknownTemplate = "unknown";

    /// <summary>
    /// Statuses that count as terminal for <see cref="TerminalAtKey"/> purposes — kept as a set here
    /// (rather than duplicated at each call site) so a future addition to
    /// <see cref="SubAgentStatus"/> cannot silently omit the timestamp stamp.
    /// </summary>
    private static readonly ImmutableHashSet<SubAgentStatus> TerminalStatuses =
        ImmutableHashSet.Create(SubAgentStatus.Completed, SubAgentStatus.Error, SubAgentStatus.Stopped);

    /// <summary>
    /// Builds the properties to stamp onto a child's metadata. <paramref name="snapshot"/> is the
    /// live manager's view of that child when it is still registered; the parent link is stamped
    /// unconditionally, so the roster survives even if identity could not be resolved.
    /// </summary>
    public static ImmutableDictionary<string, object> Build(
        string parentThreadId,
        SubAgentSnapshot? snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentThreadId);

        var builder = ImmutableDictionary.CreateBuilder<string, object>(StringComparer.Ordinal);
        builder[ParentThreadIdKey] = parentThreadId;

        if (snapshot is not null)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.Name))
            {
                builder[NameKey] = snapshot.Name;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.TemplateName))
            {
                builder[TemplateKey] = snapshot.TemplateName;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.Task))
            {
                builder[TaskKey] = snapshot.Task;
            }

            builder[StatusKey] = snapshot.Status.ToString().ToLowerInvariant();

            // Model routing. Omitted rather than defaulted when the manager has nothing to report — a
            // queued spawn has not been routed yet, and a fabricated value here would be indistinguishable
            // downstream from a recorded one.
            if (!string.IsNullOrWhiteSpace(snapshot.EffectiveModelId))
            {
                builder[ModelKey] = snapshot.EffectiveModelId;
            }

            if (snapshot.EffectiveModelIntelligence is { } tier)
            {
                builder[ModelIntelligenceKey] = tier;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.ModelSelectionSource))
            {
                builder[ModelSelectionSourceKey] = snapshot.ModelSelectionSource;
            }

            if (TerminalStatuses.Contains(snapshot.Status))
            {
                var terminalAt = snapshot.TerminalAtUtc ?? DateTimeOffset.UtcNow;
                builder[TerminalAtKey] = terminalAt.ToUnixTimeMilliseconds();
            }
            else
            {
                // Explicitly mark for removal (not merely omit) so a stale terminal instant left by
                // a PRIOR terminal transition is actually cleared by NonOwningConversationStore's
                // additive merge — otherwise a restart back to Running would leave the old value
                // untouched in persisted metadata even though the in-memory snapshot has moved on.
                builder[TerminalAtKey] = RemovalMarker;
            }
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Projects persisted thread metadata back to a <see cref="SubAgentSummary"/>, or null when the
    /// thread is not a sub-agent of <paramref name="parentThreadId"/>.
    /// </summary>
    public static SubAgentSummary? TryProject(ThreadMetadata metadata, string parentThreadId)
    {
        var node = TryProject(metadata);
        return node is not null && string.Equals(node.ParentThreadId, parentThreadId, StringComparison.Ordinal)
            ? node
            : null;
    }

    /// <summary>
    /// Projects persisted thread metadata back to a <see cref="SubAgentSummary"/> regardless of who
    /// its parent is, or null when the thread is not a stamped sub-agent at all. This is the
    /// no-filter half the recursive descendant-graph reader needs: it scans every thread once and
    /// must be able to place each sub-agent node under whichever parent it was actually stamped
    /// with, not just one expected parent (see <see cref="TryProject(ThreadMetadata, string)"/> for
    /// the single-parent-filtered convenience overload, which now delegates here).
    /// </summary>
    public static SubAgentSummary? TryProject(ThreadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var parentThreadId = ReadString(metadata, ParentThreadIdKey);
        if (parentThreadId is null)
        {
            return null;
        }

        // The agent id is the thread id minus the reserved prefix; the client keys the sub-agent
        // WebSocket route by agentId, so a thread that does not follow the convention is not
        // addressable as a child and is skipped rather than surfaced as an unopenable entry.
        if (!metadata.ThreadId.StartsWith(ThreadIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var agentId = metadata.ThreadId[ThreadIdPrefix.Length..];
        if (agentId.Length == 0)
        {
            return null;
        }

        var terminalAt = ReadUnixMillis(metadata, TerminalAtKey);

        return new SubAgentSummary
        {
            AgentId = agentId,
            Name = ReadString(metadata, NameKey),
            // Template/Task are required on the DTO. They are stamped from the live manager, so they
            // are present for any child that wrote metadata while registered; fall back to a neutral
            // value rather than dropping a child whose identity never resolved.
            Template = ReadString(metadata, TemplateKey) ?? UnknownTemplate,
            Task = ReadString(metadata, TaskKey) ?? string.Empty,
            Status = ReadString(metadata, StatusKey) ?? UnknownStatus,
            ThreadId = metadata.ThreadId,
            LastActivityUtc = terminalAt ?? DateTimeOffset.FromUnixTimeMilliseconds(metadata.LastUpdated),
            ParentThreadId = parentThreadId,
            TerminalAtUtc = terminalAt,
            // All three stay nullable. A child that never registered with the live manager — or one whose
            // metadata predates this stamp — has no model recorded, and that must project as null rather
            // than as a plausible default, because the whole reason for the field is to tell a recorded
            // model apart from a guessed one.
            EffectiveModelId = ReadString(metadata, ModelKey),
            EffectiveModelIntelligence = ReadInt32(metadata, ModelIntelligenceKey),
            ModelSelectionSource = ReadString(metadata, ModelSelectionSourceKey),
        };
    }

    /// <summary>
    /// Reads a string property from the bag. Values round-trip through JSON, so a persisted string
    /// comes back as a <c>JsonElement</c> — <c>ToString()</c> yields the underlying string for both
    /// shapes (the same read the conversation-list projection uses).
    /// </summary>
    private static string? ReadString(ThreadMetadata metadata, string key) =>
        metadata.Properties?.TryGetValue(key, out var value) == true
            ? value?.ToString()
            : null;

    /// <summary>
    /// Reads a 32-bit integer property from the bag, tolerating the same numeric-JSON round-trip
    /// <see cref="ReadUnixMillis"/> tolerates. A value that is present but not a number reads as null:
    /// an unreadable tier is "not recorded", which is what the caller already handles, and is safer than
    /// a thrown projection that would drop the whole node from a roster.
    /// </summary>
    private static int? ReadInt32(ThreadMetadata metadata, string key)
    {
        if (metadata.Properties?.TryGetValue(key, out var value) != true || value is null)
        {
            return null;
        }

        return value switch
        {
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            System.Text.Json.JsonElement je when je.TryGetInt32(out var parsed) => parsed,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a Unix-milliseconds property from the bag, tolerating the numeric-JSON round-trip
    /// (<c>JsonElement</c>/<see cref="long"/>/<see cref="int"/> all possible depending on how the
    /// value reached the store) the same way <see cref="ReadString"/> tolerates it for strings.
    /// </summary>
    private static DateTimeOffset? ReadUnixMillis(ThreadMetadata metadata, string key)
    {
        if (metadata.Properties?.TryGetValue(key, out var value) != true || value is null)
        {
            return null;
        }

        return value switch
        {
            long l => DateTimeOffset.FromUnixTimeMilliseconds(l),
            int i => DateTimeOffset.FromUnixTimeMilliseconds(i),
            System.Text.Json.JsonElement je when je.TryGetInt64(out var ms) =>
                DateTimeOffset.FromUnixTimeMilliseconds(ms),
            _ => null,
        };
    }
}

