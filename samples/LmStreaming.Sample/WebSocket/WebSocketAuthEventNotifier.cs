using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using LmStreaming.Sample.Services.Auth;

namespace LmStreaming.Sample.WebSocket;

/// <summary>
/// Broadcasts deferred-auth events to every connected chat client as out-of-band WebSocket frames
/// (single-user demo — no per-session targeting). Frame shapes:
/// <c>{"$type":"auth_required","providerId":"github","signinUrl":"/auth/github","reason":"..."}</c>
/// and <c>{"$type":"auth_completed","providerId":"github"}</c>.
/// </summary>
public sealed class WebSocketAuthEventNotifier(
    WebSocketConnectionRegistry registry,
    ILogger<WebSocketAuthEventNotifier> logger) : IAuthEventNotifier
{
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptionsFactory.CreateForProduction();

    public async Task NotifyAuthRequiredAsync(string providerId, string signinUrl, string reason, CancellationToken ct)
    {
        logger.LogInformation(
            "Broadcasting auth_required for provider {ProviderId} (signinUrl {SigninUrl}).",
            providerId,
            signinUrl);
        await registry.BroadcastAsync(BuildAuthRequiredJson(providerId, signinUrl, reason), ct);
    }

    public async Task NotifyAuthCompletedAsync(string providerId, CancellationToken ct)
    {
        logger.LogInformation("Broadcasting auth_completed for provider {ProviderId}.", providerId);
        var json = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["$type"] = "auth_completed",
                ["providerId"] = providerId,
            },
            JsonOptions);
        await registry.BroadcastAsync(json, ct);
    }

    /// <summary>
    /// Builds the <c>auth_required</c> frame JSON — shared with the replay-on-connect path in
    /// <see cref="ChatWebSocketManager"/> so a client that connects mid-hold sees the same frame.
    /// </summary>
    internal static string BuildAuthRequiredJson(string providerId, string signinUrl, string reason) =>
        JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["$type"] = "auth_required",
                ["providerId"] = providerId,
                ["signinUrl"] = signinUrl,
                ["reason"] = reason,
            },
            JsonOptions);
}
