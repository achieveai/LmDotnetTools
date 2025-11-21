# CopilotKit Test Client for AG-UI Integration

This is a React test client that demonstrates the integration between CopilotKit and the AG-UI sample server.

## Features

- 🚀 Real-time communication via WebSocket using AG-UI protocol
- 🔄 Conversation continuity with threadId/runId tracking
- 🛠️ Tool calling support (Weather, Calculator, Search, Time, Counter)
- 📡 Event streaming with AG-UI events
- 🎯 Multiple agent support (ToolCallingAgent, InstructionChainAgent)
- 📊 Structured logging with Seq integration

## Prerequisites

- Node.js 18+ and npm
- AG-UI Sample Server running on `http://localhost:5264`

## Installation

```bash
cd copilotkit-test-client
npm install
```

## Configuration

Copy `.env.example` to `.env` and configure Seq logging settings:

```bash
cp .env.example .env
```

Edit `.env` to customize:
```env
# Seq Logging Configuration
SEQ_URL=http://localhost:5341
SEQ_API_KEY=Uu5XkCVB6llwhmyF7N1e

# Logging Configuration
LOG_LEVEL=info
NODE_ENV=development
```

**Note:** The Seq API key and URL are the same as the AG-UI Sample Server for centralized logging.

## Running the Application

### 1. Start the AG-UI Sample Server

First, make sure the AG-UI sample server is running:

```bash
cd src/AG-UI/AchieveAi.LmDotnetTools.AgUi.Sample
dotnet run
```

The server should start on `http://localhost:5264`

### 2. Start the React Client

In a separate terminal:

```bash
cd copilotkit-test-client
npm run dev
```

The React app will start on `http://localhost:3000` and open in your browser.

## Usage

### Basic Interaction

1. Open the application in your browser at `http://localhost:3000`
2. Click the chat icon in the sidebar to open the CopilotKit chat
3. Type a message and press Enter
4. The assistant will respond using the AG-UI protocol

### Example Prompts

Try these prompts to test different features:

**Weather Tool:**
```
What's the weather in San Francisco?
```

**Calculator Tool:**
```
Calculate 15 * 24 + 7
```

**Time Tool:**
```
What time is it?
```

**Search Tool:**
```
Search for information about React
```

**Counter Tool:**
```
Increment the counter twice
```

**Multi-turn Conversation:**
```
1. What's the weather in New York?
2. And what about Los Angeles?
3. Which one is warmer?
```

### Agent Selection

You can switch between agents using the dropdown in the header:
- **ToolCallingAgent** - Supports tool calling with weather, calculator, search, time, and counter
- **InstructionChainAgent** - Uses instruction chain format (experimental)

### Event Monitoring

The Event Log at the bottom shows all AG-UI events received from the server:
- `SESSION_STARTED` - WebSocket connection established
- `RUN_STARTED` - Agent execution started
- `TEXT_MESSAGE` - Text response from agent
- `TOOL_CALL` - Tool invocation
- `TOOL_CALL_RESULT` - Tool execution result
- `RUN_FINISHED` - Agent execution completed
- `ERROR_EVENT` - Error occurred

## Architecture

### Request Flow

```
React App (CopilotKit)
    ↓ HTTP POST /api/copilotkit
CopilotKitController
    ↓ Maps threadId/runId ↔ sessionId
CopilotKitSessionMapper
    ↓ Executes agent in background
Agent (ToolCallingAgent/InstructionChainAgent)
    ↓ Publishes AG-UI events
EventPublisher → WebSocket Handler
    ↓ Enriches with threadId/runId
WebSocket → React App
```

### Custom AG-UI Adapter

The app uses a custom adapter that:
1. Sends messages to `/api/copilotkit` endpoint
2. Receives sessionId and WebSocket URL
3. Connects to WebSocket for event streaming
4. Processes AG-UI events and converts them to CopilotKit format
5. Maintains threadId/runId for conversation continuity

## Testing Conversation Continuity

To verify threadId/runId tracking:

1. Start a conversation: "What's the weather in Paris?"
2. Check the Thread ID in the header
3. Continue: "And what about London?"
4. Verify the same Thread ID is maintained
5. Check browser console for detailed event logs

Console logs will show:
```
🚀 Sending request to AG-UI server: { threadId: "abc-123", runId: "run-456", ... }
✅ Received response from AG-UI server: { sessionId: "...", threadId: "...", ... }
🔌 Connecting to WebSocket: ws://localhost:3000/ag-ui/ws?sessionId=...
📨 Received AG-UI event: TEXT_MESSAGE { threadId: "abc-123", runId: "run-456", ... }
```

## Troubleshooting

### Server Not Running
If you see connection errors, ensure the AG-UI server is running:
```bash
cd src/AG-UI/AchieveAi.LmDotnetTools.AgUi.Sample
dotnet run
```

### CORS Issues
The Vite dev server is configured to proxy requests to the AG-UI server. If you have CORS issues, check that the server URL in `vite.config.js` matches your server.

### WebSocket Connection Failed
Ensure the WebSocket endpoint is accessible:
- Check that `/ag-ui/ws` is properly configured in the AG-UI server
- Verify firewall settings allow WebSocket connections
- Check browser console for detailed error messages

### No Response from Agent
If the agent doesn't respond:
1. Check the AG-UI server logs for errors
2. Verify the agent name is correct (ToolCallingAgent or InstructionChainAgent)
3. Check the Event Log for ERROR_EVENT messages
4. Look at browser console for WebSocket errors

## Development

### Project Structure

```
copilotkit-test-client/
├── src/
│   ├── App.jsx          # Main app component with CopilotKit integration
│   ├── App.css          # Styles
│   └── main.jsx         # Entry point
├── index.html           # HTML template
├── vite.config.js       # Vite configuration with proxy
├── package.json         # Dependencies
└── README.md           # This file
```

### Key Dependencies

- `@copilotkit/react-core` - CopilotKit core functionality
- `@copilotkit/react-ui` - CopilotKit UI components
- `react` - React library
- `vite` - Build tool and dev server
- `winston` - Logging framework
- `@datalust/winston-seq` - Seq transport for Winston

### Building for Production

```bash
npm run build
```

The built files will be in the `dist` folder.

### Preview Production Build

```bash
npm run preview
```

## Technical Details

### Thread/Run ID Management

The app maintains conversation continuity by:
1. Storing the `threadId` received from the first response
2. Including it in subsequent requests
3. Server uses `CopilotKitSessionMapper` to map threadId ↔ sessionId
4. All events are enriched with threadId/runId by the WebSocket handler

### AG-UI Event Processing

The custom adapter handles these AG-UI events:
- `SESSION_STARTED` - Connection established
- `RUN_STARTED` - Processing begun
- `TEXT_MESSAGE` - Accumulates as assistant response
- `TOOL_CALL` - Logs tool invocation
- `TOOL_CALL_RESULT` - Logs tool result
- `RUN_FINISHED` - Closes WebSocket and returns response
- `ERROR_EVENT` - Rejects promise with error

### WebSocket Lifecycle

1. Receive sessionId from `/api/copilotkit`
2. Connect to WebSocket: `/ag-ui/ws?sessionId={sessionId}`
3. Listen for AG-UI events
4. Accumulate messages
5. Close on `RUN_FINISHED`
6. Handle errors and timeouts

## Performance

- WebSocket connection established per request (~100ms)
- Event streaming is real-time
- 30-second timeout for long-running operations
- Events are logged without blocking UI

## Logging and Monitoring

All server-side events are logged to Seq using structured logging with Winston:

- **Request logging**: All incoming HTTP requests with method, path, and IP
- **Session lifecycle**: Session creation, WebSocket connections, completion
- **Event streaming**: AG-UI events with context (threadId, runId, content preview)
- **Errors**: Detailed error logging with stack traces
- **Performance**: Request timeouts and processing metrics

**View logs in Seq:**
1. Ensure Seq is running on `http://localhost:5341`
2. Open Seq in your browser
3. Filter by `Application = "CopilotKit-Test-Client"`
4. Use structured queries like `@EventType = "RUN_STARTED"`

## Security Notes

- This is a **development/testing client** only
- No authentication implemented
- CORS is wide open (`AllowAnyOrigin`)
- For production, implement proper authentication and CORS policies

## Next Steps

- Add authentication/authorization
- Implement persistent chat history
- Add more agent configurations
- Create automated tests
- Add performance monitoring
- Deploy to production environment

## Support

For issues or questions:
1. Check the browser console for error messages
2. Review AG-UI server logs
3. Verify all prerequisites are met
4. Check the integration documentation in `scratchpad/CopilotKit-Integration/`
