# ConnectionId Removal Implementation Plan

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