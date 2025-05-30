using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Manages session defaults and HTTP header processing for MCP connections.
/// </summary>
public interface ISessionManager
{
    /// <summary>
    /// Processes HTTP headers to extract session defaults.
    /// </summary>
    Task<SessionDefaults> ProcessHeadersAsync(string connectionId, IDictionary<string, string> headers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores session defaults for a connection.
    /// </summary>
    Task StoreSessionDefaultsAsync(SessionDefaults sessionDefaults, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets session defaults for a connection.
    /// </summary>
    Task<SessionDefaults?> GetSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates session defaults from session initialization.
    /// </summary>
    Task UpdateSessionDefaultsAsync(string connectionId, string? userId = null, string? agentId = null, string? runId = null, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cleans up expired session defaults.
    /// </summary>
    Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes session defaults for a connection.
    /// </summary>
    Task RemoveSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default);
} 