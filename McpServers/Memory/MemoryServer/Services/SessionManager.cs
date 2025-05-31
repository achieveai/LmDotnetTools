using MemoryServer.Infrastructure;
using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace MemoryServer.Services;

/// <summary>
/// Manages session defaults and HTTP header processing for MCP connections.
/// Uses Database Session Pattern for reliable connection management.
/// </summary>
public class SessionManager : ISessionManager
{
    private readonly ISqliteSessionFactory _sessionFactory;
    private readonly ILogger<SessionManager> _logger;
    private readonly SessionDefaultsOptions _options;

    // HTTP header names for session defaults
    private const string UserIdHeader = "X-Memory-User-ID";
    private const string AgentIdHeader = "X-Memory-Agent-ID";
    private const string RunIdHeader = "X-Memory-Run-ID";

    public SessionManager(
        ISqliteSessionFactory sessionFactory,
        ILogger<SessionManager> logger,
        IOptions<MemoryServerOptions> options)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value.SessionDefaults;
    }

    /// <summary>
    /// Processes HTTP headers to extract session defaults.
    /// </summary>
    public async Task<SessionDefaults> ProcessHeadersAsync(string connectionId, IDictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Processing headers for connection {ConnectionId}", connectionId);

        // Extract session defaults from headers
        headers.TryGetValue(UserIdHeader, out var userId);
        headers.TryGetValue(AgentIdHeader, out var agentId);
        headers.TryGetValue(RunIdHeader, out var runId);

        var sessionDefaults = SessionDefaults.FromHeaders(connectionId, userId, agentId, runId);

        // Store the session defaults
        await StoreSessionDefaultsAsync(sessionDefaults, cancellationToken);

        _logger.LogInformation("Processed headers for connection {ConnectionId}: {SessionDefaults}", connectionId, sessionDefaults);
        return sessionDefaults;
    }

    /// <summary>
    /// Stores session defaults for a connection.
    /// </summary>
    public async Task StoreSessionDefaultsAsync(SessionDefaults sessionDefaults, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO session_defaults 
                (connection_id, user_id, agent_id, run_id, metadata, source, created_at)
                VALUES (@connectionId, @userId, @agentId, @runId, @metadata, @source, @createdAt)";

            command.Parameters.AddWithValue("@connectionId", sessionDefaults.ConnectionId);
            command.Parameters.AddWithValue("@userId", sessionDefaults.DefaultUserId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@agentId", sessionDefaults.DefaultAgentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@runId", sessionDefaults.DefaultRunId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("@metadata", sessionDefaults.Metadata != null ? JsonSerializer.Serialize(sessionDefaults.Metadata) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@source", (int)sessionDefaults.Source);
            command.Parameters.AddWithValue("@createdAt", sessionDefaults.CreatedAt);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        
        _logger.LogDebug("Stored session defaults for connection {ConnectionId}", sessionDefaults.ConnectionId);
    }

    /// <summary>
    /// Gets session defaults for a connection.
    /// </summary>
    public async Task<SessionDefaults?> GetSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        return await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT connection_id, user_id, agent_id, run_id, metadata, source, created_at
                FROM session_defaults 
                WHERE connection_id = @connectionId";

            command.Parameters.AddWithValue("@connectionId", connectionId);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var metadata = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(reader.GetString(4));

                return new SessionDefaults
                {
                    ConnectionId = reader.GetString(0), // connection_id
                    DefaultUserId = reader.IsDBNull(1) ? null : reader.GetString(1), // user_id
                    DefaultAgentId = reader.IsDBNull(2) ? null : reader.GetString(2), // agent_id
                    DefaultRunId = reader.IsDBNull(3) ? null : reader.GetString(3), // run_id
                    Metadata = metadata, // metadata
                    Source = (SessionDefaultsSource)reader.GetInt32(5), // source
                    CreatedAt = reader.GetDateTime(6) // created_at
                };
            }

            return null;
        }, cancellationToken);
    }

    /// <summary>
    /// Updates session defaults from session initialization.
    /// </summary>
    public async Task UpdateSessionDefaultsAsync(string connectionId, string? userId = null, string? agentId = null, string? runId = null, Dictionary<string, object>? metadata = null, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Updating session defaults for connection {ConnectionId}", connectionId);

        // Get existing session defaults
        var existing = await GetSessionDefaultsAsync(connectionId, cancellationToken);

        // Create new session defaults from session init
        var sessionInitDefaults = SessionDefaults.FromSessionInit(connectionId, userId, agentId, runId, metadata);

        // Update existing defaults with new values, respecting precedence
        var updated = existing?.UpdateWith(sessionInitDefaults) ?? sessionInitDefaults;

        // Store the updated defaults
        await StoreSessionDefaultsAsync(updated, cancellationToken);

        _logger.LogInformation("Updated session defaults for connection {ConnectionId}: {SessionDefaults}", connectionId, updated);
    }

    /// <summary>
    /// Cleans up expired session defaults.
    /// </summary>
    public async Task CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        var cutoffTime = DateTime.UtcNow.AddMinutes(-_options.MaxSessionAge);

        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM session_defaults 
                WHERE created_at < @cutoffTime";

            command.Parameters.AddWithValue("@cutoffTime", cutoffTime);

            var deletedCount = await command.ExecuteNonQueryAsync(cancellationToken);
            if (deletedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired session defaults", deletedCount);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Removes session defaults for a connection.
    /// </summary>
    public async Task RemoveSessionDefaultsAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        await using var session = await _sessionFactory.CreateSessionAsync(cancellationToken);
        
        await session.ExecuteAsync(async connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM session_defaults 
                WHERE connection_id = @connectionId";

            command.Parameters.AddWithValue("@connectionId", connectionId);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
        
        _logger.LogDebug("Removed session defaults for connection {ConnectionId}", connectionId);
    }
} 