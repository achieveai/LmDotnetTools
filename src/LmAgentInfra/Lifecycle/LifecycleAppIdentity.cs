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
/// This claim type is not in any inbound claim map and no authentication handler mints it, so no
/// token can carry it in. Only a host that has already established the caller is an app stamps it,
/// which makes "this principal names an app" true by construction rather than true by enumerating
/// which principals happen not to carry a name identifier. Read it and nothing else: a fallback to a
/// human-satisfiable claim reopens the hole in full.
/// </para>
/// </remarks>
public static class LifecycleAppIdentity
{
    /// <summary>
    /// The claim carrying the caller's app id. Deliberately namespaced and deliberately not one of
    /// the <c>ClaimTypes.*</c> URIs, which inbound claim mapping populates from ordinary tokens.
    /// </summary>
    public const string AppIdClaimType = "lm_lifecycle_app_id";
}
