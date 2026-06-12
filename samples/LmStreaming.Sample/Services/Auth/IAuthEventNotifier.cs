namespace LmStreaming.Sample.Services.Auth;

/// <summary>
/// Pushes deferred-auth lifecycle events to connected chat clients. Abstracted so the auth layer
/// does not reference the WebSocket layer (the production implementation broadcasts frames over
/// the chat WebSocket; tests substitute a recorder).
/// </summary>
public interface IAuthEventNotifier
{
    /// <summary>
    /// Notifies clients that an outbound sandbox request is waiting on an interactive sign-in for
    /// <paramref name="providerId"/>. <paramref name="signinUrl"/> is the same-origin page the
    /// client should open (e.g. <c>/auth/github</c>).
    /// </summary>
    Task NotifyAuthRequiredAsync(string providerId, string signinUrl, string reason, CancellationToken ct);

    /// <summary>Notifies clients that the pending sign-in for <paramref name="providerId"/> completed and the prompt can be dismissed.</summary>
    Task NotifyAuthCompletedAsync(string providerId, CancellationToken ct);

    /// <summary>
    /// Notifies clients that the held sign-in for <paramref name="providerId"/> resolved WITHOUT a
    /// token (hold timed out, sign-in failed, or deferral disabled) so the prompt can be dismissed.
    /// This is the terminal frame for the deny side — without it the banner would linger after a
    /// timeout with a now-stale "Sign in" button.
    /// </summary>
    Task NotifyAuthDeniedAsync(string providerId, string reason, CancellationToken ct);
}
