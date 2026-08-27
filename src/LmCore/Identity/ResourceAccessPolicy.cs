namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>The resource type names spec 7.2 closes the vocabulary to.</summary>
public static class ResourceTypes
{
    /// <summary>A conversation thread. Never published.</summary>
    public const string Conversation = "conversation";

    /// <summary>A workspace. Never published (spec 7.2).</summary>
    public const string Workspace = "workspace";

    /// <summary>A chat mode. The only publishable resource type.</summary>
    public const string Mode = "mode";
}

/// <summary>
/// Whether this deployment enforces authorization. One process-wide flag (spec 4.1), read through
/// an abstraction so <see cref="ResourceAccessPolicy"/> can live in <c>LmCore</c>, which has no
/// project references and no configuration stack.
/// </summary>
public interface IEnforcementGate
{
    /// <summary>True when <c>Identity:Enforce</c> is on.</summary>
    bool IsEnforced { get; }
}

/// <summary>An <see cref="IEnforcementGate"/> whose answer is fixed at construction.</summary>
/// <param name="isEnforced">Whether enforcement is on.</param>
public sealed class StaticEnforcementGate(bool isEnforced) : IEnforcementGate
{
    /// <inheritdoc />
    public bool IsEnforced { get; } = isEnforced;
}

/// <summary>
/// Which relationship a principal has to a resource (spec 7.4 step 3). Declared in the order the
/// spec evaluates them: the first relationship that allows wins, and when none allows, the denial
/// of the first APPLICABLE one is what the caller sees.
/// </summary>
internal enum ResourceRelationship
{
    /// <summary>The principal's own resource.</summary>
    Owner = 0,

    /// <summary>An unexpired <c>resource_grants</c> row names the principal.</summary>
    Grantee = 1,

    /// <summary>The principal holds the tenant's <c>admin</c> role.</summary>
    TenantAdmin = 2,

    /// <summary>A signed-in member of the tenant, and the resource is published to it.</summary>
    TenantMember = 3,

    /// <summary>An app-only caller whose app id owns the resource.</summary>
    AppOwner = 4,
}

/// <summary>
/// The decision point of spec 7.4, implementing the normative rights table of 7.4.1.
/// </summary>
/// <remarks>
/// <para>
/// Every ATTEMPT decision is written to <see cref="IAuditSink"/>, allow and deny alike: a deny-only
/// trail cannot answer "was this ever attempted successfully?". A CAPABILITY probe
/// (<see cref="IResourceAccessPolicy.EvaluateCapabilityAsync"/>) is not audited - it shapes a UI
/// affordance rather than gating an operation, and one audit record per listed row would bury the
/// real refusals (#487).
/// </para>
/// <para>
/// DEVIATION from the spec's "performs no I/O of its own": the attempt seam
/// <see cref="IResourceAccessPolicy.EvaluateAsync"/> reads the grant branch of step 3 from the
/// store here, because its signature has no parameter through which a caller could hand it in. The
/// capability seam does take the grant as a parameter and performs no I/O at all. Everything else
/// the algorithm needs arrives in the <see cref="ResourceDescriptor"/>, so the policy remains
/// directly unit-testable against an in-memory grant store.
/// </para>
/// </remarks>
public sealed class ResourceAccessPolicy : IResourceAccessPolicy
{
    /// <summary>Role name that carries the tenant-admin rights of spec 7.3.</summary>
    public const string AdminRole = "admin";

    private static readonly IReadOnlyDictionary<string, AccessAction[]> SupportedActions =
        new Dictionary<string, AccessAction[]>(StringComparer.Ordinal)
        {
            [ResourceTypes.Conversation] =
            [
                AccessAction.Read,
                AccessAction.Write,
                AccessAction.Delete,
                AccessAction.Share,
            ],
            [ResourceTypes.Workspace] =
            [
                AccessAction.Read,
                AccessAction.Use,
                AccessAction.Write,
                AccessAction.Delete,
                AccessAction.Share,
            ],
            [ResourceTypes.Mode] =
            [
                AccessAction.Read,
                AccessAction.Use,
                AccessAction.Write,
                AccessAction.Delete,
                AccessAction.Share,
                AccessAction.Publish,
            ],
        };

    private readonly IResourceGrantStore _grants;
    private readonly IAuditSink _audit;
    private readonly IEnforcementGate _enforcement;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the policy.</summary>
    /// <param name="grants">Grant registry consulted by step 3's grantee branch.</param>
    /// <param name="audit">Receives one record per decision, allow or deny.</param>
    /// <param name="enforcement">Whether enforcement is on (step 0).</param>
    /// <param name="timeProvider">Clock used to exclude expired grants.</param>
    public ResourceAccessPolicy(
        IResourceGrantStore grants,
        IAuditSink audit,
        IEnforcementGate enforcement,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(enforcement);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _grants = grants;
        _audit = audit;
        _enforcement = enforcement;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Whether the given type/action pair exists in the table of spec 7.2. Public so a caller that
    /// builds a pair from input can refuse it before reaching the policy.
    /// </summary>
    /// <param name="resourceType">Resource type name.</param>
    /// <param name="action">The action being attempted.</param>
    public static bool IsSupported(string? resourceType, AccessAction action) =>
        resourceType is not null
        && SupportedActions.TryGetValue(resourceType, out var actions)
        && Array.IndexOf(actions, action) >= 0;

    /// <inheritdoc />
    public ValueTask<AccessDecision> EvaluateAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        CancellationToken ct = default) =>
        EvaluateInternalAsync(
            principal,
            resource,
            action,
            grantSupplied: false,
            suppliedGrant: null,
            writeAudit: true,
            ct);

    /// <inheritdoc />
    public ValueTask<AccessDecision> EvaluateCapabilityAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        GrantRole? suppliedGrant,
        CancellationToken ct = default) =>
        // A probe, not an attempt (#487): the grant arrives from the caller so no store round trip
        // is made, and the decision is NOT audited - a display-time capability check is not an
        // access event, and auditing it would bury real refusals under one deny per listed row.
        EvaluateInternalAsync(
            principal,
            resource,
            action,
            grantSupplied: true,
            suppliedGrant,
            writeAudit: false,
            ct);

    /// <summary>
    /// The decision of spec 7.4, shared by the attempt and capability seams. The two differ only in
    /// where the grantee grant comes from (<paramref name="grantSupplied"/>) and whether the outcome
    /// is audited (<paramref name="writeAudit"/>); the rights table they read is identical.
    /// </summary>
    private async ValueTask<AccessDecision> EvaluateInternalAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        bool grantSupplied,
        GrantRole? suppliedGrant,
        bool writeAudit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(resource);

        // Step -1. Before step 0, deliberately, and before any audit: whether a typo throws must not
        // depend on Identity:Enforce, or a bad pair would return AllowDisabled in the configuration
        // every pre-rollout test runs under - dead exactly where it is meant to fire.
        if (!IsSupported(resource.Ref.Type, action))
        {
            throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                $"Action is not defined for resource type '{resource.Ref.Type}' (spec 7.2).");
        }

        var decision = await ComputeAsync(
                principal,
                resource,
                action,
                grantSupplied,
                suppliedGrant,
                ct)
            .ConfigureAwait(false);

        return writeAudit ? Audit(principal, resource, action, decision) : decision;
    }

    /// <summary>
    /// Steps 0 through 4 of spec 7.4, producing the decision without auditing it. Kept separate from
    /// the audit so both seams reach the SAME decision and only the attempt seam records it.
    /// </summary>
    private async ValueTask<AccessDecision> ComputeAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        bool grantSupplied,
        GrantRole? suppliedGrant,
        CancellationToken ct)
    {
        // Step 0.
        if (!_enforcement.IsEnforced)
        {
            return AccessDecision.AllowDisabled;
        }

        // Step 1. Before the tenant check: a system-defined resource is readable by every member of
        // every tenant and writable by no one, tenant admins included.
        if (resource.IsSystemDefined)
        {
            return action is AccessAction.Read or AccessAction.Use
                ? AccessDecision.AllowSystem
                : AccessDecision.Deny("system_defined_immutable");
        }

        // Step 2. The outer boundary. Admins do not bypass it - a tenant admin is an admin of
        // exactly one tenant.
        if (!string.Equals(resource.TenantId, principal.TenantId, StringComparison.Ordinal))
        {
            return AccessDecision.Deny("cross_tenant");
        }

        var user = principal.EffectiveUserId;

        if (user is null)
        {
            // An app-only principal never matches a null owner, never consults grants, carries no
            // roles, and never becomes a tenant member. The non-null guard on OwnerAppId is the
            // null rule of 7.1 principle 4: C# == on two nulls is true, which would hand every
            // unowned resource to every service credential.
            var appOwns = resource.OwnerAppId is not null
                && string.Equals(resource.OwnerAppId, principal.AppId, StringComparison.Ordinal);

            return appOwns ? RightsForAppOwner(action) : AccessDecision.Deny("app_only_no_owner");
        }

        return await ResolveForUserAsync(
                principal,
                resource,
                action,
                user,
                grantSupplied,
                suppliedGrant,
                ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Steps 3 and 4 for a principal that names an end user. Relationships are collected in the
    /// spec's order, the first ALLOW wins, and when none allows the denial of the first applicable
    /// relationship is returned - the reason strings are contract, so which denial surfaces is not
    /// an implementation detail.
    /// </summary>
    private async Task<AccessDecision> ResolveForUserAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        string user,
        bool grantSupplied,
        GrantRole? suppliedGrant,
        CancellationToken ct)
    {
        var relationships = new List<(ResourceRelationship Relationship, GrantRole? Grant)>(4);

        // The non-null guard is the rule, not defensive style: a legacy row with no owner would
        // otherwise match every principal whose EffectiveUserId is also null.
        if (resource.OwnerUserId is not null
            && string.Equals(resource.OwnerUserId, user, StringComparison.Ordinal))
        {
            relationships.Add((ResourceRelationship.Owner, null));
        }

        // A capability probe supplies the grant (resolved once for a whole page); an attempt looks
        // it up here. Same value either way - the grantee branch of step 3 - but the probe makes no
        // store round trip.
        var grant = grantSupplied
            ? suppliedGrant
            : await _grants
                .FindGrantAsync(principal.TenantId, resource.Ref, user, _timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);

        if (grant is { } role)
        {
            relationships.Add((ResourceRelationship.Grantee, role));
        }

        if (principal.Roles.Contains(AdminRole))
        {
            relationships.Add((ResourceRelationship.TenantAdmin, null));
        }

        if (resource.Visibility == Visibility.TenantPublished)
        {
            relationships.Add((ResourceRelationship.TenantMember, null));
        }

        if (relationships.Count == 0)
        {
            return AccessDecision.Deny("no_relationship");
        }

        AccessDecision? firstDenial = null;

        foreach (var (relationship, grantRole) in relationships)
        {
            var rights = Rights(relationship, action, resource.Visibility, grantRole);
            if (rights.Allowed)
            {
                return rights;
            }

            firstDenial ??= rights;
        }

        return firstDenial!;
    }

    /// <summary>
    /// The normative rights table of spec 7.4.1, one cell per relationship, action and visibility.
    /// </summary>
    private static AccessDecision Rights(
        ResourceRelationship relationship,
        AccessAction action,
        Visibility visibility,
        GrantRole? grantRole) => relationship switch
        {
            ResourceRelationship.Owner => RightsForOwner(action, visibility),
            ResourceRelationship.Grantee => RightsForGrantee(action, visibility, grantRole),
            ResourceRelationship.TenantAdmin => RightsForTenantAdmin(action, visibility),
            ResourceRelationship.TenantMember => RightsForTenantMember(action),
            _ => RightsForAppOwner(action),
        };

    private static AccessDecision RightsForOwner(AccessAction action, Visibility visibility)
    {
        var published = visibility == Visibility.TenantPublished;

        return action switch
        {
            AccessAction.Read or AccessAction.Use => AccessDecision.AllowOwner,
            AccessAction.Write => published
                ? AccessDecision.Deny("owner_write_frozen_by_publication")
                : AccessDecision.AllowOwner,
            AccessAction.Delete => published
                ? AccessDecision.Deny("unpublish_before_delete")
                : AccessDecision.AllowOwner,
            AccessAction.Share => published
                ? AccessDecision.Deny("publication_supersedes_sharing")
                : AccessDecision.AllowOwner,
            _ => AccessDecision.Deny("publish_is_admin_only"),
        };
    }

    private static AccessDecision RightsForGrantee(
        AccessAction action,
        Visibility visibility,
        GrantRole? grantRole)
    {
        // The publication freeze is uniform across relationships. Stated per relationship it
        // drifts: an editor grant issued before publication would keep writing a published
        // resource, through a door the owner opened months earlier.
        var confersWrite = grantRole == GrantRole.Editor;

        return action switch
        {
            AccessAction.Read or AccessAction.Use => AccessDecision.AllowGrant,
            AccessAction.Write when !confersWrite =>
                AccessDecision.Deny("grant_does_not_confer_action"),
            AccessAction.Write => visibility == Visibility.TenantPublished
                ? AccessDecision.Deny("grant_write_frozen_by_publication")
                : AccessDecision.AllowGrant,
            AccessAction.Delete => AccessDecision.Deny("grant_confers_no_delete"),
            AccessAction.Share => AccessDecision.Deny("grantee_may_not_reshare"),
            _ => AccessDecision.Deny("publish_is_admin_only"),
        };
    }

    private static AccessDecision RightsForTenantAdmin(AccessAction action, Visibility visibility) =>
        action switch
        {
            AccessAction.Read or AccessAction.Use => AccessDecision.AllowAdmin,
            AccessAction.Write => visibility == Visibility.TenantPublished
                ? AccessDecision.AllowAdmin
                : AccessDecision.Deny("admin_no_write"),
            AccessAction.Delete => AccessDecision.Deny("admin_no_delete"),
            AccessAction.Share => AccessDecision.Deny("admin_may_not_reshare"),
            _ => AccessDecision.AllowAdmin,
        };

    private static AccessDecision RightsForTenantMember(AccessAction action) => action switch
    {
        AccessAction.Read or AccessAction.Use => AccessDecision.Allow("tenant_member"),
        AccessAction.Publish => AccessDecision.Deny("publish_is_admin_only"),
        _ => AccessDecision.Deny("tenant_member_read_only"),
    };

    private static AccessDecision RightsForAppOwner(AccessAction action) => action switch
    {
        AccessAction.Read or AccessAction.Use or AccessAction.Write or AccessAction.Delete =>
            AccessDecision.AllowAppOwner,
        AccessAction.Share => AccessDecision.Deny("app_cannot_share"),
        _ => AccessDecision.Deny("publish_is_admin_only"),
    };

    private AccessDecision Audit(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        AccessDecision decision)
    {
        _audit.Write(new AuthorizationAuditRecord
        {
            Actor = principal.Actor,
            OnBehalfOf = principal.OnBehalfOf,
            TenantId = principal.TenantId,
            AppId = principal.AppId,
            Source = principal.Source,
            Permission = action,
            Resource = resource.Ref,
            Outcome = decision.Allowed ? AuthorizationOutcome.Allow : AuthorizationOutcome.Deny,
            Reason = decision.Reason,
            EventClass = decision.Allowed ? AuditEventClass.Routine : AuditEventClass.Security,
        });

        return decision;
    }
}
