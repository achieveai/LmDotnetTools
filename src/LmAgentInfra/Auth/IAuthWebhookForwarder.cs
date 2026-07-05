namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// The session/thread a session-aware auth-webhook forward resolved to, captured once at
/// <c>auth_required</c> time and reused unchanged at the terminal (completed/denied) outcome —
/// even if the originally-eligible thread is later deleted or a new eligible thread appears.
/// </summary>
public sealed record AuthWebhookTarget(string ThreadId, string? RunId, string WebhookUrl);

/// <summary>
/// Forwards auth-required/completed/denied signals to a session-registered webhook, in addition
/// to (and independent of) the existing WS-facing <see cref="IAuthEventNotifier"/> broadcast.
/// <see cref="NotifyAuthRequiredAsync"/> resolves and returns the forwarding target; the terminal
/// methods take that same target back so a caller spanning both ends of one gateway call (see
/// <c>AuthWebhookController.Evaluate</c>) never re-resolves eligibility mid-flight.
/// </summary>
public interface IAuthWebhookForwarder
{
    Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
        string sessionId,
        string providerId,
        string signinUrl,
        string reason,
        CancellationToken ct);

    Task NotifyAuthCompletedAsync(AuthWebhookTarget? target, string sessionId, string providerId, CancellationToken ct);

    Task NotifyAuthDeniedAsync(AuthWebhookTarget? target, string sessionId, string providerId, string reason, CancellationToken ct);
}
