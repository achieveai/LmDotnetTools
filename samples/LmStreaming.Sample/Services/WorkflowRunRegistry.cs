using System.Collections.Concurrent;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmWorkflow;
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Side-table mapping a conversation's threadId to its per-conversation <see cref="WorkflowManager"/>.
///     A StartWorkflowAgent run's isolated controller loop lives inside the (LmWorkflow-owned)
///     WorkflowManager, which the conversation loop cannot reference (LmMultiTurn does not depend on
///     LmWorkflow). This registry lets the sample's read-only <c>GET /{threadId}/subagents</c> endpoint and
///     the sub-agent WebSocket surface those runs as center-pane tabs. The manager is owned by the agent's
///     resources; this registry only holds a reference and is cleared when the agent is torn down.
///     <para>
///     Because the manager (and its runs) live only in memory, a server restart would otherwise lose every
///     workflow tab. To make the tabs durable, this registry ALSO keeps a small on-disk index of each
///     conversation's workflow + delegate tabs (see <see cref="PersistTabs"/> / <see cref="GetPersistedTabs"/>).
///     The endpoint write-throughs the live snapshot on each poll and reads back the merged (live ∪ persisted)
///     set, so completed workflow tabs survive a restart. Delegate transcripts are already persisted as
///     <c>subagent-{id}</c> threads in the conversation store, so a persisted tab replays read-only.
///     </para>
/// </summary>
public sealed class WorkflowRunRegistry
{
    private readonly ConcurrentDictionary<string, WorkflowManager> _byThread = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.Ordinal);
    private readonly string? _indexDirectory;

    private static readonly JsonSerializerOptions IndexJson =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    ///     Creates the registry. When <paramref name="indexDirectory"/> is supplied, the workflow-tab index is
    ///     persisted there (one JSON file per conversation) so tabs survive a restart; when null (the default,
    ///     used by unit tests that don't exercise persistence) the index is a no-op and tabs are in-memory only.
    /// </summary>
    public WorkflowRunRegistry(string? indexDirectory = null)
    {
        _indexDirectory = indexDirectory;
        if (!string.IsNullOrWhiteSpace(_indexDirectory))
        {
            _ = Directory.CreateDirectory(_indexDirectory);
        }
    }

    /// <summary>Associates <paramref name="manager"/> with <paramref name="threadId"/> (overwriting any stale entry).</summary>
    public void Register(string threadId, WorkflowManager manager)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(manager);
        _byThread[threadId] = manager;
    }

    /// <summary>Drops the entry for <paramref name="threadId"/> (idempotent) — call on conversation teardown.</summary>
    public void Remove(string threadId) => _byThread.TryRemove(threadId, out _);

    /// <summary>Resolves the WorkflowManager for <paramref name="threadId"/>, or false if the thread has none.</summary>
    public bool TryGet(string threadId, out WorkflowManager? manager) =>
        _byThread.TryGetValue(threadId, out manager);

    /// <summary>
    ///     Merges the given workflow + delegate tabs into the conversation's persisted index (upsert by
    ///     Kind+AgentId, never removing an entry a live snapshot no longer reports), so a run that has left
    ///     memory — e.g. after a restart — still surfaces as a tab. No-op when persistence is disabled or the
    ///     snapshot is empty.
    /// </summary>
    public void PersistTabs(string threadId, IReadOnlyList<SubAgentSummary> tabs)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentNullException.ThrowIfNull(tabs);

        if (string.IsNullOrWhiteSpace(_indexDirectory) || tabs.Count == 0)
        {
            return;
        }

        var gate = _fileLocks.GetOrAdd(threadId, static _ => new object());
        lock (gate)
        {
            var merged = new Dictionary<(string Kind, string AgentId), SubAgentSummary>();
            foreach (var existing in ReadIndex(threadId))
            {
                merged[(existing.Kind, existing.AgentId)] = existing;
            }

            foreach (var tab in tabs)
            {
                // Live snapshot wins on conflict (fresher status), and NEVER deletes a previously-persisted
                // tab that the live snapshot has dropped (that's exactly the run that has left memory).
                merged[(tab.Kind, tab.AgentId)] = tab;
            }

            try
            {
                File.WriteAllText(PathFor(threadId), JsonSerializer.Serialize(merged.Values, IndexJson));
            }
            catch (IOException)
            {
                // Best-effort durability: a transient write failure just means this poll's snapshot isn't
                // persisted; the next poll re-attempts. Never fail the read the caller is servicing.
            }
        }
    }

    /// <summary>The persisted workflow + delegate tabs for a conversation (empty when none / persistence off).</summary>
    public IReadOnlyList<SubAgentSummary> GetPersistedTabs(string threadId)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        return string.IsNullOrWhiteSpace(_indexDirectory) ? [] : ReadIndex(threadId);
    }

    private IReadOnlyList<SubAgentSummary> ReadIndex(string threadId)
    {
        var path = PathFor(threadId);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<SubAgentSummary>>(File.ReadAllText(path), IndexJson) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private string PathFor(string threadId)
    {
        var safe = string.Concat(threadId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_indexDirectory!, safe + ".json");
    }
}
