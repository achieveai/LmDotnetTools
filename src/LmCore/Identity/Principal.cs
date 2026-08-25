namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>What kind of party a <see cref="PrincipalRef"/> names.</summary>
public enum PrincipalKind
{
    /// <summary>A calling service authenticated by its app credential.</summary>
    App = 0,

    /// <summary>A human authenticated by an identity provider.</summary>
    EndUser = 1,

    /// <summary>An autonomous agent run. Always has an OnBehalfOf.</summary>
    Agent = 2,

    /// <summary>A platform-internal service with no external caller.</summary>
    Service = 3,
}

/// <summary>Which front door authenticated a request. Audit and diagnostics only.</summary>
public enum PrincipalSource
{
    /// <summary>Interactive Entra sign-in from the SPA.</summary>
    Interactive = 0,

    /// <summary>App credential plus host-asserted on-behalf-of JWT.</summary>
    HostAsserted = 1,

    /// <summary>Host-minted embed token (mode A).</summary>
    Embed = 2,

    /// <summary>App credential alone, no human asserted.</summary>
    AppOnly = 3,

    /// <summary>Process-internal, e.g. a background daemon acting as itself.</summary>
    Internal = 4,
}

/// <summary>One named party. Immutable, comparable, safe to log in full.</summary>
/// <param name="Kind">What kind of party this is.</param>
/// <param name="Id">
/// The party's identifier. For <see cref="PrincipalKind.EndUser"/> this is the namespaced
/// <c>{tid}:{oid}</c> pair described in section 3.3 of the P1 spec.
/// </param>
public readonly record struct PrincipalRef(PrincipalKind Kind, string Id);

/// <summary>
/// The authenticated identity of one request or one agent run. Constructed once at an
/// authentication boundary and never mutated.
/// </summary>
/// <remarks>
/// Lives in <c>LmCore</c> deliberately: <c>LmMultiTurn</c> must filter by owner but cannot
/// reference <c>LmAgentInfra</c>, and <c>LmCore</c> has no project references and multi-targets
/// <c>net8.0;net9.0</c>. Nothing here may depend on ASP.NET Core types.
/// </remarks>
public sealed record Principal
{
    /// <summary>
    /// Organisation this request operates within. This is our internal tenant id (<c>tnt_*</c>),
    /// not the Entra <c>tid</c> — the two are mapped by <see cref="ITenantStore"/>. Never null
    /// once authenticated.
    /// </summary>
    public required string TenantId { get; init; }

    /// <summary>The party that actually made the call.</summary>
    public required PrincipalRef Actor { get; init; }

    /// <summary>
    /// The party the actor is acting for, when the actor is not acting for itself. Null for a
    /// human signing in directly.
    /// </summary>
    public PrincipalRef? OnBehalfOf { get; init; }

    /// <summary>
    /// Prior actors, outermost-first, from a nested RFC 8693 <c>act</c> chain. Audit only —
    /// never consulted for an access decision.
    /// </summary>
    public IReadOnlyList<PrincipalRef> DelegationChain { get; init; } = [];

    /// <summary>App id from the app credential, when one authenticated the call.</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Granted scopes, already intersected with the on-behalf-of party's (spec 3.2). Populated
    /// pre-intersected so no downstream caller can widen permissions by forgetting to intersect.
    /// Ordinal comparison.
    /// </summary>
    public IReadOnlySet<string> Scopes { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Tenant-level roles, e.g. <c>member</c>, <c>admin</c>.</summary>
    public IReadOnlySet<string> Roles { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Which front door authenticated this request. Audit and diagnostics only.</summary>
    public required PrincipalSource Source { get; init; }

    /// <summary>
    /// The user this activity is attributed to: <see cref="OnBehalfOf"/> when it names an
    /// <see cref="PrincipalKind.EndUser"/>, else <see cref="Actor"/> when it is an
    /// <see cref="PrincipalKind.EndUser"/>, else null. This is the value written to owner columns
    /// and usage records.
    /// </summary>
    public string? EffectiveUserId =>
        OnBehalfOf is { Kind: PrincipalKind.EndUser } obo ? obo.Id
        : Actor is { Kind: PrincipalKind.EndUser } a ? a.Id
        : null;
}
