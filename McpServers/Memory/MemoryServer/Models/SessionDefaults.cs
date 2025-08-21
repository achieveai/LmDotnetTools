using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace MemoryServer.Models;

/// <summary>
/// Session defaults for MCP connections with transport-aware context.
/// </summary>
public class SessionDefaults
{
    /// <summary>
    /// MCP connection identifier
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Default user identifier
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// Default agent identifier
    /// </summary>
    public string? AgentId { get; set; }

    /// <summary>
    /// Default run identifier
    /// </summary>
    public string? RunId { get; set; }

    /// <summary>
    /// Default metadata
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    /// <summary>
    /// Source of the session defaults
    /// </summary>
    public SessionDefaultsSource Source { get; set; } = SessionDefaultsSource.SystemDefaults;

    /// <summary>
    /// When the session defaults were created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Creates session defaults from environment variables (STDIO transport).
    /// </summary>
    /// <returns>SessionDefaults populated from environment variables</returns>
    public static SessionDefaults FromEnvironmentVariables()
    {
        return new SessionDefaults
        {
            ConnectionId = "stdio-env",
            UserId = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_USER_ID"),
            AgentId = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_AGENT_ID"),
            RunId = EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_RUN_ID"),
            Source = SessionDefaultsSource.EnvironmentVariables,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates session defaults from URL parameters (SSE transport).
    /// </summary>
    /// <param name="queryParameters">URL query parameters</param>
    /// <returns>SessionDefaults populated from URL parameters</returns>
    public static SessionDefaults FromUrlParameters(IDictionary<string, string> queryParameters)
    {
        var defaults = new SessionDefaults
        {
            ConnectionId = "sse-url",
            Source = SessionDefaultsSource.UrlParameters,
            CreatedAt = DateTime.UtcNow
        };

        if (queryParameters.TryGetValue("user_id", out var userId))
            defaults.UserId = userId;

        if (queryParameters.TryGetValue("agent_id", out var agentId))
            defaults.AgentId = agentId;

        if (queryParameters.TryGetValue("run_id", out var runId))
            defaults.RunId = runId;

        return defaults;
    }

    /// <summary>
    /// Creates session defaults from HTTP headers (SSE transport).
    /// </summary>
    /// <param name="headers">HTTP headers</param>
    /// <returns>SessionDefaults populated from HTTP headers</returns>
    public static SessionDefaults FromHttpHeaders(IDictionary<string, string> headers)
    {
        var defaults = new SessionDefaults
        {
            ConnectionId = "sse-headers",
            Source = SessionDefaultsSource.HttpHeaders,
            CreatedAt = DateTime.UtcNow
        };

        if (headers.TryGetValue("X-Memory-User-ID", out var userId))
            defaults.UserId = userId;

        if (headers.TryGetValue("X-Memory-Agent-ID", out var agentId))
            defaults.AgentId = agentId;

        if (headers.TryGetValue("X-Memory-Run-ID", out var runId))
            defaults.RunId = runId;

        return defaults;
    }

    /// <summary>
    /// Returns a string representation of the session defaults.
    /// </summary>
    public override string ToString()
    {
        return $"SessionDefaults(UserId={UserId}, AgentId={AgentId}, RunId={RunId}, Source={Source})";
    }
}

/// <summary>
/// Source of session defaults information.
/// </summary>
public enum SessionDefaultsSource
{
    /// <summary>
    /// System default values
    /// </summary>
    SystemDefaults = 0,

    /// <summary>
    /// Set during session initialization
    /// </summary>
    SessionInitialization = 1,

    /// <summary>
    /// From environment variables (STDIO transport)
    /// </summary>
    EnvironmentVariables = 2,

    /// <summary>
    /// From URL parameters (SSE transport)
    /// </summary>
    UrlParameters = 3,

    /// <summary>
    /// From HTTP headers (SSE transport)
    /// </summary>
    HttpHeaders = 4
}