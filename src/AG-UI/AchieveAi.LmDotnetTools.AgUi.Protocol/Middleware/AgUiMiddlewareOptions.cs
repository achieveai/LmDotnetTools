namespace AchieveAi.LmDotnetTools.AgUi.Protocol.Middleware;

/// <summary>
///     Configuration options for AG-UI streaming middleware
/// </summary>
public class AgUiMiddlewareOptions
{
    /// <summary>
    ///     Maximum size of the event buffer
    /// </summary>
    public int EventBufferSize { get; set; } = 1000;

    /// <summary>
    ///     Timeout for publishing events
    /// </summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Enable performance metrics collection
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    ///     Suppress errors when publishing fails (continues streaming)
    /// </summary>
    public bool SuppressPublishErrors { get; set; } = true;

    /// <summary>
    ///     Enable detailed debug logging
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;

    /// <summary>
    ///     Maximum chunk size for text streaming
    /// </summary>
    public int MaxTextChunkSize { get; set; } = 4096;
}
