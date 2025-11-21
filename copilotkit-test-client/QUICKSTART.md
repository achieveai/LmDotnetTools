# Quick Start Guide

Get the CopilotKit test client running in 3 simple steps!

## Step 1: Start the AG-UI Server

Open a terminal and run:

```bash
cd D:\Source\repos\LmDotnetTools\src\AG-UI\AchieveAi.LmDotnetTools.AgUi.Sample
dotnet run
```

Wait for the message:
```
Now listening on: http://localhost:5264
```

## Step 2: Install Dependencies

Open a **new terminal** and run:

```bash
cd D:\Source\repos\LmDotnetTools\copilotkit-test-client
npm install
```

This will install:
- React 18.3.1
- CopilotKit 1.3.0 (react-core and react-ui)
- Vite 5.4.2

## Step 3: Start the React App

In the same terminal:

```bash
npm run dev
```

The app will open automatically in your browser at `http://localhost:3000`

## Testing the Integration

### Quick Test

1. Click the chat icon in the bottom-right corner
2. Type: **"What's the weather in San Francisco?"**
3. Press Enter

You should see:
- ✅ The agent responds with weather information
- ✅ Event log shows AG-UI events (TEXT_MESSAGE, TOOL_CALL, etc.)
- ✅ Thread ID appears in the header

### Test Conversation Continuity

1. First message: **"What's the weather in New York?"**
2. Note the Thread ID in the header
3. Second message: **"And what about Los Angeles?"**
4. Verify the same Thread ID is maintained

### Test Different Tools

**Calculator:**
```
Calculate 25 * 4 + 10
```

**Time:**
```
What time is it right now?
```

**Search:**
```
Search for React documentation
```

**Counter:**
```
Increment the counter three times
```

## What You'll See

### In the React App
- 🎨 Beautiful gradient UI with purple theme
- 💬 CopilotKit chat sidebar on the right
- 📊 Event log showing real-time AG-UI events
- 🔄 Thread ID tracking for conversation continuity
- 🎛️ Agent selector (ToolCallingAgent / InstructionChainAgent)

### In the Browser Console
```
🚀 Sending request to AG-UI server: { threadId: "...", runId: "...", ... }
✅ Received response from AG-UI server: { sessionId: "...", ... }
🔌 Connecting to WebSocket: ws://localhost:3000/ag-ui/ws?sessionId=...
📨 Received AG-UI event: SESSION_STARTED
📨 Received AG-UI event: RUN_STARTED
📨 Received AG-UI event: TEXT_MESSAGE { text: "..." }
🔧 Tool call: GetWeatherTool
✅ Tool result: { temperature: 72, ... }
🏁 Run finished: success
```

### In the Server Console
```
[INFO] POST /api/copilotkit - 200 OK
[INFO] WebSocket connection accepted
[INFO] [DEBUG] WebSocket session ID from query: abc-123-456
[DEBUG] Sent event TEXT_MESSAGE for session abc-123-456
[DEBUG] Sent event RUN_FINISHED for session abc-123-456
```

## Troubleshooting

### "Cannot GET /"
- Make sure you're on `http://localhost:3000` (not 5264)

### "Connection refused"
- Ensure AG-UI server is running on port 5264
- Check no firewall blocking

### "WebSocket closed unexpectedly"
- Check server logs for errors
- Verify agent name is correct
- Try restarting both server and client

### No response from agent
- Select "ToolCallingAgent" in the dropdown
- Check server console for errors
- Verify tools are registered

## Next Steps

After verifying the integration works:

1. ✅ Try multi-turn conversations
2. ✅ Test with different agents
3. ✅ Monitor the event log
4. ✅ Check browser console for detailed logs
5. ✅ Review threadId/runId tracking
6. ✅ Test tool calling with various prompts

## Architecture Recap

```
React (CopilotKit)
    ↓ POST /api/copilotkit
CopilotKitController
    ↓ sessionMapper.CreateOrResumeSession()
Background Agent Execution
    ↓ EventPublisher
WebSocket Handler (enriches events)
    ↓ WS: /ag-ui/ws?sessionId=...
React receives events
```

## Support

- 📖 Full docs: `README.md`
- 🗂️ Integration details: `../scratchpad/CopilotKit-Integration/`
- 🐛 Issues: Check browser console and server logs

Enjoy testing! 🚀
