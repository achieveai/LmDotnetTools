using System.Text.Json;
using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Implementation of session manager with transport-aware context and database persistence.
/// </summary>
public class SessionManager : ISessionManager
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<SessionManager> _logger;
    private readonly MemoryServerOptions _options;

    public SessionManager(
        ISqliteSessionFactory sessionFactory,
        ILogger<SessionManager> logger,
        IOptions<MemoryServerOptions> options)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Processes environment variables for STDIO transport session context.
    /// </summary>
    public async Task<SessionDefaults?> ProcessEnvironmentVariablesAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Processing environment variables for STDIO transport");

        var sessionDefaults = SessionDefaults.FromEnvironmentVariables();

        // Only return if at least one environment variable is set
        if (string.IsNullOrEmpty(sessionDefaults.UserId) && 
            string.IsNullOrEmpty(sessionDefaults.AgentId) && 
            string.IsNullOrEmpty(sessionDefaults.RunId))
        {
            _logger.LogDebug("No environment variables found for session context");
            return null;
        }

        _logger.LogInformation("Found session context in environment variables: {SessionDefaults}", sessionDefaults);

        // Store in database for persistence
        await StoreSessionDefaultsAsync(sessionDefaults, cancellationToken);

        return sessionDefaults;
    }

    /// <summary>
    /// Processes URL parameters for SSE transport session context.
    /// </summary>
    public async Task<SessionDefaults?> ProcessUrlParametersAsync(IDictionary<string, string> queryParameters, CancellationToken cancellationToken = default)
    {
        if (queryParameters == null || !queryParameters.Any())
        {
            _logger.LogDebug("No URL parameters provided");
            return null;
        }

        _logger.LogDebug("Processing URL parameters for SSE transport: {Parameters}", 
            string.Join(", ", queryParameters.Select(kvp => $"{kvp.Key}={kvp.Value}")));

        var sessionDefaults = SessionDefaults.FromUrlParameters(queryParameters);

        // Only return if at least one parameter is set
        if (string.IsNullOrEmpty(sessionDefaults.UserId) && 
            string.IsNullOrEmpty(sessionDefaults.AgentId) && 
            string.IsNullOrEmpty(sessionDefaults.RunId))
        {
            _logger.LogDebug("No relevant URL parameters found for session context");
            return null;
        }

        _logger.LogInformation("Found session context in URL parameters: {SessionDefaults}", sessionDefaults);

        // Store in database for persistence
        await StoreSessionDefaultsAsync(sessionDefaults, cancellationToken);

        return sessionDefaults;
    }

    /// <summary>
    /// Processes HTTP headers for SSE transport session context.
    /// </summary>
    public async Task<SessionDefaults?> ProcessHttpHeadersAsync(IDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        if (headers == null || !headers.Any())
        {
            _logger.LogDebug("No HTTP headers provided");
            return null;
        }

        _logger.LogDebug("Processing HTTP headers for SSE transport: {Headers}", 
            string.Join(", ", headers.Where(h => h.Key.StartsWith("X-Memory-")).Select(kvp => $"{kvp.Key}={kvp.Value}")));

        var sessionDefaults = SessionDefaults.FromHttpHeaders(headers);

        // Only return if at least one header is set
        if (string.IsNullOrEmpty(sessionDefaults.UserId) && 
            string.IsNullOrEmpty(sessionDefaults.AgentId) && 
            string.IsNullOrEmpty(sessionDefaults.RunId))
        {
            _logger.LogDebug("No relevant HTTP headers found for session context");
            return null;
        }

        _logger.LogInformation("Found session context in HTTP headers: {SessionDefaults}", sessionDefaults);

        // Store in database for persistence
        await StoreSessionDefaultsAsync(sessionDefaults, cancellationToken);

        return sessionDefaults;
    }

    /// <summary>
    /// Processes transport-specific context with proper precedence handling.
    /// Precedence: HTTP Headers > URL Parameters > Environment Variables
    /// </summary>
    public async Task<SessionDefaults?> ProcessTransportContextAsync(
        IDictionary<string, string>? queryParameters = null,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Processing transport context with precedence handling");

        SessionDefaults? result = null;

        // 1. Start with environment variables (lowest precedence)
        var envDefaults = await ProcessEnvironmentVariablesAsync(cancellationToken);
        if (envDefaults != null)
        {
            result = envDefaults;
            _logger.LogDebug("Applied environment variable defaults");
        }

        // 2. Apply URL parameters (medium precedence)
        if (queryParameters != null)
        {
            var urlDefaults = await ProcessUrlParametersAsync(queryParameters, cancellationToken);
            if (urlDefaults != null)
            {
                if (result == null)
                {
                    result = urlDefaults;
                }
                else
                {
                    // Merge with precedence: URL parameters override environment variables
                    result.UserId = urlDefaults.UserId ?? result.UserId;
                    result.AgentId = urlDefaults.AgentId ?? result.AgentId;
                    result.RunId = urlDefaults.RunId ?? result.RunId;
                    result.Source = SessionDefaultsSource.UrlParameters; // Update source to highest precedence
                }
                _logger.LogDebug("Applied URL parameter defaults");
            }
        }

        // 3. Apply HTTP headers (highest precedence)
        if (headers != null)
        {
            var headerDefaults = await ProcessHttpHeadersAsync(headers, cancellationToken);
            if (headerDefaults != null)
            {
                if (result == null)
                {
                    result = headerDefaults;
                }
                else
                {
                    // Merge with precedence: HTTP headers override everything
                    result.UserId = headerDefaults.UserId ?? result.UserId;
                    result.AgentId = headerDefaults.AgentId ?? result.AgentId;
                    result.RunId = headerDefaults.RunId ?? result.RunId;
                    result.Source = SessionDefaultsSource.HttpHeaders; // Update source to highest precedence
                }
                _logger.LogDebug("Applied HTTP header defaults");
            }
        }

        if (result != null)
        {
            _logger.LogInformation("Final transport context: {SessionDefaults}", result);
        }
        else
        {
            _logger.LogDebug("No transport context found");
        }

        return result;
    }

    /// <summary>
    /// Stores session defaults in the database.
    /// </summary>
    public async Task<bool> StoreSessionDefaultsAsync(SessionDefaults sessionDefaults, CancellationToken cancellationToken = default)
    {
        if (sessionDefaults == null)
        {
            _logger.LogWarning("Cannot store null session defaults");
            return false;
        }

        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            const string sql = @"
                INSERT OR REPLACE INTO session_defaults 
                (connection_id, user_id, agent_id, run_id, metadata, source, created_at)
                VALUES (@connectionId, @userId, @agentId, @runId, @metadata, @source, @createdAt)";

            var rowsAffected = await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@connectionId", sessionDefaults.ConnectionId);
                command.Parameters.AddWithValue("@userId", sessionDefaults.UserId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@agentId", sessionDefaults.AgentId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@runId", sessionDefaults.RunId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@metadata", JsonSerializer.Serialize(sessionDefaults.Metadata));
                command.Parameters.AddWithValue("@source", (int)sessionDefaults.Source);
                command.Parameters.AddWithValue("@createdAt", sessionDefaults.CreatedAt.ToString("O"));

                return await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);

            _logger.LogDebug("Stored session defaults for connection {ConnectionId}: {RowsAffected} rows affected", 
                sessionDefaults.ConnectionId, rowsAffected);

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store session defaults for connection {ConnectionId}", sessionDefaults.ConnectionId);
            return false;
        }
    }

    /// <summary>
    /// Retrieves session defaults by connection ID.
    /// </summary>
    public async Task<SessionDefaults?> GetSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            _logger.LogWarning("Cannot retrieve session defaults with empty connection ID");
            return null;
        }

        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            const string sql = @"
                SELECT connection_id, user_id, agent_id, run_id, metadata, source, created_at
                FROM session_defaults 
                WHERE connection_id = @connectionId";

            var sessionDefaults = await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@connectionId", connectionId);

                using var reader = await command.ExecuteReaderAsync(cancellationToken);

                if (await reader.ReadAsync(cancellationToken))
                {
                    var metadataOrdinal = reader.GetOrdinal("metadata");
                    var metadataJson = reader.IsDBNull(metadataOrdinal) ? string.Empty : reader.GetString(metadataOrdinal);
                    var metadata = string.IsNullOrEmpty(metadataJson) 
                        ? new Dictionary<string, object>() 
                        : JsonSerializer.Deserialize<Dictionary<string, object>>(metadataJson) ?? new Dictionary<string, object>();

                    // Handle source column gracefully - it might not exist in older schemas
                    var source = SessionDefaultsSource.SystemDefaults;
                    try
                    {
                        var sourceOrdinal = reader.GetOrdinal("source");
                        if (!reader.IsDBNull(sourceOrdinal))
                        {
                            source = (SessionDefaultsSource)reader.GetInt32(sourceOrdinal);
                        }
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // Column doesn't exist, use default
                        source = SessionDefaultsSource.SystemDefaults;
                    }

                    // Handle created_at column gracefully
                    var createdAt = DateTime.UtcNow;
                    try
                    {
                        var createdAtOrdinal = reader.GetOrdinal("created_at");
                        if (!reader.IsDBNull(createdAtOrdinal))
                        {
                            var createdAtValue = reader.GetValue(createdAtOrdinal);
                            if (createdAtValue is string createdAtString)
                            {
                                DateTime.TryParse(createdAtString, out createdAt);
                            }
                            else if (createdAtValue is DateTime createdAtDateTime)
                            {
                                createdAt = createdAtDateTime;
                            }
                        }
                    }
                    catch (IndexOutOfRangeException)
                    {
                        // Column doesn't exist, use current time
                        createdAt = DateTime.UtcNow;
                    }

                    var connectionIdOrdinal = reader.GetOrdinal("connection_id");
                    var userIdOrdinal = reader.GetOrdinal("user_id");
                    var agentIdOrdinal = reader.GetOrdinal("agent_id");
                    var runIdOrdinal = reader.GetOrdinal("run_id");

                    return new SessionDefaults
                    {
                        ConnectionId = reader.GetString(connectionIdOrdinal),
                        UserId = reader.IsDBNull(userIdOrdinal) ? null : reader.GetString(userIdOrdinal),
                        AgentId = reader.IsDBNull(agentIdOrdinal) ? null : reader.GetString(agentIdOrdinal),
                        RunId = reader.IsDBNull(runIdOrdinal) ? null : reader.GetString(runIdOrdinal),
                        Metadata = metadata,
                        Source = source,
                        CreatedAt = createdAt
                    };
                }

                return null;
            }, cancellationToken);

            if (sessionDefaults != null)
            {
                _logger.LogDebug("Retrieved session defaults for connection {ConnectionId}: {SessionDefaults}", 
                    connectionId, sessionDefaults);
            }
            else
            {
                _logger.LogDebug("No session defaults found for connection {ConnectionId}", connectionId);
            }

            return sessionDefaults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve session defaults for connection {ConnectionId}", connectionId);
            return null;
        }
    }

    /// <summary>
    /// Removes session defaults for a connection.
    /// </summary>
    public async Task<bool> RemoveSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(connectionId))
        {
            _logger.LogWarning("Cannot remove session defaults with empty connection ID");
            return false;
        }

        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            const string sql = "DELETE FROM session_defaults WHERE connection_id = @connectionId";

            var rowsAffected = await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@connectionId", connectionId);

                return await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);

            _logger.LogDebug("Removed session defaults for connection {ConnectionId}: {RowsAffected} rows affected", 
                connectionId, rowsAffected);

            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove session defaults for connection {ConnectionId}", connectionId);
            return false;
        }
    }

    /// <summary>
    /// Cleans up expired session defaults.
    /// </summary>
    public async Task<int> CleanupExpiredSessionsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);

            var cutoffTime = DateTime.UtcNow.Subtract(maxAge);
            const string sql = "DELETE FROM session_defaults WHERE created_at < @cutoffTime";

            var rowsAffected = await session.ExecuteAsync(async connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@cutoffTime", cutoffTime.ToString("O"));

                return await command.ExecuteNonQueryAsync(cancellationToken);
            }, cancellationToken);

            _logger.LogInformation("Cleaned up {Count} expired session defaults older than {MaxAge}", 
                rowsAffected, maxAge);

            return rowsAffected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired session defaults");
            return 0;
        }
    }
} 