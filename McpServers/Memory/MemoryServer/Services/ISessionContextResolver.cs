using MemoryServer.Models;

namespace MemoryServer.Services;

/// <summary>
/// Resolves session context using precedence rules and session defaults.
/// </summary>
public interface ISessionContextResolver
{
    /// <summary>
    /// Resolves session context using precedence hierarchy.
    /// Precedence: Explicit Parameters > HTTP Headers > Session Init > System Defaults
    /// </summary>
    Task<SessionContext> ResolveSessionContextAsync(
        string connectionId,
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates that a session context is valid and accessible.
    /// </summary>
    Task<bool> ValidateSessionContextAsync(SessionContext sessionContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the effective session context for a connection without explicit parameters.
    /// </summary>
    Task<SessionContext> GetEffectiveSessionContextAsync(string connectionId, CancellationToken cancellationToken = default);
} 