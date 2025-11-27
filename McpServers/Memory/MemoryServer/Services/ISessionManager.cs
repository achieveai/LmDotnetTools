using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
///     Interface for managing session defaults with transport-aware context.
/// </summary>
public interface ISessionManager
{
    /// <summary>
    ///     Processes environment variables for STDIO transport session context.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults from environment variables</returns>
    Task<SessionDefaults?> ProcessEnvironmentVariablesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     Processes URL parameters for SSE transport session context.
    /// </summary>
    /// <param name="queryParameters">URL query parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults from URL parameters</returns>
    Task<SessionDefaults?> ProcessUrlParametersAsync(
        IDictionary<string, string> queryParameters,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Processes HTTP headers for SSE transport session context.
    /// </summary>
    /// <param name="headers">HTTP headers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults from HTTP headers</returns>
    Task<SessionDefaults?> ProcessHttpHeadersAsync(
        IDictionary<string, string> headers,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Processes transport-specific context (combines environment variables, URL parameters, and headers).
    /// </summary>
    /// <param name="queryParameters">URL query parameters (for SSE)</param>
    /// <param name="headers">HTTP headers (for SSE)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults from transport context</returns>
    Task<SessionDefaults?> ProcessTransportContextAsync(
        IDictionary<string, string>? queryParameters = null,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Stores session defaults in the database.
    /// </summary>
    /// <param name="sessionDefaults">Session defaults to store</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if stored successfully</returns>
    Task<bool> StoreSessionDefaultsAsync(
        SessionDefaults sessionDefaults,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Retrieves session defaults by connection ID.
    /// </summary>
    /// <param name="connectionId">Connection identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults if found</returns>
    Task<SessionDefaults?> GetSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes session defaults for a connection.
    /// </summary>
    /// <param name="connectionId">Connection identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if removed successfully</returns>
    Task<bool> RemoveSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Cleans up expired session defaults.
    /// </summary>
    /// <param name="maxAge">Maximum age for session defaults</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of cleaned up sessions</returns>
    Task<int> CleanupExpiredSessionsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}
