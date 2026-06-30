using AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

namespace CodeReviewDaemon.Sample.Tests.Infrastructure;

/// <summary>
/// An <see cref="IAuthEventNotifier"/> that records every lifecycle call so tests can assert the
/// daemon raised the operator "auth required" signal (instead of denying silently).
/// </summary>
internal sealed class RecordingAuthEventNotifier : IAuthEventNotifier
{
    public List<(string ProviderId, string SigninUrl, string Reason)> Required { get; } = [];
    public List<string> Completed { get; } = [];
    public List<(string ProviderId, string Reason)> Denied { get; } = [];

    public Task NotifyAuthRequiredAsync(string providerId, string signinUrl, string reason, CancellationToken ct)
    {
        Required.Add((providerId, signinUrl, reason));
        return Task.CompletedTask;
    }

    public Task NotifyAuthCompletedAsync(string providerId, CancellationToken ct)
    {
        Completed.Add(providerId);
        return Task.CompletedTask;
    }

    public Task NotifyAuthDeniedAsync(string providerId, string reason, CancellationToken ct)
    {
        Denied.Add((providerId, reason));
        return Task.CompletedTask;
    }
}
