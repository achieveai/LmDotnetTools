namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// The attended-app auth policy: holds the webhook call open while connected chat clients are
/// prompted to sign in, resolving the moment a token lands (or denying when the hold times out /
/// sign-in fails / deferral is disabled). Delegates verbatim to <see cref="PendingAuthCoordinator"/>,
/// so behavior is identical to the original inline call this seam replaced.
/// </summary>
public sealed class DeferredInteractiveAuthPolicy(PendingAuthCoordinator pendingAuth) : IAuthResolutionPolicy
{
    public Task<OAuthAccessToken?> ResolveAsync(
        IOAuthTokenProvider provider,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken) =>
        pendingAuth.WaitForTokenAsync(provider, scopes, cancellationToken);
}
