namespace MemoryServer.Models;

/// <summary>
/// Stores default session parameters that can be set via HTTP headers or session initialization.
/// Provides precedence hierarchy: Explicit Parameters > HTTP Headers > Session Init > System Defaults.
/// </summary>
public class SessionDefaults
{
    /// <summary>
    /// Unique connection identifier for this session.
    /// </summary>
    public string ConnectionId { get; set; } = string.Empty;

    /// <summary>
    /// Default user ID for operations in this session.
    /// </summary>
    public string? DefaultUserId { get; set; }

    /// <summary>
    /// Default agent ID for operations in this session.
    /// </summary>
    public string? DefaultAgentId { get; set; }

    /// <summary>
    /// Default run ID for operations in this session.
    /// </summary>
    public string? DefaultRunId { get; set; }

    /// <summary>
    /// Additional session metadata.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// When these defaults were created.
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Source of these defaults (Headers, SessionInit, System).
    /// </summary>
    public SessionDefaultsSource Source { get; set; } = SessionDefaultsSource.System;

    /// <summary>
    /// Resolves session context using precedence rules.
    /// </summary>
    public SessionContext ResolveSessionContext(
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        string? systemDefaultUserId = null)
    {
        return new SessionContext
        {
            UserId = explicitUserId ?? DefaultUserId ?? systemDefaultUserId ?? "default_user",
            AgentId = explicitAgentId ?? DefaultAgentId,
            RunId = explicitRunId ?? DefaultRunId
        };
    }

    /// <summary>
    /// Creates session defaults from HTTP headers.
    /// </summary>
    public static SessionDefaults FromHeaders(
        string connectionId,
        string? userIdHeader = null,
        string? agentIdHeader = null,
        string? runIdHeader = null)
    {
        return new SessionDefaults
        {
            ConnectionId = connectionId,
            DefaultUserId = userIdHeader,
            DefaultAgentId = agentIdHeader,
            DefaultRunId = runIdHeader,
            Source = SessionDefaultsSource.Headers,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates session defaults from session initialization.
    /// </summary>
    public static SessionDefaults FromSessionInit(
        string connectionId,
        string? userId = null,
        string? agentId = null,
        string? runId = null,
        Dictionary<string, object>? metadata = null)
    {
        return new SessionDefaults
        {
            ConnectionId = connectionId,
            DefaultUserId = userId,
            DefaultAgentId = agentId,
            DefaultRunId = runId,
            Metadata = metadata,
            Source = SessionDefaultsSource.SessionInit,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Creates system default session defaults.
    /// </summary>
    public static SessionDefaults SystemDefaults(string connectionId, string systemUserId)
    {
        return new SessionDefaults
        {
            ConnectionId = connectionId,
            DefaultUserId = systemUserId,
            Source = SessionDefaultsSource.System,
            CreatedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Updates the defaults with new values, preserving higher precedence.
    /// </summary>
    public SessionDefaults UpdateWith(SessionDefaults other)
    {
        // Only update if the other source has higher or equal precedence
        if (other.Source >= this.Source)
        {
            return new SessionDefaults
            {
                ConnectionId = this.ConnectionId,
                DefaultUserId = other.DefaultUserId ?? this.DefaultUserId,
                DefaultAgentId = other.DefaultAgentId ?? this.DefaultAgentId,
                DefaultRunId = other.DefaultRunId ?? this.DefaultRunId,
                Metadata = other.Metadata ?? this.Metadata,
                Source = other.Source,
                CreatedAt = DateTime.UtcNow
            };
        }
        
        return this;
    }

    /// <summary>
    /// Gets a string representation for logging.
    /// </summary>
    public override string ToString()
    {
        var parts = new List<string> { $"Connection:{ConnectionId}", $"Source:{Source}" };
        if (!string.IsNullOrEmpty(DefaultUserId)) parts.Add($"User:{DefaultUserId}");
        if (!string.IsNullOrEmpty(DefaultAgentId)) parts.Add($"Agent:{DefaultAgentId}");
        if (!string.IsNullOrEmpty(DefaultRunId)) parts.Add($"Run:{DefaultRunId}");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Defines the source of session defaults with precedence ordering.
/// Higher values have higher precedence.
/// </summary>
public enum SessionDefaultsSource
{
    /// <summary>
    /// System defaults (lowest precedence).
    /// </summary>
    System = 0,
    
    /// <summary>
    /// Session initialization defaults (medium precedence).
    /// </summary>
    SessionInit = 1,
    
    /// <summary>
    /// HTTP header defaults (highest precedence).
    /// </summary>
    Headers = 2
} 