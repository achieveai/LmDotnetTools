# Transport & Sessions

This consolidated document merges the following original files:

- TransportSessionManagement.md  
- demo-transport-session.md  
- ConnectionIdRemovalPlan.md

---

## Transport Session Management

<details>
<summary>Full Transport Session Management</summary>

```markdown
<!-- Begin TransportSessionManagement.md content -->
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
<!-- End TransportSessionManagement.md content -->
```

</details>  

---

## Demo: Transport Session

<details>
<summary>Full Demo: Transport Session</summary>

```markdown
<!-- Begin demo-transport-session.md content -->
# Transport-Aware Session Management Demo

This document demonstrates the new transport-aware session management feature that eliminates the need for LLMs to pass `userId`, `agentId`, and `runId` parameters in every tool call.

## Demo 1: STDIO Transport with Environment Variables

### Setup
```bash
# Set session context via environment variables
export MCP_MEMORY_USER_ID="alice"
export MCP_MEMORY_AGENT_ID="coding-assistant"
export MCP_MEMORY_RUN_ID="$(date +%Y%m%d)"  # Daily sessions

echo "Session context configured:"
echo "  User ID: $MCP_MEMORY_USER_ID"
echo "  Agent ID: $MCP_MEMORY_AGENT_ID"
echo "  Run ID: $MCP_MEMORY_RUN_ID"
```

### Start Server
```bash
# Start the MemoryServer with STDIO transport
cd McpServers/Memory/MemoryServer
dotnet run
```

### Example Tool Calls (Simplified)
```json
// Before: LLM had to pass all parameters
{
  "method": "tools/call",
  "params": {
    "name": "add_memory",
    "arguments": {
      "content": "Alice prefers TypeScript over JavaScript",
      "userId": "alice",
      "agentId": "coding-assistant", 
      "runId": "20240115"
    }
  }
}

// After: LLM only needs to pass content
{
  "method": "tools/call",
  "params": {
    "name": "add_memory",
    "arguments": {
      "content": "Alice prefers TypeScript over JavaScript"
    }
  }
}
```

## Demo 2: SSE Transport with URL Parameters

### Setup
```bash
# Start server
cd McpServers/Memory/MemoryServer
dotnet run --urls "https://localhost:5001"
```

### Connect with Session Context in URL
```javascript
// Client connects with session context in URL
const eventSource = new EventSource(
  'https://localhost:5001/mcp?userId=alice&agentId=coding-assistant&runId=project-alpha'
);

// All subsequent tool calls automatically use this session context
```

## Demo 3: Project-Based Session Isolation

### Scenario: Multiple Projects
```bash
# Project Alpha session
export MCP_MEMORY_USER_ID="alice"
export MCP_MEMORY_AGENT_ID="coding-assistant"
export MCP_MEMORY_RUN_ID="project-alpha"

# Add project-specific memories
echo "Adding memories for Project Alpha..."
# Tool calls will be isolated to project-alpha runId
```

```bash
# Project Beta session (new terminal/session)
export MCP_MEMORY_USER_ID="alice"
export MCP_MEMORY_AGENT_ID="coding-assistant"
export MCP_MEMORY_RUN_ID="project-beta"

# Add different project memories
echo "Adding memories for Project Beta..."
# Tool calls will be isolated to project-beta runId
```

### Verification
```bash
# Search memories in Project Alpha context
export MCP_MEMORY_RUN_ID="project-alpha"
# search_memories will only return project-alpha memories

# Search memories in Project Beta context  
export MCP_MEMORY_RUN_ID="project-beta"
# search_memories will only return project-beta memories
```

## Demo 4: Daily Session Pattern

### Automatic Daily Isolation
```bash
# Setup automatic daily sessions
export MCP_MEMORY_USER_ID="alice"
export MCP_MEMORY_AGENT_ID="daily-assistant"
export MCP_MEMORY_RUN_ID="$(date +%Y%m%d)"

echo "Today's session: $MCP_MEMORY_RUN_ID"
# Each day gets a separate memory context automatically
```

### Weekly Review Pattern
```bash
# Review this week's sessions
for day in {0..6}; do
  run_id=$(date -d "$day days ago" +%Y%m%d)
  echo "Reviewing memories from $run_id"
  export MCP_MEMORY_RUN_ID="$run_id"
  # Call search_memories to review that day's memories
done
```

## Demo 5: Development Workflow Sessions

### Feature Development Session
```bash
# Start feature development session
export MCP_MEMORY_USER_ID="developer"
export MCP_MEMORY_AGENT_ID="code-assistant"
export MCP_MEMORY_RUN_ID="feature-user-auth-$(date +%Y%m%d)"

# All memories during feature development are isolated
echo "Working on user authentication feature..."
```

### Code Review Session
```bash
# Switch to code review session
export MCP_MEMORY_RUN_ID="code-review-$(date +%Y%m%d)"

# Different context for code review activities
echo "Reviewing code changes..."
```

### Bug Fix Session
```bash
# Emergency bug fix session
export MCP_MEMORY_RUN_ID="bugfix-urgent-$(date +%Y%m%d-%H%M)"

# Isolated context for bug investigation
echo "Investigating production bug..."
```

## Demo 6: Multi-User Collaboration

### Team Lead Session
```bash
export MCP_MEMORY_USER_ID="team-lead"
export MCP_MEMORY_AGENT_ID="project-manager"
export MCP_MEMORY_RUN_ID="sprint-planning-$(date +%Y%m%d)"
```

### Developer Session
```bash
export MCP_MEMORY_USER_ID="developer-1"
export MCP_MEMORY_AGENT_ID="coding-assistant"
export MCP_MEMORY_RUN_ID="development-$(date +%Y%m%d)"
```

### QA Session
```bash
export MCP_MEMORY_USER_ID="qa-engineer"
export MCP_MEMORY_AGENT_ID="testing-assistant"
export MCP_MEMORY_RUN_ID="testing-$(date +%Y%m%d)"
```

## Benefits Demonstrated

### 1. Simplified Tool Calls
- **Before**: Every tool call required userId, agentId, runId parameters
- **After**: Tool calls only need domain-specific parameters

### 2. Flexible Session Management
- **Daily Sessions**: Automatic date-based isolation
- **Project Sessions**: Logical project-based grouping
- **Workflow Sessions**: Task-specific memory contexts
- **User Sessions**: Multi-user collaboration support

### 3. Transport Independence
<!-- End demo-transport-session.md content -->
```

</details>  

---

## Connection ID Removal Plan

<details>
<summary>Full Connection ID Removal Plan</summary>

```markdown
<!-- Begin ConnectionIdRemovalPlan.md content -->
# Connection ID Removal Plan

## Objective

Outlines the strategy for cleaning up stale connection IDs from the database, including scheduling, detection of inactive sessions, batch deletion processes, and safety measures.

## Overview

This document outlines the plan to remove `connectionId` from the MemoryServer architecture and simplify session management to focus on `runId` as the primary session identifier.

## Current State Analysis

### Current Architecture Issues
1. **Dual Session Identifiers**: Both `connectionId` and `runId` serve similar purposes
2. **Transport Coupling**: `connectionId` is tied to transport implementation details
3. **Complex Session Storage**: Requires database storage for session defaults
4. **User Confusion**: Users don't understand when to use `connectionId` vs `runId`
5. **Over-Engineering**: Most use cases don't need connection-level isolation

### Current Components to Modify
- `SessionDefaults` model (remove connectionId)
- `SessionManager` service (simplify session handling)
- `SessionContextResolver` (remove connection-based resolution)
- `TransportSessionInitializer` (remove connection tracking)
- Database schema (remove session_defaults table)
- All MCP tools (remove connectionId parameters)

## Target Architecture

### Simplified Session Model
```csharp
public class SessionContext
{
    public string UserId { get; set; } = string.Empty;    // Required
    public string? AgentId { get; set; }                  // Optional
    public string? RunId { get; set; }                    // Optional - primary session identifier
}
```

### Session Resolution Strategy
1. **Explicit Parameters** (highest precedence) - Tool call parameters
2. **Transport Context** - Environment variables, URL parameters, HTTP headers
3. **System Defaults** (lowest precedence) - Configuration fallbacks

### RunId as Primary Session Identifier
- **Daily Sessions**: `runId = "$(date +%Y%m%d)"`
- **Project Sessions**: `runId = "project-alpha"`
- **Workflow Sessions**: `runId = "customer-onboarding-$(timestamp)"`
- **Development Sessions**: `runId = "dev-$(username)-$(date)"`

## Implementation Plan

### Phase 1: Model Simplification

#### 1.1 Update SessionContext Model
```csharp
// File: Models/SessionContext.cs
public class SessionContext
{
    public string UserId { get; set; } = string.Empty;
    public string? AgentId { get; set; }
    public string? RunId { get; set; }
    
    // Remove: ConnectionId, Type, Metadata
}
```

#### 1.2 Remove SessionDefaults Model
```csharp
// File: Models/SessionDefaults.cs - DELETE THIS FILE
// All session defaults logic will be moved to SessionContextResolver
```

#### 1.3 Update Database Schema
```sql
-- Remove session_defaults table entirely
DROP TABLE IF EXISTS session_defaults;

-- Core tables remain unchanged (they already use user_id, agent_id, run_id)
-- No migration needed for memories, entities, relationships tables
```

### Phase 2: Service Layer Simplification

#### 2.1 Simplify SessionContextResolver
```csharp
public interface ISessionContextResolver
{
    Task<SessionContext> ResolveSessionContextAsync(
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default);
}

public class SessionContextResolver : ISessionContextResolver
{
    public async Task<SessionContext> ResolveSessionContextAsync(
        string? explicitUserId = null,
        string? explicitAgentId = null,
        string? explicitRunId = null,
        CancellationToken cancellationToken = default)
    {
        return new SessionContext
        {
            UserId = explicitUserId 
                ?? GetFromTransportContext("userId") 
                ?? GetSystemDefaultUserId(),
            AgentId = explicitAgentId 
                ?? GetFromTransportContext("agentId"),
            RunId = explicitRunId 
                ?? GetFromTransportContext("runId") 
                ?? GenerateDefaultRunId()
        };
    }
    
    private string GenerateDefaultRunId()
    {
        return DateTime.UtcNow.ToString("yyyyMMdd");
    }
}
```

#### 2.2 Remove SessionManager Service
```csharp
// File: Services/SessionManager.cs - DELETE THIS FILE
// File: Services/ISessionManager.cs - DELETE THIS FILE
// Session management logic moves to SessionContextResolver
```

#### 2.3 Remove TransportSessionInitializer
```csharp
// File: Services/TransportSessionInitializer.cs - DELETE THIS FILE
// File: Services/ITransportSessionInitializer.cs - DELETE THIS FILE
// Transport initialization becomes much simpler
```

### Phase 3: Transport Layer Simplification

#### 3.1 Simplify Program.cs
```csharp
// Remove complex session initialization
// Remove transport session initializer registration
// Simplify to basic MCP server setup

public static async Task Main(string[] args)
{
    var builder = WebApplication.CreateBuilder(args);
    
    // Register core services
    builder.Services.AddScoped<ISessionContextResolver, SessionContextResolver>();
    builder.Services.AddScoped<IMemoryService, MemoryService>();
    // Remove: ISessionManager, ITransportSessionInitializer
    
    var app = builder.Build();
    
    // Simple MCP server setup without complex session initialization
    var mcpServer = new McpServer(/* simplified config */);
    
    app.Run();
}
```

#### 3.2 Update Transport Context Extraction
```csharp
public class TransportContextExtractor
{
    public static SessionContext ExtractFromEnvironment()
    {
        return new SessionContext
        {
            UserId = Environment.GetEnvironmentVariable("MCP_MEMORY_USER_ID") ?? "default_user",
            AgentId = Environment.GetEnvironmentVariable("MCP_MEMORY_AGENT_ID"),
            RunId = Environment.GetEnvironmentVariable("MCP_MEMORY_RUN_ID")
        };
    }
    
    public static SessionContext ExtractFromUrlParameters(IQueryCollection query)
    {
        return new SessionContext
        {
            UserId = query["userId"].FirstOrDefault() ?? "default_user",
            AgentId = query["agentId"].FirstOrDefault(),
            RunId = query["runId"].FirstOrDefault()
        };
    }
    
    public static SessionContext ExtractFromHeaders(IHeaderDictionary headers)
    {
        return new SessionContext
        {
            UserId = headers["X-Memory-User-ID"].FirstOrDefault() ?? "default_user",
            AgentId = headers["X-Memory-Agent-ID"].FirstOrDefault(),
            RunId = headers["X-Memory-Run-ID"].FirstOrDefault()
        };
    }
}
```

### Phase 4: Tool Layer Updates

#### 4.1 Simplify All MCP Tools
```csharp
[McpTool("add_memory")]
public async Task<AddMemoryResult> AddMemoryAsync(
    [Description("Memory content")] string content,
    [Description("User identifier")] string? userId = null,
    [Description("Agent identifier")] string? agentId = null,
    [Description("Run identifier")] string? runId = null)
{
    var sessionContext = await _sessionResolver.ResolveSessionContextAsync(userId, agentId, runId);
    return await _memoryService.AddMemoryAsync(content, sessionContext);
}

// Remove connectionId parameter from ALL tools:
// - MemoryMcpTools
// - GraphMcpTools  
// - SessionMcpTools (may be removed entirely)
```

#### 4.2 Update Tool Descriptions
```csharp
// Update all tool parameter descriptions to remove connectionId references
// Focus descriptions on userId (required), agentId (optional), runId (optional)
```

### Phase 5: Testing Updates

#### 5.1 Update Test Data
```csharp
// Update all test cases to remove connectionId
var sessionContext = new SessionContext 
{ 
    UserId = "test_user", 
    AgentId = "test_agent", 
    RunId = "test_run" 
    // Remove: ConnectionId
};
```

#### 5.2 Update Integration Tests
```csharp
// Update McpIntegrationTests to use simplified session management
// Remove connection-based test scenarios
// Add runId-based session isolation tests
```

### Phase 6: Documentation Updates

#### 6.1 Update API Documentation
- Remove all connectionId references
- Focus on runId as the primary session identifier
- Update examples to show practical runId usage patterns

#### 6.2 Update User Guides
- Simplify session management documentation
- Provide clear runId usage examples
- Remove complex connection management instructions

## Migration Strategy

### Backward Compatibility
- **Database**: No migration needed (core tables unchanged)
- **API**: Tools remain backward compatible (connectionId parameter removed but optional parameters still work)
- **Configuration**: Environment variables remain the same

### Deployment Strategy
1. **Deploy new version** with simplified session management
2. **Update client configurations** to remove connectionId usage
3. **Monitor logs** for any session resolution issues
4. **Clean up unused code** after successful deployment

## Benefits After Implementation

### For Users
- **Simpler Configuration**: Only need to set userId, agentId, runId
- **Intuitive Session Control**: runId clearly represents logical sessions
- **Flexible Patterns**: Use runId for any meaningful grouping

### For Developers
- **Reduced Complexity**: Fewer concepts and components
- **Easier Maintenance**: Less code to maintain and debug
- **Clear Architecture**: Single session identifier with clear purpose

### For LLMs
- **Simplified Tool Calls**: Fewer parameters to manage
- **Consistent Behavior**: Same session context across all operations
- **Automatic Context**: Session context resolved transparently

## Risk Mitigation

### Potential Issues
1. **Existing Integrations**: May reference connectionId in client code
2. **Session Isolation**: Need to ensure runId provides adequate isolation
3. **Default Generation**: Default runId generation must be consistent

### Mitigation Strategies
1. **Gradual Rollout**: Deploy to staging environment first
2. **Comprehensive Testing**: Test all session isolation scenarios
3. **Clear Documentation**: Provide migration guide for existing users
4. **Monitoring**: Add logging to track session resolution behavior

## Success Criteria

### Technical Metrics
- [ ] All connectionId references removed from codebase
- [ ] All tests pass with simplified session management
- [ ] Session isolation still works correctly with runId
- [ ] Performance remains the same or improves

### User Experience Metrics
- [ ] Simplified configuration reduces setup time
- [ ] Documentation is clearer and easier to follow
- [ ] User feedback indicates improved usability
- [ ] Support requests related to session management decrease

## Timeline

- **Week 1**: Phase 1-2 (Model and Service Layer)
- **Week 2**: Phase 3-4 (Transport and Tool Layer)
- **Week 3**: Phase 5-6 (Testing and Documentation)
- **Week 4**: Deployment and monitoring

This plan will result in a significantly simplified and more intuitive session management system focused on runId as the primary session identifier.
```


<!-- End ConnectionIdRemovalPlan.md content -->
```

</details>
