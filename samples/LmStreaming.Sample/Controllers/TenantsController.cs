using System.ComponentModel.DataAnnotations;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

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

/// <summary>Tenant provisioning. The only supported way to make an Entra directory a customer.</summary>
[ApiController]
[Route("api/admin/tenants")]
[OperatorSecretAuth]
public sealed class TenantsController : ControllerBase
{
    private readonly ITenantStore _tenantStore;
    private readonly IAuditSink _auditSink;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the controller.</summary>
    /// <param name="tenantStore">Tenant registry.</param>
    /// <param name="auditSink">Receives one record per provisioning attempt.</param>
    /// <param name="timeProvider">Clock stamped onto the created tenant.</param>
    public TenantsController(ITenantStore tenantStore, IAuditSink auditSink, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tenantStore);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _tenantStore = tenantStore;
        _auditSink = auditSink;
        _timeProvider = timeProvider;
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
