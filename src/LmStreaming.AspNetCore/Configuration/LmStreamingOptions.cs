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
    /// Allowed origins for CORS. Default: ["*"]
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = ["*"];

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
