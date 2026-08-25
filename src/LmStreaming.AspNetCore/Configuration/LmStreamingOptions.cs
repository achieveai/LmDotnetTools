namespace AchieveAi.LmDotnetTools.LmStreaming.AspNetCore.Configuration;

/// <summary>
/// Configuration options for LmStreaming transport layer.
/// </summary>
public sealed class LmStreamingOptions
{
    /// <summary>
    /// WebSocket endpoint path. Default: "/lm-stream/ws"
    /// </summary>
    public string WebSocketPath { get; set; } = "/lm-stream/ws";

    /// <summary>
    /// SSE endpoint path. Default: "/lm-stream/sse"
    /// </summary>
    public string SsePath { get; set; } = "/lm-stream/sse";

    /// <summary>
    /// Enable CORS for streaming endpoints. Default: true
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// Allowed origins for CORS. Default: empty — same-origin only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The default was <c>["*"]</c>, which combines with <see cref="EnableCors"/> defaulting to
    /// true to make every streaming endpoint readable by script on any page a user happens to have
    /// open. That is defensible while an endpoint carries no identity; it stops being defensible
    /// the moment a request carries a bearer token, because the response body is then tenant data.
    /// This is a deliberate breaking change to the default (P1 slice 1, #301).
    /// </para>
    /// <para>
    /// An empty list allows no cross-origin caller. It does NOT affect the SPA, which is served
    /// from the same origin as the API and therefore never makes a cross-origin request. A
    /// deployment that genuinely hosts its client elsewhere must now name that origin explicitly;
    /// <c>"*"</c> is still honoured for anyone who sets it deliberately, and still cannot be
    /// combined with credentials.
    /// </para>
    /// </remarks>
    public List<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// WebSocket keep-alive interval. Default: 30 seconds
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum message size in bytes. Default: 1MB
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Write indented JSON for debugging. Default: false
    /// </summary>
    public bool WriteIndentedJson { get; set; }
}
