namespace AchieveAi.LmDotnetTools.LmAgentInfra.Auth;

/// <summary>
/// Builds the sign-in URL and human-readable reason for a provider needing interactive auth.
/// Shared by <see cref="PendingAuthCoordinator"/> (WS leg) and <c>AuthWebhookController</c>
/// (webhook-forwarding leg) so the two never drift apart.
/// </summary>
internal static class AuthSigninUrls
{
    public static string BuildSigninUrl(string providerId) => $"/auth/{providerId}";

    public static string BuildReason(string providerId) => $"sandbox egress requires sign-in to '{providerId}'";
}
