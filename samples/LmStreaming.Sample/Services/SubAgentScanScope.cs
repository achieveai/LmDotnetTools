using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Resolves the listing scope the two persisted sub-agent scans run under (#388a).
/// </summary>
/// <remarks>
///     <para>
///         Both scans used to call the UNSCOPED
///         <see cref="IConversationStore.ListThreadsAsync(int, int, CancellationToken)"/> overload, pulling
///         every tenant's rows into memory and then discarding all but one conversation's descendants.
///         Neither disclosed anything - the projection that follows narrows to the requested root either
///         way - but both paid for the whole deployment to answer a question about one thread, and both
///         were a pattern waiting to be copied into a call site that does NOT narrow its output.
///     </para>
///     <para>
///         <b>The scope comes from the ROOT ROW, not from a principal.</b> That is not a shortcut, it is
///         the only thing available: these scans run from the transcript writer and the in-agent
///         transcript tool as well as from HTTP, and <c>IPrincipalAccessor</c> reads
///         <c>HttpContext.Items</c> - an agent run outlives the request that started it, so on those
///         paths there is no principal to build a scope from and never will be. Scoping by whoever
///         happened to ask would also make the same conversation report a different sub-agent roster to
///         different callers, and that roster is CACHED per root; the first caller's answer would become
///         everyone's.
///     </para>
///     <para>
///         Scoping by the root's own tenant has neither problem, and it is very nearly complete:
///         <c>AgentThreadOwnership.InheritAsync</c> stamps a descendant from its parent at creation
///         (#385), so a descendant of a root in tenant T is normally itself in tenant T. The one gap is a
///         stamping-order race - inheritance reads the parent's stored row AS IT STANDS at the child's
///         creation, so a child minted before its parent's own stamp has landed keeps a null tenant while
///         the root ends up stamped. That child is a genuine descendant the scan must still find, so the
///         scope admits the root's tenant OR an untenanted row
///         (<see cref="ConversationListScope.ForTenantIncludingUntenanted"/>). It is not widened to other
///         real tenants: a row in a different tenant is one the parentage projection would discard anyway.
///     </para>
/// </remarks>
internal static class SubAgentScanScope
{
    /// <summary>
    ///     The scope to scan under for <paramref name="rootThreadId"/>, or <see langword="null"/> when
    ///     the root carries no tenant and the caller must fall back to the unscoped overload.
    /// </summary>
    /// <remarks>
    ///     The null answer is for legacy rows written before tenancy existed. Narrowing to "tenant is
    ///     null" is not an option: <see cref="ConversationListScope"/> requires a tenant id by
    ///     construction, and inventing a sentinel one would silently exclude the very rows the fallback
    ///     exists to keep visible. Spec 8.5.4's startup repair is what retires this branch - once every
    ///     row is stamped into a real or quarantine tenant, it stops being reachable.
    /// </remarks>
    /// <param name="store">The conversation store the scan will read.</param>
    /// <param name="rootThreadId">The conversation whose descendants are being scanned.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ConversationListScope?> ForRootAsync(
        IConversationStore store,
        string rootThreadId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootThreadId);

        var root = await store.LoadMetadataAsync(rootThreadId, ct).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(root?.TenantId)
            ? null
            : ConversationListScope.ForTenantIncludingUntenanted(root.TenantId);
    }
}
