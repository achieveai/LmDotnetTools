using System.Text.Json;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// Persists and reads one root conversation's <see cref="AgentIdentityBindingSet"/> inside
/// <see cref="ThreadMetadata.Properties"/> — no schema migration, uniform across every
/// <see cref="IConversationStore"/> backend, exactly as the todo board's projection does.
/// </summary>
/// <remarks>
/// <para>
/// The set is stored as a JSON <b>string</b> so it round-trips identically whether the backing store
/// keeps native CLR objects (in-memory) or re-hydrates property-bag values as
/// <see cref="JsonElement"/> (file / SQLite).
/// </para>
/// <para>
/// Reads are tolerant by construction: a corrupt or newer-schema blob reads as <b>absent</b> and never
/// throws. This document exists so a restart can say honestly what did not survive; a bad blob must not
/// turn conversation startup into a failure.
/// </para>
/// </remarks>
public static class ConversationAgentBindingProjection
{
    /// <summary>The metadata property-bag key under which the binding JSON is stored.</summary>
    public const string PropertyKey = "collab.identity";

    /// <summary>
    /// Atomically persists <paramref name="binding"/> under the row of the collaboration it describes.
    /// </summary>
    /// <remarks>
    /// <b>Never creates a conversation.</b> A binding is an attribute of a conversation that already
    /// exists; a row minted here would carry no tenant or owner, and an unstamped row is one nobody can
    /// read. If no metadata row is present this is a no-op.
    /// </remarks>
    public static async Task SaveAsync(
        IConversationStore store,
        AgentIdentityBindingSet binding,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(binding);

        if (await store.LoadMetadataAsync(binding.CollaborationId, ct) is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(binding);

        await store.UpdateMetadataAsync(
            binding.CollaborationId,
            existing =>
            {
                // Re-checked INSIDE the store's write serialization: the row can be deleted between
                // the probe above and this callback, and every IConversationStore.UpdateMetadataAsync
                // persists whatever the callback returns, so returning the null is not an option.
                if (existing is null)
                {
                    throw new AgentIdentityBindingDeclinedException(
                        $"Conversation '{binding.CollaborationId}' no longer exists; refusing to recreate "
                            + "its metadata row to persist a collaboration identity binding."
                    );
                }

                // Forward-compatibility: refuse to overwrite a document a newer build wrote, so an
                // older build during a rollback or a mixed-version deployment preserves it.
                if (
                    ThreadMetadataProjection.PersistedSchemaVersion(
                        existing,
                        PropertyKey,
                        CollaborationNodeRecord.SchemaVersionPropertyName,
                        whenUnversioned: AgentIdentityBindingSet.CurrentSchemaVersion
                    ) > AgentIdentityBindingSet.CurrentSchemaVersion
                )
                {
                    return existing;
                }

                // Monotonic in capture time. Equal instants are ACCEPTED, not rejected: at coarse clock
                // resolution successive captures routinely share a tick, and treating those as stale
                // would silently drop every write landing inside one tick of the previous one.
                if (FromMetadata(existing) is { } persisted && persisted.CapturedAtUtc > binding.CapturedAtUtc)
                {
                    return existing;
                }

                return ThreadMetadataProjection.WithProjection(existing, PropertyKey, json);
            },
            ct
        );
    }

    /// <summary>
    /// Loads the binding persisted for <paramref name="collaborationId"/>, or null when there is none
    /// this build can use.
    /// </summary>
    public static async Task<AgentIdentityBindingSet?> LoadAsync(
        IConversationStore store,
        string collaborationId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        return FromMetadata(await store.LoadMetadataAsync(collaborationId, ct));
    }

    /// <summary>
    /// Extracts the binding from already-loaded metadata. Store-agnostic: accepts the value whether it
    /// is a native JSON string (in-memory) or a re-hydrated <see cref="JsonElement"/> (file / SQLite).
    /// </summary>
    public static AgentIdentityBindingSet? FromMetadata(ThreadMetadata? metadata)
    {
        var json = ThreadMetadataProjection.RawJson(metadata, PropertyKey);
        if (json is null || metadata is null)
        {
            return null;
        }

        try
        {
            var binding = JsonSerializer.Deserialize<AgentIdentityBindingSet>(json);
            if (binding is not { SchemaVersion: <= AgentIdentityBindingSet.CurrentSchemaVersion })
            {
                return null;
            }

            // The scope half of every (scope, agent id) lookup, enforced at the seam where the two are
            // first put together. Since #705 an agent id is an ordinal minted per ROOT conversation, so
            // every conversation has an `agent-1`: a set applied to a row it does not describe would
            // name real agents belonging to a different hierarchy, and every id and name in it would
            // look plausible. Nothing wires a set onto a foreign row today; this is what keeps it that
            // way, most obviously for a nested root (LmWorkflow builds a bundle whose CollaborationId
            // is the ROOT conversation while the loop's own thread is `workflow-*`).
            return string.Equals(binding.CollaborationId, metadata.ThreadId, StringComparison.Ordinal) ? binding : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Thrown by <see cref="ConversationAgentBindingProjection.SaveAsync"/> when the conversation vanished
/// mid-write, so the writer can tell that deliberate, final decline apart from a store fault.
/// </summary>
/// <remarks>
/// A dedicated type rather than a bare <see cref="InvalidOperationException"/>: the store
/// infrastructure throws subtypes of that on its own — <see cref="ObjectDisposedException"/> from the
/// SQLite connection factory is one — and those are genuine faults that must stay pending and be
/// retried, not be swallowed under a false "conversation no longer exists" record.
/// </remarks>
public sealed class AgentIdentityBindingDeclinedException(string message) : InvalidOperationException(message);
