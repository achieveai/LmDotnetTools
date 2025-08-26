using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Resolves session context using precedence rules and transport context.
/// </summary>
public interface ISessionContextResolver
{
    /// <summary>
    /// Resolves session context using precedence hierarchy.
    /// Precedence: Explicit Parameters > Transport Context > System Defaults
    /// </summary>
    Task<SessionContext> ResolveSessionContextAsync(
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Validates that a session context is valid and accessible.
    /// </summary>
    Task<bool> ValidateSessionContextAsync(
        SessionContext sessionContext,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Gets the default session context based on transport context and system defaults.
    /// </summary>
    Task<SessionContext> GetDefaultSessionContextAsync(
        CancellationToken cancellationToken = default
    );
}
