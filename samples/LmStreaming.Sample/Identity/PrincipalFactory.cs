using System.Security.Claims;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Turns a validated Entra token into a <see cref="Principal"/>, or into the refusal that explains
/// why none was constructed.
/// </summary>
/// <remarks>
/// One helper owns every path that mints a principal, so the intersection rule of spec 3.2 is
/// applied in exactly one place. Slice 1 builds only the interactive door; the host-asserted and
/// embed doors join it in slices 5 and 7.
/// </remarks>
public sealed class PrincipalFactory
{
    /// <summary>The tenant GUID claim. Entra emits the short form; some pipelines map it to the long one.</summary>
    private static readonly string[] TenantIdClaimTypes =
    [
        "tid",
        "http://schemas.microsoft.com/identity/claims/tenantid",
    ];

    /// <summary>
    /// The immutable object id claim. NOT <c>sub</c>, which Entra scopes pairwise to the
    /// (user, client app) pair, so the same human gets a different value in a different app
    /// registration.
    /// </summary>
    private static readonly string[] ObjectIdClaimTypes =
    [
        "oid",
        "http://schemas.microsoft.com/identity/claims/objectidentifier",
    ];

    private static readonly string[] UpnClaimTypes =
    [
        "preferred_username",
        ClaimTypes.Upn,
        ClaimTypes.Email,
    ];

    private readonly ILogger<PrincipalFactory> _logger;
    private readonly ITenantStore _tenantStore;
    private readonly IAuditSink _auditSink;
    private readonly IOptions<IdentityOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly AuditThrottle _rejectionThrottle;

    /// <summary>Creates a factory over the tenant registry and audit sink.</summary>
    /// <param name="tenantStore">Resolves the token's Entra directory to a provisioned tenant.</param>
    /// <param name="auditSink">Receives one record per sign-in outcome.</param>
    /// <param name="options">Identity configuration.</param>
    /// <param name="timeProvider">Clock used for admin binding and rejection deduplication.</param>
    /// <param name="logger">Records the detail of a tenant-directory outage.</param>
    public PrincipalFactory(
        ITenantStore tenantStore,
        IAuditSink auditSink,
        IOptions<IdentityOptions> options,
        TimeProvider timeProvider,
        ILogger<PrincipalFactory> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _tenantStore = tenantStore;
        _auditSink = auditSink;
        _options = options;
        _timeProvider = timeProvider;
        _rejectionThrottle = new AuditThrottle(
            timeProvider,
            options.Value.Audit.RejectionDeduplicationWindow);
    }

    /// <summary>
    /// The principal an unauthenticated interactive request resolves to while
    /// <c>Identity:Enforce</c> is false.
    /// </summary>
    /// <remarks>
    /// Two properties make this safe to reason about. It is a REAL principal, so no code path
    /// needs a null check and no existing test needs a second code path - which is what keeps the
    /// current suite green. And it never authorizes anything by its own contents: with enforcement
    /// off, <see cref="IResourceAccessPolicy"/> short-circuits to
    /// <see cref="AccessDecision.AllowDisabled"/> before looking at tenant, owner or role. The
    /// <c>admin</c> role is here so listing queries and UI affordances behave sensibly in
    /// development, NOT because the policy consults it. This is not a security boundary at any
    /// point; the flip to <c>Enforce=true</c> is what turns the model on.
    /// </remarks>
    public Principal CreateDevelopmentPrincipal() =>
        new()
        {
            TenantId = _options.Value.LegacyTenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, "dev:local"),
            OnBehalfOf = null,
            AppId = null,
            Roles = new HashSet<string>(StringComparer.Ordinal) { "admin" },
            Source = PrincipalSource.Interactive,
        };

    /// <summary>
    /// Resolves a validated interactive token. Writes exactly one audit record either way, because
    /// a deny-only trail cannot answer "was this ever attempted successfully?".
    /// </summary>
    /// <param name="user">The claims principal the JWT bearer handler validated.</param>
    /// <param name="correlationId">Ambient request correlation id.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<PrincipalResolution> ResolveInteractiveAsync(
        ClaimsPrincipal user,
        string? correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var entraTenantId = FindClaim(user, TenantIdClaimTypes);
        var objectId = FindClaim(user, ObjectIdClaimTypes);
        var upn = FindClaim(user, UpnClaimTypes);
        var jti = FindClaim(user, ["jti"]);

        if (string.IsNullOrWhiteSpace(entraTenantId) || string.IsNullOrWhiteSpace(objectId))
        {
            // 401, not 403: the token could not be established as naming a usable identity, and a
            // caller may retry with a better one.
            return Reject(
                PrincipalResolution.InvalidToken,
                StatusCodes.Status401Unauthorized,
                entraTenantId,
                objectId,
                upn,
                jti,
                resolvedTenantId: null,
                correlationId);
        }

        try
        {
            return await ResolveAgainstDirectoryAsync(
                    entraTenantId,
                    objectId,
                    upn,
                    jti,
                    correlationId,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The token is fine; the tenant DIRECTORY is unreadable. Letting this escape would
            // fail token validation, which the JwtBearer handler answers with 401 - and a 401 is
            // the one answer a browser responds to by signing in AGAIN. The token it comes back
            // with is just as valid as this one, so the outage would become an infinite redirect
            // loop against Entra. 503 says the server is unwell, which signing in cannot fix.
            _logger.LogError(
                ex,
                "Tenant directory unreadable while resolving a sign-in for {ClaimedEntraTenantId}; "
                    + "answering {StatusCode}.",
                entraTenantId,
                StatusCodes.Status503ServiceUnavailable);

            return Reject(
                PrincipalResolution.IdentityUnavailable,
                StatusCodes.Status503ServiceUnavailable,
                entraTenantId,
                objectId,
                upn,
                jti,
                resolvedTenantId: null,
                correlationId);
        }
    }

    /// <summary>
    /// The half of interactive resolution that reads the tenant directory. Split out so its caller
    /// can turn a store outage into a refusal without also wrapping the claim parsing above, where
    /// an exception would mean a genuine bug rather than an outage.
    /// </summary>
    private async Task<PrincipalResolution> ResolveAgainstDirectoryAsync(
        string entraTenantId,
        string objectId,
        string? upn,
        string? jti,
        string? correlationId,
        CancellationToken ct)
    {
        var tenant = await _tenantStore.FindByEntraTenantIdAsync(entraTenantId, ct).ConfigureAwait(false);

        if (tenant is null)
        {
            // 403, not 401. Issuer validation alone only proves that SOME real Entra tenant signed
            // the token; it does not prove the tenant is a customer. Retrying with a fresh token
            // changes nothing, so answering 401 would put the client in a sign-in loop.
            return Reject(
                PrincipalResolution.TenantNotProvisioned,
                StatusCodes.Status403Forbidden,
                entraTenantId,
                objectId,
                upn,
                jti,
                resolvedTenantId: null,
                correlationId);
        }

        if (tenant.Status != TenantStatus.Active)
        {
            // Same shape, different code, so support can tell the two apart.
            return Reject(
                PrincipalResolution.TenantSuspended,
                StatusCodes.Status403Forbidden,
                entraTenantId,
                objectId,
                upn,
                jti,
                resolvedTenantId: tenant.TenantId,
                correlationId);
        }

        // The namespaced pair, not oid alone: an oid is unique only within a tenant, so a guest
        // present in two directories has two of them. Prefixing with tid makes the key globally
        // unique and makes a cross-tenant collision structurally impossible.
        var userId = $"{entraTenantId}:{objectId}";

        if (!string.IsNullOrWhiteSpace(upn))
        {
            // The only place preferred_username is trusted, and it is trusted only to BIND. The
            // store's own predicate is what makes this happen at most once.
            _ = await _tenantStore
                .TryBindFirstAdminAsync(tenant.TenantId, upn, userId, _timeProvider.GetUtcNow(), ct)
                .ConfigureAwait(false);
        }

        var isAdmin = await _tenantStore
            .IsTenantAdminAsync(tenant.TenantId, userId, ct)
            .ConfigureAwait(false);

        var roles = new HashSet<string>(StringComparer.Ordinal) { "member" };
        if (isAdmin)
        {
            _ = roles.Add("admin");
        }

        _auditSink.Write(new AuthenticationAuditRecord
        {
            FrontDoor = AuditFrontDoor.Interactive,
            ClaimedEntraTenantId = entraTenantId,
            ClaimedObjectId = objectId,
            ClaimedUpn = _options.Value.Audit.IncludeUpn ? upn : null,
            AppId = null,
            ResolvedTenantId = tenant.TenantId,
            Jti = jti,
            Outcome = AuthenticationOutcome.Accepted,
            Reason = null,
            CorrelationId = correlationId,
            EventClass = AuditEventClass.Routine,
        });

        return PrincipalResolution.Success(new Principal
        {
            TenantId = tenant.TenantId,
            Actor = new PrincipalRef(PrincipalKind.EndUser, userId),
            OnBehalfOf = null,
            AppId = null,
            Roles = roles,
            Source = PrincipalSource.Interactive,
        });
    }

    private PrincipalResolution Reject(
        string code,
        int statusCode,
        string? entraTenantId,
        string? objectId,
        string? upn,
        string? jti,
        string? resolvedTenantId,
        string? correlationId)
    {
        // Deduplicated per (claimed tenant, reason) so a client retry loop cannot flood the log.
        // The FIRST of each burst is always written - that record is the signal an operator uses
        // to notice that someone is waiting to be onboarded.
        //
        // The reason is part of the key, not just the tenant. A directory that moves from
        // tenant_not_provisioned to tenant_suspended inside one window is the transition an
        // operator most needs to see, and keying on the tenant alone would suppress it as a repeat
        // of a refusal that was actually about something else.
        if (_rejectionThrottle.ShouldRecord($"{entraTenantId ?? "<none>"}|{code}"))
        {
            _auditSink.Write(new AuthenticationAuditRecord
            {
                FrontDoor = AuditFrontDoor.Interactive,
                ClaimedEntraTenantId = entraTenantId,
                ClaimedObjectId = objectId,
                ClaimedUpn = _options.Value.Audit.IncludeUpn ? upn : null,
                AppId = null,
                ResolvedTenantId = resolvedTenantId,
                Jti = jti,
                Outcome = AuthenticationOutcome.Rejected,
                Reason = code,
                CorrelationId = correlationId,
                EventClass = AuditEventClass.Security,
            });
        }

        return PrincipalResolution.Reject(code, statusCode);
    }

    private static string? FindClaim(ClaimsPrincipal user, string[] claimTypes)
    {
        foreach (var claimType in claimTypes)
        {
            var value = user.FindFirst(claimType)?.Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}
