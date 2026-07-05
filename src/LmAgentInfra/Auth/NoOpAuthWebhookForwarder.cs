namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Default <see cref="IAuthWebhookForwarder"/> registration for hosts that don't wire up
/// session-aware webhook forwarding (e.g. no eligible thread ever registers a forwarding
/// webhook). Resolves no target and no-ops on the terminal calls.
/// </summary>
public sealed class NoOpAuthWebhookForwarder : IAuthWebhookForwarder
{
    public Task<AuthWebhookTarget?> NotifyAuthRequiredAsync(
        string sessionId,
        string providerId,
        string signinUrl,
        string reason,
        CancellationToken ct) => Task.FromResult<AuthWebhookTarget?>(null);

    public Task NotifyAuthCompletedAsync(AuthWebhookTarget? target, string sessionId, string providerId, CancellationToken ct) =>
        Task.CompletedTask;

    public Task NotifyAuthDeniedAsync(AuthWebhookTarget? target, string sessionId, string providerId, string reason, CancellationToken ct) =>
        Task.CompletedTask;
}
