using System.ComponentModel.DataAnnotations;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Identity;
using LmStreaming.Sample.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Controllers;

/// <summary>
/// Guards the tenant admin surface with the operator shared secret.
/// </summary>
/// <remarks>
/// <para>
/// Two properties are deliberate and both are load-bearing.
/// </para>
/// <para>
/// UNCONDITIONAL. Unlike <see cref="InboundS2SAuthAttribute"/>, this guard is not marker-gated and
/// is not disabled by <c>Identity:Enforce</c> being false. Provisioning a tenant is how an
/// unknown Entra directory becomes a customer, so an unguarded provisioning route would make the
/// "tenants are explicitly provisioned" rule enforceable by anyone who can reach the port.
/// </para>
/// <para>
/// FAILS CLOSED. When the secret is unconfigured the route answers <c>503</c>, never success. The
/// S2S guard's keyless-dev behaviour - unset secret means the guard is off - is right for a
/// same-origin UI path that would otherwise break, and exactly wrong here: the failure mode of an
/// operator who forgets to set it would be a world-writable tenant registry.
/// </para>
/// <para>
/// RUNS FIRST. <see cref="ApiControllerAttribute"/> installs MVC's model-state validation filter at
/// <c>Order = -2000</c>. An unordered attribute filter sits at <c>Order = 0</c>, so without
/// <see cref="Order"/> below <c>-2000</c> a malformed body would be answered <c>400</c> before this
/// guard ever ran - an unauthenticated caller could probe the route's existence and its request
/// schema, and reach the JSON deserializer, by sending nonsense with no header at all.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class)]
public sealed class OperatorSecretAuthAttribute : Attribute, IAsyncActionFilter, IOrderedFilter
{
    /// <summary>Configuration key the operator secret is read from.</summary>
    public const string SecretConfigKey = "Identity:OperatorSecret";

    /// <summary>Environment variable operators set the secret through.</summary>
    public const string SecretEnvironmentVariable = "LMSTREAMING_IDENTITY_OPERATOR_SECRET";

    /// <summary>Header the caller must present the operator secret in.</summary>
    public const string HeaderName = "X-Operator-Secret";

    /// <summary>
    /// Runs ahead of MVC's model-state validation filter (<c>Order = -2000</c>) so an
    /// unauthenticated caller is refused before model binding, validation or JSON deserialization
    /// can answer on the route's behalf.
    /// </summary>
    public int Order => -2100;

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var httpContext = context.HttpContext;
        var secret = httpContext.RequestServices.GetService<IConfiguration>()?[SecretConfigKey];

        if (string.IsNullOrWhiteSpace(secret))
        {
            httpContext.RequestServices
                .GetService<ILogger<OperatorSecretAuthAttribute>>()
                ?.LogError(
                    "{ConfigKey} is not configured; the tenant admin surface is unavailable. "
                        + "Set {EnvVar} to enable it.",
                    SecretConfigKey,
                    SecretEnvironmentVariable);

            context.Result = new ObjectResult(
                new { error = "unavailable", code = "operator_secret_not_configured" })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable,
            };
            return;
        }

        if (!InboundS2SAuthAttribute.ConstantTimeEquals(
                secret,
                httpContext.Request.Headers[HeaderName].ToString()))
        {
            context.Result = new UnauthorizedObjectResult(
                new { error = "unauthorized", code = "operator_auth_failed" });
            return;
        }

        await next().ConfigureAwait(false);
    }
}

/// <summary>Request body for <c>POST /api/admin/tenants</c>.</summary>
public sealed class ProvisionTenantRequest
{
    /// <summary>Our internal tenant id, e.g. <c>tnt_contoso</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public string? TenantId { get; set; }

    /// <summary>
    /// The Entra directory (<c>tid</c>) this tenant maps to. Required: a tenant with no directory
    /// can never be resolved from a token, so creating one would be a silent no-op.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string? EntraTenantId { get; set; }

    /// <summary>Human-readable organisation name.</summary>
    [Required(AllowEmptyStrings = false)]
    public string? DisplayName { get; set; }

    /// <summary>
    /// UPN of the first admin. Recorded now and bound to a user id on that person's first
    /// sign-in - the operator provisioning the tenant does not yet know their <c>oid</c>.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string? FirstAdminUpn { get; set; }
}

/// <summary>Request body for <c>POST /api/admin/tenants/{tenantId}/adopt-legacy</c> (spec 8.5.3).</summary>
public sealed class AdoptLegacyRequest
{
    /// <summary>
    /// Owner to assign, as the <c>{tid}:{oid}</c> pair of spec 3.3. Optional: omitting it lands the
    /// rows in the tenant unowned, where the tenant's own admins can see them and nobody else can.
    /// That is the recommended first step.
    /// </summary>
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// <c>thread</c>. Workspaces and modes are stamped by the JSON stores of spec 8.6, which this
    /// slice does not build, so any other value is refused rather than silently treated as
    /// <c>thread</c> - an operator who asked to adopt workspaces must not be told it worked.
    /// </summary>
    public string ResourceType { get; set; } = AdoptLegacyResourceTypes.Thread;

    /// <summary>Restrict to these ids, or omit for every quarantined resource of that type.</summary>
    public IList<string>? ResourceIds { get; set; }

    /// <summary>
    /// Rehearse without writing customer data. The audit record is still written: a rehearsal that
    /// leaves no trace is a reconnaissance tool.
    /// </summary>
    public bool DryRun { get; set; }
}

/// <summary>The resource types <c>adopt-legacy</c> accepts.</summary>
public static class AdoptLegacyResourceTypes
{
    /// <summary>A conversation thread. The only type P1 slice 2 stamps.</summary>
    public const string Thread = "thread";
}

/// <summary>Response body for a successful (or rehearsed) adoption.</summary>
public sealed class AdoptLegacyResponse
{
    /// <summary>Tenant the rows moved (or would move) into.</summary>
    public required string TenantId { get; init; }

    /// <summary>How many rows moved, or would have.</summary>
    public required int AffectedCount { get; init; }

    /// <summary>Whether this was a rehearsal.</summary>
    public required bool DryRun { get; init; }

    /// <summary>A bounded sample of the affected ids, so an operator can eyeball a rehearsal.</summary>
    public required IReadOnlyList<string> Sample { get; init; }
}

/// <summary>Tenant provisioning. The only supported way to make an Entra directory a customer.</summary>
[ApiController]
[Route("api/admin/tenants")]
[OperatorSecretAuth]
public sealed class TenantsController : ControllerBase
{
    /// <summary>How many affected ids a response echoes back. A rehearsal over 40,000 rows must
    /// not answer with 40,000 ids.</summary>
    private const int SampleSize = 20;

    /// <summary>
    /// Ceiling on the single scan a subset adoption walks the sub-agent tree over (#405).
    /// </summary>
    /// <remarks>
    /// Matches the roster scan's own cap in <c>ConversationDescendantScanner</c>, and is one call
    /// rather than a paging loop for the reason recorded there: the store is contractually ordered
    /// by last-updated, so a conversation touched between two pages moves across the offset boundary
    /// and the next page skips it. Here that skip would drop a parent link and split a tree - which
    /// is the defect this walk exists to prevent - so the over-cap case is refused rather than
    /// walked partially.
    /// </remarks>
    internal const int AdoptionScanMaxThreads = 2000;

    private readonly ITenantStore _tenantStore;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;
    private readonly IConversationStore _conversationStore;
    private readonly IOptions<IdentityOptions> _identityOptions;
    private readonly ILogger<TenantsController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="tenantStore">Tenant registry.</param>
    /// <param name="auditSink">Receives one record per provisioning attempt.</param>
    /// <param name="timeProvider">Clock stamped onto the created tenant.</param>
    /// <param name="conversationStore">Conversation store whose rows adoption moves.</param>
    /// <param name="identityOptions">Supplies the quarantine tenant id adoption selects on.</param>
    /// <param name="logger">Diagnostics.</param>
    public TenantsController(
        ITenantStore tenantStore,
        IAuditSink auditSink,
        TimeProvider timeProvider,
        IConversationStore conversationStore,
        IOptions<IdentityOptions> identityOptions,
        ILogger<TenantsController> logger)
    {
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(conversationStore);
        ArgumentNullException.ThrowIfNull(identityOptions);
        ArgumentNullException.ThrowIfNull(logger);

        _tenantStore = tenantStore;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
        _conversationStore = conversationStore;
        _identityOptions = identityOptions;
        _logger = logger;
    }

    /// <summary>Provisions a tenant.</summary>
    /// <param name="request">The tenant to create.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    public async Task<IActionResult> ProvisionAsync(
        [FromBody] ProvisionTenantRequest request,
        CancellationToken ct)
    {
        // Unreachable in practice: [ApiController] makes a [FromBody] parameter implicitly
        // required, so a missing or null body is answered 400 by model validation long before this
        // line. It stays because CA1062 is an error in this repo and every other [FromBody] action
        // in this sample carries the same line - it is an analyzer contract, not a live guard.
        ArgumentNullException.ThrowIfNull(request);

        var record = new TenantRecord
        {
            TenantId = request.TenantId!.Trim(),
            EntraTenantId = request.EntraTenantId!.Trim(),
            DisplayName = request.DisplayName!.Trim(),
            Status = TenantStatus.Active,
            CreatedAt = _timeProvider.GetUtcNow(),
            CreatedBy = "operator",
        };

        var outcome = await _tenantStore
            .ProvisionAsync(record, request.FirstAdminUpn!.Trim(), ct)
            .ConfigureAwait(false);

        WriteAudit(record, outcome);

        // A conflict is reported rather than merged. Two tenant ids pointing at one directory, or
        // one id quietly repointed at another directory, are both silent cross-tenant data leaks;
        // an operator who meant to change a mapping should have to say so explicitly.
        return outcome switch
        {
            TenantProvisionOutcome.Created => Created(
                $"/api/admin/tenants/{record.TenantId}",
                new { tenantId = record.TenantId, entraTenantId = record.EntraTenantId }),
            TenantProvisionOutcome.TenantIdExists => Conflict(
                new { error = "conflict", code = "tenant_id_exists" }),
            _ => Conflict(new { error = "conflict", code = "entra_tenant_id_claimed" }),
        };
    }


    /// <summary>
    /// Moves quarantined conversations into a real tenant, optionally assigning an owner
    /// (spec 8.5.3).
    /// </summary>
    /// <remarks>
    /// The only operation in P1 that moves customer data across a tenancy boundary, which is why it
    /// has a rehearsal mode and why every call - rehearsed, applied or rejected - writes one
    /// <see cref="AdministrationAuditRecord"/>. "No customer row was written" never means "no audit
    /// record was written".
    /// </remarks>
    /// <param name="tenantId">The tenant to adopt into. Must exist and be active.</param>
    /// <param name="request">What to adopt, and whether to rehearse.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{tenantId}/adopt-legacy")]
    public async Task<IActionResult> AdoptLegacyAsync(
        string tenantId,
        [FromBody] AdoptLegacyRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var quarantineTenantId = _identityOptions.Value.LegacyTenantId;

        if (!string.Equals(request.ResourceType, AdoptLegacyResourceTypes.Thread, StringComparison.Ordinal))
        {
            return RejectAdoption(
                tenantId,
                request,
                StatusCodes.Status400BadRequest,
                "unsupported_resource_type");
        }

        var tenant = await _tenantStore.FindByTenantIdAsync(tenantId, ct).ConfigureAwait(false);

        // A suspended or absent tenant is the same answer. Distinguishing them would let an
        // operator secret holder enumerate which tenant ids exist but are switched off.
        if (tenant is not { Status: TenantStatus.Active })
        {
            return RejectAdoption(tenantId, request, StatusCodes.Status404NotFound, "tenant_not_found");
        }

        // Adopting INTO the quarantine tenant would be a no-op that reported success: the source
        // and target selections are the same rows, so every one of them is "already adopted".
        if (string.Equals(tenantId, quarantineTenantId, StringComparison.Ordinal))
        {
            return RejectAdoption(
                tenantId,
                request,
                StatusCodes.Status400BadRequest,
                "target_is_quarantine_tenant");
        }

        var ownerUserId = string.IsNullOrWhiteSpace(request.OwnerUserId)
            ? null
            : request.OwnerUserId.Trim();

        // Validated BEFORE any write. A user id from another Entra directory would produce rows
        // that 7.4 step 2 then denies to everybody - the data would be re-quarantined under a name
        // that looks adopted, which is worse than leaving it where it was.
        if (ownerUserId is not null && !OwnerBelongsToTenant(ownerUserId, tenant.EntraTenantId))
        {
            return RejectAdoption(
                tenantId,
                request,
                StatusCodes.Status400BadRequest,
                "owner_tenant_mismatch");
        }

        if (_conversationStore is not IConversationOwnershipStore ownership)
        {
            return RejectAdoption(
                tenantId,
                request,
                StatusCodes.Status503ServiceUnavailable,
                "adoption_unsupported_store");
        }

        // ResourceIds is distinguished from its absence, not normalised into it: an explicitly empty
        // list means "adopt nothing", and treating it as "omitted" would adopt EVERY quarantined
        // conversation on a call that asked for none.
        var resourceIds = request.ResourceIds?.ToArray();

        // #405. A submitted subset is expanded to the whole conversation TREE each named id belongs
        // to. Leaving a sub-agent behind is not a tidiness problem: the roster scan scopes by the
        // root's tenant, so a descendant stranded in quarantine drops out of its parent's roster
        // silently, and the incomplete roster is then cached for the life of the process.
        if (resourceIds is { Length: > 0 })
        {
            var expansion = await ExpandToWholeTreesAsync(quarantineTenantId, resourceIds, ct)
                .ConfigureAwait(false);

            if (expansion is null)
            {
                return RejectAdoption(
                    tenantId,
                    request,
                    StatusCodes.Status503ServiceUnavailable,
                    "adoption_scan_truncated");
            }

            resourceIds = expansion;
        }

        var eligible = await ownership
            .ListThreadIdsByTenantAsync(quarantineTenantId, resourceIds, ct)
            .ConfigureAwait(false);

        if (request.DryRun)
        {
            WriteAdoptionAudit(
                tenantId,
                ownerUserId,
                eligible.Count,
                dryRun: true,
                AdministrationOutcome.Rehearsed,
                reason: null);

            return Ok(new AdoptLegacyResponse
            {
                TenantId = tenantId,
                AffectedCount = eligible.Count,
                DryRun = true,
                Sample = [.. eligible.Take(SampleSize)],
            });
        }

        var affected = await ownership
            .AdoptThreadsAsync(quarantineTenantId, tenantId, ownerUserId, resourceIds, ct)
            .ConfigureAwait(false);

        WriteAdoptionAudit(
            tenantId,
            ownerUserId,
            affected,
            dryRun: false,
            AdministrationOutcome.Applied,
            reason: null);

        _logger.LogWarning(
            "Adopted {AffectedCount} legacy conversation(s) from {QuarantineTenantId} into {TenantId}.",
            affected,
            quarantineTenantId,
            tenantId);

        return Ok(new AdoptLegacyResponse
        {
            TenantId = tenantId,
            AffectedCount = affected,
            DryRun = false,
            Sample = [.. eligible.Take(SampleSize)],
        });
    }

    /// <summary>
    /// Grows a submitted id list into every whole sub-agent tree those ids touch, or returns
    /// <see langword="null"/> when the one bounded scan it walks could not read the tenant (#405).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk goes BOTH ways along each <c>sample.subAgentOf</c> edge, and one connected-component
    /// pass is why. A downward-only expansion still splits a tree whenever the operator names a
    /// sub-agent rather than a root: the child moves, the root stays, and the roster scan rooted at
    /// the row still in quarantine drops the adopted child instead. Same disclosure, same edge,
    /// selected from the other end. Following the component makes the direction of the operator's
    /// selection stop mattering, at the cost of adopting rows they did not name - which is reported
    /// in the count and the rehearsal sample, and which is the whole tree they were already
    /// implicitly asking for.
    /// </para>
    /// <para>
    /// Scoped to the quarantine tenant WITHOUT <c>IncludeUntenanted</c>, unlike the roster scan.
    /// That tolerance exists there because a scan rooted in a real tenant must still see a
    /// descendant whose stamp has not landed yet; here it would buy nothing, because an untenanted
    /// row is not in the source tenant and <c>AdoptThreadsAsync</c> would not move it - and it is
    /// not dropped from any roster either, which is exactly what #395 established.
    /// </para>
    /// <para>
    /// A parent that is NOT in the scan is not followed. Rows leave quarantine here; they never
    /// leave a real tenant, or adopting a conversation would become a way to move somebody else's
    /// by claiming to be its child.
    /// </para>
    /// </remarks>
    /// <param name="quarantineTenantId">The tenant the adoption reads from.</param>
    /// <param name="seeds">The ids the operator named.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<string[]?> ExpandToWholeTreesAsync(
        string quarantineTenantId,
        IReadOnlyCollection<string> seeds,
        CancellationToken ct)
    {
        var rows = await _conversationStore
            .ListThreadsAsync(
                ConversationListScope.ForTenant(quarantineTenantId),
                AdoptionScanMaxThreads + 1,
                0,
                ct: ct)
            .ConfigureAwait(false) ?? [];

        if (rows.Count > AdoptionScanMaxThreads)
        {
            // Refused, not truncated. A partial walk cannot see the parent links past the cap, so
            // proceeding would reintroduce the split this method exists to prevent - on exactly the
            // deployments too large for anyone to notice it happening.
            _logger.LogWarning(
                "Refusing a subset adoption from {QuarantineTenantId}: more than {MaxThreads} "
                    + "quarantined conversations, so the sub-agent tree cannot be walked in one scan.",
                quarantineTenantId,
                AdoptionScanMaxThreads);
            return null;
        }

        var childrenOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var inScan = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            _ = inScan.Add(row.ThreadId);

            if (SubAgentProvenance.TryProject(row)?.ParentThreadId is not { } parentThreadId)
            {
                continue;
            }

            parentOf[row.ThreadId] = parentThreadId;
            if (!childrenOf.TryGetValue(parentThreadId, out var children))
            {
                childrenOf[parentThreadId] = children = [];
            }

            children.Add(row.ThreadId);
        }

        // The seeds are filtered by the SAME in-quarantine rule the walk applies to a parent, and for
        // the same reason. A seed naming a row in a real tenant is not a row this route may move, but
        // an unfiltered seed still descends into that row's quarantined children and moves THEM -
        // severing them from a parent that stayed put, which is #405 itself, manufactured here. The
        // operator having typed the id does not make the row eligible; only its tenant does.
        //
        // The blank guard is not decoration: `["thread-1", null]` is valid JSON, and both the parent
        // dictionary and this set are ordinal-comparer keyed, so a null key throws
        // ArgumentNullException - answering a typo with an unhandled 500 on a route whose every other
        // refusal is a stable code in an audit record.
        var reached = new HashSet<string>(
            seeds.Where(seed => !string.IsNullOrWhiteSpace(seed) && inScan.Contains(seed)),
            StringComparer.Ordinal);
        var frontier = new Queue<string>(reached);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();

            if (parentOf.TryGetValue(current, out var parent)
                && inScan.Contains(parent)
                && reached.Add(parent))
            {
                frontier.Enqueue(parent);
            }

            if (!childrenOf.TryGetValue(current, out var children))
            {
                continue;
            }

            foreach (var child in children)
            {
                if (reached.Add(child))
                {
                    frontier.Enqueue(child);
                }
            }
        }

        return [.. reached];
    }

    /// <summary>
    /// Whether an owner id names a user of the target tenant's Entra directory.
    /// </summary>
    /// <remarks>
    /// Split on the FIRST colon only. An <c>oid</c> is a GUID today, but the pair's contract is
    /// "tid, colon, the rest"; splitting on every colon would reject a future subject format by
    /// arity rather than by tenancy, which is a different refusal wearing the same code.
    /// </remarks>
    private static bool OwnerBelongsToTenant(string ownerUserId, string? entraTenantId)
    {
        // A tenant with no directory can be matched by nothing. It cannot be resolved from a token
        // either, so adopting rows into it would hide them from everyone.
        if (string.IsNullOrWhiteSpace(entraTenantId))
        {
            return false;
        }

        var separator = ownerUserId.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == ownerUserId.Length - 1)
        {
            return false;
        }

        // Case-insensitive because a directory id is a GUID, whose textual case carries no meaning -
        // and because #347's normalisation stores it lower-cased while an operator will paste it
        // from a portal that shows it however it likes.
        return string.Equals(
            ownerUserId[..separator],
            entraTenantId,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Answers a refused adoption, writing its audit record first. Every early return goes through
    /// here so that "rejected calls are audited too" is a property of the shape, not of remembering.
    /// </summary>
    private ObjectResult RejectAdoption(
        string tenantId,
        AdoptLegacyRequest request,
        int statusCode,
        string code)
    {
        WriteAdoptionAudit(
            tenantId,
            string.IsNullOrWhiteSpace(request.OwnerUserId) ? null : request.OwnerUserId.Trim(),
            affectedCount: 0,
            request.DryRun,
            AdministrationOutcome.Rejected,
            code);

        return new ObjectResult(new { error = "rejected", code })
        {
            StatusCode = statusCode,
        };
    }

    private void WriteAdoptionAudit(
        string tenantId,
        string? ownerUserId,
        int affectedCount,
        bool dryRun,
        AdministrationOutcome outcome,
        string? reason) =>
        _auditSink.Write(new AdministrationAuditRecord
        {
            Operation = "tenant.adopt_legacy",
            RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            TargetTenantId = tenantId,
            TargetOwnerUserId = ownerUserId,
            AffectedCount = affectedCount,
            DryRun = dryRun,
            Outcome = outcome,
            Reason = reason,
            CorrelationId = HttpContext.TraceIdentifier,
            EventClass = AuditEventClass.Security,
        });

    private void WriteAudit(TenantRecord record, TenantProvisionOutcome outcome)
    {
        _auditSink.Write(new AdministrationAuditRecord
        {
            Operation = "tenant.provision",
            RemoteAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            TargetTenantId = record.TenantId,
            TargetOwnerUserId = null,
            AffectedCount = outcome == TenantProvisionOutcome.Created ? 1 : 0,
            DryRun = false,
            Outcome = outcome == TenantProvisionOutcome.Created
                ? AdministrationOutcome.Applied
                : AdministrationOutcome.Rejected,
            Reason = outcome == TenantProvisionOutcome.Created ? null : outcome.ToString(),
            CorrelationId = HttpContext.TraceIdentifier,
            EventClass = AuditEventClass.Security,
        });
    }
}
