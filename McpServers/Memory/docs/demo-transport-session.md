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
- **STDIO**: Environment variables work seamlessly
- **SSE**: URL parameters provide same functionality
- **Headers**: HTTP headers work across both transports

### 4. Developer Experience
- **Set Once, Use Everywhere**: Configure session context once
- **Logical Grouping**: Use runId for any meaningful grouping
- **Easy Switching**: Change runId to switch contexts
- **Automatic Defaults**: Sensible defaults when not specified

## Advanced Patterns

### Dynamic RunId Generation
```bash
# Generate runId based on current context
export MCP_MEMORY_RUN_ID="$(git branch --show-current)-$(date +%Y%m%d)"
# Combines git branch with date for development sessions
```

### Hierarchical Sessions
```bash
# Parent session
export MCP_MEMORY_RUN_ID="project-alpha"

# Child sessions
export MCP_MEMORY_RUN_ID="project-alpha-frontend"
export MCP_MEMORY_RUN_ID="project-alpha-backend"
export MCP_MEMORY_RUN_ID="project-alpha-testing"
```

### Conditional Session Logic
```bash
# Environment-based sessions
if [ "$NODE_ENV" = "production" ]; then
  export MCP_MEMORY_RUN_ID="prod-$(date +%Y%m%d)"
else
  export MCP_MEMORY_RUN_ID="dev-$(whoami)-$(date +%Y%m%d)"
fi
```

This simplified session management makes the MemoryServer much more intuitive and powerful for real-world usage patterns. 