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
    /// Status reported for a child reconstructed from the store. Lifecycle status is in-memory state
    /// that dies with the manager, so a persisted-only child gets this honest marker rather than a
    /// guessed <c>completed</c>/<c>failed</c>.
    /// </summary>
    public const string PersistedStatus = "persisted";

    /// <summary>
    /// Template reported for a persisted child whose identity was never stamped (it wrote metadata
    /// only after leaving the manager's registry). The DTO requires a template, and inventing a
    /// plausible one would misreport which reviewer ran.
    /// </summary>
    public const string UnknownTemplate = "unknown";

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
        }

        return builder.ToImmutable();
    }

    /// <summary>
    /// Projects persisted thread metadata back to a <see cref="SubAgentSummary"/>, or null when the
    /// thread is not a sub-agent of <paramref name="parentThreadId"/>.
    /// </summary>
    public static SubAgentSummary? TryProject(ThreadMetadata metadata, string parentThreadId)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!string.Equals(ReadString(metadata, ParentThreadIdKey), parentThreadId, StringComparison.Ordinal))
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

        return new SubAgentSummary
        {
            AgentId = agentId,
            Name = ReadString(metadata, NameKey),
            // Template/Task are required on the DTO. They are stamped from the live manager, so they
            // are present for any child that wrote metadata while registered; fall back to a neutral
            // value rather than dropping a child whose identity never resolved.
            Template = ReadString(metadata, TemplateKey) ?? UnknownTemplate,
            Task = ReadString(metadata, TaskKey) ?? string.Empty,
            Status = PersistedStatus,
            ThreadId = metadata.ThreadId,
            LastActivityUtc = DateTimeOffset.FromUnixTimeMilliseconds(metadata.LastUpdated),
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
}
