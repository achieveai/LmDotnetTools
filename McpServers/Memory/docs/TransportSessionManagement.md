# Transport-Aware Session Management

## Overview

The MemoryServer supports transport-aware session management, allowing session context (userId, agentId, runId) to be provided through transport-specific mechanisms instead of requiring LLMs to pass these parameters in every tool call.

## Session Context Model

The simplified session context consists of three components:

- **userId** (required) - User identifier for data isolation
- **agentId** (optional) - Agent identifier for multi-agent scenarios  
- **runId** (optional) - Run/session identifier for conversation or workflow isolation

## Supported Transport Methods

### STDIO Transport (Environment Variables)

For local STDIO connections, session context can be provided via environment variables:

```bash
# Set session context via environment variables
export MCP_MEMORY_USER_ID="user123"
export MCP_MEMORY_AGENT_ID="agent456"
export MCP_MEMORY_RUN_ID="$(date +%Y%m%d)"  # Daily sessions

# Start the server
dotnet run --project MemoryServer
```

**Environment Variable Names:**
- `MCP_MEMORY_USER_ID` - User identifier for session isolation
- `MCP_MEMORY_AGENT_ID` - Agent identifier for multi-agent scenarios
- `MCP_MEMORY_RUN_ID` - Run identifier for conversation/workflow isolation

### SSE Transport (URL Parameters)

For web-based SSE connections, session context can be provided via URL query parameters:

```
GET /mcp?userId=user123&agentId=agent456&runId=project-alpha
```

**URL Parameter Names:**
- `userId` - User identifier for session isolation
- `agentId` - Agent identifier for multi-agent scenarios  
- `runId` - Run identifier for conversation/workflow isolation

### HTTP Headers (Both Transports)

Session context can also be provided via HTTP headers:

```
X-Memory-User-ID: user123
X-Memory-Agent-ID: agent456
X-Memory-Run-ID: project-alpha
```

## RunId Use Cases

The `runId` parameter provides flexible session isolation for various scenarios:

### Daily Sessions
```bash
export MCP_MEMORY_RUN_ID="$(date +%Y%m%d)"
# Creates separate memory contexts for each day
```

### Project-Based Sessions
```bash
export MCP_MEMORY_RUN_ID="project-website-redesign"
# Isolates memories by project
```

### Workflow Sessions
```bash
export MCP_MEMORY_RUN_ID="customer-onboarding-$(date +%H%M)"
# Separate contexts for different workflow instances
```

### Development Environment Sessions
```bash
export MCP_MEMORY_RUN_ID="dev-session-$(whoami)"
# Separate contexts per developer
```

## Precedence Hierarchy

The session context resolution follows this precedence order (highest to lowest):

1. **Explicit Parameters** - Parameters passed directly to tool calls
2. **HTTP Headers** - X-Memory-* headers (SSE transport)
3. **Transport Context** - Environment variables (STDIO) or URL parameters (SSE)
4. **System Defaults** - Configured in app settings

## Session Management Benefits

### For Users
- **No Parameter Management** - Set once via environment/URL, use everywhere
- **Flexible Session Control** - Use runId for any logical grouping
- **Transport Independence** - Same session concept works across STDIO and SSE

### For LLMs
- **Simplified Tool Calls** - No need to pass userId/agentId/runId repeatedly
- **Automatic Context** - Session context automatically applied to all operations
- **Consistent Behavior** - Same session context across all tool invocations

## Configuration Examples

### Development Setup
```bash
# ~/.bashrc or equivalent
export MCP_MEMORY_USER_ID="$(whoami)"
export MCP_MEMORY_AGENT_ID="dev-assistant"
export MCP_MEMORY_RUN_ID="dev-$(date +%Y%m%d)"
```

### Production Setup
```bash
# Environment-specific configuration
export MCP_MEMORY_USER_ID="prod-user"
export MCP_MEMORY_AGENT_ID="production-assistant"
export MCP_MEMORY_RUN_ID="prod-session-$(date +%Y%m%d-%H)"
```

### Multi-Project Setup
```bash
# Project-specific sessions
export MCP_MEMORY_USER_ID="team-lead"
export MCP_MEMORY_AGENT_ID="project-manager"
export MCP_MEMORY_RUN_ID="${PROJECT_NAME:-default-project}"
```

## Implementation Notes

- **RunId Generation** - If not provided, defaults to current date (YYYYMMDD)
- **Session Persistence** - Session context persists for the duration of the transport connection
- **Memory Isolation** - Each unique combination of (userId, agentId, runId) creates an isolated memory space
- **Backward Compatibility** - All parameters remain optional with sensible defaults

## Benefits

### For LLM Applications
- **Reduced Cognitive Load**: LLMs no longer need to track and pass session identifiers
- **Fewer Errors**: Eliminates parameter passing mistakes
- **Cleaner Tool Calls**: Tool calls focus on actual functionality, not session management
- **Better Performance**: Fewer tokens used for session parameters

### For Developers
- **Simplified Integration**: Set session context once at connection time
- **Transport Flexibility**: Choose the most appropriate method for your deployment
- **Backward Compatibility**: Existing explicit parameters still work
- **Consistent Behavior**: Same session management across all tools

## Usage Examples

### Example 1: STDIO with Environment Variables

```bash
#!/bin/bash
# setup-memory-session.sh

# Configure session context
export MCP_MEMORY_USER_ID="alice"
export MCP_MEMORY_AGENT_ID="coding-assistant"
export MCP_MEMORY_RUN_ID="$(date +%Y%m%d)"

# Start memory server
dotnet run --project MemoryServer

# Now all tool calls will automatically use this session context
# No need to pass userId, agentId, runId in tool parameters
```

### Example 2: SSE with URL Parameters

```javascript
// Client connection with session context in URL
const mcpClient = new McpClient({
  transport: 'sse',
  url: 'https://localhost:64478/sse?userId=alice&agentId=coding-assistant&runId=project-alpha'
});

// Tool calls automatically inherit session context
await mcpClient.callTool('memory_add', {
  content: 'User prefers TypeScript over JavaScript'
  // No need for userId, agentId, runId parameters
});
```

### Example 3: SSE with HTTP Headers

```javascript
// Client connection with session context in headers
const mcpClient = new McpClient({
  transport: 'sse',
  url: 'https://localhost:64478/sse',
  headers: {
    'X-Memory-User-ID': 'alice',
    'X-Memory-Agent-ID': 'coding-assistant',
    'X-Memory-Run-ID': 'project-alpha'
  }
});
```

## Tool Call Comparison

### Before (Manual Session Management)
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "User prefers dark mode",
      "userId": "alice",
      "agentId": "coding-assistant", 
      "runId": "session-123",
      "connectionId": "conn-456"
    }
  }
}
```

### After (Transport-Aware Session Management)
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "User prefers dark mode"
    }
  }
}
```

## Configuration

### Environment Variables (STDIO)

The server automatically reads these environment variables on startup:

```bash
# Required for user-level isolation
MCP_MEMORY_USER_ID=alice

# Optional for agent-level isolation  
MCP_MEMORY_AGENT_ID=coding-assistant

# Optional for run-level isolation
MCP_MEMORY_RUN_ID=session-123
```

### URL Parameters (SSE)

Supported query parameters:

- `userId` - User identifier
- `agentId` - Agent identifier (optional)
- `runId` - Run identifier (optional)

### HTTP Headers (SSE)

Supported headers:

- `X-Memory-User-ID` - User identifier
- `X-Memory-Agent-ID` - Agent identifier (optional)
- `X-Memory-Run-ID` - Run identifier (optional)

## Migration Guide

### For Existing Integrations

1. **No Breaking Changes**: Existing tool calls with explicit parameters continue to work
2. **Gradual Migration**: You can migrate tools one by one
3. **Mixed Mode**: Some tools can use transport context while others use explicit parameters

### Migration Steps

1. **Identify Session Context**: Determine your userId, agentId, and runId
2. **Choose Transport Method**: 
   - Use environment variables for STDIO
   - Use URL parameters or headers for SSE
3. **Update Client Code**: Remove explicit session parameters from tool calls
4. **Test**: Verify session isolation still works correctly

## Best Practices

### Security
- **Environment Variables**: Secure for local STDIO deployments
- **URL Parameters**: Be cautious with logging and URL exposure
- **HTTP Headers**: Preferred for SSE deployments

### Session Management
- **User ID**: Always required, identifies the user
- **Agent ID**: Optional, useful for multi-agent scenarios
- **Run ID**: Optional, useful for conversation-level isolation

### Error Handling
- **Missing Context**: Server falls back to system defaults
- **Invalid Context**: Server logs warnings but continues operation
- **Connection Failures**: Session context is re-established on reconnection

## Troubleshooting

### Common Issues

1. **Session Context Not Applied**
   - Check environment variables are set correctly
   - Verify URL parameters are properly encoded
   - Ensure headers are being sent

2. **Wrong Session Isolation**
   - Check precedence hierarchy
   - Verify explicit parameters aren't overriding transport context
   - Review session resolution logs

3. **Performance Issues**
   - Monitor session defaults storage
   - Check for excessive session context updates
   - Review connection lifecycle management

### Debugging

Enable debug logging to see session context resolution:

```json
{
  "Logging": {
    "LogLevel": {
      "MemoryServer.Services.SessionManager": "Debug",
      "MemoryServer.Services.TransportSessionInitializer": "Debug"
    }
  }
}
```

## Implementation Details

### Architecture Components

1. **SessionDefaults**: Extended with new source types
2. **SessionManager**: Added transport-specific processing methods
3. **TransportSessionInitializer**: New service for startup initialization
4. **Program.cs**: Updated with middleware for SSE and startup logic for STDIO

### Database Schema

Session defaults are stored in the existing `session_defaults` table with updated source enumeration:

```sql
CREATE TABLE session_defaults (
    connection_id TEXT PRIMARY KEY,
    user_id TEXT,
    agent_id TEXT,
    run_id TEXT,
    metadata TEXT,
    source INTEGER, -- 0=System, 1=SessionInit, 2=EnvironmentVariables/UrlParameters, 3=Headers
    created_at TEXT
);
```

### Performance Considerations

- **Startup Cost**: Minimal overhead for environment variable reading
- **Connection Cost**: Small overhead for URL parameter parsing
- **Runtime Cost**: No additional cost, uses existing session resolution
- **Memory Usage**: Negligible increase for session defaults storage

## Future Enhancements

### Planned Features
- **Dynamic Session Updates**: Update session context without reconnection
- **Session Context Validation**: Validate session identifiers against external systems
- **Multi-Tenant Support**: Enhanced isolation for multi-tenant deployments
- **Session Analytics**: Track session usage patterns and performance

### Extensibility
- **Custom Transport Sources**: Support for additional transport mechanisms
- **Session Context Enrichment**: Automatic enhancement of session context
- **Integration Hooks**: Callbacks for session lifecycle events 