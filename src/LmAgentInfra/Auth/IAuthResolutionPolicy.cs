namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Decides what the auth webhook does when a provider has <b>no immediately-available token</b>.
/// The webhook always tries <see cref="IOAuthTokenProvider.GetAccessTokenAsync"/> first; only the
/// "not signed in / refresh failed" case is handed to this policy, which returns a token (allow) or
/// <c>null</c> (deny). This is the seam that lets the two hosts diverge without forking the
/// controller:
/// <list type="bullet">
/// <item><b>Attended chat app</b> (<c>LmStreaming.Sample</c>): hold the call open and prompt a
/// connected human to sign in — see the deferred-interactive policy backed by
/// <see cref="PendingAuthCoordinator"/>.</item>
/// <item><b>Unattended daemon</b> (<c>CodeReviewDaemon.Sample</c>): there is no human in the loop, so
/// fail fast — raise an operator "auth required" signal and deny immediately.</item>
/// </list>
/// </summary>
public interface IAuthResolutionPolicy
{
    /// <summary>
    /// Resolves a credential for <paramref name="provider"/> after a direct token fetch already
    /// failed with "not signed in". Returns the token to inject (allow), or <c>null</c> to deny.
    /// Throws <see cref="OperationCanceledException"/> only when <paramref name="cancellationToken"/>
    /// fires (the gateway gave up); all other failures must resolve to <c>null</c> so the webhook can
    /// honor its always-200 allow/deny contract.
    /// </summary>
    Task<OAuthAccessToken?> ResolveAsync(
        IOAuthTokenProvider provider,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken);
}
