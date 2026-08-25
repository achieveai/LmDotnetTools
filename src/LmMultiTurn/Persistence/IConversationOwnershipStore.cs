namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The tenancy-maintenance half of a conversation store: the startup repair of P1 spec 8.5.4 and
/// the operator adoption path of 8.5.3.
/// </summary>
/// <remarks>
/// Deliberately separate from <see cref="IConversationStore"/>. Both members act on EVERY row in a
/// store, and a per-thread decorator - the sample's <c>NonOwningConversationStore</c>, which scopes
/// a sub-agent to one thread - has no honest implementation of "stamp every unstamped row". Keeping
/// them off the main interface is what stops a decorator from having to fake one.
/// </remarks>
public interface IConversationOwnershipStore
{
    /// <summary>
    /// Stamps every row whose tenant is unset with the quarantine tenant, and returns how many were
    /// stamped.
    /// </summary>
    /// <remarks>
    /// A STARTUP REPAIR, not a one-time migration (spec 8.5.4). A build rolled back to before this
    /// slice does not know about the column, so every conversation it creates is unstamped; rolling
    /// forward would not repair them, because the schema version is already past the step that
    /// would have. The invariant this buys is worth stating: no conversation has an unset tenant
    /// while the process is serving requests, whatever sequence of builds wrote it.
    /// </remarks>
    /// <param name="quarantineTenantId">The configured <c>Identity:LegacyTenantId</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<int> StampUnownedThreadsAsync(string quarantineTenantId, CancellationToken ct = default);

    /// <summary>
    /// The ids of conversations currently stamped with <paramref name="tenantId"/>, optionally
    /// restricted to <paramref name="threadIds"/>. This is what a <c>dryRun</c> adoption reports,
    /// and what an applied one then moves.
    /// </summary>
    /// <param name="tenantId">Tenant to select on, normally the quarantine tenant.</param>
    /// <param name="threadIds">Restrict to these ids, or null for every eligible row.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<string>> ListThreadIdsByTenantAsync(
        string tenantId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default);

    /// <summary>
    /// Moves conversations out of <paramref name="fromTenantId"/> into
    /// <paramref name="toTenantId"/>, optionally assigning an owner.
    /// </summary>
    /// <remarks>
    /// Selecting on the SOURCE tenant is what makes a repeated call idempotent rather than
    /// destructive: a conversation already adopted into a real tenant is no longer eligible, so it
    /// is never re-stamped.
    /// </remarks>
    /// <param name="fromTenantId">Only rows currently in this tenant are eligible.</param>
    /// <param name="toTenantId">Tenant the rows move into.</param>
    /// <param name="ownerUserId">Owner to assign, or null to leave the rows unowned.</param>
    /// <param name="threadIds">Restrict to these ids, or null for every eligible row.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows moved.</returns>
    Task<int> AdoptThreadsAsync(
        string fromTenantId,
        string toTenantId,
        string? ownerUserId,
        IReadOnlyCollection<string>? threadIds,
        CancellationToken ct = default);
}
