namespace LmStreaming.Sample.Identity;

/// <summary>
/// The <c>Identity</c> configuration section: the enforcement flag, the tenant registry's
/// location, and the development seed.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>Configuration section these options bind from.</summary>
    public const string SectionName = "Identity";

    /// <summary>
    /// Whether every <c>/api/*</c> route requires an authenticated principal. GLOBAL - one
    /// process-wide flag, not per tenant. This supersedes the per-tenant
    /// <c>IDENTITY_ENFORCE</c> of issue #237: enforcement is a property of the deployment, not of
    /// a customer.
    /// </summary>
    /// <remarks>
    /// Defaults to false, which keeps every current call path working: an unauthenticated
    /// interactive request resolves to the development principal rather than being rejected, so no
    /// code path needs a null check and no existing test needs a second code path. The consequence
    /// to be aware of is that a shared deployment cannot stage the flip customer by customer -
    /// when it flips, it flips for everyone in that process. That is the trade accepted in
    /// exchange for one unambiguous answer to "is this deployment enforcing?".
    /// </remarks>
    public bool Enforce { get; set; }

    /// <summary>
    /// Tenant id carried by the development principal while <see cref="Enforce"/> is false. It is
    /// there so listing queries and UI affordances behave sensibly in development, NOT because any
    /// policy consults it.
    /// </summary>
    public string LegacyTenantId { get; set; } = "legacy";

    /// <summary>
    /// SQLite file holding the tenant registry. Defaults to <c>identity.db</c> beside the app.
    /// </summary>
    public string? DatabasePath { get; set; }

    /// <summary>
    /// Tenants applied idempotently at startup, and only while <see cref="Enforce"/> is false.
    /// </summary>
    /// <remarks>
    /// Deliberately not the production provisioning surface: tenant creation is a per-customer
    /// onboarding event, and a config seed would make every new customer a redeploy. This exists
    /// for development and single-tenant installs; production uses
    /// <c>POST /api/admin/tenants</c>.
    /// </remarks>
    public IList<SeedTenantOptions> SeedTenants { get; set; } = [];

    /// <summary>Audit-record content controls.</summary>
    public IdentityAuditOptions Audit { get; set; } = new();
}

/// <summary>One tenant to create at startup if it does not already exist.</summary>
public sealed class SeedTenantOptions
{
    /// <summary>Our internal tenant id, e.g. <c>tnt_dev</c>.</summary>
    public string? TenantId { get; set; }

    /// <summary>The Entra directory (<c>tid</c>) this tenant maps to.</summary>
    public string? EntraTenantId { get; set; }

    /// <summary>Human-readable organisation name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>UPN of the first admin, bound on that user's first sign-in.</summary>
    public string? FirstAdminUpn { get; set; }
}

/// <summary>Audit-record content controls.</summary>
public sealed class IdentityAuditOptions
{
    /// <summary>
    /// Whether a rejected sign-in's <c>preferred_username</c> is recorded. Off by default: a
    /// rejected sign-in is exactly the case where the presented identifier belongs to someone who
    /// is by definition not our user. Some deployments want it for support; some will not want it
    /// retained at all.
    /// </summary>
    public bool IncludeUpn { get; set; }

    /// <summary>
    /// How long one (tenant, reason) rejection is suppressed after being recorded, so a client
    /// retry loop cannot flood the log. Records are deduplicated, not dropped: the first of each
    /// burst is always written.
    /// </summary>
    public TimeSpan RejectionDeduplicationWindow { get; set; } = TimeSpan.FromMinutes(1);
}
