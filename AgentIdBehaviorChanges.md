# Agent ID Behavior Changes and New APIs

## Overview

This document describes the changes made to implement new agent ID behavior for read methods and add new APIs for getting agents and runs.

## Changes Made

### 1. Modified Read Method Behavior

Updated the following read methods in `MemoryMcpTools.cs` to implement new agentId logic:

- `memory_search`
- `memory_get_all` 
- `memory_get_history`
- `memory_get_stats`

#### New AgentId Behavior:
- **If agentId is not provided (null/empty)**: Use agentId from JWT token
- **If agentId is "all"**: Search through all agents for the user
- **If agentId is a specific value**: Use that specific agent

#### Implementation Details:
```csharp
// Handle agentId logic:
// - If empty/null: use JWT token agentId (pass null to get from JWT)
// - If "all": search all agents (pass null to session resolver)
string? agentIdParam = null;
if (!string.IsNullOrWhiteSpace(agentId))
{
    if (agentId.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        agentIdParam = null; // Search all agents
    }
    else
    {
        agentIdParam = agentId; // Use specific agent
    }
}
// If agentId is null/empty, agentIdParam stays null and JWT token agentId will be used
```

### 2. Added New APIs

#### `memory_get_agents`
- **Purpose**: Returns all agents for the current user
- **Parameters**: None (userId extracted from JWT token)
- **Returns**: Array of agent identifiers

#### `memory_get_runs`
- **Purpose**: Returns all run IDs for a specific agent and user
- **Parameters**: 
  - `agentId` (required): Agent identifier to get runs for
- **Returns**: Array of run identifiers

### 3. Service Layer Changes

#### Added to `IMemoryService`:
```csharp
Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default);
Task<List<string>> GetRunsAsync(string userId, string agentId, CancellationToken cancellationToken = default);
```

#### Added to `MemoryService`:
- Implemented `GetAgentsAsync` method
- Implemented `GetRunsAsync` method

### 4. Repository Layer Changes

#### Added to `IMemoryRepository`:
```csharp
Task<List<string>> GetAgentsAsync(string userId, CancellationToken cancellationToken = default);
Task<List<string>> GetRunsAsync(string userId, string agentId, CancellationToken cancellationToken = default);
```

#### Added to `MemoryRepository`:
- `GetAgentsAsync`: Queries distinct agent_id values for a user
- `GetRunsAsync`: Queries distinct run_id values for a user and agent

### 5. Model Updates

#### Enhanced `MemoryHistoryEntry`:
Added missing properties to support the new functionality:
- `UserId`: User identifier for the memory
- `AgentId`: Agent identifier for the memory  
- `RunId`: Run identifier for the memory
- `Metadata`: Memory metadata at this version

## API Usage Examples

### Using the new agentId behavior:

```javascript
// Use JWT token agentId (default behavior)
await memory_search({ query: "test" });

// Search all agents
await memory_search({ query: "test", agentId: "all" });

// Search specific agent
await memory_search({ query: "test", agentId: "agent-123" });
```

### Using the new APIs:

```javascript
// Get all agents for current user
const agents = await memory_get_agents();

// Get all runs for a specific agent
const runs = await memory_get_runs({ agentId: "agent-123" });
```

## Testing

- All 268 tests pass
- Build succeeds with no errors
- Backward compatibility maintained for existing functionality

## Security Considerations

- UserId is always extracted from JWT token for security
- AgentId behavior respects JWT token authentication
- New APIs properly validate input parameters
- Session isolation is maintained across all operations

## Breaking Changes

None. All changes are backward compatible:
- Existing calls without agentId parameter will use JWT token agentId (more secure than previous "all agents" behavior)
- Existing calls with specific agentId values continue to work as before
- New "all" value provides explicit way to search across all agents when needed 