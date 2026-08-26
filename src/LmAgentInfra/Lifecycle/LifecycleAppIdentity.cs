namespace AchieveAi.LmDotnetTools.LmAgentInfra.Lifecycle;

/// <summary>
/// The one claim the lifecycle control plane accepts as naming the calling <em>app</em>.
/// </summary>
/// <remarks>
/// <para>
/// The lifecycle controllers do not authenticate; they read <c>HttpContext.User</c> and turn it into
/// an owner key. They used to derive that key from <c>ClaimTypes.NameIdentifier</c>, falling back to
/// <c>Identity.Name</c> — claims a signed-in <em>human</em> satisfies, because a JWT bearer handler
/// with <c>MapInboundClaims</c> on maps the token's <c>sub</c> onto exactly that claim type. So any
/// signed-in user could register a lifecycle callback and be handed a signing secret, acting as an
/// "app" whose id was their own subject id (#433).
/// </para>
/// <para>
/// The guarantee is the single stamping site, not a property of tokens. A claim's contents are
/// whatever the handler reading a token is configured to map, so a handler told to map a claim of
/// this name would put it on the principal. What makes it safe is that a host stamps it in exactly
/// one place, downstream of having already established the caller is an app, and configures no
/// inbound mapping for it — so nothing a caller presents populates it. That makes "this principal
/// names an app" true by construction rather than true by enumerating which principals happen not to
/// carry a name identifier. Read it and nothing else: a fallback to a human-satisfiable claim reopens
/// the hole in full.
/// </para>
/// </remarks>
public static class LifecycleAppIdentity
{
    /// <summary>
    /// The claim carrying the caller's app id. Deliberately namespaced and deliberately not one of
    /// the <c>ClaimTypes.*</c> URIs, which inbound claim mapping populates from ordinary tokens.
    /// </summary>
    public const string AppIdClaimType = "lm_lifecycle_app_id";

    /// <summary>Why a principal did not yield an app id.</summary>
    public enum AppIdRefusal
    {
        /// <summary>An app id was found; nothing was refused.</summary>
        None = 0,

        /// <summary>No principal, or one that is not authenticated.</summary>
        Unauthenticated,

        /// <summary>
        /// An authenticated principal that carries no <see cref="AppIdClaimType"/> claim — a signed-in
        /// human, or an app whose host never stamped the claim.
        /// </summary>
        NotAnApp,
    }

    /// <summary>
    /// Reads the caller's app id off <paramref name="user"/>, reporting WHY when there is none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two refusals are kept apart because they send an operator to opposite places, and a single
    /// message for both sent them to the wrong one. "Not authenticated" describes a host with no
    /// authentication scheme wired; the far more likely case in a working deployment is the second —
    /// authentication succeeded and the principal simply is not an app, either because a person signed
    /// in or because a host that populates <c>HttpContext.User</c> itself never stamped the claim.
    /// Telling that operator their caller is unauthenticated points them at the one part of the
    /// pipeline that is demonstrably working.
    /// </para>
    /// <para>
    /// Shared by both lifecycle controllers rather than copied into each. They enforce one rule, and
    /// two copies of it are two things that can drift — the claim read, the whitespace handling, and
    /// the refusal each surfaces all have to stay identical for the rule to mean anything.
    /// </para>
    /// <para>
    /// Nothing here is an authorization decision: it says who the caller claims to be, not what they
    /// may do. Resolving that id to an owner is still the caller's next gate.
    /// </para>
    /// </remarks>
    /// <param name="user">The principal on the request, if any.</param>
    /// <returns>The caller's app id, or the reason there is none.</returns>
    public static AppIdResolution ResolveAppId(System.Security.Claims.ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return new AppIdResolution(null, AppIdRefusal.Unauthenticated);
        }

        var claimed = user.FindFirst(AppIdClaimType)?.Value;
        return string.IsNullOrWhiteSpace(claimed)
            ? new AppIdResolution(null, AppIdRefusal.NotAnApp)
            : new AppIdResolution(claimed, AppIdRefusal.None);
    }

    /// <summary>
    /// What <see cref="ResolveAppId"/> found: an app id, or the reason there was none.
    /// </summary>
    /// <param name="AppId">
    /// The caller's app id, or null. Non-null exactly when <paramref name="Refusal"/> is
    /// <see cref="AppIdRefusal.None"/>, which is what lets a caller test the id and get the reason
    /// narrowed for free.
    /// </param>
    /// <param name="Refusal">Why <paramref name="AppId"/> is null, or <see cref="AppIdRefusal.None"/>.</param>
    public readonly record struct AppIdResolution(string? AppId, AppIdRefusal Refusal);
}
