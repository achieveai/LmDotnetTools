namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Builds the sign-in URL and human-readable reason for a provider needing interactive auth.
/// Shared by <see cref="PendingAuthCoordinator"/> (WS leg) and <c>AuthWebhookController</c>
/// (webhook-forwarding leg) so the two never drift apart.
/// </summary>
internal static class AuthSigninUrls
{
    public static string BuildSigninUrl(string providerId) => $"/auth/{providerId}";

    /// <summary>
    /// Absolute sign-in URL for the webhook-forwarding leg: an external caller polling/receiving
    /// the forwarded payload cannot resolve a same-origin relative path the way the in-app UI can.
    /// </summary>
    /// <param name="callbackBaseUrl">
    /// <see cref="WebhookOptions.CallbackBaseUrl"/> — already trailing-slash-trimmed.
    /// </param>
    /// <param name="providerId">The OAuth provider id, e.g. <c>github</c>.</param>
    public static string BuildAbsoluteSigninUrl(string callbackBaseUrl, string providerId) =>
        callbackBaseUrl + BuildSigninUrl(providerId);

    public static string BuildReason(string providerId) => $"sandbox egress requires sign-in to '{providerId}'";
}
