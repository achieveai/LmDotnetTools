using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// The four facts the listing filter of P1 spec 7.5 binds before the query runs, plus the grant
/// branch resolved for this principal.
/// </summary>
/// <remarks>
/// <para>
/// Listing is a FILTER, not a loop: the predicate is pushed into the store so a page is trimmed by
/// the database, not after it. In-memory filtering after a <c>LIMIT</c> silently returns short
/// pages.
/// </para>
/// <para>
/// The predicate must mirror EVERY allow branch of 7.4, not just the owner branch. A query that
/// omits the admin branch produces the worst possible outcome: an empty list for a tenant admin
/// while the point read on the same rows returns 200.
/// </para>
/// <para>
/// DEVIATION from 7.5's SQL, explained in the PR body: the grant branch arrives as a resolved id
/// set rather than as an <c>EXISTS</c> sub-query over <c>resource_grants</c>. The grant registry is
/// not guaranteed to live in the same database file as <c>thread_metadata</c> - and for the
/// sample's FILE conversation store there is no SQL at all - so a sub-query is not expressible in
/// every implementation. The admitted row set is identical.
/// </para>
/// </remarks>
public sealed record ConversationListScope
{
    /// <summary>The principal's tenant. Every query filters on this first.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// <c>Principal.EffectiveUserId</c>. Null for an app-only caller, which never matches an owner
    /// and never reaches the grant branch.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>The calling app id. Null for an interactive caller.</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Whether the principal holds the tenant's <c>admin</c> role. Computed once, not per row.
    /// </summary>
    public bool IsTenantAdmin { get; init; }

    /// <summary>
    /// Thread ids on which <see cref="UserId"/> holds an unexpired grant. Empty for an app-only
    /// caller: an app-only principal never consults grants (7.4 step 3).
    /// </summary>
    public IReadOnlySet<string> GrantedThreadIds { get; init; } =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// The whole of one tenant, with no principal narrowing inside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For BACKGROUND scans that have no principal and cannot acquire one - the persisted sub-agent
    /// scans run from an agent turn as well as from HTTP, and the request whose principal they
    /// would borrow is long gone by then. Their answer is also cached per root, so borrowing the
    /// first caller's identity would publish that caller's view to everyone who asked afterwards.
    /// </para>
    /// <para>
    /// It sets <see cref="IsTenantAdmin"/>, which reads alarmingly and is why it exists as a named
    /// factory rather than as an object initializer at each call site: the flag is the predicate's
    /// spelling of "no narrowing WITHIN the tenant", and the tenant filter still runs first and
    /// still runs unconditionally. It confers nothing across a tenant boundary. Do NOT reach for
    /// this to answer a request on a caller's behalf - a caller has a principal, and
    /// <c>ConversationAuthorizer.CreateListScopeAsync</c> is how that becomes a scope.
    /// </para>
    /// </remarks>
    /// <param name="tenantId">The tenant to scan.</param>
    public static ConversationListScope ForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        return new ConversationListScope { TenantId = tenantId, IsTenantAdmin = true };
    }

    /// <summary>
    /// Whether the given row is admitted. The single in-memory spelling of the SQL predicate, so
    /// the file and in-memory stores cannot drift from the SQL one.
    /// </summary>
    /// <param name="metadata">The candidate row.</param>
    public bool Admits(ThreadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!string.Equals(metadata.TenantId, TenantId, StringComparison.Ordinal))
        {
            return false;
        }

        if (IsTenantAdmin)
        {
            return true;
        }

        if (UserId is not null)
        {
            // The tenant-published branch of 7.4, which the point-read policy has and this
            // predicate did not. Its absence was the exact failure the remarks above describe: a
            // published conversation answered 200 on a direct read while being silently missing
            // from the list. Inside the UserId guard, because an app-only principal never becomes
            // a tenant member - the policy says so explicitly, and placing it above this guard
            // would hand every published conversation to every service credential in the tenant.
            if (metadata.Visibility == Visibility.TenantPublished)
            {
                return true;
            }

            // The non-null guard on the OWNER is the C# half of spec 7.1 principle 4. SQL gets this
            // right on its own because NULL = 'x' is NULL; C# `==` on two nulls is true, which
            // would hand every unclaimed row to every caller with no user id.
            return (metadata.OwnerUserId is not null
                    && string.Equals(metadata.OwnerUserId, UserId, StringComparison.Ordinal))
                || GrantedThreadIds.Contains(metadata.ThreadId);
        }

        return AppId is not null
            && metadata.OwnerAppId is not null
            && string.Equals(metadata.OwnerAppId, AppId, StringComparison.Ordinal);
    }
}
