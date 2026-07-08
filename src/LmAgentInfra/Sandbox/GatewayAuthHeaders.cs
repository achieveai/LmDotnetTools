namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Builds the per-app bearer headers the sandbox gateway requires under auth enforcement (gateway
/// ADR 0029 / work-item #109): <c>X-Sbx-App-Id</c> + <c>X-Sbx-App-Key</c>. A single place owns the header
/// names and the "attach only when a key is configured" rule so the REST path
/// (<see cref="GatewayAuthHandler"/>) and the MCP transport path (<c>HttpClientTransportOptions.AdditionalHeaders</c>)
/// can never drift.
/// <para>
/// Backward-compatible by design: with no <c>AppKey</c> configured the app headers are omitted, so a client
/// keeps talking to an <c>AUTH_ENFORCE=off</c> gateway exactly as it did before this existed.
/// </para>
/// </summary>
public static class GatewayAuthHeaders
{
    /// <summary>Header carrying the authenticated app identity.</summary>
    public const string AppIdHeader = "X-Sbx-App-Id";

    /// <summary>Header carrying the app's base64 shared secret. SECRET — never log its value.</summary>
    public const string AppKeyHeader = "X-Sbx-App-Key";

    /// <summary>The session-binding header the gateway already required before app auth existed.</summary>
    public const string SessionHeader = "X-Session-ID";

    /// <summary>
    /// True when both an app id and a non-blank app key are present — the only case in which the bearer
    /// headers are sent (both go together or not at all).
    /// </summary>
    public static bool IsConfigured(string? appId, string? appKey) =>
        !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(appKey);

    /// <summary>
    /// Adds <see cref="AppIdHeader"/>/<see cref="AppKeyHeader"/> to <paramref name="headers"/> when
    /// <see cref="IsConfigured"/> holds; a no-op otherwise. Overwrites any existing values so a caller can
    /// call this idempotently.
    /// </summary>
    public static void Apply(IDictionary<string, string> headers, string? appId, string? appKey)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (!IsConfigured(appId, appKey))
        {
            return;
        }

        headers[AppIdHeader] = appId!;
        headers[AppKeyHeader] = appKey!;
    }

    /// <summary>
    /// Builds the header dictionary an MCP <c>HttpClientTransport</c> should carry: the session header
    /// always, plus the two app-bearer headers when configured. Replaces the inline
    /// <c>new Dictionary { ["X-Session-ID"] = sessionId }</c> at every sandbox MCP transport site.
    /// </summary>
    public static Dictionary<string, string> ForMcp(string sessionId, string? appId, string? appKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var headers = new Dictionary<string, string> { [SessionHeader] = sessionId };
        Apply(headers, appId, appKey);
        return headers;
    }
}
