using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// Establishes the request's <see cref="Principal"/> after authentication has run, and answers the
/// refusals that authentication deliberately did not answer itself.
/// </summary>
/// <remarks>
/// <para>
/// The order matters and is the reason this is a middleware rather than a filter. The JWT bearer
/// handler validates the token's signature and stashes a <see cref="PrincipalResolution"/> on
/// <see cref="HttpContext.Items"/>; it never fails the authentication for a tenant refusal, because
/// <c>context.Fail()</c> produces a <c>401</c> challenge and a <c>401</c> is what makes a browser
/// client sign in again. Signing in again cannot conjure a provisioned tenant, so that would be an
/// infinite loop. This middleware reads the stashed outcome and writes the <c>403</c> directly.
/// </para>
/// <para>
/// It runs for <c>/api</c> routes and for the WebSocket transports at <c>/ws</c> (#342). Static
/// files and the SPA's own index have no principal to establish and must stay reachable while
/// signed out - in particular the rejection screen itself is served by the SPA, so locking the SPA
/// behind the principal would hide the very page that explains the refusal.
/// </para>
/// <para>
/// The WebSocket transports are inside the boundary even though they sit outside the <c>/api</c>
/// prefix. Gating only the prefix left a fully functional unauthenticated channel open beside the
/// gated REST surface, which is what #342 reported. They reach it because
/// <c>UseSampleIdentity</c> is registered ahead of the <c>/ws</c> endpoints in
/// <c>Program.cs</c>, so a middleware guard genuinely covers them.
/// </para>
/// </remarks>
public sealed class IdentityMiddleware
{
    /// <summary>Routes below this prefix are the ones that carry a principal.</summary>
    public const string ApiPathPrefix = "/api";

    /// <summary>
    /// The WebSocket transports (<c>/ws</c> and <c>/ws/subagent</c>), which sit OUTSIDE
    /// <see cref="ApiPathPrefix"/> and are inside the identity boundary all the same (#342).
    /// </summary>
    public const string WebSocketPathPrefix = "/ws";

    /// <summary>
    /// Prefix of the <c>Sec-WebSocket-Protocol</c> token that carries the caller's bearer
    /// credential, e.g. <c>lm.bearer.eyJhbGci...</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The browser WebSocket API admits no custom headers, so the bearer token <c>apiFetch</c>
    /// attaches to every <c>/api</c> call cannot be attached to the <c>/ws</c> handshake. The
    /// subprotocol list is the one request header a page DOES choose, so the credential travels
    /// there. Deliberately not the query string: a URL is written to proxy logs, to browser history
    /// and into <c>Referer</c>, and a token is exactly the kind of value that must never sit in one.
    /// </para>
    /// <para>
    /// The token is lifted into <c>Authorization</c> before <c>UseAuthentication</c> runs
    /// (<see cref="PromoteWebSocketCredential"/>), so <c>/ws</c> resolves its principal through the
    /// SAME front doors as the REST surface - the JWT bearer handler and the
    /// <see cref="IRequestPrincipalSource"/> chain - rather than through a second, parallel
    /// validator that could drift from the first.
    /// </para>
    /// </remarks>
    public const string WebSocketCredentialSubProtocolPrefix = "lm.bearer.";

    /// <summary>
    /// The application subprotocol a client offers alongside its credential, and the only one this
    /// host ever echoes. RFC 6455 requires the selected subprotocol to be one the client offered;
    /// selecting the credential token instead would write the caller's own bearer token back out in
    /// a response header.
    /// </summary>
    public const string WebSocketSubProtocol = "lm.chat.v1";

    /// <summary>
    /// Refusal code for a WebSocket handshake that establishes no principal.
    /// </summary>
    /// <remarks>
    /// Answered with <c>403</c>, never <c>401</c>. A <c>401</c> is the one status a browser answers
    /// by re-authenticating, and re-authenticating cannot attach a credential to a handshake that
    /// carried none - so the client would loop (#341).
    /// </remarks>
    public const string WebSocketRefusalCode = "websocket_authentication_required";

    /// <summary>
    /// Response header carrying the same stable refusal code as the body, so a client can classify
    /// a refusal without consuming the response body.
    /// </summary>
    public const string RefusalCodeHeader = "X-Identity-Refusal";

    /// <summary>
    /// Endpoints under <see cref="ApiPathPrefix"/> that stay reachable while signed out. The
    /// identity config is here because the SPA must read it BEFORE it can sign in - gating it on
    /// being signed in is a deadlock. The tenant admin surface is here because it authenticates
    /// with the operator secret instead of a user token.
    /// </summary>
    /// <remarks>
    /// Every entry must name a route this host actually maps. <c>/api/health</c> was listed here and
    /// matched nothing: the sample maps no health endpoint, so the exemption granted nothing that
    /// could be observed (#350). That is what made it worth removing rather than leaving:
    /// <see cref="IsGuardedApiPath"/> matches an entry with <c>StartsWithSegments</c>, so a reserved
    /// prefix covers the whole subtree beneath it the moment a route lands there - and it would land
    /// there anonymous, silently, with no edit to this list to review. Add the endpoint first if one
    /// is wanted, then the exemption.
    /// </remarks>
    private static readonly string[] AnonymousApiPaths =
    [
        "/api/identity/config",
        "/api/admin/tenants",
    ];

    /// <summary>
    /// Endpoints under <see cref="ApiPathPrefix"/> that sit OUTSIDE the identity boundary
    /// altogether (#345).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberate decision, not an oversight, and a different one from
    /// <see cref="AnonymousApiPaths"/>. Those are user-facing routes that must stay reachable while
    /// signed out. This one is an infrastructure callback that has no user and no tenant to
    /// resolve, and — the load-bearing half — <b>cannot produce a <see cref="Principal"/> at all</b>,
    /// so guarding it would refuse the only caller it has and grant nothing.
    /// </para>
    /// <para>
    /// The webhook is the whole list, and it is the sharpest case: its <c>Authorization</c> header
    /// carries a per-session secret, not a JWT. The bearer handler tries to parse it, fails, stashes
    /// nothing, and the guard would then refuse the caller for presenting the credential its own
    /// endpoint requires. No front door in <see cref="IRequestPrincipalSource"/> recognises a
    /// session secret, so there is no principal to be had here at any price.
    /// </para>
    /// <para>
    /// <b><c>/api/lifecycle</c> used to be here and is deliberately NOT any more (#402).</b> The
    /// entry rested on two claims, and the second was false. The plane is indeed config-gated off by
    /// default — but it is <i>not</i> "gated behind its own signature check":
    /// <c>LifecycleApprovalController</c>'s own remarks state that it "does not authenticate", that
    /// it reads <c>HttpContext.User</c> established by whatever the host wired in front of it, and
    /// that "no subscriber-to-host signing convention exists, so nothing a caller sends to this
    /// endpoint carries a signature for anyone to check". The plane's only signing is OUTBOUND, in
    /// <c>HttpLifecycleDeliverySender</c>. So the carve-out bought no authority it did not already
    /// have, and cost the one thing it did: with the routes outside the boundary, a suspended or
    /// not-provisioned tenant's still-valid token was never answered here, reached those controllers,
    /// and satisfied their <c>AuthenticatedAppId()</c> — which reads the raw <c>ClaimsPrincipal</c>.
    /// <c>Identity:Enforce</c> gated the REST front door and silently did not gate this one.
    /// </para>
    /// <para>
    /// Unlike the webhook, lifecycle HAS a front door that can speak for it:
    /// <c>ServiceCallerPrincipalSource</c> turns the inbound S2S secret plus an
    /// <c>X-Sbx-App-Id</c> registration into a tenant-bearing <c>AppOnly</c> principal. Guarding these
    /// routes therefore refuses no caller that is onboarded under <c>Identity:Apps</c>; it only requires
    /// that they be onboarded, which is what enforcement means everywhere else.
    /// </para>
    /// <para>
    /// <b>And the caller now actually arrives.</b> Admitting a caller at the boundary was for a while
    /// not the same as the plane authorizing it: the principal was published on
    /// <c>HttpContext.Items</c> (<see cref="IdentityHttpItems.PrincipalKey"/>) alone, both lifecycle
    /// controllers' <c>AuthenticatedAppId()</c> reads <c>HttpContext.User</c>, and nothing bridged the
    /// two — the only registered scheme is JWT bearer, which the S2S headers do not trigger. A caller
    /// presenting only those headers was therefore refused by the controllers, exactly as it had been
    /// before #402 when these routes were exempt. #424 closed that: <see cref="BridgeToHttpUser"/>
    /// publishes an app-bearing principal on <c>User</c> as well, so a caller onboarded under
    /// <c>Identity:Apps</c> reaches the plane's actions and is answered as their own owner. Pinned
    /// end-to-end against the real host by
    /// <c>IdentityBoundaryPipelineTests.WithEnforcementOn_ARegisteredServiceCaller_ReachesTheLifecycleControlPlane</c>.
    /// Recorded in <c>docs/specs/P1-identity-authorization.md</c> §4.5.
    /// </para>
    /// <para>
    /// <c>/api/auth/egress-keys</c> is deliberately NOT here. It looks like an infrastructure route
    /// but is a SPA management surface: the browser calls it through <c>apiFetch</c>, which attaches
    /// the bearer token under enforcement exactly as it does for <c>/api/workspaces</c> and
    /// <c>/api/providers</c>. Its controller carries no credential of its own - it is loopback-gated
    /// only (<c>EgressKeysController.RejectNonLoopback</c>) - so carving it out would let a
    /// credential-less loopback caller plant, read and destroy egress keys under
    /// <c>Identity:Enforce</c>. It stays inside the boundary, guarded like every other management
    /// route, with the loopback check remaining as defence in depth.
    /// </para>
    /// <para>
    /// This list is a security boundary, so it is asserted rather than trusted: a test enumerates
    /// the host's real endpoint table and requires every <c>/api</c> route to be either guarded or
    /// named here. Adding a route cannot silently land it outside the boundary.
    /// </para>
    /// </remarks>
    private static readonly string[] InfrastructureApiPaths =
    [
        "/api/auth/webhook",
    ];

    /// <summary>
    /// Every <c>/api</c> prefix this middleware lets past without establishing a principal. Public
    /// so a route-coverage test can assert the partition rather than restate it.
    /// </summary>
    public static IReadOnlyList<string> UnguardedApiPaths { get; } =
        [.. AnonymousApiPaths, .. InfrastructureApiPaths];

    private readonly RequestDelegate _next;
    private readonly IOptions<IdentityOptions> _options;
    private readonly PrincipalFactory _principalFactory;
    private readonly IRequestPrincipalSource[] _principalSources;
    private readonly ILogger<IdentityMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">Next component in the pipeline.</param>
    /// <param name="options">Identity configuration.</param>
    /// <param name="principalFactory">Builds the development principal when enforcement is off.</param>
    /// <param name="principalSources">
    /// Front doors consulted, in registration order, when the bearer handler stashed nothing.
    /// </param>
    /// <param name="logger">Diagnostics.</param>
    public IdentityMiddleware(
        RequestDelegate next,
        IOptions<IdentityOptions> options,
        PrincipalFactory principalFactory,
        IEnumerable<IRequestPrincipalSource> principalSources,
        ILogger<IdentityMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(principalSources);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _options = options;
        _principalFactory = principalFactory;
        _principalSources = [.. principalSources];
        _logger = logger;
    }

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsGuardedPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A CORS preflight carries no Authorization header - browsers never attach one, by
        // specification - so guarding it can only ever produce a 401 (#346). The browser then
        // abandons the real request without sending it, and every cross-origin call fails before
        // identity has had anything to say about it. A preflight reads nothing and reaches no
        // endpoint: the CORS middleware answers it and short-circuits, and if CORS is not
        // configured the router answers 405. Neither outcome discloses anything.
        if (IsCorsPreflight(context.Request))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var resolution = await ResolveAsync(context).ConfigureAwait(false);

        // A refusal - whether recorded during token validation or returned by a later front door -
        // is answered here, whether or not enforcement is on. A caller who presented a token from
        // an unprovisioned tenant gets a straight answer rather than being silently downgraded to
        // the development principal, which would make the refusal invisible in exactly the
        // deployment where an operator is testing the rollout.
        if (resolution is { IsRejected: true } rejection)
        {
            await WriteRefusalAsync(context, rejection.StatusCode, rejection.Code!).ConfigureAwait(false);
            return;
        }

        var principal = resolution?.Principal;

        if (principal is null)
        {
            if (!_options.Value.Enforce)
            {
                // Unreachable in practice - with enforcement off ResolvePrincipal always yields the
                // development principal. Refusing rather than continuing means a future change that
                // breaks that invariant fails closed instead of running a guarded route with no
                // principal at all.
                _logger.LogError(
                    "No principal could be established for {Path} even though Identity:Enforce is false.",
                    context.Request.Path.Value);
            }

            // A WebSocket handshake is refused with 403, not 401 (#342). 401 is the one status a
            // browser answers by re-authenticating, and re-authenticating cannot attach a credential
            // to a handshake that carried none, so a 401 here is an infinite loop rather than a
            // refusal. The REST surface keeps its 401 because there re-authenticating IS the fix.
            var isWebSocket = IsGuardedWebSocketPath(context.Request.Path);

            await WriteRefusalAsync(
                context,
                isWebSocket ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
                isWebSocket ? WebSocketRefusalCode : "authentication_required").ConfigureAwait(false);
            return;
        }

        context.Items[IdentityHttpItems.PrincipalKey] = principal;
        BridgeToHttpUser(context, principal);
        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes an app-bearing principal on <see cref="HttpContext.User"/> as well as on
    /// <c>Items</c>, so a controller that reads the claims principal sees the identity this middleware
    /// established (#424).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lifecycle control plane's two controllers live in <c>LmAgentInfra</c>, cannot reference this
    /// sample's <see cref="Principal"/>, and authenticate off <c>HttpContext.User</c>. Nothing bridged
    /// the two, so a registered <c>Identity:Apps</c> caller presenting the daemon's S2S headers passed
    /// the boundary here and was then refused by those controllers - the only registered scheme is JWT
    /// bearer, which those headers do not trigger. This is that bridge.
    /// </para>
    /// <para>
    /// <b>An already-authenticated request is never overwritten.</b> When the JWT bearer handler
    /// validated a token it has already populated <c>User</c> with the token's own claims; replacing
    /// them with a reconstruction would narrow a real identity to the three claims this bridge knows
    /// about, and would do it silently. This guard is defensive rather than reachable today:
    /// <see cref="ResolveAsync"/> returns the stashed interactive resolution before any other front
    /// door runs, and every interactive principal carries <c>AppId = null</c>
    /// (see <see cref="PrincipalFactory.ToClaimsPrincipalOrNull"/>), so no live request arrives here
    /// already authenticated by a principal that also has an app id to displace.
    /// </para>
    /// <para>
    /// What is bridged is decided in one place -
    /// <see cref="PrincipalFactory.ToClaimsPrincipalOrNull"/> - and is narrow on purpose: only a
    /// principal that names an app. The development principal carries no app id, so with
    /// <c>Identity:Enforce</c> off nothing here changes what an anonymous request looks like to a
    /// controller reading <c>User</c>.
    /// </para>
    /// <para>
    /// <b>Endpoint-visible only, not policy-visible.</b> <c>UseSampleIdentity</c>
    /// (<c>IdentityServiceCollectionExtensions.cs</c>) calls <c>UseAuthorization</c> - and the
    /// auto-inserted <c>UseRouting</c> ahead of it - before <c>UseMiddleware&lt;IdentityMiddleware&gt;</c>,
    /// so this bridge runs strictly AFTER authorization policy evaluation for the current request, not
    /// before it. A controller action that reads <c>HttpContext.User</c> itself sees the bridged
    /// principal; the authorization middleware that decided whether the action could run at all never
    /// does. Adding <c>[Authorize]</c> or a <c>FallbackPolicy</c> anywhere on this surface would 401 a
    /// legitimate service-to-service caller this bridge exists to authenticate, because the bridge has
    /// not run yet at the point the policy is evaluated.
    /// </para>
    /// </remarks>
    private static void BridgeToHttpUser(HttpContext context, Principal principal)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return;
        }

        if (PrincipalFactory.ToClaimsPrincipalOrNull(principal) is { } claimsPrincipal)
        {
            context.User = claimsPrincipal;
        }
    }

    /// <summary>
    /// Runs the front doors in order and returns the first that recognised this request, or null
    /// when none did and enforcement leaves nothing to fall back on.
    /// </summary>
    /// <remarks>
    /// The interactive door is first because it is the only one that already ran: the JWT bearer
    /// handler validated the token during <c>UseAuthentication</c> and stashed its outcome, success
    /// or refusal. Everything else is consulted here, INSIDE the middleware, for the reason
    /// <see cref="IRequestPrincipalSource"/> exists - a filter runs after this component has
    /// already written the response, so it can never be the thing that stops it writing one.
    /// </remarks>
    private async ValueTask<PrincipalResolution?> ResolveAsync(HttpContext context)
    {
        if (context.Items.TryGetValue(IdentityHttpItems.ResolutionKey, out var stashed)
            && stashed is PrincipalResolution interactive)
        {
            return interactive;
        }

        foreach (var source in _principalSources)
        {
            var resolved = await source
                .ResolveAsync(context, context.RequestAborted)
                .ConfigureAwait(false);

            if (resolved is not null)
            {
                return resolved;
            }
        }

        // No token, no service credential, or no bearer handler configured at all. With enforcement
        // off this is the ordinary development path and every existing call site keeps working
        // unchanged.
        return _options.Value.Enforce
            ? null
            : PrincipalResolution.Success(_principalFactory.CreateDevelopmentPrincipal());
    }

    /// <summary>
    /// Writes the refusal body. Deliberately hand-written rather than routed through
    /// <c>context.ChallengeAsync</c>: a challenge is what emits the <c>WWW-Authenticate</c> header
    /// (and, in a cookie-based setup, a redirect to the identity provider) that would restart
    /// sign-in. The absence of any challenge here is the mechanism that makes "the rejection does
    /// not redirect to Entra" true.
    /// </summary>
    private static async Task WriteRefusalAsync(HttpContext context, int statusCode, string code)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        // The same code the body carries, repeated in a header. The SPA routes every /api call
        // through one helper, and that helper must recognise a refusal without reading the body -
        // the body belongs to whichever caller made the request, and consuming it there would hand
        // that caller an already-read stream. A header is readable without taking anything away.
        context.Response.Headers[RefusalCodeHeader] = code;

        // Same body shape the S2S guard already answers with: a lowercase `error` label plus a
        // stable machine-readable `code`. The SPA chooses between "not signed in", "organisation
        // not set up" and "organisation suspended" from `code` alone.
        //
        // Switched on the status rather than on "is it a 401", which labelled everything else
        // "forbidden" - including the 503 PrincipalFactory returns when the tenant DIRECTORY is
        // unreadable (PrincipalResolution.IdentityUnavailable). That refusal says nothing about the
        // caller's authorization, and describing an outage as one points a client at the one remedy
        // that cannot work: 503 means retry the same credential later, "forbidden" means do not.
        // `unavailable` is the label the sample's other 503 already uses
        // (OperatorSecretAuthAttribute), so this adds no new vocabulary.
        //
        // The default is reached by no status this middleware emits today. Enumerated over THIS
        // repository's principal sources: PrincipalFactory answers 401/503/403/403,
        // ServiceCallerPrincipalSource 403/403, and InvokeAsync writes 401 and 403 itself - so 401,
        // 403 and 503 are the only three here.
        //
        // That is a fact about this repository, not a bound the type enforces.
        // PrincipalResolution.Reject validates the CODE and accepts any status int, and
        // IRequestPrincipalSource is a documented public seam a host outside this repository
        // implements. So a fourth status can arrive without anyone editing this file. The default
        // exists so that when one does, it cannot silently borrow a label that misdescribes it.
        var error = statusCode switch
        {
            StatusCodes.Status401Unauthorized => "unauthorized",
            StatusCodes.Status403Forbidden => "forbidden",
            StatusCodes.Status503ServiceUnavailable => "unavailable",
            _ => "error",
        };

        var body = new { error, code };

        await context.Response
            .WriteAsync(JsonSerializer.Serialize(body))
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is inside the identity boundary.
    /// </summary>
    /// <remarks>
    /// Public so a route-coverage test can ask the real predicate rather than restate the rule. A
    /// test that restates it agrees with itself by construction and would keep passing through the
    /// exact edit it exists to catch.
    /// </remarks>
    /// <param name="path">Request path.</param>
    public static bool IsGuardedApiPath(PathString path)
    {
        if (!path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var unguarded in UnguardedApiPaths)
        {
            if (path.StartsWithSegments(unguarded, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is one of the WebSocket transports (#342).
    /// </summary>
    /// <remarks>
    /// Segment-based, so it matches <c>/ws</c> and <c>/ws/subagent</c> and does not match a route
    /// that merely starts with the same letters. Public for the same reason
    /// <see cref="IsGuardedApiPath"/> is: a route-coverage test asks the real predicate rather than
    /// restating it.
    /// </remarks>
    /// <param name="path">Request path.</param>
    public static bool IsGuardedWebSocketPath(PathString path) =>
        path.StartsWithSegments(WebSocketPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether <paramref name="path"/> is inside the identity boundary at all - the <c>/api</c>
    /// surface, or the WebSocket transports beside it.
    /// </summary>
    /// <param name="path">Request path.</param>
    public static bool IsGuardedPath(PathString path) =>
        IsGuardedApiPath(path) || IsGuardedWebSocketPath(path);

    /// <summary>
    /// Lifts a handshake credential out of <c>Sec-WebSocket-Protocol</c> into <c>Authorization</c>
    /// and drops it from the offered list, so it is never echoed back and never reaches a log.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Must run BEFORE <c>UseAuthentication</c>, which is where it is wired
    /// (<c>IdentityServiceCollectionExtensions.UseSampleIdentity</c>). Everything downstream then
    /// sees an ordinary bearer request and needs no WebSocket-specific knowledge at all.
    /// </para>
    /// <para>
    /// An <c>Authorization</c> header already on the request WINS. A caller that can set headers has
    /// presented its credential the ordinary way, and letting a subprotocol token displace it would
    /// let the most client-controllable field on the handshake overwrite the one the caller actually
    /// authenticated with.
    /// </para>
    /// <para>
    /// That precedence defers the PROMOTION only. Stripping happens either way: whether a token is
    /// honoured and whether it travels onward - into request logs, diagnostic dumps, and the accept's
    /// subprotocol echo - are unrelated questions, and answering both with one early return left the
    /// token in the offered list for the whole pipeline whenever the header was already present.
    /// </para>
    /// </remarks>
    /// <param name="request">The inbound request.</param>
    /// <returns>
    /// True when a credential was promoted into <c>Authorization</c>. False when there was none to
    /// promote, when the path is not a transport, or when an existing header took precedence - in
    /// which case the credential has still been stripped from the offered list.
    /// </returns>
    public static bool PromoteWebSocketCredential(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsGuardedWebSocketPath(request.Path))
        {
            return false;
        }

        string? credential = null;
        var stripped = 0;
        var kept = new List<string>();
        foreach (var value in request.Headers[HeaderNames.SecWebSocketProtocol])
        {
            foreach (var token in (value ?? string.Empty).Split(','))
            {
                var candidate = token.Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }

                // Deliberately NOT gated on "credential is null". Which token gets PROMOTED and which
                // tokens get STRIPPED are separate decisions: at most one credential is ever honoured,
                // but every credential-shaped entry leaves the request. Fusing them let a handshake
                // offering two lm.bearer.* entries keep the second one - it fell straight through to
                // the keep list and was written back into the header.
                if (candidate.StartsWith(WebSocketCredentialSubProtocolPrefix, StringComparison.Ordinal))
                {
                    // Strip on the prefix alone; promote only what actually has a token behind it. A
                    // bare "lm.bearer." carries nothing to honour, but it is still credential-shaped
                    // and has no business travelling on into logs or the accept's echo.
                    stripped++;
                    if (candidate.Length > WebSocketCredentialSubProtocolPrefix.Length)
                    {
                        credential ??= candidate[WebSocketCredentialSubProtocolPrefix.Length..];
                    }

                    continue;
                }

                kept.Add(candidate);
            }
        }

        // Strip BEFORE either early return below, never after: the whole point is that a
        // credential-shaped entry leaves the request regardless of whether anything ends up using it.
        // Gating this on "we found something to promote" is the same fusion of two decisions that let
        // a second lm.bearer.* survive above - and it let a BARE "lm.bearer." survive here, because
        // that sets no credential yet is excluded from the keep list, so returning early left the
        // original header in place untouched.
        if (stripped > 0)
        {
            if (kept.Count == 0)
            {
                _ = request.Headers.Remove(HeaderNames.SecWebSocketProtocol);
            }
            else
            {
                request.Headers[HeaderNames.SecWebSocketProtocol] = string.Join(", ", kept);
            }
        }

        if (credential is null)
        {
            return false;
        }

        if (request.Headers.ContainsKey(HeaderNames.Authorization))
        {
            return false;
        }

        request.Headers.Authorization = $"Bearer {credential}";
        return true;
    }

    /// <summary>
    /// The subprotocol to echo when accepting the handshake, or null when the client offered none of
    /// ours - in which case the accept must select nothing.
    /// </summary>
    /// <param name="request">
    /// The inbound request, read AFTER <see cref="PromoteWebSocketCredential"/> has removed the
    /// credential token.
    /// </param>
    public static string? NegotiateWebSocketSubProtocol(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        foreach (var value in request.Headers[HeaderNames.SecWebSocketProtocol])
        {
            foreach (var token in (value ?? string.Empty).Split(','))
            {
                if (string.Equals(token.Trim(), WebSocketSubProtocol, StringComparison.Ordinal))
                {
                    return WebSocketSubProtocol;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// True for a CORS preflight: an <c>OPTIONS</c> request carrying
    /// <c>Access-Control-Request-Method</c>.
    /// </summary>
    /// <remarks>
    /// Both conditions, never <c>OPTIONS</c> alone. A bare <c>OPTIONS</c> is an ordinary request
    /// that a route may answer with real content, and letting every one of them past would widen
    /// the unguarded surface by a whole HTTP method. The header is what makes it a preflight, and
    /// the browser attaches it on every preflight by specification.
    /// </remarks>
    private static bool IsCorsPreflight(HttpRequest request) =>
        HttpMethods.IsOptions(request.Method)
        && request.Headers.ContainsKey(HeaderNames.AccessControlRequestMethod);
}
