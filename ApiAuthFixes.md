# API Authentication Fixes

## Overview
This document outlines the changes needed to fix memory API authentication behavior based on JWT token claims.

## Current State
All memory APIs currently accept optional `userId`, `agentId`, and `runId` parameters. The `SessionContextResolver` uses precedence: Explicit Parameters > JWT Claims > Transport Context > System Defaults.

## Required Changes

### Authentication Context
- JWT tokens contain `userId` and `agentId` claims
- These should be extracted automatically from the token
- No need to pass them as explicit parameters for write operations
- For read operations, `agentId` should be optional to allow searching across all user memories

### Write APIs (Remove userId and agentId parameters)
These APIs should get userId and agentId from JWT token only:

1. **memory_add**
   - Remove: `userId` parameter
   - Remove: `agentId` parameter  
   - Keep: `runId` parameter (optional)
   - Behavior: Use userId/agentId from JWT token

2. **memory_update**
   - Remove: `userId` parameter
   - Remove: `agentId` parameter
   - Keep: `runId` parameter (optional)
   - Behavior: Use userId/agentId from JWT token for session validation

3. **memory_delete**
   - Remove: `userId` parameter
   - Remove: `agentId` parameter
   - Keep: `runId` parameter (optional)
   - Behavior: Use userId/agentId from JWT token for session validation

4. **memory_delete_all**
   - Remove: `userId` parameter
   - Remove: `agentId` parameter
   - Keep: `runId` parameter (optional)
   - Behavior: Use userId/agentId from JWT token for session targeting

### Read APIs (Make agentId optional)
These APIs should get userId from JWT token, but allow optional agentId:

1. **memory_search**
   - Remove: `userId` parameter (get from JWT)
   - Keep: `agentId` parameter (optional - if not provided, search all user memories)
   - Keep: `runId` parameter (optional)
   - Behavior: Always filter by userId from JWT, optionally filter by agentId if provided

2. **memory_get_all**
   - Remove: `userId` parameter (get from JWT)
   - Keep: `agentId` parameter (optional - if not provided, get all user memories)
   - Keep: `runId` parameter (optional)
   - Behavior: Always filter by userId from JWT, optionally filter by agentId if provided

3. **memory_get_history**
   - Remove: `userId` parameter (get from JWT)
   - Keep: `agentId` parameter (optional - if not provided, search all user memories)
   - Keep: `runId` parameter (optional)
   - Behavior: Always filter by userId from JWT, optionally filter by agentId if provided

4. **memory_get_stats**
   - Remove: `userId` parameter (get from JWT)
   - Keep: `agentId` parameter (optional - if not provided, get stats for all user memories)
   - Keep: `runId` parameter (optional)
   - Behavior: Always filter by userId from JWT, optionally filter by agentId if provided

## Implementation Plan

### Step 1: Update SessionContextResolver ✅ COMPLETED
- Modify the resolver to handle the case where agentId is explicitly null for read operations
- Ensure it still gets userId from JWT token claims

### Step 2: Update MemoryMcpTools.cs ✅ COMPLETED
- Remove userId parameters from all methods
- Remove agentId parameters from write methods
- Keep agentId as optional for read methods
- Update method signatures and parameter descriptions

### Step 3: Update Repository Layer (if needed) ✅ COMPLETED
- Ensure repository methods can handle null agentId for read operations
- Verify that filtering logic works correctly when agentId is null

### Step 4: Update Tests ✅ COMPLETED
- Update all test cases to reflect new parameter structure
- Test scenarios where agentId is null for read operations
- Verify JWT token claims are properly extracted

### Step 5: Update Documentation ✅ COMPLETED
- Update API documentation to reflect new parameter structure
- Update examples and usage instructions

## Expected Behavior After Changes

### Write Operations
```csharp
// Before
memory_add(content: "test", userId: "user1", agentId: "agent1")

// After  
memory_add(content: "test") // userId/agentId from JWT token
```

### Read Operations
```csharp
// Before
memory_search(query: "test", userId: "user1", agentId: "agent1")

// After - search specific agent
memory_search(query: "test", agentId: "agent1") // userId from JWT

// After - search all user memories
memory_search(query: "test") // userId from JWT, agentId = null
```

## Security Considerations
- UserId always comes from authenticated JWT token (cannot be spoofed)
- AgentId for write operations comes from JWT token (cannot be spoofed)
- AgentId for read operations is optional and can be overridden to allow cross-agent search within same user
- RunId remains optional and can be explicitly provided for both read/write operations

## Implementation Status: ✅ COMPLETED

All changes have been successfully implemented and tested:

- **API Changes**: All memory API methods updated to remove userId parameters and make agentId optional for read operations
- **Authentication**: JWT token claims are now properly extracted for userId and agentId
- **Tests**: All 268 tests pass, including updated unit tests and integration tests
- **Build**: Clean build with 0 errors and 0 warnings
- **Functionality**: Write operations get userId/agentId from JWT token, read operations allow optional agentId for cross-agent search

The API now properly implements the authentication requirements where:
1. Write APIs (memory_add, memory_update, memory_delete, memory_delete_all) get userId and agentId from JWT token only
2. Read APIs (memory_search, memory_get_all, memory_get_history, memory_get_stats) get userId from JWT token and accept optional agentId parameter 