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
/// files, the SPA's own index and the health endpoint have no principal to establish and must
/// stay reachable while signed out - in particular the rejection screen itself is served by the
/// SPA, so locking the SPA behind the principal would hide the very page that explains the
/// refusal.
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
    /// with the operator secret instead of a user token, and health because a liveness probe has no
    /// user.
    /// </summary>
    private static readonly string[] AnonymousApiPaths =
    [
        "/api/identity/config",
        "/api/admin/tenants",
        "/api/health",
    ];

    /// <summary>
    /// Endpoints under <see cref="ApiPathPrefix"/> that sit OUTSIDE the identity boundary
    /// altogether (#345).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A deliberate decision, not an oversight, and a different one from
    /// <see cref="AnonymousApiPaths"/>. Those are user-facing routes that must stay reachable while
    /// signed out. These are infrastructure callbacks that have no user and no tenant to resolve:
    /// the sandbox gateway's deferred-auth webhook presents a per-session secret, and the lifecycle
    /// control plane is a service-to-service surface gated behind its own signature check (and off
    /// by default). Neither can produce a <see cref="Principal"/>, so guarding them would refuse
    /// every legitimate caller and grant nothing.
    /// </para>
    /// <para>
    /// The webhook is the sharpest case: its <c>Authorization</c> header carries a session secret,
    /// not a JWT. The bearer handler tries to parse it, fails, stashes nothing, and the guard would
    /// then refuse the caller for presenting the credential its own endpoint requires.
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
        "/api/lifecycle",
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
        await _next(context).ConfigureAwait(false);
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
        var body = statusCode == StatusCodes.Status401Unauthorized
            ? new { error = "unauthorized", code }
            : new { error = "forbidden", code };

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
    /// </remarks>
    /// <param name="request">The inbound request.</param>
    /// <returns>True when a credential was promoted.</returns>
    public static bool PromoteWebSocketCredential(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsGuardedWebSocketPath(request.Path)
            || request.Headers.ContainsKey(HeaderNames.Authorization))
        {
            return false;
        }

        string? credential = null;
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

                if (credential is null
                    && candidate.Length > WebSocketCredentialSubProtocolPrefix.Length
                    && candidate.StartsWith(
                        WebSocketCredentialSubProtocolPrefix,
                        StringComparison.Ordinal))
                {
                    credential = candidate[WebSocketCredentialSubProtocolPrefix.Length..];
                    continue;
                }

                kept.Add(candidate);
            }
        }

        if (credential is null)
        {
            return false;
        }

        request.Headers.Authorization = $"Bearer {credential}";

        if (kept.Count == 0)
        {
            _ = request.Headers.Remove(HeaderNames.SecWebSocketProtocol);
        }
        else
        {
            request.Headers[HeaderNames.SecWebSocketProtocol] = string.Join(", ", kept);
        }

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
