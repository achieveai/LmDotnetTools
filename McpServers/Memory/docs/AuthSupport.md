# MemoryServer Authentication Support

## Status: ✅ COMPLETED

JWT-based authentication has been successfully implemented for the MemoryServer. The implementation provides secure token-based authentication for SSE transport while maintaining backward compatibility with STDIO transport.

## Implementation Overview

### Phase 1: Infrastructure & Token Generation ✅ COMPLETED
- **NuGet Packages**: Added Microsoft.AspNetCore.Authentication.JwtBearer, System.CommandLine, and System.IdentityModel.Tokens.Jwt
- **Configuration**: Created JwtOptions class with Secret, Issuer, Audience, and ExpirationMinutes properties
- **Token Service**: Implemented ITokenService and TokenService for JWT generation and validation
- **CLI Command**: Added `generate-token` command with --userId and --agentId parameters
- **Configuration Files**: Updated appsettings.json and appsettings.Development.json with JWT settings

### Phase 2: Server Authentication Pipeline ✅ COMPLETED
- **SSE Transport**: JWT authentication enabled for SSE transport with proper middleware configuration
- **STDIO Transport**: Authentication-free operation maintained for STDIO transport
- **Session Context**: Enhanced SessionContextResolver with JWT claims extraction
- **Dependency Injection**: Proper service registration for both transport modes
- **Middleware Ordering**: Correct authentication/authorization middleware placement

### Phase 3: Testing & Validation ✅ COMPLETED
- **All Tests Passing**: 268 tests pass, confirming no regression in existing functionality
- **Token Generation**: Successfully tested CLI token generation
- **Transport Isolation**: STDIO and SSE transports work independently with appropriate authentication requirements

## Usage

### Token Generation

Generate a JWT token using the CLI command:

```bash
# Set environment to Development for testing
$env:ASPNETCORE_ENVIRONMENT = "Development"

# Generate token
./MemoryServer.exe generate-token --userId "your-user-id" --agentId "your-agent-id"
```

Example output:
```
✅ JWT Token generated successfully!

UserId: test-user
AgentId: test-agent
Expires: 2025-06-15 17:07:19 UTC

Token:
eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...

Usage:
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

### SSE Transport with Authentication

When using SSE transport, include the JWT token in the Authorization header:

```http
GET /sse?userId=test-user&agentId=test-agent
Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...
```

### STDIO Transport (No Authentication Required)

STDIO transport continues to work without authentication:

```bash
./MemoryServer.exe  # Runs in STDIO mode by default
```

## Configuration

### JWT Settings

Configure JWT settings in `appsettings.json`:

```json
{
  "Jwt": {
    "Secret": "${JWT_SECRET}",
    "Issuer": "MemoryServer",
    "Audience": "MemoryServer",
    "ExpirationMinutes": 60
  }
}
```

For development, use `appsettings.Development.json` with a hardcoded secret:

```json
{
  "Jwt": {
    "Secret": "test-secret-key-that-is-at-least-256-bits-long-for-hmac-sha256-algorithm-testing",
    "Issuer": "MemoryServer",
    "Audience": "MemoryServer",
    "ExpirationMinutes": 60
  }
}
```

### Environment Variables

Set the JWT secret via environment variable for production:

```bash
export JWT_SECRET="your-production-secret-key-at-least-256-bits-long"
```

## Session Context Resolution

The authentication system enhances session context resolution with the following precedence:

1. **Explicit Parameters** (highest priority)
2. **JWT Token Claims** 
3. **Transport Context** (environment variables for STDIO)
4. **System Defaults** (lowest priority)

JWT tokens include `userId` and `agentId` claims that are automatically extracted and used for session context.

## Security Features

- **HMAC SHA-256 Signing**: Tokens are signed using HMAC SHA-256 algorithm
- **Configurable Expiration**: Token lifetime is configurable (default: 60 minutes)
- **Standard Claims**: Includes standard JWT claims (sub, iat, exp, jti, iss, aud)
- **Transport Isolation**: Authentication only required for SSE transport
- **Secure Defaults**: Proper validation of issuer, audience, and lifetime

## Architecture

### Key Components

1. **JwtOptions**: Configuration class for JWT settings
2. **ITokenService/TokenService**: Token generation and validation
3. **TokenGeneratorCommand**: CLI command for token generation
4. **SessionContextResolver**: Enhanced with JWT claims extraction
5. **Authentication Middleware**: ASP.NET Core JWT Bearer authentication

### Transport-Specific Behavior

- **SSE Transport**: Requires JWT authentication, extracts session context from JWT claims
- **STDIO Transport**: No authentication required, uses environment variables for session context

## Testing

All existing functionality has been preserved:
- ✅ 268 tests passing
- ✅ STDIO transport tests pass (no authentication required)
- ✅ SSE transport functionality maintained
- ✅ Session context resolution works correctly
- ✅ Token generation CLI command functional

## Future Enhancements

Potential future improvements:
1. **Role-Based Authorization**: Add role claims and authorization policies
2. **Token Refresh**: Implement refresh token mechanism
3. **API Key Authentication**: Alternative authentication method for service-to-service calls
4. **Audit Logging**: Enhanced logging of authentication events
5. **Rate Limiting**: Token-based rate limiting for API endpoints
