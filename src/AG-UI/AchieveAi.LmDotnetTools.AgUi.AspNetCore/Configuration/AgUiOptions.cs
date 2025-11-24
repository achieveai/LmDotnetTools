using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;

namespace AchieveAi.LmDotnetTools.AgUi.AspNetCore.Configuration;

/// <summary>
/// Configuration options for AG-UI ASP.NET Core integration
/// </summary>
public sealed class AgUiOptions
{
    /// <summary>
    /// WebSocket endpoint path for AG-UI connections
    /// Default: "/ag-ui/ws"
    /// </summary>
    [Required]
    public string WebSocketPath { get; set; } = "/ag-ui/ws";

    /// <summary>
    /// Enable CORS support for WebSocket connections
    /// Default: true
    /// </summary>
    public bool EnableCors { get; set; } = true;

    /// <summary>
    /// Allowed origins for CORS
    /// Use "*" to allow all origins (not recommended for production)
    /// Default: empty list (allows all)
    /// </summary>
    public ImmutableList<string> AllowedOrigins { get; set; } = [];

    /// <summary>
    /// Maximum message size in bytes
    /// Default: 1MB
    /// </summary>
    [Range(1024, int.MaxValue)]
    public int MaxMessageSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// WebSocket keep-alive interval
    /// Default: 30 seconds
    /// </summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Enable detailed debug logging
    /// Default: false
    /// </summary>
    public bool EnableDebugLogging { get; set; } = false;

    /// <summary>
    /// Session timeout duration
    /// Default: 30 minutes
    /// </summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Maximum number of concurrent connections
    /// Default: 1000
    /// </summary>
    [Range(1, 10000)]
    public int MaxConcurrentConnections { get; set; } = 1000;

    /// <summary>
    /// Enable event compression for WebSocket messages
    /// Default: false
    /// </summary>
    public bool EnableCompression { get; set; } = false;

    /// <summary>
    /// Buffer size for event publishing
    /// Default: 1000
    /// </summary>
    [Range(10, 100000)]
    public int EventBufferSize { get; set; } = 1000;

    /// <summary>
    /// Enable performance metrics collection
    /// Default: true
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Enable SQLite persistence for session recovery and history
    /// Default: false
    /// </summary>
    public bool EnablePersistence { get; set; } = false;

    /// <summary>
    /// SQLite database file path
    /// Default: "agui.db"
    /// </summary>
    public string DatabasePath { get; set; } = "agui.db";

    /// <summary>
    /// Maximum session age in hours before cleanup
    /// Default: 24 hours
    /// </summary>
    [Range(1, 8760)]
    public int MaxSessionAgeHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of records per session before warning
    /// Default: 10000
    /// </summary>
    [Range(100, 1000000)]
    public int MaxRecordsPerSession { get; set; } = 10000;

    /// <summary>
    /// Maximum number of concurrent database connections
    /// Default: 10
    /// </summary>
    [Range(1, 100)]
    public int MaxDatabaseConnections { get; set; } = 10;

    /// <summary>
    /// Validates the configuration options
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WebSocketPath))
        {
            throw new InvalidOperationException("WebSocketPath cannot be null or empty");
        }

        if (!WebSocketPath.StartsWith("/"))
        {
            throw new InvalidOperationException("WebSocketPath must start with '/'");
        }

        if (MaxMessageSize < 1024)
        {
            throw new InvalidOperationException("MaxMessageSize must be at least 1024 bytes");
        }

        if (SessionTimeout <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("SessionTimeout must be positive");
        }

        if (KeepAliveInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("KeepAliveInterval must be positive");
        }

        if (MaxConcurrentConnections < 1)
        {
            throw new InvalidOperationException("MaxConcurrentConnections must be at least 1");
        }

        if (EventBufferSize < 10)
        {
            throw new InvalidOperationException("EventBufferSize must be at least 10");
        }

        if (EnablePersistence)
        {
            if (string.IsNullOrWhiteSpace(DatabasePath))
            {
                throw new InvalidOperationException("DatabasePath cannot be null or empty when persistence is enabled");
            }

            if (MaxSessionAgeHours < 1)
            {
                throw new InvalidOperationException("MaxSessionAgeHours must be at least 1");
            }

            if (MaxRecordsPerSession < 100)
            {
                throw new InvalidOperationException("MaxRecordsPerSession must be at least 100");
            }

            if (MaxDatabaseConnections < 1)
            {
                throw new InvalidOperationException("MaxDatabaseConnections must be at least 1");
            }
        }
    }
}
