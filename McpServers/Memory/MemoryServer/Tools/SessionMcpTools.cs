using System.ComponentModel;
using System.Text.Json;
using MemoryServer.Models;
using MemoryServer.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace MemoryServer.Tools;

/// <summary>
/// MCP tools for session management and initialization.
/// </summary>
[McpServerToolType]
public class SessionMcpTools
{
    private readonly ISessionManager _sessionManager;
    private readonly ISessionContextResolver _sessionResolver;
    private readonly ILogger<SessionMcpTools> _logger;

    public SessionMcpTools(
        ISessionManager sessionManager,
        ISessionContextResolver sessionResolver,
        ILogger<SessionMcpTools> logger)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _sessionResolver = sessionResolver ?? throw new ArgumentNullException(nameof(sessionResolver));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Initializes session defaults for the MCP connection lifetime.
    /// This tool sets default session context that will be used for subsequent tool calls
    /// when explicit session parameters are not provided.
    /// </summary>
    /// <param name="userId">Default user identifier for the session</param>
    /// <param name="agentId">Optional default agent identifier for the session</param>
    /// <param name="runId">Optional default run identifier for the session</param>
    /// <param name="metadata">Optional session metadata as JSON string</param>
    /// <param name="connectionId">Optional connection identifier (auto-generated if not provided)</param>
    /// <returns>Session initialization result with active defaults</returns>
    [McpServerTool(Name = "memory_init_session"), Description("Initializes session defaults for the MCP connection lifetime")]
    public async Task<object> InitializeSessionAsync(
        [Description("Default user identifier for the session")] string userId,
        [Description("Optional default agent identifier for the session")] string? agentId = null,
        [Description("Optional default run identifier for the session")] string? runId = null,
        [Description("Optional session metadata as JSON string")] string? metadata = null,
        [Description("Optional connection identifier (auto-generated if not provided)")] string? connectionId = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return new { success = false, error = "User ID is required for session initialization" };
            }

            // Generate connection ID if not provided
            connectionId ??= Guid.NewGuid().ToString();

            // Parse metadata if provided
            Dictionary<string, object>? metadataDict = null;
            if (!string.IsNullOrEmpty(metadata))
            {
                try
                {
                    metadataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse session metadata JSON: {Metadata}", metadata);
                    return new { success = false, error = "Invalid metadata JSON format" };
                }
            }

            // Update session defaults
            await _sessionManager.UpdateSessionDefaultsAsync(
                connectionId, 
                userId, 
                agentId, 
                runId, 
                metadataDict);

            _logger.LogInformation("Initialized session defaults for connection {ConnectionId}: User={UserId}, Agent={AgentId}, Run={RunId}", 
                connectionId, userId, agentId, runId);

            return new
            {
                success = true,
                connection_id = connectionId,
                session_defaults = new
                {
                    user_id = userId,
                    agent_id = agentId,
                    run_id = runId,
                    metadata = metadataDict
                },
                message = "Session defaults initialized successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing session");
            return new { success = false, error = $"Error initializing session: {ex.Message}" };
        }
    }

    /// <summary>
    /// Gets the current session defaults for a connection.
    /// </summary>
    /// <param name="connectionId">Connection identifier to get defaults for</param>
    /// <returns>Current session defaults</returns>
    [McpServerTool(Name = "memory_get_session"), Description("Gets the current session defaults for a connection")]
    public async Task<object> GetSessionDefaultsAsync(
        [Description("Connection identifier to get defaults for")] string connectionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return new { success = false, error = "Connection ID is required" };
            }

            var sessionDefaults = await _sessionManager.GetSessionDefaultsAsync(connectionId);

            if (sessionDefaults == null)
            {
                return new { success = false, error = $"No session defaults found for connection {connectionId}" };
            }

            _logger.LogDebug("Retrieved session defaults for connection {ConnectionId}", connectionId);

            return new
            {
                success = true,
                connection_id = connectionId,
                session_defaults = new
                {
                    user_id = sessionDefaults.DefaultUserId,
                    agent_id = sessionDefaults.DefaultAgentId,
                    run_id = sessionDefaults.DefaultRunId,
                    metadata = sessionDefaults.Metadata,
                    created_at = sessionDefaults.CreatedAt,
                    source = sessionDefaults.Source.ToString()
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting session defaults for connection {ConnectionId}", connectionId);
            return new { success = false, error = $"Error getting session defaults: {ex.Message}" };
        }
    }

    /// <summary>
    /// Updates session defaults for an existing connection.
    /// </summary>
    /// <param name="connectionId">Connection identifier to update</param>
    /// <param name="userId">Optional new user identifier</param>
    /// <param name="agentId">Optional new agent identifier</param>
    /// <param name="runId">Optional new run identifier</param>
    /// <param name="metadata">Optional new metadata as JSON string</param>
    /// <returns>Updated session defaults</returns>
    [McpServerTool(Name = "memory_update_session"), Description("Updates session defaults for an existing connection")]
    public async Task<object> UpdateSessionDefaultsAsync(
        [Description("Connection identifier to update")] string connectionId,
        [Description("Optional new user identifier")] string? userId = null,
        [Description("Optional new agent identifier")] string? agentId = null,
        [Description("Optional new run identifier")] string? runId = null,
        [Description("Optional new metadata as JSON string")] string? metadata = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return new { success = false, error = "Connection ID is required" };
            }

            // Parse metadata if provided
            Dictionary<string, object>? metadataDict = null;
            if (!string.IsNullOrEmpty(metadata))
            {
                try
                {
                    metadataDict = JsonSerializer.Deserialize<Dictionary<string, object>>(metadata);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to parse session metadata JSON: {Metadata}", metadata);
                    return new { success = false, error = "Invalid metadata JSON format" };
                }
            }

            // Update session defaults
            await _sessionManager.UpdateSessionDefaultsAsync(
                connectionId, 
                userId, 
                agentId, 
                runId, 
                metadataDict);

            // Get updated defaults to return
            var updatedDefaults = await _sessionManager.GetSessionDefaultsAsync(connectionId);

            _logger.LogInformation("Updated session defaults for connection {ConnectionId}", connectionId);

            return new
            {
                success = true,
                connection_id = connectionId,
                session_defaults = updatedDefaults != null ? new
                {
                    user_id = updatedDefaults.DefaultUserId,
                    agent_id = updatedDefaults.DefaultAgentId,
                    run_id = updatedDefaults.DefaultRunId,
                    metadata = updatedDefaults.Metadata,
                    created_at = updatedDefaults.CreatedAt,
                    source = updatedDefaults.Source.ToString()
                } : null,
                message = "Session defaults updated successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating session defaults for connection {ConnectionId}", connectionId);
            return new { success = false, error = $"Error updating session defaults: {ex.Message}" };
        }
    }

    /// <summary>
    /// Removes session defaults for a connection.
    /// </summary>
    /// <param name="connectionId">Connection identifier to remove defaults for</param>
    /// <returns>Removal result</returns>
    [McpServerTool(Name = "memory_clear_session"), Description("Removes session defaults for a connection")]
    public async Task<object> ClearSessionDefaultsAsync(
        [Description("Connection identifier to remove defaults for")] string connectionId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(connectionId))
            {
                return new { success = false, error = "Connection ID is required" };
            }

            await _sessionManager.RemoveSessionDefaultsAsync(connectionId);

            _logger.LogInformation("Cleared session defaults for connection {ConnectionId}", connectionId);

            return new
            {
                success = true,
                connection_id = connectionId,
                message = "Session defaults cleared successfully"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing session defaults for connection {ConnectionId}", connectionId);
            return new { success = false, error = $"Error clearing session defaults: {ex.Message}" };
        }
    }

    /// <summary>
    /// Resolves the effective session context for the current request.
    /// This shows what session context would be used based on current defaults and any provided parameters.
    /// </summary>
    /// <param name="userId">Optional explicit user identifier</param>
    /// <param name="agentId">Optional explicit agent identifier</param>
    /// <param name="runId">Optional explicit run identifier</param>
    /// <param name="connectionId">Optional connection identifier for defaults lookup</param>
    /// <returns>Resolved session context with precedence information</returns>
    [McpServerTool(Name = "memory_resolve_session"), Description("Resolves the effective session context for the current request")]
    public async Task<object> ResolveSessionContextAsync(
        [Description("Optional explicit user identifier")] string? userId = null,
        [Description("Optional explicit agent identifier")] string? agentId = null,
        [Description("Optional explicit run identifier")] string? runId = null,
        [Description("Optional connection identifier for defaults lookup")] string? connectionId = null)
    {
        try
        {
            // Generate connection ID if not provided
            connectionId ??= Guid.NewGuid().ToString();

            // Resolve session context using the same logic as other tools
            var sessionContext = await _sessionResolver.ResolveSessionContextAsync(
                connectionId,
                userId,
                agentId,
                runId);

            _logger.LogDebug("Resolved session context: {SessionContext}", sessionContext);

            return new
            {
                success = true,
                resolved_context = new
                {
                    user_id = sessionContext.UserId,
                    agent_id = sessionContext.AgentId,
                    run_id = sessionContext.RunId
                },
                precedence_info = new
                {
                    user_id_source = !string.IsNullOrEmpty(userId) ? "explicit" : "default",
                    agent_id_source = !string.IsNullOrEmpty(agentId) ? "explicit" : "default",
                    run_id_source = !string.IsNullOrEmpty(runId) ? "explicit" : "default"
                },
                connection_id = connectionId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resolving session context");
            return new { success = false, error = $"Error resolving session context: {ex.Message}" };
        }
    }
} 