using System.Collections.Immutable;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Remembers each conversation's dedicated sandboxes (<see cref="OwnedSandboxRecord"/>) in its own metadata, under
/// <see cref="PropertyKey"/>, so they outlive the host: a restarted host reuses the current one, a conversation delete
/// reaches every one, and a workspace restore can name the conversations whose sandboxes still hold the workspace.
/// </summary>
/// <remarks>
/// Every write goes through <see cref="IConversationStore.UpdateMetadataAsync"/> and replaces only this key, so it
/// never loses another writer's properties. The list is stored as one JSON string, which every store round-trips
/// unchanged. A fork does not carry it: a fork gets sandboxes of its own.
/// </remarks>
public sealed class ConversationOwnedSandboxes(IConversationStore store) : IOwnedSandboxStore
{
    /// <summary>Property key in a thread's <c>ThreadMetadata.Properties</c> holding its sandboxes as a JSON array.</summary>
    public const string PropertyKey = "sample.ownedSandboxes";

    private static readonly JsonSerializerOptions S_json = new(JsonSerializerDefaults.Web);

    private static readonly ConversationListOptions S_listOptions = new() { ExcludeDeleted = true };

    /// <summary>Abandons a write to a conversation that no longer exists.</summary>
    private sealed class ThreadGoneException : Exception;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OwnedSandboxRecord>> LoadAsync(string threadId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        return Read(await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task UpdateAsync(
        string threadId,
        Func<IReadOnlyList<OwnedSandboxRecord>, IReadOnlyList<OwnedSandboxRecord>> update,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            await store
                .UpdateMetadataAsync(
                    threadId,
                    // The store's update must return metadata, so a conversation deleted meanwhile is left alone by
                    // abandoning the write: a sandbox record must never bring it back.
                    metadata =>
                        metadata is null
                            ? throw new ThreadGoneException()
                            : metadata with
                            {
                                Properties = Write(
                                    metadata.Properties ?? ImmutableDictionary<string, object>.Empty,
                                    update(Read(metadata))
                                ),
                            },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (ThreadGoneException) { }
    }

    /// <summary>
    /// Every sandbox a conversation still records on <paramref name="gatewayWorkspace"/> under <paramref name="appId"/>,
    /// current and retired, with the conversation that owns it. Deleted conversations kept only for their forks are
    /// left out: their sandboxes went with them.
    /// </summary>
    /// <remarks>
    /// One list of every conversation, read at once: pages over a list that changes between reads can skip a
    /// conversation. A sandbox two conversations record is listed for each, so neither owner is hidden.
    /// </remarks>
    public async Task<IReadOnlyList<(string ThreadId, OwnedSandboxRecord Record)>> ListOnWorkspaceAsync(
        string gatewayWorkspace,
        string appId,
        CancellationToken ct = default
    )
    {
        var conversations = await store.ListThreadsAsync(int.MaxValue, 0, S_listOptions, ct).ConfigureAwait(false);
        var seen = new HashSet<(string ThreadId, string SessionId)>();
        return
        [
            .. conversations.SelectMany(metadata =>
                Read(metadata)
                    .Where(record =>
                        string.Equals(record.GatewayWorkspace, gatewayWorkspace, StringComparison.Ordinal)
                        && string.Equals(record.AppId, appId, StringComparison.Ordinal)
                        && seen.Add((metadata.ThreadId, record.SessionId))
                    )
                    .Select(record => (metadata.ThreadId, record))
            ),
        ];
    }

    private static IReadOnlyList<OwnedSandboxRecord> Read(ThreadMetadata? metadata)
    {
        if (metadata?.Properties is not { } properties || !properties.TryGetValue(PropertyKey, out var raw))
        {
            return [];
        }

        var json =
            ThreadPropertyValue.AsStringPreservingEmpty(raw)
            ?? throw new InvalidOperationException(
                $"Stored sandboxes of conversation '{metadata.ThreadId}' are not a JSON string."
            );
        return JsonSerializer.Deserialize<OwnedSandboxRecord[]>(json, S_json)
            ?? throw new InvalidOperationException(
                $"Stored sandboxes of conversation '{metadata.ThreadId}' are not a JSON array."
            );
    }

    private static ImmutableDictionary<string, object> Write(
        ImmutableDictionary<string, object> properties,
        IReadOnlyList<OwnedSandboxRecord> records
    ) =>
        records.Count == 0
            ? properties.Remove(PropertyKey)
            : properties.SetItem(PropertyKey, JsonSerializer.Serialize(records, S_json));
}
