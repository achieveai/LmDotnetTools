using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// One access decision about one conversation, plus whether the refusal must hide the
/// conversation's existence.
/// </summary>
/// <param name="Allowed">Whether the action is permitted.</param>
/// <param name="Reason">The policy's stable reason code (spec 7.4.1). Contract.</param>
/// <param name="HidesExistence">
/// True when the refusal must be answered as <c>404</c> rather than <c>403</c>. A <c>403</c> is an
/// admission that the id names something; for a caller outside the tenant that admission is itself
/// the leak, because thread ids are enumerable across a deployment.
/// </param>
public sealed record ConversationAccessResult(bool Allowed, string Reason, bool HidesExistence);

/// <summary>
/// The one place a conversation route turns a <see cref="ThreadMetadata"/> row plus the current
/// request's <see cref="Principal"/> into an allow or a refusal.
/// </summary>
/// <remarks>
/// <para>
/// Injected as a SINGLE dependency rather than as its four collaborators so that
/// <c>ConversationsController</c> gains one constructor parameter instead of four - and, more
/// importantly, so no route can assemble its own variation of the decision. Every conversation
/// REST route - <c>ConversationsController</c>'s and <c>FileBrowserController</c>'s alike -
/// resolves through <see cref="AuthorizeAsync"/> or <see cref="CreateListScopeAsync"/>.
/// </para>
/// <para>
/// The WebSocket transports <c>/ws</c> and <c>/ws/subagent</c> reach the same decision through
/// <see cref="WebSocketConversationGate"/> (#419), which calls <see cref="AuthorizeAsync"/> before the
/// handshake is accepted rather than reimplementing anything. They used to be AUTHENTICATED and
/// nothing more - inside <c>IdentityMiddleware</c>'s boundary, with the pooled agent owned by the
/// connecting user (#342, #399), but with no per-conversation check at all - so "the single seam"
/// meant "the single seam for the REST routes". It no longer does.
/// </para>
/// <para>
/// Note what this does NOT do: it never decides on its own contents. Every allow and every deny
/// below <see cref="IsEnforced"/> comes from <see cref="IResourceAccessPolicy"/>. What lives here
/// is the mapping from a store row to a <see cref="ResourceDescriptor"/>, and from a reason code to
/// an HTTP status - both of which are transport concerns the policy is deliberately ignorant of.
/// </para>
/// </remarks>
public sealed class ConversationAuthorizer
{
    /// <summary>
    /// Reason codes whose refusal must be a <c>404</c>. Each one means "this caller has no
    /// relationship at all with the resource", which is indistinguishable from the resource not
    /// existing - and must stay indistinguishable, or the API becomes an existence oracle for
    /// another tenant's conversation ids.
    /// </summary>
    private static readonly HashSet<string> ExistenceHidingReasons = new(StringComparer.Ordinal)
    {
        "cross_tenant",
        "no_relationship",
        "app_only_no_owner",
        NotFoundReason,
    };

    /// <summary>Refusal for a conversation with no row at all, or an unstamped legacy row.</summary>
    public const string NotFoundReason = "conversation_not_found";

    /// <summary>Refusal when enforcement is on and the request carries no principal.</summary>
    public const string UnauthenticatedReason = "authentication_required";

    /// <summary>
    /// Tenant id used by a listing scope built for a request that carries no principal. A tenant id
    /// is minted as <c>tnt_*</c> and a seeded one is a plain identifier, so a value carrying a colon
    /// and a space cannot collide with a stored one - and every row therefore fails the scope's very
    /// first comparison.
    /// </summary>
    private const string UnsatisfiableTenantId = "no principal: matches nothing";

    private readonly IPrincipalAccessor _principalAccessor;
    private readonly IResourceAccessPolicy _policy;
    private readonly IResourceGrantStore _grants;
    private readonly IEnforcementGate _enforcement;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the authorizer.</summary>
    /// <param name="principalAccessor">Supplies the current request's principal.</param>
    /// <param name="policy">The decision point of spec 7.4.</param>
    /// <param name="grants">Grant registry, read to build the listing scope.</param>
    /// <param name="enforcement">Whether <c>Identity:Enforce</c> is on.</param>
    /// <param name="timeProvider">Clock used to exclude expired grants.</param>
    public ConversationAuthorizer(
        IPrincipalAccessor principalAccessor,
        IResourceAccessPolicy policy,
        IResourceGrantStore grants,
        IEnforcementGate enforcement,
        TimeProvider timeProvider
    )
    {
        ArgumentNullException.ThrowIfNull(principalAccessor);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _principalAccessor = principalAccessor;
        _policy = policy;
        _grants = grants;
        _enforcement = enforcement;
        _timeProvider = timeProvider;
    }

    /// <summary>Whether this deployment enforces authorization.</summary>
    public bool IsEnforced => _enforcement.IsEnforced;

    /// <summary>The current request's principal, or null outside a request.</summary>
    public Principal? Current => _principalAccessor.Current;

    /// <summary>The grant registry, for the sharing routes.</summary>
    public IResourceGrantStore Grants => _grants;

    /// <summary>The clock, so a caller computes grant expiry against the same one this does.</summary>
    public TimeProvider Clock => _timeProvider;

    /// <summary>Addresses one conversation as a policy resource.</summary>
    /// <param name="threadId">The conversation's id.</param>
    public static ResourceRef ConversationRef(string threadId) => new(ResourceTypes.Conversation, threadId);

    /// <summary>
    /// Decides one action on one conversation.
    /// </summary>
    /// <param name="threadId">The conversation being addressed.</param>
    /// <param name="metadata">
    /// The stored row, or null when there is none. A missing row and a row belonging to another
    /// tenant produce the SAME refusal, because they must be indistinguishable to the caller.
    /// </param>
    /// <param name="action">The action being attempted.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConversationAccessResult> AuthorizeAsync(
        string threadId,
        ThreadMetadata? metadata,
        AccessAction action,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        if (!_enforcement.IsEnforced)
        {
            // Step 0 of spec 7.4, taken here as well as inside the policy so that a route can skip
            // loading the row it would only need in order to be refused. The policy would answer
            // AllowDisabled for the same input; short-circuiting cannot change the outcome, only
            // the work done to reach it.
            return new ConversationAccessResult(true, AccessDecision.AllowDisabled.Reason, false);
        }

        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // IdentityMiddleware answers 401 before a route runs, so this is unreachable through
            // HTTP. It is here so that a future caller reaching the authorizer outside the request
            // pipeline fails closed rather than authorizing with no identity at all.
            return new ConversationAccessResult(false, UnauthenticatedReason, false);
        }

        await EqualizeGrantLookupAsync(principal, threadId, metadata, ct).ConfigureAwait(false);

        // An absent row and an unstamped row are the same refusal on purpose. An unstamped row is
        // one the startup repair has not reached (a rolled-back build wrote it, and the process has
        // not restarted since); it belongs to no tenant, so no tenant may read it.
        if (metadata is null || metadata.TenantId is null)
        {
            return new ConversationAccessResult(false, NotFoundReason, true);
        }

        var descriptor = new ResourceDescriptor
        {
            Ref = ConversationRef(threadId),
            TenantId = metadata.TenantId,
            OwnerUserId = metadata.OwnerUserId,
            OwnerAppId = metadata.OwnerAppId,
            Visibility = metadata.Visibility ?? Visibility.Private,
        };

        var decision = await _policy.EvaluateAsync(principal, descriptor, action, ct).ConfigureAwait(false);

        return new ConversationAccessResult(
            decision.Allowed,
            decision.Reason,
            !decision.Allowed && ExistenceHidingReasons.Contains(decision.Reason)
        );
    }

    /// <summary>
    /// Issues the grant lookup on the refusal paths that would otherwise skip it, so every refusal
    /// this method can produce costs the same shape of work (#389).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three refusals leave here as the same existence-hiding <c>404</c>: the id names no row, the
    /// row belongs to another tenant, and the row is in the caller's own tenant but grants them
    /// nothing. Only the third reached <see cref="ResourceAccessPolicy"/>'s grant lookup, so the
    /// three answers were byte-identical and one round trip apart - and an authenticated caller
    /// could read that difference as "this id exists inside my tenant".
    /// </para>
    /// <para>
    /// The narrow-looking fix - a lookup only on the absent-row path - is the WRONG one, and worth
    /// naming because it is the obvious one. It equalises the two same-tenant answers by making the
    /// cross-tenant answer the odd one out, turning an intra-tenant existence oracle into a
    /// cross-tenant existence oracle. That is a strictly worse trade: the caller is already a
    /// trusted member of their own tenant, and thread ids are enumerable across the deployment.
    /// Every path that skips the policy's lookup issues one here instead.
    /// </para>
    /// <para>
    /// It does NOT run for an app-only principal, and that is equalisation too, not an exemption: a
    /// principal with no end user never consults grants on ANY path (spec 7.4 step 3), so those
    /// paths already cost the same as each other, and adding a lookup to one of them would be the
    /// same mistake in the other direction.
    /// </para>
    /// <para>
    /// The result is discarded, and it must be: this call confers nothing and decides nothing. The
    /// decision is <see cref="IResourceAccessPolicy"/>'s alone, which is why this lives beside the
    /// refusal rather than inside it. It also writes no audit entry and logs nothing - the refusal
    /// paths this pads are the silent ones by design, and a log line here would reintroduce the
    /// same oracle in a place an operator can read.
    /// </para>
    /// <para>
    /// A known consequence, accepted on purpose: a refusal that previously did no I/O can now fail
    /// if the grant registry is unavailable, turning a <c>404</c> into a <c>500</c>. It is NOT
    /// wrapped in a catch. A swallow here would restore the very asymmetry this removes - the
    /// same-tenant path does not swallow, so a registry outage would once again make the two
    /// answers distinguishable, and this time by a difference an attacker can provoke at will. Only
    /// requests that were going to be refused anyway are affected; no request that would have
    /// succeeded now fails.
    /// </para>
    /// </remarks>
    private async Task EqualizeGrantLookupAsync(
        Principal principal,
        string threadId,
        ThreadMetadata? metadata,
        CancellationToken ct
    )
    {
        if (principal.EffectiveUserId is not { } user)
        {
            return;
        }

        var policyWillLookUp =
            metadata?.TenantId is not null
            && string.Equals(metadata.TenantId, principal.TenantId, StringComparison.Ordinal);

        if (policyWillLookUp)
        {
            return;
        }

        // Against the CALLER's tenant, never the row's. It is the query the same-tenant path would
        // have issued, which is the whole point; issuing it against another tenant would both cost
        // differently and read another tenant's registry for no reason.
        _ = await _grants
            .FindGrantAsync(principal.TenantId, ConversationRef(threadId), user, _timeProvider.GetUtcNow(), ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the listing filter of spec 7.5, resolving the principal's grants once for the whole
    /// page rather than once per row. Returns null when enforcement is off, which means "no
    /// filter" - the pre-P1 listing, unchanged.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ConversationListScope?> CreateListScopeAsync(CancellationToken ct = default)
    {
        if (!_enforcement.IsEnforced)
        {
            return null;
        }

        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            // A scope no row can satisfy, rather than a null that would be read as "no filter".
            // Returning null here is the fail-OPEN version of this method and would list every
            // conversation in the deployment to an unauthenticated caller.
            return new ConversationListScope { TenantId = UnsatisfiableTenantId };
        }

        var user = principal.EffectiveUserId;

        // An app-only principal never consults grants (spec 7.4 step 3), so the set stays empty
        // rather than being resolved against a null subject.
        IReadOnlyList<string> granted = user is null
            ? []
            : await _grants
                .ListGrantedResourceIdsAsync(
                    principal.TenantId,
                    user,
                    ResourceTypes.Conversation,
                    _timeProvider.GetUtcNow(),
                    ct
                )
                .ConfigureAwait(false);

        return new ConversationListScope
        {
            TenantId = principal.TenantId,
            UserId = user,
            AppId = principal.AppId,
            IsTenantAdmin = principal.Roles.Contains(ResourceAccessPolicy.AdminRole),
            GrantedThreadIds = new HashSet<string>(granted, StringComparer.Ordinal),
        };
    }

    /// <summary>
    /// Whether the current principal may SHARE one already-loaded conversation row, computed for a
    /// LISTING without writing an attempt-grade audit record and without a per-row grant lookup
    /// (#487). The grant is taken from the batch the listing scope already resolved, not re-queried
    /// per row.
    /// </summary>
    /// <remarks>
    /// The share capability is grant-INDEPENDENT under the rights table of spec 7.4.1: only an owner
    /// of an unpublished conversation, or an app owner, may share, and every grantee is refused
    /// (<c>grantee_may_not_reshare</c>) - so the grant's PRESENCE, never its role, is the most that
    /// could matter, and even that never turns a deny into an allow. Presence is passed as
    /// <see cref="GrantRole.Viewer"/> only to satisfy the seam's contract; the answer is identical
    /// with any role or with none, which is exactly why the per-row store lookup can be dropped.
    /// </remarks>
    /// <param name="metadata">The stored row, already materialized by the listing.</param>
    /// <param name="grantedThreadIds">
    /// Thread ids this principal holds an unexpired grant on, resolved once for the page (spec 7.5),
    /// or null when enforcement is off.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> MayShareForListingAsync(
        ThreadMetadata metadata,
        IReadOnlySet<string>? grantedThreadIds,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Step 0: enforcement off allows every action, so the share control stays visible through
        // the pre-enforcement window docs/deployment/AUTH_ENFORCE.md exists to make survivable. Read
        // BEFORE the principal so an unauthenticated pre-rollout request is answered the same way.
        if (!_enforcement.IsEnforced)
        {
            return true;
        }

        if (_principalAccessor.Current is not { } principal)
        {
            return false;
        }

        // An unstamped or untenanted row belongs to no tenant, so no principal may share it - the
        // same refusal AuthorizeAsync gives it, minus the 404 mapping a listing has no use for.
        if (metadata.TenantId is null)
        {
            return false;
        }

        var descriptor = new ResourceDescriptor
        {
            Ref = ConversationRef(metadata.ThreadId),
            TenantId = metadata.TenantId,
            OwnerUserId = metadata.OwnerUserId,
            OwnerAppId = metadata.OwnerAppId,
            Visibility = metadata.Visibility ?? Visibility.Private,
        };

        var suppliedGrant = grantedThreadIds?.Contains(metadata.ThreadId) == true ? GrantRole.Viewer : (GrantRole?)null;

        var decision = await _policy
            .EvaluateCapabilityAsync(principal, descriptor, AccessAction.Share, suppliedGrant, ct)
            .ConfigureAwait(false);

        return decision.Allowed;
    }

    /// <summary>
    /// Stamps ownership onto a newly created conversation, so the row is claimed at creation rather
    /// than by a later repair.
    /// </summary>
    /// <remarks>
    /// Runs whether or not enforcement is on. That ordering is the whole point of the rollout in
    /// <c>docs/deployment/AUTH_ENFORCE.md</c>: conversations created during the pre-enforcement
    /// window must already carry an owner, or flipping the flag would make every one of them
    /// invisible to the person who created it. While enforcement is off the development principal
    /// supplies <c>Identity:LegacyTenantId</c> - the same id the quarantine stamp uses - so those
    /// rows are picked up by <c>adopt-legacy</c> exactly like genuinely legacy ones.
    /// </remarks>
    /// <param name="metadata">The row about to be written.</param>
    public ThreadMetadata StampOwnership(ThreadMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var principal = _principalAccessor.Current;
        if (principal is null)
        {
            return metadata;
        }

        return metadata with
        {
            // Existing values win. A re-provision of an id that already exists must not be able to
            // move a conversation between tenants or between users; that is the durable form of
            // the pool's caller freeze.
            TenantId = metadata.TenantId ?? principal.TenantId,
            OwnerUserId = metadata.OwnerUserId ?? principal.EffectiveUserId,
            OwnerAppId = metadata.OwnerAppId ?? principal.AppId,
            Visibility = metadata.Visibility ?? Visibility.Private,
        };
    }
}
