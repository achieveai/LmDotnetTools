# Debugging Guide for ClaudeAgentSDK Example

## What to Expect (Happy Path)

When you run `dotnet run` in the LmConfigUsageExample directory, you should see:

### Examples 1-6 Output
```
=== LmConfig Enhanced Usage Examples ===

1. File-Based Configuration Loading
==================================
✓ Successfully loaded configuration from file
Agent type: UnifiedAgent

2. Embedded Resource Configuration Loading
==========================================
...
(continues through examples 1-6)
```

### Example 7 - ClaudeAgentSDK (The Important One)

**If everything works:**

```
7. ClaudeAgentSDK Provider with MCP Tools
==========================================
✓ ClaudeAgentSDK provider is available
✓ Resolved model: claude-sonnet-4-5-20250929
  Provider: ClaudeAgentSDK
  Compatibility: ClaudeAgentSDK
✓ Created ClaudeAgentSDK streaming agent

Testing basic interaction...

Agent response (streaming):
----------------------------
[Thinking: I need to check what MCP tools are available to me. Let me think about how to respond to this...]
I have access to the following MCP tools:
- filesystem: read_file, write_file, list_directory
- fetch: get, post

These tools allow me to interact with the file system and make HTTP requests.
----------------------------

✓ ClaudeAgentSDK interaction completed successfully
✓ Agent disposed
```

**If the provider is not available:**

```
7. ClaudeAgentSDK Provider with MCP Tools
==========================================
✗ ClaudeAgentSDK provider is not available
  Required:
  - ANTHROPIC_API_KEY environment variable
  - Node.js installed
  - @anthropic-ai/claude-agent-sdk npm package installed globally
  - .mcp.json configuration file in the project root
```

---

## Debugging Checklist (Step-by-Step)

### Step 1: Verify Prerequisites

**Check Node.js:**
```bash
node --version
```
Expected: `v18.0.0` or higher

**Check NPM:**
```bash
npm --version
```

**Check Claude Agent SDK installation:**
```bash
npm list -g @anthropic-ai/claude-agent-sdk
```
Expected: Something like `@anthropic-ai/claude-agent-sdk@1.0.0` (version may vary)

If not installed:
```bash
npm install -g @anthropic-ai/claude-agent-sdk
```

**Check API Key:**
```bash
# Windows (PowerShell)
echo $env:ANTHROPIC_API_KEY

# Windows (CMD)
echo %ANTHROPIC_API_KEY%
```
Expected: Should show your API key (starts with `sk-ant-`)

If not set:
```bash
# Windows (PowerShell)
$env:ANTHROPIC_API_KEY="sk-ant-your-key-here"

# Windows (CMD)
set ANTHROPIC_API_KEY=sk-ant-your-key-here
```

### Step 2: Verify Build Outputs

**Check if .mcp.json was copied:**
```bash
dir example\LmConfigUsageExample\bin\Debug\net9.0\.mcp.json
```
Expected: File should exist

**Check if models.json was copied:**
```bash
dir example\LmConfigUsageExample\bin\Debug\net9.0\models.json
```
Expected: File should exist

### Step 3: Test the Claude Agent SDK CLI Manually

**Create a test directory:**
```bash
mkdir test-claude-sdk
cd test-claude-sdk
```

**Create a simple .mcp.json:**
```json
{
  "mcpServers": {
    "fetch": {
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-fetch"]
    }
  }
}
```

**Test the CLI directly:**
```bash
npx @anthropic-ai/claude-agent-sdk
```

Expected: Should start an interactive session. Type `Hello` and press Enter. You should see Claude respond.

If this doesn't work, the issue is with the Claude Agent SDK installation, not our code.

### Step 4: Enable Detailed Logging

**Modify Program.cs temporarily to add verbose logging:**

Find this line in Example 7:
```csharp
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information));
```

Change to:
```csharp
services.AddLogging(builder => builder.AddConsole().SetMinimumLevel(LogLevel.Debug));
```

This will show detailed logs from the ClaudeAgentSdkClient about:
- Node.js process startup
- JSONL stream parsing
- Message conversion
- MCP server initialization

### Step 5: Check for Common Errors

**Error: "Provider not available"**
- Root cause: API key not set or Node.js/SDK not installed
- Check: Steps 1 and 2 above

**Error: "Failed to start Node.js process"**
- Root cause: Node.js not in PATH or permission issues
- Check: `node --version` works from the same terminal
- Solution: Add Node.js to PATH or restart terminal after installation

**Error: "MCP server failed to start"**
- Root cause: MCP server package not found or .mcp.json misconfigured
- Check: .mcp.json syntax is valid JSON
- Check: Paths in .mcp.json are absolute or correct relative paths
- Try: Use simpler MCP server like "fetch" first

**Error: "Process exited unexpectedly"**
- Root cause: Node.js process crashed
- Check: Look for error output in console
- Check: Verify .mcp.json is valid
- Debug: Run the SDK CLI manually (Step 3)

**Error: "Timeout waiting for response"**
- Root cause: First request can be slow (MCP servers initializing)
- Solution: Increase timeout in models.json (already set to 5 minutes)
- Check: Network connectivity if using fetch MCP server

---

## Key Files to Check

### 1. ClaudeAgentSdkClient.cs
**Location:** `src/ClaudeAgentSdkProvider/Agents/ClaudeAgentSdkClient.cs`

**What it does:**
- Starts the Node.js process
- Manages stdin/stdout communication
- Parses JSONL output

**Key methods to add breakpoints:**
- `StartAsync()` - Process startup
- `SendRequestAsync()` - Sending messages to Node.js
- `ReadMessagesAsync()` - Reading responses

**Logging to check:**
```csharp
_logger.LogInformation("Starting Claude Agent SDK process: {NodePath} {SdkPath}", nodePath, sdkPath);
_logger.LogDebug("Sending request: {Request}", JsonSerializer.Serialize(request));
```

### 2. JsonlStreamParser.cs
**Location:** `src/ClaudeAgentSdkProvider/Parsers/JsonlStreamParser.cs`

**What it does:**
- Parses JSONL lines from stdout
- Converts to IMessage objects

**Key methods:**
- `ParseLine()` - Parses individual JSONL lines
- `ConvertToMessages()` - Converts events to messages

### 3. ClaudeAgentSdkAgent.cs
**Location:** `src/ClaudeAgentSdkProvider/Agents/ClaudeAgentSdkAgent.cs`

**What it does:**
- Orchestrates client interaction
- Manages session state
- Implements IStreamingAgent

**Key methods:**
- `GenerateReplyStreamingAsync()` - Main entry point

---

## Debugging with Visual Studio / VS Code

### Setting Breakpoints

1. **First breakpoint:** `ClaudeAgentSdkAgent.GenerateReplyStreamingAsync()` line where it checks `_client.IsRunning`
   - This shows if the client started successfully

2. **Second breakpoint:** `ClaudeAgentSdkClient.StartAsync()` inside the if block
   - This shows the Node.js startup process

3. **Third breakpoint:** `ClaudeAgentSdkClient.SendRequestAsync()`
   - This shows the request being sent to Node.js

4. **Fourth breakpoint:** `JsonlStreamParser.ConvertToMessages()`
   - This shows responses being parsed

### Watch Variables

- `_process.HasExited` - Is Node.js still running?
- `_process.ExitCode` - Did Node.js crash?
- `request` - What's being sent to the SDK?
- `line` - What JSONL is coming back from stdout?

---

## Manual Testing Without the Example

**Create a simple test program:**

```csharp
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Agents;
using AchieveAi.LmDotnetTools.ClaudeAgentSdkProvider.Configuration;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;

var loggerFactory = LoggerFactory.Create(builder =>
    builder.AddConsole().SetMinimumLevel(LogLevel.Debug));

var options = new ClaudeAgentSdkOptions
{
    ProjectRoot = Directory.GetCurrentDirectory(),
    McpConfigPath = ".mcp.json"
};

var clientLogger = loggerFactory.CreateLogger<ClaudeAgentSdkClient>();
var client = new ClaudeAgentSdkClient(options, clientLogger);

var agentLogger = loggerFactory.CreateLogger<ClaudeAgentSdkAgent>();
var agent = new ClaudeAgentSdkAgent("test", client, options, agentLogger);

var messages = new[] { new TextMessage { Text = "Hello!", Role = Role.User } };

try
{
    var responses = await agent.GenerateReplyAsync(messages);
    foreach (var response in responses)
    {
        Console.WriteLine(response);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Error: {ex.Message}");
    Console.WriteLine($"Stack: {ex.StackTrace}");
}
finally
{
    if (client is IDisposable disposable)
    {
        disposable.Dispose();
    }
}
```

**Save as `test-claude-sdk.csx` and run:**
```bash
dotnet script test-claude-sdk.csx
```

---

## Common Debug Scenarios

### Scenario 1: Process starts but no output

**Symptoms:**
- Agent created successfully
- No streaming messages received
- Request appears to hang

**Debug steps:**
1. Check stdout is being read correctly
2. Add logging to `JsonlStreamParser.ParseLine()` to see raw JSONL
3. Verify JSONL format matches expected format
4. Check if stderr has errors

**Expected stdout format:**
```json
{"type":"assistant","uuid":"...","sessionId":"...","message":{...}}
```

### Scenario 2: MCP tools not available

**Symptoms:**
- Agent works but says no tools available
- No tool calls in response

**Debug steps:**
1. Verify .mcp.json is in the correct location
2. Check MCP server logs (if enabled)
3. Test MCP servers individually
4. Verify MCP server packages are accessible

### Scenario 3: First request works, second fails

**Symptoms:**
- First `GenerateReplyAsync` works
- Subsequent calls fail or hang

**Debug steps:**
1. Check if Node.js process is still running (`_process.HasExited`)
2. Verify session management in `ClaudeAgentSdkAgent`
3. Check for deadlocks in stream reading
4. Ensure proper disposal between calls

---

## Getting Help

If you're still stuck after trying the above:

1. **Collect debug information:**
   - Node.js version: `node --version`
   - NPM version: `npm --version`
   - SDK version: `npm list -g @anthropic-ai/claude-agent-sdk`
   - .NET version: `dotnet --version`
   - OS: Windows version

2. **Collect logs:**
   - Run with `LogLevel.Debug`
   - Capture full console output
   - Note any error messages or stack traces

3. **Check the implementation status:**
   - See `scratchpad/conversation_memories/ClaudeAgentSDK-Provider-Implementation/README.md`
   - Check if known issues are listed

4. **Test isolation:**
   - Does the SDK CLI work standalone?
   - Does a simple mock client work?
   - Does the issue occur with minimal .mcp.json?

---

## Quick Diagnostic Script

Save this as `diagnose.ps1` and run with PowerShell:

```powershell
# ClaudeAgentSDK Diagnostic Script

Write-Host "=== ClaudeAgentSDK Diagnostics ===" -ForegroundColor Cyan

# Check Node.js
Write-Host "`nChecking Node.js..." -ForegroundColor Yellow
try {
    $nodeVersion = node --version
    Write-Host "✓ Node.js found: $nodeVersion" -ForegroundColor Green
} catch {
    Write-Host "✗ Node.js not found!" -ForegroundColor Red
}

# Check NPM
Write-Host "`nChecking NPM..." -ForegroundColor Yellow
try {
    $npmVersion = npm --version
    Write-Host "✓ NPM found: $npmVersion" -ForegroundColor Green
} catch {
    Write-Host "✗ NPM not found!" -ForegroundColor Red
}

# Check Claude Agent SDK
Write-Host "`nChecking Claude Agent SDK..." -ForegroundColor Yellow
try {
    $sdkInfo = npm list -g @anthropic-ai/claude-agent-sdk 2>&1
    if ($sdkInfo -match "@anthropic-ai/claude-agent-sdk") {
        Write-Host "✓ Claude Agent SDK installed" -ForegroundColor Green
        Write-Host $sdkInfo
    } else {
        Write-Host "✗ Claude Agent SDK not found!" -ForegroundColor Red
    }
} catch {
    Write-Host "✗ Error checking SDK!" -ForegroundColor Red
}

# Check API Key
Write-Host "`nChecking ANTHROPIC_API_KEY..." -ForegroundColor Yellow
if ($env:ANTHROPIC_API_KEY) {
    $keyPreview = $env:ANTHROPIC_API_KEY.Substring(0, [Math]::Min(10, $env:ANTHROPIC_API_KEY.Length))
    Write-Host "✓ API Key set: $keyPreview..." -ForegroundColor Green
} else {
    Write-Host "✗ ANTHROPIC_API_KEY not set!" -ForegroundColor Red
}

# Check .mcp.json
Write-Host "`nChecking .mcp.json..." -ForegroundColor Yellow
$mcpPath = "example\LmConfigUsageExample\bin\Debug\net9.0\.mcp.json"
if (Test-Path $mcpPath) {
    Write-Host "✓ .mcp.json found in output directory" -ForegroundColor Green
} else {
    Write-Host "✗ .mcp.json not found in output!" -ForegroundColor Red
}

# Check models.json
Write-Host "`nChecking models.json..." -ForegroundColor Yellow
$modelsPath = "example\LmConfigUsageExample\bin\Debug\net9.0\models.json"
if (Test-Path $modelsPath) {
    Write-Host "✓ models.json found in output directory" -ForegroundColor Green
} else {
    Write-Host "✗ models.json not found in output!" -ForegroundColor Red
}

Write-Host "`n=== Diagnostics Complete ===" -ForegroundColor Cyan
```

Run with: `.\diagnose.ps1`
