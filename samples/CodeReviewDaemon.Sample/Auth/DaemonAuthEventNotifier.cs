using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

namespace CodeReviewDaemon.Sample.Auth;

/// <summary>
/// The daemon's <see cref="IAuthEventNotifier"/>. Unlike the chat sample — which pushes
/// <c>auth_required</c>/<c>auth_completed</c> frames to a connected browser so a human can sign in
/// mid-session — the daemon runs unattended: there is no live client to prompt. Auth is a one-time
/// human bootstrap (a console sign-in subcommand) after which the daemon silently refreshes tokens,
/// so this notifier only records the lifecycle events to the operator log.
/// </summary>
/// <remarks>
/// The daemon's <see cref="FailFastDaemonAuthPolicy"/> denies a not-signed-in webhook call
/// immediately (rather than holding it open for an interactive sign-in) and raises an "auth required"
/// signal through this notifier; the log line here is the operator's cue that a credential needs the
/// one-time human sign-in / refresh. A later phase routes that signal to a durable auth-required
/// surface; logging is the seam.
/// </remarks>
internal sealed class DaemonAuthEventNotifier(ILogger<DaemonAuthEventNotifier> logger) : IAuthEventNotifier
{
    public Task NotifyAuthRequiredAsync(string providerId, string signinUrl, string reason, CancellationToken ct)
    {
        logger.LogWarning(
            "Auth required for provider {ProviderId}: {Reason}. A one-time human sign-in is needed before sandbox egress to this provider can be authorized.",
            providerId,
            reason);
        return Task.CompletedTask;
    }

    public Task NotifyAuthCompletedAsync(string providerId, CancellationToken ct)
    {
        logger.LogInformation("Auth completed for provider {ProviderId}.", providerId);
        return Task.CompletedTask;
    }

    public Task NotifyAuthDeniedAsync(string providerId, string reason, CancellationToken ct)
    {
        logger.LogInformation("Auth denied for provider {ProviderId}: {Reason}.", providerId, reason);
        return Task.CompletedTask;
    }
}
