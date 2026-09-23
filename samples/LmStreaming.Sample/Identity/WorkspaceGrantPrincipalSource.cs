using AchieveAi.LmDotnetTools.LmCore.Identity;
using LmStreaming.Sample.FileBrowser;
using Microsoft.Extensions.Options;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// The front door for Bug#15's path-addressed raw workspace route: a request that carries a valid
/// <see cref="WorkspaceGrantService"/> token — in its <c>lm_ws_grant</c> COOKIE, or in its path where a
/// <c>Secure</c> cookie is not available — resolves to the principal that grant was minted for, with no
/// bearer token anywhere.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this has to exist.</b> <see cref="IdentityMiddleware"/> refuses every <c>/api</c> request without
/// a resolvable principal, and it runs BEFORE routing. The raw route is fetched by an
/// <c>&lt;iframe src&gt;</c>, an <c>&lt;img src&gt;</c> and by every relative <c>&lt;link&gt;</c> inside the
/// document it serves — none of which can carry an <c>Authorization</c> header. Under
/// <c>Identity:Enforce=true</c> the mint therefore succeeds (it is an ordinary bearer-authenticated POST),
/// the client sets an iframe <c>src</c>, every fetch 401s at the middleware, and the user sees a blank pane
/// with no error and no fallback, because the client cannot observe an iframe's status code. This class is
/// what makes the grant THE credential on that one route family.
/// </para>
/// <para>
/// <b>Scope is deliberately one route shape.</b> It matches only
/// <c>GET /api/conversations/{threadId}/workspace/{grant}/…</c>. Every other request — including
/// <c>POST …/files/grant</c>, which is how a grant is obtained in the first place — returns null and is
/// resolved by the doors that already existed. A source returning null means "not my kind of request", so
/// this can never promote a caller another door refused. The grant COOKIE is scoped by its own <c>Path</c>
/// to the same one route family, so it is not even sent anywhere else.
/// </para>
/// <para>
/// <b>Where the token is read from is not this door's decision.</b>
/// <see cref="WorkspaceGrantService.PresentedToken"/> resolves the path segment to a token: the cookie when
/// the segment is the public <see cref="WorkspaceGrantService.CookieTransportMarker"/>, the segment itself
/// otherwise. The marker presented with no cookie — which is all a script inside the sandboxed document can
/// construct from its own <c>location</c> — arrives here as an unreadable token and is refused with
/// <see cref="InvalidGrantCode"/>, exactly as a forged one is.
/// </para>
/// <para>
/// <b>It cannot bootstrap identity out of nothing.</b> Every principal it yields was serialised into a
/// token by this host, for a caller some other front door had already authenticated, under a key ring only
/// this host holds. That is why <see cref="IdentityServiceCollectionExtensions"/> excludes it — exactly as
/// it excludes <see cref="ServiceCallerPrincipalSource"/> — from the "does this host have a front door at
/// all?" boot check: counting it would let a misconfigured enforcing host boot on a door that can only ever
/// re-present an identity someone else established.
/// </para>
/// <para>
/// <b>Inert while <c>Identity:Enforce</c> is false.</b> With enforcement off the middleware already hands
/// every request the development principal, and a grant minted in that state carries no principal to
/// reconstruct. Returning null there keeps that path byte-for-byte what it was.
/// </para>
/// <para>
/// Only REFUSALS are audited. An accepted grant produces one record per subresource — a rendered page
/// pulls its stylesheet, its script, its fonts and every image through here — and that volume would bury
/// the real refusals, which is the same reason <c>ResourceAccessPolicy</c> does not audit capability
/// probes (#487). The refusal path is low-volume and is where the security signal is.
/// </para>
/// </remarks>
public sealed class WorkspaceGrantPrincipalSource : IRequestPrincipalSource
{
    /// <summary>The grant was unreadable: forged, truncated, minted under another key, or expired.</summary>
    public const string InvalidGrantCode = "invalid_workspace_grant";

    /// <summary>The grant was genuine but was minted for a different conversation than the path names.</summary>
    public const string ThreadMismatchCode = "workspace_grant_thread_mismatch";

    /// <summary>
    /// The grant was genuine but carries no principal — minted while enforcement was off, then presented to
    /// an enforcing host. Refused rather than admitted anonymously.
    /// </summary>
    public const string AnonymousGrantCode = "workspace_grant_not_authenticated";

    private readonly IOptions<IdentityOptions> _options;
    private readonly WorkspaceGrantService _grants;
    private readonly IAuditSink _auditSink;
    private readonly ILogger<WorkspaceGrantPrincipalSource> _logger;

    /// <summary>Creates the source.</summary>
    /// <param name="options">Identity configuration; the whole class is inert unless <c>Enforce</c> is on.</param>
    /// <param name="grants">Opens the presented grant and reconstructs its principal.</param>
    /// <param name="auditSink">Receives one record per refusal.</param>
    /// <param name="logger">Diagnostics. Never given the grant itself.</param>
    public WorkspaceGrantPrincipalSource(
        IOptions<IdentityOptions> options,
        WorkspaceGrantService grants,
        IAuditSink auditSink,
        ILogger<WorkspaceGrantPrincipalSource> logger
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(auditSink);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _grants = grants;
        _auditSink = auditSink;
        _logger = logger;
    }

    /// <inheritdoc />
    public ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ValueTask.FromResult(Resolve(context));
    }

    /// <summary>
    /// Pulls the conversation id and the grant out of a raw workspace path, or returns false for any other
    /// request. Kept internal so the shape this door recognises is pinned by a test rather than inferred
    /// from the routes it happens to admit.
    /// </summary>
    /// <remarks>
    /// <c>HttpRequest.Path</c> is already percent-decoded, so this sees the same <c>threadId</c> value model
    /// binding will later hand the action — a caller cannot make the two disagree by encoding one of them.
    /// </remarks>
    internal static bool TryReadRawWorkspaceRoute(HttpRequest request, out string threadId, out string grant)
    {
        threadId = string.Empty;
        grant = string.Empty;

        if (!HttpMethods.IsGet(request.Method) || !request.Path.HasValue)
        {
            return false;
        }

        var segments = request.Path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // api / conversations / {threadId} / workspace / {grant} / {**path}. The trailing path may be empty
        // (the route itself allows it and the controller answers 404); the five leading segments may not.
        if (
            segments.Length < 5
            || !string.Equals(segments[0], "api", StringComparison.Ordinal)
            || !string.Equals(segments[1], "conversations", StringComparison.Ordinal)
            || !string.Equals(segments[3], "workspace", StringComparison.Ordinal)
        )
        {
            return false;
        }

        threadId = segments[2];
        grant = segments[4];
        return threadId.Length > 0 && grant.Length > 0;
    }

    private PrincipalResolution? Resolve(HttpContext context)
    {
        if (!_options.Value.Enforce)
        {
            return null;
        }

        if (!TryReadRawWorkspaceRoute(context.Request, out var threadId, out var grant))
        {
            return null;
        }

        var opened = _grants.Open(WorkspaceGrantService.PresentedToken(context.Request, grant), threadId);

        switch (opened.Failure)
        {
            case WorkspaceGrantFailure.None when opened.Principal is { } principal:
                return PrincipalResolution.Success(principal);

            case WorkspaceGrantFailure.None:
                return Reject(AnonymousGrantCode, StatusCodes.Status401Unauthorized, threadId, context);

            case WorkspaceGrantFailure.ThreadMismatch:
                // 403, not 401: the grant IS genuine, so re-minting the same one changes nothing. This is
                // the "grant for conversation A replayed against conversation B" case.
                return Reject(ThreadMismatchCode, StatusCodes.Status403Forbidden, threadId, context);

            // PrincipalMismatch cannot arrive here — Open compares nobody, which is the whole point of it
            // being separate from Validate: this door is where the principal COMES FROM. It is listed so
            // the set stays exhaustive if that ever changes, and refuses rather than falling through.
            case WorkspaceGrantFailure.PrincipalMismatch:
            case WorkspaceGrantFailure.Invalid:
            default:
                return Reject(InvalidGrantCode, StatusCodes.Status401Unauthorized, threadId, context);
        }
    }

    private PrincipalResolution Reject(string code, int statusCode, string threadId, HttpContext context)
    {
        // threadId is an identifier and safe to log; the grant is a credential and is never logged, here or
        // in the request log (Program.cs redacts it out of the request path).
        _logger.LogWarning(
            "A raw workspace request for thread {ThreadId} presented a grant that was refused: {Reason}.",
            threadId,
            code
        );

        _auditSink.Write(
            new AuthenticationAuditRecord
            {
                FrontDoor = AuditFrontDoor.Embed,
                ClaimedEntraTenantId = null,
                ClaimedObjectId = null,
                ClaimedUpn = null,
                AppId = null,
                ResolvedTenantId = null,
                Jti = null,
                Outcome = AuthenticationOutcome.Rejected,
                Reason = code,
                CorrelationId = context.TraceIdentifier,
                EventClass = AuditEventClass.Security,
            }
        );

        return PrincipalResolution.Reject(code, statusCode);
    }
}
