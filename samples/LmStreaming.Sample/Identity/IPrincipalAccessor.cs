using AchieveAi.LmDotnetTools.LmCore.Identity;

namespace LmStreaming.Sample.Identity;

/// <summary>
/// The request-scoped principal, populated once by <see cref="IdentityMiddleware"/> before any
/// controller action runs. Controllers read it once per action and pass the value on as an
/// ordinary parameter.
/// </summary>
/// <remarks>
/// Deliberately not an ambient <c>AsyncLocal</c>. A conversation's agent run is a background task
/// that OUTLIVES the HTTP request that started it - that is the whole point of
/// <c>POST /api/conversations/{threadId}/messages</c> returning while the agent keeps streaming.
/// An ambient read from inside that loop would be null, or worse, a different user's context if a
/// pooled thread was reused. Long-lived state captures the principal at creation instead, exactly
/// as <c>MultiTurnAgentPool.AgentEntry.CallerCredential</c> already does.
/// </remarks>
public interface IPrincipalAccessor
{
    /// <summary>
    /// The current request's principal, or null when there is none - which happens only while
    /// <c>Identity:Enforce</c> is true and the request is unauthenticated, or outside a request.
    /// </summary>
    Principal? Current { get; }
}

/// <summary>
/// <see cref="IPrincipalAccessor"/> over the ambient <see cref="IHttpContextAccessor"/>, reading
/// the value <see cref="IdentityMiddleware"/> stashed on the request.
/// </summary>
/// <remarks>
/// Reads from <c>HttpContext.Items</c> rather than holding its own field so that the value is
/// scoped to the request by the framework, not by DI lifetime - a scoped service resolved from the
/// root provider would otherwise silently share one instance across requests.
/// </remarks>
public sealed class HttpContextPrincipalAccessor : IPrincipalAccessor
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    /// <summary>Creates an accessor over the given HTTP context accessor.</summary>
    /// <param name="httpContextAccessor">Ambient HTTP context accessor.</param>
    public HttpContextPrincipalAccessor(IHttpContextAccessor httpContextAccessor)
    {
        ArgumentNullException.ThrowIfNull(httpContextAccessor);
        _httpContextAccessor = httpContextAccessor;
    }

    /// <inheritdoc />
    public Principal? Current =>
        _httpContextAccessor.HttpContext?.Items.TryGetValue(IdentityHttpItems.PrincipalKey, out var value) == true
            ? value as Principal
            : null;
}

/// <summary>
/// Keys under which the identity pipeline stashes per-request state on
/// <see cref="HttpContext.Items"/>.
/// </summary>
public static class IdentityHttpItems
{
    /// <summary>The resolved <see cref="Principal"/> for this request.</summary>
    public const string PrincipalKey = "LmStreaming.Identity.Principal";

    /// <summary>
    /// The <see cref="PrincipalResolution"/> produced while validating a bearer token. Written by
    /// the JWT bearer handler and read by <see cref="IdentityMiddleware"/>, which is what turns a
    /// rejection into a response.
    /// </summary>
    public const string ResolutionKey = "LmStreaming.Identity.Resolution";
}
