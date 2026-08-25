namespace AchieveAi.LmDotnetTools.LmCore.Identity;

/// <summary>Whether a tenant may currently sign users in.</summary>
public enum TenantStatus
{
    /// <summary>Normal operation.</summary>
    Active = 0,

    /// <summary>Provisioned but sign-in is refused, e.g. non-payment. Distinguishable in support.</summary>
    Suspended = 1,

    /// <summary>
    /// The quarantine tenant of spec 8.5 - the holding pen pre-identity data is stamped with. It
    /// has no Entra directory, so no token can ever resolve to it, and its status is not
    /// <see cref="Active"/>, so adding one by hand would still not produce a sign-in. Data leaves
    /// quarantine by being MOVED (adopt-legacy), never by a rule being relaxed.
    /// </summary>
    Quarantined = 2,
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
    /// Lower-cases every stored <c>entra_tenant_id</c> that is not already lower-cased (#347).
    /// </summary>
    /// <remarks>
    /// A startup repair rather than a one-time migration, for the same reason the null-tenant stamp
    /// is one (spec 8.5.4): a rolled-back build has never heard of normalization and would write
    /// mixed-case ids again, and rolling forward would not repair them because the schema version
    /// is already past. Rows whose lower-cased form would collide with an existing row are left
    /// alone - two tenants claiming one directory in different cases is a pre-existing data defect,
    /// and refusing to start is a worse answer to it than leaving it exactly as broken as it was.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many rows were rewritten.</returns>
    Task<int> NormalizeEntraTenantIdsAsync(CancellationToken ct = default);

    /// <summary>
    /// Ensures the quarantine tenant of spec 8.5.1 exists under <paramref name="tenantId"/>, and
    /// verifies that the configured id names THAT row and no other.
    /// </summary>
    /// <remarks>
    /// This is statement 1 of the backfill, and it is a recurring check rather than a one-time
    /// migration step (spec 8.5.4): a configured id that drifts onto a real, active tenant would
    /// otherwise hand every unstamped row to that customer's admins, once per reboot, indefinitely.
    /// An <c>INSERT OR IGNORE</c> cannot express "ignore only when the existing row is the one I
    /// meant", which is why the check and the insert are one transaction here rather than one
    /// statement.
    /// </remarks>
    /// <param name="tenantId">The configured <c>Identity:LegacyTenantId</c>.</param>
    /// <param name="createdAt">Creation timestamp, used only when the row is created.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// True when the id names the quarantine tenant afterwards; false when it names some other
    /// tenant, in which case nothing was written and the caller must fail with
    /// <c>legacy_tenant_id_collision</c>.
    /// </returns>
    Task<bool> TryEnsureQuarantineTenantAsync(
        string tenantId,
        DateTimeOffset createdAt,
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
