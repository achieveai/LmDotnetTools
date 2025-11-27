using AchieveAi.LmDotnetTools.LmCore.Utils;
using MemoryServer.Models;
using Microsoft.Extensions.Options;

namespace MemoryServer.Services;

/// <summary>
///     Service for initializing transport-specific session context at startup.
/// </summary>
public class TransportSessionInitializer
{
    private readonly ILogger<TransportSessionInitializer> _logger;
    private readonly MemoryServerOptions _options;
    private readonly ISessionManager _sessionManager;

    public TransportSessionInitializer(
        ISessionManager sessionManager,
        ILogger<TransportSessionInitializer> logger,
        IOptions<MemoryServerOptions> options
    )
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    ///     Initializes session context for STDIO transport by reading environment variables.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults if found, null otherwise</returns>
    public async Task<SessionDefaults?> InitializeStdioSessionAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing STDIO transport session context");

        try
        {
            var sessionDefaults = await _sessionManager.ProcessEnvironmentVariablesAsync(cancellationToken);

            if (sessionDefaults != null)
            {
                _logger.LogInformation("STDIO session initialized with context: {SessionDefaults}", sessionDefaults);

                // Log environment variables found (for debugging)
                LogEnvironmentVariables();
            }
            else
            {
                _logger.LogInformation("No environment variables found for STDIO session context");
            }

            return sessionDefaults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize STDIO session context");
            return null;
        }
    }

    /// <summary>
    ///     Initializes session context for SSE transport by processing URL parameters and headers.
    /// </summary>
    /// <param name="queryParameters">URL query parameters</param>
    /// <param name="headers">HTTP headers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Session defaults if found, null otherwise</returns>
    public async Task<SessionDefaults?> InitializeSseSessionAsync(
        IDictionary<string, string>? queryParameters = null,
        IDictionary<string, string>? headers = null,
        CancellationToken cancellationToken = default
    )
    {
        _logger.LogInformation("Initializing SSE transport session context");

        try
        {
            var sessionDefaults = await _sessionManager.ProcessTransportContextAsync(
                queryParameters,
                headers,
                cancellationToken
            );

            if (sessionDefaults != null)
            {
                _logger.LogInformation("SSE session initialized with context: {SessionDefaults}", sessionDefaults);

                // Log what was found (for debugging)
                LogSseContext(queryParameters, headers);
            }
            else
            {
                _logger.LogInformation("No URL parameters or headers found for SSE session context");
            }

            return sessionDefaults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SSE session context");
            return null;
        }
    }

    /// <summary>
    ///     Performs cleanup of expired session defaults.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of cleaned up sessions</returns>
    public async Task<int> CleanupExpiredSessionsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting cleanup of expired session defaults");

        try
        {
            var maxAge = TimeSpan.FromMinutes(_options.SessionDefaults.MaxSessionAge);
            var cleanedCount = await _sessionManager.CleanupExpiredSessionsAsync(maxAge, cancellationToken);

            if (cleanedCount > 0)
            {
                _logger.LogInformation("Cleaned up {Count} expired session defaults", cleanedCount);
            }
            else
            {
                _logger.LogDebug("No expired session defaults found for cleanup");
            }

            return cleanedCount;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cleanup expired session defaults");
            return 0;
        }
    }

    /// <summary>
    ///     Validates that the transport session context is properly configured.
    /// </summary>
    /// <param name="sessionDefaults">Session defaults to validate</param>
    /// <returns>True if valid, false otherwise</returns>
    public bool ValidateSessionContext(SessionDefaults? sessionDefaults)
    {
        if (sessionDefaults == null)
        {
            _logger.LogWarning("Session context validation failed: No session defaults provided");
            return false;
        }

        // Validate user ID is present (required for memory operations)
        if (string.IsNullOrWhiteSpace(sessionDefaults.UserId))
        {
            _logger.LogWarning("Session context validation failed: UserId is required");
            return false;
        }

        // Validate identifier lengths
        if (sessionDefaults.UserId.Length > 100)
        {
            _logger.LogWarning(
                "Session context validation failed: UserId too long ({Length} > 100)",
                sessionDefaults.UserId.Length
            );
            return false;
        }

        if (!string.IsNullOrEmpty(sessionDefaults.AgentId) && sessionDefaults.AgentId.Length > 100)
        {
            _logger.LogWarning(
                "Session context validation failed: AgentId too long ({Length} > 100)",
                sessionDefaults.AgentId.Length
            );
            return false;
        }

        if (!string.IsNullOrEmpty(sessionDefaults.RunId) && sessionDefaults.RunId.Length > 100)
        {
            _logger.LogWarning(
                "Session context validation failed: RunId too long ({Length} > 100)",
                sessionDefaults.RunId.Length
            );
            return false;
        }

        _logger.LogDebug("Session context validation passed for {SessionDefaults}", sessionDefaults);
        return true;
    }

    /// <summary>
    ///     Logs environment variables for debugging purposes.
    /// </summary>
    private void LogEnvironmentVariables()
    {
        var envVars = new[]
        {
            ("MCP_MEMORY_USER_ID", EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_USER_ID")),
            (
                "MCP_MEMORY_AGENT_ID",
                EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_AGENT_ID")
            ),
            ("MCP_MEMORY_RUN_ID", EnvironmentVariableHelper.GetEnvironmentVariableWithFallback("MCP_MEMORY_RUN_ID")),
        };

        foreach (var (name, value) in envVars)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _logger.LogDebug("Found environment variable {Name}={Value}", name, value);
            }
        }
    }

    /// <summary>
    ///     Logs SSE context for debugging purposes.
    /// </summary>
    private void LogSseContext(IDictionary<string, string>? queryParameters, IDictionary<string, string>? headers)
    {
        if (queryParameters != null && queryParameters.Any())
        {
            var relevantParams = queryParameters.Where(kvp => kvp.Key.EndsWith("_id")).ToList();

            if (relevantParams.Count != 0)
            {
                _logger.LogDebug(
                    "Found URL parameters: {Parameters}",
                    string.Join(", ", relevantParams.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                );
            }
        }

        if (headers != null && headers.Any())
        {
            var relevantHeaders = headers.Where(kvp => kvp.Key.StartsWith("X-Memory-")).ToList();

            if (relevantHeaders.Count != 0)
            {
                _logger.LogDebug(
                    "Found HTTP headers: {Headers}",
                    string.Join(", ", relevantHeaders.Select(kvp => $"{kvp.Key}={kvp.Value}"))
                );
            }
        }
    }
}
