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
///     <para>
///     Since #244 the same index also carries hierarchy nodes spawned by the Agent tool, and every row
///     stamps the shared <see cref="AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration.CollaborationNodeRecord"/>
///     schema version so a row can always be read back as the node shape the rest of the system speaks.
///     Rows written before #244 carry none of the collaboration fields and still deserialize — the added
///     members are optional, so an old index file loads as exactly the tabs it always described.
///     </para>
///     <para>
///     <b>The index is BOUNDED.</b> Its upsert never deletes a row the live snapshot dropped, which is
///     exactly what makes a completed run survive a restart — and equally what would let a long-lived
///     conversation accumulate rows without limit, since every poll rewrites and re-reads the whole file
///     and the projection walks all of it. So a write keeps at most
///     <see cref="MaxPersistedEntriesPerConversation"/> rows per conversation, preferring the ones the
///     live snapshot still reports and then the most recently active. The retained tail is what is
///     dropped, and only ever from the on-disk index: a live run is never evicted by this bound, and
///     nothing here touches the collaboration directory, which is the library's to bound.
///     </para>
/// </summary>
public sealed class WorkflowRunRegistry
{
    /// <summary>
    ///     Default ceiling on how many tab rows one conversation's persisted index keeps. Chosen well above
    ///     any plausible interactive hierarchy (a conversation's workflow runs plus their delegates) so the
    ///     bound is invisible in normal use and only ever trims a runaway.
    /// </summary>
    public const int DefaultMaxPersistedEntriesPerConversation = 256;

    private readonly ConcurrentDictionary<string, WorkflowManager> _byThread = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, object> _fileLocks = new(StringComparer.Ordinal);
    private readonly string? _indexDirectory;

    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    ///     Creates the registry. When <paramref name="indexDirectory"/> is supplied, the workflow-tab index is
    ///     persisted there (one JSON file per conversation) so tabs survive a restart; when null (the default,
    ///     used by unit tests that don't exercise persistence) the index is a no-op and tabs are in-memory only.
    /// </summary>
    /// <param name="indexDirectory">Where the per-conversation index files live, or null to disable persistence.</param>
    /// <param name="maxPersistedEntriesPerConversation">
    ///     Ceiling on retained rows per conversation; see <see cref="MaxPersistedEntriesPerConversation"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="maxPersistedEntriesPerConversation"/> is not positive — an index that may hold
    ///     nothing would silently discard every tab it was asked to make durable.
    /// </exception>
    public WorkflowRunRegistry(
        string? indexDirectory = null,
        int maxPersistedEntriesPerConversation = DefaultMaxPersistedEntriesPerConversation
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPersistedEntriesPerConversation, 1);

        _indexDirectory = indexDirectory;
        MaxPersistedEntriesPerConversation = maxPersistedEntriesPerConversation;
        if (!string.IsNullOrWhiteSpace(_indexDirectory))
        {
            _ = Directory.CreateDirectory(_indexDirectory);
        }
    }

    /// <summary>The configured ceiling on retained rows per conversation.</summary>
    public int MaxPersistedEntriesPerConversation { get; }

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
    public bool TryGet(string threadId, out WorkflowManager? manager) => _byThread.TryGetValue(threadId, out manager);

    /// <summary>
    ///     Merges the given workflow + delegate tabs into the conversation's persisted index (upsert by
    ///     Kind+AgentId, never removing an entry a live snapshot no longer reports), so a run that has left
    ///     memory — e.g. after a restart — still surfaces as a tab. The result is capped at
    ///     <see cref="MaxPersistedEntriesPerConversation"/> rows. No-op when persistence is disabled or the
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
                // The viewer-scoped flags are dropped on the way in: they answer "for the reader of this
                // poll", and the file is read by every later reader.
                merged[(tab.Kind, tab.AgentId)] = tab with
                {
                    IsCurrent = false,
                    IsReadable = false,
                };
            }

            try
            {
                var path = PathFor(threadId);
                var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllText(temp, JsonSerializer.Serialize(Bound(merged, tabs), IndexJson));
                    File.Move(temp, path, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        File.Delete(temp);
                    }
                }
            }
            catch (IOException)
            {
                // Best-effort durability: a transient write failure just means this poll's snapshot isn't
                // persisted; the next poll re-attempts. Never fail the read the caller is servicing.
            }
        }
    }

    /// <summary>
    ///     Applies the per-conversation ceiling to a merged index. Rows the live snapshot still reports are
    ///     kept first — evicting one would hide a run that is actually happening — and the remaining places
    ///     go to the most recently active retained rows, so what falls off the end is the oldest history.
    /// </summary>
    private IReadOnlyCollection<SubAgentSummary> Bound(
        Dictionary<(string Kind, string AgentId), SubAgentSummary> merged,
        IReadOnlyList<SubAgentSummary> live
    )
    {
        if (merged.Count <= MaxPersistedEntriesPerConversation)
        {
            return merged.Values;
        }

        var liveKeys = live.Select(static tab => (tab.Kind, tab.AgentId)).ToHashSet();
        return
        [
            .. merged
                .OrderByDescending(entry => liveKeys.Contains(entry.Key))
                .ThenByDescending(entry => entry.Value.LastActivityUtc ?? DateTimeOffset.MinValue)
                .Take(MaxPersistedEntriesPerConversation)
                .Select(static entry => entry.Value),
        ];
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

        return
        [
            .. (JsonSerializer.Deserialize<List<SubAgentSummary>>(File.ReadAllText(path), IndexJson) ?? []).Select(
                static tab => tab.AsRetained()
            ),
        ];
    }

    private string PathFor(string threadId)
    {
        var safe = string.Concat(threadId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_indexDirectory!, safe + ".json");
    }
}
