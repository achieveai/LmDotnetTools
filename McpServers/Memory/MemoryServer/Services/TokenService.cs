using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using MemoryServer.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MemoryServer.Services;

/// <summary>
/// Service for generating and validating JWT tokens
/// </summary>
public class TokenService : ITokenService
{
    private readonly JwtOptions _jwtOptions;
    private readonly ILogger<TokenService> _logger;
    private readonly SymmetricSecurityKey _signingKey;

    public TokenService(IOptions<JwtOptions> jwtOptions, ILogger<TokenService> logger)
    {
        ArgumentNullException.ThrowIfNull(jwtOptions);
        _jwtOptions = jwtOptions.Value;
        _logger = logger;

        if (!_jwtOptions.IsValid())
        {
            throw new InvalidOperationException("JWT options are not properly configured");
        }

        _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.Secret));
    }

    /// <summary>
    /// Generates a JWT token for the specified user and agent
    /// </summary>
    /// <param name="userId">The user identifier</param>
    /// <param name="agentId">The agent identifier</param>
    /// <returns>A JWT token string</returns>
    public string GenerateToken(string userId, string agentId)
    {
        if (string.IsNullOrEmpty(userId))
        {
            throw new ArgumentException("UserId cannot be null or empty", nameof(userId));
        }

        if (string.IsNullOrEmpty(agentId))
        {
            throw new ArgumentException("AgentId cannot be null or empty", nameof(agentId));
        }

        var tokenHandler = new JwtSecurityTokenHandler();
        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, userId),
            new Claim("userId", userId),
            new Claim("agentId", agentId),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64
            ),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(_jwtOptions.ExpirationMinutes),
            Issuer = _jwtOptions.Issuer,
            Audience = _jwtOptions.Audience,
            SigningCredentials = credentials,
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var tokenString = tokenHandler.WriteToken(token);

        _logger.LogDebug("Generated JWT token for userId: {UserId}, agentId: {AgentId}", userId, agentId);

        return tokenString;
    }

    /// <summary>
    /// Validates a JWT token and extracts claims
    /// </summary>
    /// <param name="token">The JWT token to validate</param>
    /// <returns>True if the token is valid, false otherwise</returns>
    public bool ValidateToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        try
        {
            var tokenHandler = new JwtSecurityTokenHandler();
            var validationParameters = new TokenValidationParameters
            {
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _signingKey,
                ValidateIssuer = true,
                ValidIssuer = _jwtOptions.Issuer,
                ValidateAudience = true,
                ValidAudience = _jwtOptions.Audience,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.Zero,
            };

            _ = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Token validation failed for token: {Token}",
                token[..Math.Min(token.Length, 20)] + "..."
            );
            return false;
        }
    }
}
