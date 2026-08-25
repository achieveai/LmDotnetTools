namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>Addresses a resource: what kind, and which one.</summary>
/// <param name="Type">Resource type, e.g. <c>conversation</c>, <c>workspace</c>, <c>mode</c>.</param>
/// <param name="Id">The resource's id, or <c>*</c> for a listing decision.</param>
public readonly record struct ResourceRef(string Type, string Id);

/// <summary>How widely a resource is exposed within its tenant.</summary>
public enum Visibility
{
    /// <summary>Visible only to its owner.</summary>
    Private = 0,

    /// <summary>Visible to its owner plus the parties named by an explicit grant.</summary>
    Shared = 1,

    /// <summary>Published by a tenant admin to every member of the tenant.</summary>
    TenantPublished = 2,
}

/// <summary>The actions a principal can be authorized to take on a resource.</summary>
public enum AccessAction
{
    /// <summary>See the resource and its contents.</summary>
    Read,

    /// <summary>Act with the resource, e.g. run a conversation in a workspace.</summary>
    Use,

    /// <summary>Modify the resource.</summary>
    Write,

    /// <summary>Remove the resource.</summary>
    Delete,

    /// <summary>Grant another named party access to the resource.</summary>
    Share,

    /// <summary>Expose the resource to the whole tenant. Admin-only (spec 7.4.1).</summary>
    Publish,
}

/// <summary>
/// The ownership facts the policy needs about one resource. Loaded by the caller from whichever
/// store owns the resource, so the policy performs no I/O of its own and is directly unit-testable.
/// </summary>
public sealed record ResourceDescriptor
{
    /// <summary>Which resource this describes.</summary>
    public required ResourceRef Ref { get; init; }

    /// <summary>Owning tenant. Ignored when <see cref="IsSystemDefined"/> is true.</summary>
    public required string TenantId { get; init; }

    /// <summary>Owning end user, <c>{tid}:{oid}</c>. Null for an app-owned or legacy resource.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>Owning app id. Null for a resource created through the interactive UI.</summary>
    public string? OwnerAppId { get; init; }

    /// <summary>Read-only built-in — a system mode or the seeded workspace.</summary>
    public bool IsSystemDefined { get; init; }

    /// <summary>
    /// Publication state. Gates which actions the owner retains — see the rights table in spec
    /// 7.4.1. Resource types that cannot be published are always <see cref="Visibility.Private"/>
    /// or <see cref="Visibility.Shared"/>.
    /// </summary>
    public required Visibility Visibility { get; init; }
}

/// <summary>The outcome of one access decision, plus the stable reason code it is audited under.</summary>
/// <param name="Allowed">Whether the action is permitted.</param>
/// <param name="Reason">
/// Stable reason code. Contract: it is what the audit record stores (spec 7.7) and what the slice
/// tests assert. Recorded for allows as well as denies — a deny-only trail cannot answer "was this
/// ever attempted successfully?".
/// </param>
public sealed record AccessDecision(bool Allowed, string Reason)
{
    /// <summary>Denies with the given stable reason code.</summary>
    public static AccessDecision Deny(string reason) => new(false, reason);

    /// <summary>Allows with the given stable reason code.</summary>
    public static AccessDecision Allow(string reason) => new(true, reason);

    /// <summary>The principal owns the resource.</summary>
    public static readonly AccessDecision AllowOwner = new(true, "owner");

    /// <summary>An unexpired grant confers the action.</summary>
    public static readonly AccessDecision AllowGrant = new(true, "grant");

    /// <summary>The principal is an admin of the resource's tenant.</summary>
    public static readonly AccessDecision AllowAdmin = new(true, "tenant_admin");

    /// <summary>The calling app owns the resource.</summary>
    public static readonly AccessDecision AllowAppOwner = new(true, "app_owner");

    /// <summary>The resource is a read-only built-in.</summary>
    public static readonly AccessDecision AllowSystem = new(true, "system_defined");

    /// <summary>
    /// <c>Identity:Enforce</c> is false, so authorization is off (spec 7.4 step 0). Not a
    /// permissive decision about the principal's contents — it is the pre-rollout path, and it
    /// short-circuits before any rule below it is exercised.
    /// </summary>
    public static readonly AccessDecision AllowDisabled = new(true, "enforcement_disabled");
}

/// <summary>
/// Decides whether one <see cref="Principal"/> may take one <see cref="AccessAction"/> on one
/// described resource.
/// </summary>
/// <remarks>
/// Named <c>IResourceAccessPolicy</c>, not <c>IAuthorizationService</c>, to avoid colliding with
/// the ASP.NET Core interface of that name. The implementation lands in slice 2 (#302) along with
/// the <c>resource_grants</c> table it consults; slice 1 ships the contract because the resource
/// slices are written against it.
/// </remarks>
public interface IResourceAccessPolicy
{
    /// <summary>Evaluates one access decision. Performs no I/O.</summary>
    /// <param name="principal">The authenticated caller.</param>
    /// <param name="resource">Ownership facts about the target, loaded by the caller.</param>
    /// <param name="action">The action being attempted.</param>
    /// <param name="ct">Cancellation token.</param>
    ValueTask<AccessDecision> EvaluateAsync(
        Principal principal,
        ResourceDescriptor resource,
        AccessAction action,
        CancellationToken ct = default);
}
