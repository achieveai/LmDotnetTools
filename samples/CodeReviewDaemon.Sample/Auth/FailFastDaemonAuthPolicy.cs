using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

namespace CodeReviewDaemon.Sample.Auth;

/// <summary>
/// The unattended daemon's <see cref="IAuthResolutionPolicy"/>. There is no human in the loop to
/// complete an interactive sign-in mid-request, so holding the webhook call open (as the chat app
/// does) would only stall the gateway until it times out and surface no operator signal. Instead this
/// policy <b>fails fast</b>: it raises an "auth required" signal through the
/// <see cref="IAuthEventNotifier"/> — the operator's cue that the one-time human sign-in / token
/// refresh is needed — and denies immediately (returns <c>null</c>).
/// </summary>
/// <remarks>
/// This is the piece the config-only approach (<c>Auth:Webhook:HoldTimeoutSeconds = 0</c>) could not
/// provide: <see cref="PendingAuthCoordinator.WaitForTokenAsync"/> returns <c>null</c> at once when
/// deferral is disabled but never emits <see cref="IAuthEventNotifier.NotifyAuthRequiredAsync"/>, so
/// the daemon would deny silently. Routing the signal here satisfies the locked architecture's
/// "auth-required signal on refresh failure" requirement.
/// </remarks>
internal sealed class FailFastDaemonAuthPolicy(
    IAuthEventNotifier notifier,
    ILogger<FailFastDaemonAuthPolicy> logger) : IAuthResolutionPolicy
{
    public async Task<OAuthAccessToken?> ResolveAsync(
        IOAuthTokenProvider provider,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);

        const string reason = "no valid token; one-time human sign-in or token refresh required";
        logger.LogWarning(
            "Auth-resolution fail-fast for provider {ProviderId}: {Reason}. Denying the webhook call and signaling that sign-in is needed.",
            provider.ProviderId,
            reason);

        // Operator signal — the daemon's notifier records this to the operator log (a later phase
        // routes it to a durable auth-required surface). Use CancellationToken.None: the signal must
        // fire even if the gateway has already abandoned this particular webhook call.
        await notifier.NotifyAuthRequiredAsync(
            provider.ProviderId,
            $"/auth/{provider.ProviderId}",
            reason,
            CancellationToken.None);

        return null;
    }
}
