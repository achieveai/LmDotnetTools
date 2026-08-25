using AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.Controllers;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// The service-to-service front door (spec 4.2, step 1): an app credential plus the inbound S2S
/// shared secret, with no on-behalf-of token, yields a <c>Source = AppOnly</c> principal.
/// </summary>
/// <remarks>
/// <para>
/// Runs as an <see cref="IRequestPrincipalSource"/> inside <see cref="IdentityMiddleware"/> rather
/// than inside <see cref="InboundS2SAuthAttribute"/>, because the attribute is an action filter and
/// filters run at endpoint execution - after the middleware has already written its refusal (#345).
/// The attribute stays exactly as it is and keeps enforcing the secret at the endpoint; this class
/// duplicates neither the secret nor the comparison, calling straight into the attribute's own
/// helpers so the two can never disagree about what a service request is or about how the secret
/// is compared.
/// </para>
/// <para>
/// The whole class is INERT while <c>Identity:Enforce</c> is false. That is deliberate: with
/// enforcement off a service caller already resolves to the development principal, every existing
/// deployment works, and a source that started refusing unregistered apps would be a behaviour
/// change nobody asked for on the one setting that is supposed to change nothing.
/// </para>
/// <para>
/// The secret being unset does NOT mean "admit the caller". The keyless dev path disables the
/// attribute's guard, and admitting an app principal on the strength of a header anyone can type
/// would turn that convenience into an authentication bypass the moment enforcement was on. With
/// enforcement on and no secret configured, a service caller gets the ordinary 401 - which an
/// operator fixes by configuring the secret.
/// </para>
/// </remarks>
public sealed class ServiceCallerPrincipalSource : IRequestPrincipalSource
{
    /// <summary>The caller authenticated, but its app id is not onboarded to this deployment.</summary>
    public const string AppNotRegisteredCode = "service_app_not_registered";

    /// <summary>The app's registration names a tenant it may not act within.</summary>
    public const string AppTenantInvalidCode = "service_app_tenant_invalid";

    private readonly IOptions<IdentityOptions> _options;
    private readonly IConfiguration _configuration;
    private readonly IAuditSink _auditSink;
    private readonly ILogger<ServiceCallerPrincipalSource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="options">Identity configuration, including the app registry.</param>
    /// <param name="configuration">Root configuration, read for the inbound S2S secret.</param>
    /// <param name="auditSink">Receives one record per service-caller outcome.</param>
    /// <param name="logger">Diagnostics.</param>
    public ServiceCallerPrincipalSource(
        IOptions<IdentityOptions> options,
        IConfiguration configuration,
        IAuditSink auditSink,
        ILogger<ServiceCallerPrincipalSource> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _configuration = configuration;
        _auditSink = auditSink;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        return ValueTask.FromResult(Resolve(context));
    }

    private PrincipalResolution? Resolve(HttpContext context)
    {
        if (!_options.Value.Enforce)
        {
            return null;
        }

        // The SAME predicate the endpoint filter marker-gates on. A same-origin browser request
        // carries neither header and must fall through to the interactive door untouched.
        if (!InboundS2SAuthAttribute.IsServiceToServiceRequest(context.Request))
        {
            return null;
        }

        var secret = _configuration[InboundS2SAuthAttribute.SecretConfigKey];
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogError(
                "A service caller reached {Path} while Identity:Enforce is true and {ConfigKey} is "
                    + "not configured. No app principal can be established, so the request is "
                    + "refused. Set {EnvVar} to onboard service callers.",
                context.Request.Path.Value,
                InboundS2SAuthAttribute.SecretConfigKey,
                "LMSTREAMING_S2S_INBOUND_SECRET");
            return null;
        }

        var presented = context.Request.Headers[InboundS2SAuthAttribute.HeaderName].ToString();
        if (!InboundS2SAuthAttribute.ConstantTimeEquals(secret, presented))
        {
            // Null, not a rejection. The endpoint filter answers this case with its own
            // s2s_auth_failed today, and the middleware's 401 says the same thing one layer up;
            // inventing a second code here would give one failure two names.
            return null;
        }

        var appId = context.Request.Headers[SandboxCredential.AppIdHeader].ToString();
        var registrationKey = string.IsNullOrWhiteSpace(appId)
            ? IdentityOptions.DefaultServiceAppKey
            : appId;

        if (!_options.Value.Apps.TryGetValue(registrationKey, out var registration)
            || string.IsNullOrWhiteSpace(registration?.TenantId))
        {
            // 403, not 401: the caller DID authenticate. Retrying with the same credential cannot
            // help, and answering 401 would invite a client to go and get another one.
            return Reject(AppNotRegisteredCode, StatusCodes.Status403Forbidden, registrationKey, null);
        }

        var tenantId = registration.TenantId.Trim();

        if (string.Equals(tenantId, _options.Value.LegacyTenantId, StringComparison.Ordinal))
        {
            // Spec 8.5.2: no principal may carry the quarantine tenant. A registration that names
            // it would hand this app every conversation on the deployment nobody has adopted.
            _logger.LogError(
                "Identity:Apps:{AppKey}:TenantId is {TenantId}, which is the quarantine tenant "
                    + "named by Identity:LegacyTenantId. No principal may carry it (spec 8.5.2), "
                    + "so the caller is refused. Onboard the app to a real tenant.",
                registrationKey,
                tenantId);
            return Reject(AppTenantInvalidCode, StatusCodes.Status403Forbidden, registrationKey, tenantId);
        }

        _auditSink.Write(new AuthenticationAuditRecord
        {
            FrontDoor = AuditFrontDoor.S2SObo,
            ClaimedEntraTenantId = null,
            ClaimedObjectId = null,
            ClaimedUpn = null,
            AppId = registrationKey,
            ResolvedTenantId = tenantId,
            Jti = null,
            Outcome = AuthenticationOutcome.Accepted,
            Reason = null,
            CorrelationId = context.TraceIdentifier,
            EventClass = AuditEventClass.Routine,
        });

        return PrincipalResolution.Success(new Principal
        {
            TenantId = tenantId,
            Actor = new PrincipalRef(PrincipalKind.App, registrationKey),

            // Null, and it stays null until slice 5 (#305) validates an on-behalf-of token. The
            // no-fallback rule of spec 4.2 step 3 lives with that work: a caller that ASSERTS a
            // user and gets it wrong must fail rather than silently act as the app.
            OnBehalfOf = null,
            AppId = registrationKey,
            Scopes = new HashSet<string>(registration.Scopes, StringComparer.Ordinal),
            Roles = new HashSet<string>(StringComparer.Ordinal),
            Source = PrincipalSource.AppOnly,
        });
    }

    private PrincipalResolution Reject(string code, int statusCode, string appId, string? tenantId)
    {
        _auditSink.Write(new AuthenticationAuditRecord
        {
            FrontDoor = AuditFrontDoor.S2SObo,
            ClaimedEntraTenantId = null,
            ClaimedObjectId = null,
            ClaimedUpn = null,
            AppId = appId,
            ResolvedTenantId = tenantId,
            Jti = null,
            Outcome = AuthenticationOutcome.Rejected,
            Reason = code,
            CorrelationId = null,
            EventClass = AuditEventClass.Security,
        });

        return PrincipalResolution.Reject(code, statusCode);
    }
}
