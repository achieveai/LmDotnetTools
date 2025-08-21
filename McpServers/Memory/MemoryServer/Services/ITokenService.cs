namespace MemoryServer.Services;

/// <summary>
/// Service for generating and validating JWT tokens
/// </summary>
public interface ITokenService
{
    /// <summary>
    /// Generates a JWT token for the specified user and agent
    /// </summary>
    /// <param name="userId">The user identifier</param>
    /// <param name="agentId">The agent identifier</param>
    /// <returns>A JWT token string</returns>
    string GenerateToken(string userId, string agentId);

    /// <summary>
    /// Validates a JWT token and extracts claims
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <returns>True if the token is valid, false otherwise</returns>
    bool ValidateToken(string token);
}