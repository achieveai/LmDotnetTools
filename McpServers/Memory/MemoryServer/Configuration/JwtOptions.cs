namespace MemoryServer.Configuration;

/// <summary>
/// Configuration options for JWT token generation and validation
/// </summary>
public class JwtOptions
{
    /// <summary>
    /// The secret key used for signing JWT tokens
    /// </summary>
    public string Secret { get; set; } = string.Empty;

    /// <summary>
    /// The issuer of the JWT token
    /// </summary>
    public string Issuer { get; set; } = "MemoryServer";

    /// <summary>
    /// The audience for the JWT token
    /// </summary>
    public string Audience { get; set; } = "MemoryServer";

    /// <summary>
    /// Token expiration time in minutes
    /// </summary>
    public int ExpirationMinutes { get; set; } = 60 * 24 * 30; // 30 days

    /// <summary>
    /// Validates that all required JWT options are configured
    /// </summary>
    public bool IsValid()
    {
        return !string.IsNullOrEmpty(Secret) &&
               !string.IsNullOrEmpty(Issuer) &&
               !string.IsNullOrEmpty(Audience) &&
               ExpirationMinutes > 0;
    }
}