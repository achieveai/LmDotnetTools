# MemoryServer Testing Guide - .env Configuration

This guide covers testing the MemoryServer with the new .env file loading functionality.

## Prerequisites

- .NET 9.0 SDK installed
- PowerShell (for Windows commands shown)
- API keys for testing (optional for basic functionality testing)

## Setup Environment Variables

### Option 1: Create .env File (Recommended)

Create a `.env` file in the workspace root (`D:\Source\repos\LmDotnetTools\.env`):

```env
# LLM Provider API Keys
OPENAI_API_KEY=your_openai_api_key_here
ANTHROPIC_API_KEY=your_anthropic_api_key_here

# Embedding Service Configuration (for vector storage)
EMBEDDING_API_KEY=your_embedding_api_key_here
EMBEDDING_API_URL=https://api.openai.com/v1
EMBEDDING_MODEL=text-embedding-3-small
EMBEDDING_SIZE=1536

# Alternative Provider API Keys (optional)
OPENROUTER_API_KEY=your_openrouter_api_key_here
DEEPINFRA_API_KEY=your_deepinfra_api_key_here
GROQ_API_KEY=your_groq_api_key_here

# Session Context (for STDIO transport - optional)
MEMORY_USER_ID=test_user
MEMORY_AGENT_ID=test_agent
MEMORY_RUN_ID=test_run_001
```

### Option 2: Set Environment Variables Manually

```powershell
$env:OPENAI_API_KEY="your_key_here"
$env:EMBEDDING_API_KEY="your_key_here"
$env:ANTHROPIC_API_KEY="your_key_here"
```

## Launch MemoryServer

### 1. Navigate to MemoryServer Directory
```powershell
cd McpServers/Memory/MemoryServer
```

### 2. Launch Server (SSE Mode - Default)
```powershell
dotnet run
```

**Expected Output:**
```
Loading environment variables from: D:\Source\repos\LmDotnetTools\.env
Environment variables loaded successfully
🚀 Starting Memory MCP Server with SSE transport
info: MemoryServer.Infrastructure.SqliteSessionFactory[0]
      Initializing database schema...
info: MemoryServer.Infrastructure.SqliteSession[0]
      sqlite-vec extension loaded successfully for session [id]
info: MemoryServer.Infrastructure.SqliteSessionFactory[0]
      Database schema initialized successfully
info: Program[0]
      Database initialized successfully
info: Program[0]
      🌐 Memory MCP Server configured for SSE transport
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: https://localhost:64478
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:64479
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### 3. Launch Server (STDIO Mode - Alternative)
```powershell
dotnet run -- --stdio
```

## Kill MemoryServer

### Method 1: Using Ctrl+C (If Running in Foreground)
- Press `Ctrl+C` in the terminal where the server is running

### Method 2: Kill by Process Command Line (Recommended)
```powershell
# Find and kill the specific dotnet process running MemoryServer
Get-WmiObject Win32_Process | Where-Object {$_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*run*"} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

### Method 3: Kill by Process ID
```powershell
# First, find the process
Get-WmiObject Win32_Process | Where-Object {$_.Name -eq "dotnet.exe" -and $_.CommandLine -like "*MemoryServer*"} | Select-Object ProcessId, CommandLine

# Then kill by specific PID
Stop-Process -Id [PID] -Force
```

## Validation Testing

### 1. Test Server Health (SSE Mode)
```powershell
# Test health endpoint
Invoke-RestMethod -Uri "http://localhost:64479/health"
# Expected: "OK"
```

### 2. Test MCP Tools (Memory Operations)

Start the server and use these MCP tool calls to test functionality:

#### Add Memory Test
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "Test memory content for validation",
      "metadata": {
        "test_type": "validation",
        "timestamp": "2025-01-10T15:30:00Z"
      }
    }
  }
}
```

**Expected Response:** Memory ID and success confirmation

#### Search Memory Test
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_search",
    "arguments": {
      "query": "validation",
      "limit": 5
    }
  }
}
```

**Expected Response:** List of memories containing "validation"

#### Get All Memories Test
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_get_all",
    "arguments": {
      "limit": 10,
      "offset": 0
    }
  }
}
```

### 3. Test Database Tools (memory_db Tools)

These tools access the persistent database directly:

#### Database Query Test
```sql
-- Test direct database access
SELECT COUNT(*) as total_memories FROM memories;
```

#### Database Write Test
```sql
-- Test database write capability
INSERT INTO test_table (name, value) VALUES ('test', 'validation');
```

### 4. Environment Variable Validation

#### Check if .env Variables are Loaded
Add this test to verify environment loading:

```powershell
# After starting the server, check if environment variables are available
# Look for these log messages in the server output:
# - "Loading environment variables from: [path]"
# - "Environment variables loaded successfully"
```

#### API Key Configuration Test
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "Test embedding generation to validate API keys"
    }
  }
}
```

**Expected Behavior:**
- ✅ **With API Keys**: Memory saved with embedding generated
- ⚠️ **Without API Keys**: Memory saved but embedding generation fails (with warning logs)

## Testing Scenarios

### Scenario 1: Full Functionality Test (With API Keys)
1. Create `.env` file with valid API keys
2. Launch MemoryServer
3. Verify .env loading in logs
4. Add memory with `memory_add`
5. Search memory with `memory_search`
6. Verify vector search works
7. Check database with `memory_db` tools

### Scenario 2: Basic Functionality Test (Without API Keys)
1. Launch MemoryServer without `.env` file
2. Verify graceful fallback in logs
3. Add memory with `memory_add`
4. Search memory with `memory_search` (FTS5 only)
5. Verify basic functionality works

### Scenario 3: Configuration Override Test
1. Set environment variables manually: `$env:OPENAI_API_KEY="manual_key"`
2. Create `.env` file with different key
3. Launch MemoryServer
4. Verify which key takes precedence (manual env vars should override .env)

### Scenario 4: Story Paragraph Memory Test - Complete Workflow

This scenario tests comprehensive memory operations with structured storytelling data, including MCP memory operations and database validation.

#### Step 1: Setup Story Session Context
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "The Mystery of Willowbrook's Lost Compass - Chapter 1: In the heart of Willowbrook village, beneath the ancient oak tree that had stood sentinel for over two centuries, young Emily Chen discovered something that would change everything. Half-buried in the gnarled roots was an ornate brass compass, its needle spinning wildly as if confused by some unseen magnetic force.",
      "metadata": {
        "story": "willowbrook_mystery",
        "chapter": "1",
        "characters": ["Emily Chen"],
        "locations": ["Willowbrook village", "ancient oak tree"],
        "objects": ["brass compass"],
        "paragraph_type": "introduction"
      }
    }
  }
}
```

#### Step 2: Add Multiple Story Paragraphs
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "Chapter 2: Emily's fingers traced the intricate engravings on the compass face. Strange symbols she'd never seen before formed a circle around the cardinal directions. As she held it up to catch the late afternoon sunlight filtering through the oak's leaves, the compass grew warm in her palm, and for just a moment, the erratic needle pointed steadily toward the old Blackwood Manor on the hill.",
      "metadata": {
        "story": "willowbrook_mystery",
        "chapter": "2", 
        "characters": ["Emily Chen"],
        "locations": ["ancient oak tree", "Blackwood Manor"],
        "objects": ["brass compass", "strange symbols"],
        "relationships": ["Emily-compass", "compass-Blackwood Manor"],
        "paragraph_type": "discovery"
      }
    }
  }
}
```

```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_add",
    "arguments": {
      "content": "Chapter 3: The next morning, Emily shared her discovery with her best friend Marcus Rodriguez and the village librarian, Mrs. Abernathy. The elderly librarian's eyes widened when she saw the compass. 'I've seen symbols like these before,' she whispered, her voice trembling with excitement. 'In the old archives. They're connected to the Blackwood family - the ones who disappeared fifty years ago.'",
      "metadata": {
        "story": "willowbrook_mystery", 
        "chapter": "3",
        "characters": ["Emily Chen", "Marcus Rodriguez", "Mrs. Abernathy"],
        "locations": ["village library"],
        "objects": ["brass compass", "old archives"],
        "relationships": ["Emily-Marcus", "Emily-Mrs.Abernathy", "compass-Blackwood family"],
        "historical_events": ["Blackwood family disappearance"],
        "paragraph_type": "revelation"
      }
    }
  }
}
```

#### Step 3: Test Memory Recall via MCP Tools

**Search by Character:**
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_search",
    "arguments": {
      "query": "Emily Chen",
      "limit": 10
    }
  }
}
```

**Search by Location:**
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_search",
    "arguments": {
      "query": "Blackwood Manor",
      "limit": 5
    }
  }
}
```

**Search by Object:**
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_search",
    "arguments": {
      "query": "brass compass",
      "limit": 5
    }
  }
}
```

#### Step 4: Validate Database Footprints using memory_db Tools

**Check Total Story Memories:**
```sql
SELECT COUNT(*) as story_memories 
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery';
```

**Verify Character Relationships:**
```sql
SELECT 
  id,
  substr(content, 1, 100) as content_preview,
  json_extract(metadata, '$.characters') as characters,
  json_extract(metadata, '$.chapter') as chapter
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery'
ORDER BY cast(json_extract(metadata, '$.chapter') as integer);
```

**Check Memory Embedding Status:**
```sql
SELECT 
  id,
  CASE WHEN embedding IS NOT NULL THEN 'YES' ELSE 'NO' END as has_embedding,
  length(embedding) as embedding_size,
  json_extract(metadata, '$.paragraph_type') as paragraph_type
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery';
```

**Analyze Character Mentions Across Chapters:**
```sql
SELECT 
  json_extract(metadata, '$.chapter') as chapter,
  json_extract(metadata, '$.characters') as characters,
  substr(content, 1, 150) as content_preview
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery'
  AND json_extract(metadata, '$.characters') LIKE '%Emily Chen%'
ORDER BY cast(json_extract(metadata, '$.chapter') as integer);
```

**Check Session Isolation:**
```sql
SELECT DISTINCT 
  user_id,
  agent_id,
  run_id,
  COUNT(*) as memory_count
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery'
GROUP BY user_id, agent_id, run_id;
```

#### Step 5: Test Vector Search Capabilities (If API Keys Available)

**Semantic Search Test:**
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_search",
    "arguments": {
      "query": "mysterious ancient artifacts with supernatural properties",
      "limit": 5
    }
  }
}
```

**Expected**: Should return compass-related memories due to semantic similarity

#### Step 6: Clean Up Test Data

**Remove Story Memories via MCP:**
```json
{
  "method": "tools/call",
  "params": {
    "name": "memory_delete_all",
    "arguments": {}
  }
}
```

**Verify Cleanup via Database:**
```sql
SELECT COUNT(*) as remaining_story_memories 
FROM memories 
WHERE json_extract(metadata, '$.story') = 'willowbrook_mystery';
```

**Expected Result**: Should return 0 memories

#### Expected Validation Results

**✅ Successful Story Test Results:**
- All paragraphs stored with unique IDs
- Character/location searches return relevant paragraphs
- Database queries show proper metadata structure
- Session isolation maintained
- Vector embeddings generated (if API keys present)
- Cross-referential searches work correctly

**⚠️ Potential Issues to Watch For:**
- Embedding generation failures (check API key configuration)
- Session context not properly isolated
- JSON metadata parsing errors in database queries
- Search results not semantically relevant

This comprehensive story scenario validates the entire memory pipeline from storage through retrieval and database analysis.

## Expected Log Patterns

### Successful .env Loading
```
Loading environment variables from: D:\Source\repos\LmDotnetTools\.env
Environment variables loaded successfully
```

### No .env File Found
```
No .env file found in current directory or parent directories
```

### API Key Issues
```
warn: MemoryServer.Services.LmConfigService[0]
      OpenAI API key not configured
```

### Successful Operations
```
info: MemoryServer.Tools.MemoryMcpTools[0]
      Added memory [ID] for session [session]
```

## Troubleshooting

### Issue: Server Won't Start
- Check if port 5000/64478/64479 is available
- Verify .NET 9.0 SDK is installed
- Check for database permission issues

### Issue: .env File Not Loading
- Verify `.env` file is in workspace root
- Check file permissions
- Ensure no syntax errors in `.env` file

### Issue: API Operations Failing
- Verify API keys are valid
- Check network connectivity
- Review server logs for specific error messages

### Issue: Database Operations Failing
- Check SQLite file permissions
- Verify sqlite-vec extension loaded successfully
- Check database initialization logs

## Clean Up

### Reset Database
```powershell
# Stop server first, then delete database files
Remove-Item memory.db* -ErrorAction SilentlyContinue
```

### Reset Environment
```powershell
# Remove test .env file
Remove-Item .env -ErrorAction SilentlyContinue
```

This guide provides comprehensive testing coverage for the MemoryServer's .env loading functionality and integration with both MCP tools and direct database access. 