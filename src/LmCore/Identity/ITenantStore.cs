namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>Whether a tenant may currently sign users in.</summary>
public enum TenantStatus
{
    /// <summary>Normal operation.</summary>
    Active = 0,

    /// <summary>Provisioned but sign-in is refused, e.g. non-payment. Distinguishable in support.</summary>
    Suspended = 1,
}

/// <summary>The outcome of one tenant-provisioning attempt.</summary>
public enum TenantProvisionOutcome
{
    /// <summary>The tenant row and its first-admin row were created.</summary>
    Created = 0,

    /// <summary>A tenant already exists under that internal id. Nothing was written.</summary>
    TenantIdExists = 1,

    /// <summary>A different tenant already claims that Entra directory. Nothing was written.</summary>
    EntraTenantIdClaimed = 2,
}

/// <summary>One provisioned organisation.</summary>
public sealed record TenantRecord
{
    /// <summary>Our stable internal id, e.g. <c>tnt_acme</c>. This is what lands in <see cref="Principal.TenantId"/>.</summary>
    public required string TenantId { get; init; }

    /// <summary>
    /// The Entra <c>tid</c> GUID this tenant maps to. Null only for the legacy tenant, which
    /// predates Entra; every provisioned tenant has one.
    /// </summary>
    public string? EntraTenantId { get; init; }

    /// <summary>Human-readable organisation name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Whether sign-in is currently permitted.</summary>
    public required TenantStatus Status { get; init; }

    /// <summary>When the tenant was provisioned.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Operator identifier from the provisioning call.</summary>
    public required string CreatedBy { get; init; }
}

/// <summary>
/// Reads and writes the tenant registry: the Entra-directory-to-internal-tenant mapping every
/// sign-in resolves against, and the named-first-admin rows an operator seeds before anyone from
/// that organisation has ever signed in.
/// </summary>
/// <remarks>
/// Tenants are explicitly provisioned (spec 4.4). There is deliberately no "get or create" member:
/// a first sign-in from an unknown Entra tenant is a rejection, never an implicit new tenant.
/// </remarks>
public interface ITenantStore
{
    /// <summary>
    /// Resolves the token's <c>tid</c> claim to a provisioned tenant. Null means the directory is
    /// not a customer — the caller rejects the sign-in and audits it.
    /// </summary>
    /// <param name="entraTenantId">The raw Entra <c>tid</c> claim.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TenantRecord?> FindByEntraTenantIdAsync(string entraTenantId, CancellationToken ct = default);

    /// <summary>Looks a tenant up by our internal id. Null when no such tenant exists.</summary>
    /// <param name="tenantId">Our internal tenant id, e.g. <c>tnt_acme</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TenantRecord?> FindByTenantIdAsync(string tenantId, CancellationToken ct = default);

    /// <summary>
    /// Creates a tenant and its named first admin in one transaction. Never overwrites: an
    /// existing internal id or an already-claimed Entra directory leaves every row untouched and
    /// is reported through the returned outcome.
    /// </summary>
    /// <param name="tenant">The tenant to create.</param>
    /// <param name="firstAdminUpn">
    /// The first admin's UPN. Stored lower-cased and consulted exactly once, on that user's first
    /// successful sign-in, to bind their durable <c>{tid}:{oid}</c> id.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<TenantProvisionOutcome> ProvisionAsync(
        TenantRecord tenant,
        string firstAdminUpn,
        CancellationToken ct = default);

    /// <summary>
    /// Binds a named-but-unbound admin row to a durable user id, on that user's first successful
    /// sign-in. A row whose <c>user_id</c> is already set is never rebound, so this returns false
    /// on every sign-in after the first.
    /// </summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="upn">The signing-in user's <c>preferred_username</c>; matched lower-cased.</param>
    /// <param name="userId">The durable <c>{tid}:{oid}</c> key to bind.</param>
    /// <param name="boundAt">Bind timestamp, stamped only when this call is the one that binds.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when this call performed the binding; false when it was already bound or no row matched.</returns>
    Task<bool> TryBindFirstAdminAsync(
        string tenantId,
        string upn,
        string userId,
        DateTimeOffset boundAt,
        CancellationToken ct = default);

    /// <summary>
    /// Whether the given durable user id holds the <c>admin</c> role in the given tenant. Reads
    /// the bound <c>user_id</c> only — the UPN is never consulted again once bound.
    /// </summary>
    /// <param name="tenantId">Our internal tenant id.</param>
    /// <param name="userId">The durable <c>{tid}:{oid}</c> key.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsTenantAdminAsync(string tenantId, string userId, CancellationToken ct = default);
}
