using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Identity;
using Microsoft.Extensions.Options;

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
/// It runs for <c>/api</c> routes only. Static files, the SPA's own index and the health endpoint
/// have no principal to establish and must stay reachable while signed out - in particular the
/// rejection screen itself is served by the SPA, so locking the SPA behind the principal would hide
/// the very page that explains the refusal.
/// </para>
/// </remarks>
public sealed class IdentityMiddleware
{
    /// <summary>Routes below this prefix are the ones that carry a principal.</summary>
    public const string ApiPathPrefix = "/api";

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

    private readonly RequestDelegate _next;
    private readonly IOptions<IdentityOptions> _options;
    private readonly PrincipalFactory _principalFactory;
    private readonly ILogger<IdentityMiddleware> _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">Next component in the pipeline.</param>
    /// <param name="options">Identity configuration.</param>
    /// <param name="principalFactory">Builds the development principal when enforcement is off.</param>
    /// <param name="logger">Diagnostics.</param>
    public IdentityMiddleware(
        RequestDelegate next,
        IOptions<IdentityOptions> options,
        PrincipalFactory principalFactory,
        ILogger<IdentityMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(principalFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _next = next;
        _options = options;
        _principalFactory = principalFactory;
        _logger = logger;
    }

    /// <summary>Runs the middleware.</summary>
    /// <param name="context">The current request.</param>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!IsGuardedApiPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A refusal recorded during token validation is answered here, whether or not enforcement
        // is on. A caller who presented a token from an unprovisioned tenant gets a straight answer
        // rather than being silently downgraded to the development principal, which would make the
        // refusal invisible in exactly the deployment where an operator is testing the rollout.
        if (context.Items.TryGetValue(IdentityHttpItems.ResolutionKey, out var stashed)
            && stashed is PrincipalResolution { IsRejected: true } rejection)
        {
            await WriteRefusalAsync(context, rejection.StatusCode, rejection.Code!).ConfigureAwait(false);
            return;
        }

        var principal = ResolvePrincipal(context);

        if (principal is null)
        {
            if (!_options.Value.Enforce)
            {
                // Unreachable in practice - with enforcement off ResolvePrincipal always yields the
                // development principal. Answering 401 rather than continuing means a future change
                // that breaks that invariant fails closed instead of running an /api route with no
                // principal at all.
                _logger.LogError(
                    "No principal could be established for {Path} even though Identity:Enforce is false.",
                    context.Request.Path.Value);
            }

            await WriteRefusalAsync(
                context,
                StatusCodes.Status401Unauthorized,
                "authentication_required").ConfigureAwait(false);
            return;
        }

        context.Items[IdentityHttpItems.PrincipalKey] = principal;
        await _next(context).ConfigureAwait(false);
    }

    private Principal? ResolvePrincipal(HttpContext context)
    {
        if (context.Items.TryGetValue(IdentityHttpItems.ResolutionKey, out var stashed)
            && stashed is PrincipalResolution { Principal: { } resolved })
        {
            return resolved;
        }

        // No token, or no bearer handler configured at all. With enforcement off this is the
        // ordinary development path and every existing call site keeps working unchanged.
        return _options.Value.Enforce ? null : _principalFactory.CreateDevelopmentPrincipal();
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

    private static bool IsGuardedApiPath(PathString path)
    {
        if (!path.StartsWithSegments(ApiPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var anonymous in AnonymousApiPaths)
        {
            if (path.StartsWithSegments(anonymous, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
