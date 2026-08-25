using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Stamps an agent-owned thread - a sub-agent's or a workflow controller's - with the ownership of
/// the conversation that launched it.
/// </summary>
/// <remarks>
/// <para>
/// Conversations created through the HTTP provisioning route are stamped from the caller's
/// principal at creation. Agent-owned threads are not: they are minted deep inside a background run
/// that outlives the request that started it, so there is no principal to read. Left alone they
/// persist with a null tenant, and a null tenant is treated as an absent row under
/// <c>Identity:Enforce</c> - the thread becomes unreadable to everyone, including its own owner.
/// </para>
/// <para>
/// Inheritance is the answer rather than a later repair pass, for the reason the provisioning route
/// gives: a repair that runs at the next boot leaves a window in which the run's own threads are
/// invisible, and there is no boot between spawning a sub-agent and reading its transcript.
/// </para>
/// <para>
/// <b>Visibility is not inherited.</b> Tenant and owner are identity - the child genuinely belongs
/// to whoever owns the parent. Visibility is a publication decision someone made about the parent
/// document, and re-applying it to a transcript nobody chose to publish would widen access by
/// inference. The child starts <see cref="Visibility.Private"/> and can be published on its own
/// merits.
/// </para>
/// </remarks>
public static class AgentThreadOwnership
{
    /// <summary>
    /// Copies the launching conversation's tenant and owner onto a newly minted agent thread.
    /// </summary>
    /// <remarks>
    /// A no-op when the parent carries no tenant, which is the ordinary state while enforcement is
    /// off. Writing a row full of nulls in that case would create metadata for a thread that has
    /// none, changing what the listing endpoints return on a deployment that asked for nothing.
    /// </remarks>
    /// <param name="store">The store both threads live in.</param>
    /// <param name="parentThreadId">The launching conversation's thread id.</param>
    /// <param name="childThreadId">The agent-owned thread being created.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task InheritAsync(
        IConversationStore? store,
        string? parentThreadId,
        string childThreadId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(childThreadId);

        if (store is null || string.IsNullOrWhiteSpace(parentThreadId))
        {
            return;
        }

        var parent = await store.LoadMetadataAsync(parentThreadId, ct).ConfigureAwait(false);
        if (parent?.TenantId is null)
        {
            return;
        }

        await store
            .UpdateMetadataAsync(
                childThreadId,
                existing => (existing
                    ?? new ThreadMetadata
                    {
                        ThreadId = childThreadId,
                        LastUpdated = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    }) with
                {
                    ThreadId = childThreadId,

                    // Existing values win, so a re-spawn onto the same thread id cannot move
                    // ownership - the same rule the provisioning route's stamp follows.
                    TenantId = existing?.TenantId ?? parent.TenantId,
                    OwnerUserId = existing?.OwnerUserId ?? parent.OwnerUserId,
                    OwnerAppId = existing?.OwnerAppId ?? parent.OwnerAppId,
                    Visibility = existing?.Visibility ?? Visibility.Private,
                },
                ct)
            .ConfigureAwait(false);
    }
}
