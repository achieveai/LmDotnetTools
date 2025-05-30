using MemoryServer.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Resolves session context using precedence rules and session defaults.
/// </summary>
public class SessionContextResolver : ISessionContextResolver
{
    private readonly ISessionManager _sessionManager;
    private readonly ILogger<SessionContextResolver> _logger;
    private readonly SessionDefaultsOptions _options;

    public SessionContextResolver(
        ISessionManager sessionManager,
        ILogger<SessionContextResolver> logger,
        IOptions<MemoryServerOptions> options)
    {
        _sessionManager = sessionManager;
        _logger = logger;
        _options = options.Value.SessionDefaults;
    }

    /// <summary>
    /// Resolves session context using precedence hierarchy.
    /// Precedence: Explicit Parameters > HTTP Headers > Session Init > System Defaults
    /// </summary>
    public async Task<SessionContext> ResolveSessionContextAsync(
        string connectionId,
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Resolving session context for connection {ConnectionId}", connectionId);

        // Get session defaults for this connection
        var sessionDefaults = await _sessionManager.GetSessionDefaultsAsync(connectionId, cancellationToken);

        // Resolve using precedence hierarchy
        var sessionContext = sessionDefaults?.ResolveSessionContext(
            explicitUserId,
            explicitAgentId,
            explicitRunId,
            _options.DefaultUserId) ?? new SessionContext
        {
            UserId = explicitUserId ?? _options.DefaultUserId,
            AgentId = explicitAgentId,
            RunId = explicitRunId
        };

        _logger.LogDebug("Resolved session context for connection {ConnectionId}: {SessionContext}", connectionId, sessionContext);
        return sessionContext;
    }

    /// <summary>
    /// Validates that a session context is valid and accessible.
    /// </summary>
    public Task<bool> ValidateSessionContextAsync(SessionContext sessionContext, CancellationToken cancellationToken = default)
    {
        // Basic validation rules
        if (string.IsNullOrWhiteSpace(sessionContext.UserId))
        {
            _logger.LogWarning("Session context validation failed: UserId is required");
            return Task.FromResult(false);
        }

        if (sessionContext.UserId.Length > 100)
        {
            _logger.LogWarning("Session context validation failed: UserId too long ({Length} > 100)", sessionContext.UserId.Length);
            return Task.FromResult(false);
        }

        if (!string.IsNullOrEmpty(sessionContext.AgentId) && sessionContext.AgentId.Length > 100)
        {
            _logger.LogWarning("Session context validation failed: AgentId too long ({Length} > 100)", sessionContext.AgentId.Length);
            return Task.FromResult(false);
        }

        if (!string.IsNullOrEmpty(sessionContext.RunId) && sessionContext.RunId.Length > 100)
        {
            _logger.LogWarning("Session context validation failed: RunId too long ({Length} > 100)", sessionContext.RunId.Length);
            return Task.FromResult(false);
        }

        // Additional validation rules can be added here
        // For example, checking against allowed user lists, etc.

        _logger.LogDebug("Session context validation passed for {SessionContext}", sessionContext);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets the effective session context for a connection without explicit parameters.
    /// </summary>
    public async Task<SessionContext> GetEffectiveSessionContextAsync(string connectionId, CancellationToken cancellationToken = default)
    {
        return await ResolveSessionContextAsync(connectionId, cancellationToken: cancellationToken);
    }
} 