using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
/// Resolves session context using precedence rules and transport context.
/// </summary>
public class SessionContextResolver : ISessionContextResolver
{
    private readonly ILogger<SessionContextResolver> _logger;
    private readonly SessionDefaultsOptions _options;

    public SessionContextResolver(
        ILogger<SessionContextResolver> logger,
        IOptions<MemoryServerOptions> options)
    {
        _logger = logger;
        _options = options.Value.SessionDefaults;
    }

    /// <summary>
    /// Resolves session context using precedence hierarchy.
    /// Precedence: Explicit Parameters > Transport Context > System Defaults
    /// </summary>
    public Task<SessionContext> ResolveSessionContextAsync(
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Resolving session context with explicit parameters: UserId={UserId}, AgentId={AgentId}, RunId={RunId}", 
            explicitUserId, explicitAgentId, explicitRunId);

        var sessionContext = new SessionContext
        {
            // Start with explicit parameters (highest precedence)
            UserId = explicitUserId ?? GetFromTransportContext("userId") ?? _options.DefaultUserId,
            AgentId = explicitAgentId ?? GetFromTransportContext("agentId"),
            RunId = explicitRunId ?? GetFromTransportContext("runId") ?? GenerateDefaultRunId()
        };

        _logger.LogDebug("Resolved session context: {SessionContext}", sessionContext);
        return Task.FromResult(sessionContext);
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

        _logger.LogDebug("Session context validation passed for {SessionContext}", sessionContext);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Gets the default session context based on transport context and system defaults.
    /// </summary>
    public Task<SessionContext> GetDefaultSessionContextAsync(CancellationToken cancellationToken = default)
    {
        return ResolveSessionContextAsync(cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Gets session context from transport-specific sources.
    /// </summary>
    private string? GetFromTransportContext(string parameterName)
    {
        // Try environment variables first (STDIO transport)
        var envValue = parameterName.ToLowerInvariant() switch
        {
            "userid" => Environment.GetEnvironmentVariable("MCP_MEMORY_USER_ID"),
            "agentid" => Environment.GetEnvironmentVariable("MCP_MEMORY_AGENT_ID"),
            "runid" => Environment.GetEnvironmentVariable("MCP_MEMORY_RUN_ID"),
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            _logger.LogDebug("Found {ParameterName} in environment variables: {Value}", parameterName, envValue);
            return envValue;
        }

        // TODO: Add support for HTTP headers and URL parameters when available in context
        // This will be implemented when we update the transport layer

        return null;
    }

    /// <summary>
    /// Generates a default runId based on current date.
    /// </summary>
    private string GenerateDefaultRunId()
    {
        var defaultRunId = DateTime.UtcNow.ToString("yyyyMMdd");
        _logger.LogDebug("Generated default runId: {RunId}", defaultRunId);
        return defaultRunId;
    }
} 