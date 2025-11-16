# Feature Specification: AG-UI Protocol Integration

## High-Level Overview

This specification defines the integration of AG-UI protocol support into LmDotnetTools, enabling consumption by CopilotKit (React-based UI framework) and other AG-UI-compatible frontends. The implementation will expose the existing LmCore agentic workflow capabilities through a standardized, event-based WebSocket protocol.

### Purpose

Enable real-time, bidirectional communication between LmDotnetTools agents and web-based UIs through the AG-UI protocol, allowing frontend applications to:
- Stream agent responses in real-time
- Display tool calls and their results
- Show reasoning/thinking processes
- Track token usage and metrics
- Maintain conversation state

### Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                     CopilotKit / AG-UI Frontend             │
│                         (React App)                         │
└────────────────────────┬────────────────────────────────────┘
                         │ WebSocket (AG-UI Protocol)
                         │
┌────────────────────────▼────────────────────────────────────┐
│              AG-UI.AspNetCore (WebSocket Endpoint)          │
│  - WebSocket handler                                        │
│  - Request routing                                          │
│  - Connection lifecycle management                          │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│           AG-UI.Protocol (Protocol & State Management)      │
│  - AG-UI event streaming                                    │
│  - Session state management                                 │
│  - Message history tracking                                 │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│         AG-UI.DataObjects (Data Types & Conversions)        │
│  - AG-UI event types                                        │
│  - LmCore → AG-UI converters                                │
│  - Extension methods                                        │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│                    LmCore (Existing)                        │
│  - IStreamingAgent                                          │
│  - Middleware pipeline                                      │
│  - Message types (TextMessage, ToolsCallMessage, etc.)      │
└─────────────────────────────────────────────────────────────┘
```

---

## High-Level Requirements

### REQ-1: AG-UI Protocol Support
**User Story**: As a frontend developer using CopilotKit, I want to connect to LmDotnetTools agents using the standard AG-UI protocol so that I can build interactive UIs without custom integration code.

#### Acceptance Criteria:
1. WHEN a client connects via WebSocket THEN the system SHALL establish an AG-UI-compliant session
2. WHEN the client sends a RunAgentInput message THEN the system SHALL initiate an agent conversation
3. WHEN the agent generates responses THEN the system SHALL stream AG-UI events in real-time
4. WHEN the conversation completes THEN the system SHALL emit appropriate completion events

---

### REQ-2: Real-Time Streaming
**User Story**: As a user, I want to see agent responses streaming in real-time so that I have immediate feedback during long-running operations.

#### Acceptance Criteria:
1. WHEN the agent produces a TextMessage THEN the system SHALL emit text-chunk events immediately
2. WHEN the agent produces multiple message chunks THEN the system SHALL stream them without buffering
3. WHEN network latency occurs THEN the system SHALL maintain message ordering
4. WHEN streaming fails THEN the system SHALL emit appropriate error events

---

### REQ-3: Tool Call Visibility
**User Story**: As a user, I want to see when the agent calls tools and their results so that I understand what actions are being taken.

#### Acceptance Criteria:
1. WHEN the agent calls a tool THEN the system SHALL emit tool-call-chunk events
2. WHEN a tool returns results THEN the system SHALL emit tool-result-chunk events
3. WHEN multiple tools are called in parallel THEN the system SHALL emit events for each tool call
4. WHEN a tool call fails THEN the system SHALL emit error information in the tool-result-chunk

---

### REQ-4: Reasoning Transparency
**User Story**: As a user, I want to see the agent's reasoning process so that I can understand how it arrives at conclusions.

#### Acceptance Criteria:
1. WHEN the agent produces ReasoningMessage THEN the system SHALL emit thought-chunk events
2. WHEN reasoning is streamed THEN the system SHALL preserve the reasoning text content
3. WHEN reasoning is complete THEN the system SHALL mark the thought as complete
4. WHEN reasoning is disabled THEN the system SHALL NOT emit thought-chunk events

---

### REQ-5: WebSocket Transport
**User Story**: As a system integrator, I need WebSocket-based communication so that I have bidirectional, real-time connectivity.

#### Acceptance Criteria:
1. WHEN a client initiates a WebSocket connection THEN the system SHALL accept it on the configured endpoint
2. WHEN the connection is established THEN the system SHALL maintain it until explicitly closed
3. WHEN messages are exchanged THEN the system SHALL use JSON serialization
4. WHEN the connection drops THEN the system SHALL clean up session state

---

### REQ-6: Reusable Libraries
**User Story**: As a .NET developer, I want to use AG-UI protocol support as reusable libraries so that I can integrate them into my own applications.

#### Acceptance Criteria:
1. WHEN I reference AG-UI.DataObjects THEN I SHALL have access to all AG-UI data types and converters
2. WHEN I reference AG-UI.Protocol THEN I SHALL have access to protocol and state management
3. WHEN I reference AG-UI.AspNetCore THEN I SHALL have WebSocket middleware for ASP.NET Core apps
4. WHEN I run AG-UI.Sample THEN I SHALL see a working demonstration of the integration

---

## Existing Solutions

### AG-UI Protocol
- **Repository**: https://github.com/ckpearson/ag-ui.git
- **Documentation**: https://docs.ag-ui.com/concepts/architecture
- **Description**: Standardized protocol for agentic UI interactions with 16 event types across 5 categories

### CopilotKit
- **Description**: React-based framework for building AI-powered applications
- **Compatibility**: Consumes AG-UI protocol for agent communication
- **Use Case**: Primary consumer of this integration

---

## Current Implementation

### LmCore Architecture
The existing LmCore library provides:

1. **IStreamingAgent Interface**: Core abstraction for streaming agents
2. **Message Types**:
   - `TextMessage`: Text responses from the agent
   - `ToolsCallMessage`: Tool invocation requests
   - `ToolsCallAggregateMessage`: Combined tool call and result
   - `ToolsCallResultMessage`: Tool execution results
   - `ReasoningMessage`: Agent reasoning/thinking process
   - `UsageMessage`: Token usage and metrics
   - `CompositeMessage`: Grouping of multiple messages
   - `ImageMessage`: Image generation results

3. **Middleware Pipeline**: Composable middleware for processing messages
   - `JsonFragmentUpdateMiddleware`: JSON fragment handling
   - `FunctionCallMiddleware`: Tool/function calling support
   - `ConsolePrinterHelperMiddleware`: Console output
   - `MessageUpdateJoinerMiddleware`: Message aggregation

4. **Streaming Pattern**: `IAsyncEnumerable<IMessage>` for streaming responses

### Reference Workflow (ExamplePythonMCPClient)
```csharp
var agent = baseAgent
    .WithMiddleware(jsonFragmentUpdateMiddleware)
    .WithMiddleware(functionRegistry.BuildMiddleware())
    .WithMiddleware(consolePrinterMiddleware)
    .WithMiddleware(functionCallMiddleware)
    .WithMiddleware(new MessageUpdateJoinerMiddleware());

var repliesStream = await agent.GenerateReplyStreamingAsync(messages, options);

await foreach (var reply in repliesStream)
{
    // Process each message
    // Continue loop if ToolsCallAggregateMessage (tool calling flow)
}
```

---

## Detailed Requirements

## Component 1: AG-UI.DataObjects

### REQ-DO-1: AG-UI Event Types
**User Story**: As a developer, I need strongly-typed AG-UI event classes so that I can work with AG-UI events in a type-safe manner.

#### Acceptance Criteria:
1. WHEN I reference the library THEN I SHALL have classes for all 16 AG-UI event types:
   - **Session Events**: `session-started`, `session-ended`, `session-error`
   - **Message Events**: `text-chunk`, `text-message-complete`
   - **Tool Events**: `tool-call-chunk`, `tool-call-complete`, `tool-result-chunk`, `tool-result-complete`
   - **Thought Events**: `thought-chunk`, `thought-complete`
   - **Status Events**: `status-update`, `progress-update`
   - **Completion Events**: `agent-response-complete`, `agent-error`
   - **Metadata Events**: `metadata-update`

2. WHEN I create an event object THEN it SHALL serialize to valid AG-UI JSON format
3. WHEN I deserialize AG-UI JSON THEN it SHALL create the correct event type
4. WHEN I access event properties THEN they SHALL match AG-UI specification

---

### REQ-DO-2: AG-UI Data Objects
**User Story**: As a developer, I need AG-UI data object types so that I can construct valid AG-UI messages.

#### Acceptance Criteria:
1. WHEN I reference the library THEN I SHALL have classes for:
   - `RunAgentInput`: Input for running an agent
   - `AgentMessage`: Message structure
   - `ToolCall`: Tool call representation
   - `ToolResult`: Tool result representation
   - `ThoughtBlock`: Reasoning/thought representation
   - `SessionMetadata`: Session information

2. WHEN I create data objects THEN they SHALL validate according to AG-UI schema
3. WHEN I serialize data objects THEN they SHALL produce AG-UI-compliant JSON
4. WHEN I deserialize AG-UI JSON THEN it SHALL populate data objects correctly

---

### REQ-DO-3: LmCore to AG-UI Conversion
**User Story**: As a protocol implementer, I need conversion logic from LmCore messages to AG-UI events so that I can transform streaming responses.

#### Acceptance Criteria:
1. WHEN I receive a `TextMessage` THEN I SHALL convert it to `text-chunk` event
2. WHEN I receive a `ToolsCallMessage` THEN I SHALL convert it to `tool-call-chunk` events (one per tool call)
3. WHEN I receive a `ToolsCallResultMessage` THEN I SHALL convert it to `tool-result-chunk` events
4. WHEN I receive a `ToolsCallAggregateMessage` THEN I SHALL convert it to both `tool-call-chunk` and `tool-result-chunk` events
5. WHEN I receive a `ReasoningMessage` THEN I SHALL convert it to `thought-chunk` event
6. WHEN I receive a `UsageMessage` THEN I SHALL convert it to `metadata-update` event
7. WHEN I receive a `CompositeMessage` THEN I SHALL convert each contained message recursively
8. WHEN I receive an `ImageMessage` THEN I SHALL convert it to appropriate AG-UI representation

---

### REQ-DO-4: Extension Methods
**User Story**: As a developer, I want convenient extension methods so that I can easily convert between LmCore and AG-UI types.

#### Acceptance Criteria:
1. WHEN I call `message.ToAgUiEvents()` on any IMessage THEN I SHALL get a collection of AG-UI events
2. WHEN I call `toolCall.ToAgUiToolCall()` on a ToolCall THEN I SHALL get an AG-UI ToolCall object
3. WHEN I call `usage.ToAgUiMetadata()` on UsageMessage THEN I SHALL get an AG-UI metadata-update event
4. WHEN I call `messages.ToAgUiEventStream()` on IAsyncEnumerable&lt;IMessage&gt; THEN I SHALL get IAsyncEnumerable of AG-UI events

---

### REQ-DO-5: JSON Serialization Configuration
**User Story**: As a developer, I need proper JSON serialization configuration so that AG-UI events serialize correctly.

#### Acceptance Criteria:
1. WHEN I serialize AG-UI events THEN property names SHALL use camelCase (AG-UI convention)
2. WHEN I serialize AG-UI events THEN null values SHALL be omitted
3. WHEN I serialize AG-UI events THEN enums SHALL serialize as strings
4. WHEN I serialize AG-UI events THEN timestamps SHALL use ISO 8601 format

---

## Component 2: AG-UI.Protocol

### REQ-PR-1: Protocol Handler
**User Story**: As a protocol implementer, I need a protocol handler that manages AG-UI message flow so that I can process incoming requests and generate responses.

#### Acceptance Criteria:
1. WHEN I create a protocol handler THEN I SHALL provide an IStreamingAgent instance
2. WHEN I receive a `RunAgentInput` message THEN the handler SHALL invoke the agent
3. WHEN the agent streams responses THEN the handler SHALL convert them to AG-UI events
4. WHEN the agent completes THEN the handler SHALL emit `agent-response-complete` event
5. WHEN an error occurs THEN the handler SHALL emit `agent-error` event

---

### REQ-PR-2: Event Streaming
**User Story**: As a protocol implementer, I need event streaming logic so that LmCore message streams are converted to AG-UI event streams.

#### Acceptance Criteria:
1. WHEN the agent produces messages THEN the system SHALL emit `session-started` event first
2. WHEN streaming messages THEN the system SHALL convert each IMessage to AG-UI events immediately
3. WHEN multiple events are produced from one message THEN the system SHALL emit them in sequence
4. WHEN streaming completes THEN the system SHALL emit `agent-response-complete` event
5. WHEN streaming completes THEN the system SHALL emit `session-ended` event last

---

### REQ-PR-3: Session State Management
**User Story**: As a protocol implementer, I need session state management so that I can track conversation history and context.

#### Acceptance Criteria:
1. WHEN a session starts THEN the system SHALL create a session object with unique ID
2. WHEN messages are exchanged THEN the system SHALL append them to session history
3. WHEN a session is active THEN the system SHALL provide access to current state
4. WHEN a session ends THEN the system SHALL clean up session resources
5. WHEN a session is terminated THEN the system SHALL preserve history for retrieval

---

### REQ-PR-4: Message History Tracking
**User Story**: As a developer, I need message history tracking so that I can maintain conversation context across interactions.

#### Acceptance Criteria:
1. WHEN a user message is received THEN the system SHALL add it to conversation history
2. WHEN an agent message is produced THEN the system SHALL add it to conversation history
3. WHEN tool calls occur THEN the system SHALL add them to conversation history
4. WHEN retrieving history THEN the system SHALL return messages in chronological order
5. WHEN history is serialized THEN the system SHALL preserve all message metadata

---

### REQ-PR-5: Tool Call State Tracking
**User Story**: As a protocol implementer, I need tool call state tracking so that I can correlate tool calls with their results.

#### Acceptance Criteria:
1. WHEN a tool call begins THEN the system SHALL track it with a unique call ID
2. WHEN a tool call completes THEN the system SHALL match result with the original call
3. WHEN multiple tools run in parallel THEN the system SHALL track each independently
4. WHEN a tool call fails THEN the system SHALL record the error state
5. WHEN retrieving tool call state THEN the system SHALL include timing information

---

### REQ-PR-6: Error Handling
**User Story**: As a protocol implementer, I need comprehensive error handling so that failures are reported through AG-UI protocol.

#### Acceptance Criteria:
1. WHEN an agent error occurs THEN the system SHALL emit `agent-error` event with error details
2. WHEN a tool call fails THEN the system SHALL emit `tool-result-chunk` with error information
3. WHEN deserialization fails THEN the system SHALL emit `session-error` event
4. WHEN an exception occurs THEN the system SHALL log it and emit appropriate error event
5. WHEN an error is non-recoverable THEN the system SHALL emit `session-ended` with error status

---

## Component 3: AG-UI.AspNetCore

### REQ-AC-1: WebSocket Middleware
**User Story**: As an ASP.NET Core developer, I need WebSocket middleware so that I can expose AG-UI endpoints in my web application.

#### Acceptance Criteria:
1. WHEN I add AG-UI services THEN I SHALL call `services.AddAgUi()`
2. WHEN I configure the pipeline THEN I SHALL call `app.UseAgUi()`
3. WHEN I configure endpoints THEN I SHALL call `endpoints.MapAgUi("/api/ag-ui")`
4. WHEN a WebSocket connection is made THEN the middleware SHALL handle it automatically
5. WHEN the middleware is configured THEN it SHALL integrate with ASP.NET Core DI

---

### REQ-AC-2: WebSocket Handler
**User Story**: As a protocol implementer, I need a WebSocket handler that manages connection lifecycle so that clients can connect and communicate.

#### Acceptance Criteria:
1. WHEN a WebSocket connection is established THEN the handler SHALL accept it
2. WHEN the client sends a message THEN the handler SHALL deserialize and route it
3. WHEN the protocol emits events THEN the handler SHALL serialize and send them
4. WHEN the connection closes THEN the handler SHALL clean up resources
5. WHEN an error occurs THEN the handler SHALL attempt graceful shutdown

---

### REQ-AC-3: Request/Response Handling
**User Story**: As a protocol implementer, I need request/response handling so that AG-UI messages are processed correctly.

#### Acceptance Criteria:
1. WHEN a RunAgentInput is received THEN the system SHALL validate the input
2. WHEN input is valid THEN the system SHALL invoke the protocol handler
3. WHEN events are generated THEN the system SHALL send them via WebSocket
4. WHEN the response is complete THEN the system SHALL wait for the next request
5. WHEN multiple requests arrive THEN the system SHALL queue them (or handle concurrently based on configuration)

---

### REQ-AC-4: Configuration Options
**User Story**: As a developer, I need configuration options so that I can customize AG-UI behavior for my application.

#### Acceptance Criteria:
1. WHEN I configure AG-UI THEN I SHALL specify the WebSocket endpoint path
2. WHEN I configure AG-UI THEN I SHALL specify the agent factory (how to create IStreamingAgent)
3. WHEN I configure AG-UI THEN I SHALL specify session timeout duration
4. WHEN I configure AG-UI THEN I SHALL specify buffer sizes and limits
5. WHEN I configure AG-UI THEN I SHALL specify error handling behavior

---

### REQ-AC-5: Dependency Injection Integration
**User Story**: As a developer, I need DI integration so that I can inject dependencies into AG-UI components.

#### Acceptance Criteria:
1. WHEN I register AG-UI services THEN they SHALL be available via DI
2. WHEN I inject IAgUiProtocolHandler THEN I SHALL receive the configured implementation
3. WHEN I inject IAgUiSessionManager THEN I SHALL receive the session manager
4. WHEN I configure agent factories THEN they SHALL support constructor injection
5. WHEN I register custom services THEN they SHALL be available in agent factories

---

### REQ-AC-6: CORS Support
**User Story**: As a web developer, I need CORS support so that frontend applications on different domains can connect.

#### Acceptance Criteria:
1. WHEN I configure CORS THEN I SHALL specify allowed origins
2. WHEN a WebSocket connection is made THEN CORS SHALL be enforced
3. WHEN CORS is configured globally THEN AG-UI SHALL respect it
4. WHEN CORS is configured for AG-UI only THEN it SHALL apply to AG-UI endpoints only
5. WHEN CORS validation fails THEN the connection SHALL be rejected

---

## Component 4: AG-UI.Sample

### REQ-SA-1: Basic Chat Endpoint
**User Story**: As a developer learning AG-UI integration, I need a basic chat endpoint example so that I can see how to set up a simple agent.

#### Acceptance Criteria:
1. WHEN I run the sample app THEN it SHALL expose a WebSocket endpoint at `/api/ag-ui/chat`
2. WHEN I connect to the endpoint THEN I SHALL receive a session-started event
3. WHEN I send a message THEN the agent SHALL respond with text
4. WHEN I disconnect THEN the session SHALL clean up properly
5. WHEN I review the code THEN it SHALL include comments explaining each step

---

### REQ-SA-2: Tool Calling Example
**User Story**: As a developer, I need a tool calling example so that I can see how to expose agents with tool capabilities.

#### Acceptance Criteria:
1. WHEN I run the sample app THEN it SHALL expose an endpoint at `/api/ag-ui/tools`
2. WHEN I send a request THEN the agent SHALL have access to sample tools (e.g., weather, calculator)
3. WHEN the agent calls tools THEN I SHALL see tool-call-chunk and tool-result-chunk events
4. WHEN tools return results THEN the agent SHALL incorporate them in the response
5. WHEN I review the code THEN it SHALL show how to register tools with the agent

---

### REQ-SA-3: Reasoning Example
**User Story**: As a developer, I need a reasoning example so that I can see how to expose agent reasoning process.

#### Acceptance Criteria:
1. WHEN I run the sample app THEN it SHALL expose an endpoint at `/api/ag-ui/reasoning`
2. WHEN the agent reasons THEN I SHALL see thought-chunk events
3. WHEN reasoning is complete THEN I SHALL see thought-complete event
4. WHEN I enable/disable reasoning THEN the behavior SHALL change accordingly
5. WHEN I review the code THEN it SHALL show how to configure reasoning

---

### REQ-SA-4: Configuration Examples
**User Story**: As a developer, I need configuration examples so that I can see how to customize AG-UI behavior.

#### Acceptance Criteria:
1. WHEN I review the sample THEN it SHALL include appsettings.json with AG-UI configuration
2. WHEN I review the sample THEN it SHALL show how to configure different agent types
3. WHEN I review the sample THEN it SHALL show how to configure middleware pipeline
4. WHEN I review the sample THEN it SHALL show how to configure session management
5. WHEN I review the sample THEN it SHALL include environment-specific configurations

---

### REQ-SA-5: HTML Test Client
**User Story**: As a developer, I need an HTML test client so that I can test the WebSocket endpoints without setting up a full React app.

#### Acceptance Criteria:
1. WHEN I navigate to `/` in the sample app THEN I SHALL see an HTML test page
2. WHEN I use the test client THEN I can connect to any AG-UI endpoint
3. WHEN I send messages THEN I SHALL see events displayed in real-time
4. WHEN events arrive THEN they SHALL be formatted for readability
5. WHEN I review the HTML THEN it SHALL include JavaScript for WebSocket communication

---

### REQ-SA-6: Documentation and README
**User Story**: As a developer, I need clear documentation so that I can understand how to use the sample and integrate AG-UI.

#### Acceptance Criteria:
1. WHEN I open the sample project THEN I SHALL find a README.md file
2. WHEN I read the README THEN it SHALL explain how to run the sample
3. WHEN I read the README THEN it SHALL explain each endpoint and its purpose
4. WHEN I read the README THEN it SHALL link to AG-UI documentation
5. WHEN I read the README THEN it SHALL include troubleshooting tips

---

## Data Mapping Specifications

### Message Type to AG-UI Event Mapping

#### TextMessage → text-chunk
```csharp
// LmCore
TextMessage {
    Text: string,
    Role: Role,
    FromAgent: string,
    GenerationId: string
}

// AG-UI
{
    "type": "text-chunk",
    "sessionId": "{session-id}",
    "messageId": "{generation-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "text": "{text}",
        "role": "assistant",
        "isComplete": false
    }
}
```

**Acceptance Criteria**:
1. WHEN converting TextMessage THEN `data.text` SHALL contain the message text
2. WHEN converting TextMessage THEN `messageId` SHALL be the GenerationId
3. WHEN converting TextMessage THEN `data.role` SHALL map from LmCore Role enum
4. WHEN text streaming is complete THEN emit `text-message-complete` event

---

#### ToolsCallMessage → tool-call-chunk
```csharp
// LmCore
ToolsCallMessage {
    ToolCalls: ImmutableList<ToolCall>,
    Role: Role,
    FromAgent: string,
    GenerationId: string
}

ToolCall {
    Index: int,
    CallId: string,
    FunctionName: string,
    FunctionArgs: string
}

// AG-UI (one event per tool call)
{
    "type": "tool-call-chunk",
    "sessionId": "{session-id}",
    "messageId": "{generation-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "toolCallId": "{call-id}",
        "toolName": "{function-name}",
        "arguments": {parsed-json-args},
        "isComplete": false
    }
}
```

**Acceptance Criteria**:
1. WHEN converting ToolsCallMessage THEN emit one event per ToolCall
2. WHEN converting ToolCall THEN `data.toolCallId` SHALL be the CallId
3. WHEN converting ToolCall THEN `data.toolName` SHALL be the FunctionName
4. WHEN converting ToolCall THEN `data.arguments` SHALL parse FunctionArgs JSON
5. WHEN all tool calls are emitted THEN emit `tool-call-complete` event

---

#### ToolsCallResultMessage → tool-result-chunk
```csharp
// LmCore
ToolsCallResultMessage {
    ToolCallResults: ImmutableList<ToolCallResult>,
    Role: Role
}

ToolCallResult {
    Index: int,
    CallId: string,
    FunctionName: string,
    Result: string
}

// AG-UI (one event per result)
{
    "type": "tool-result-chunk",
    "sessionId": "{session-id}",
    "messageId": "{message-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "toolCallId": "{call-id}",
        "result": "{result}",
        "isError": false,
        "isComplete": false
    }
}
```

**Acceptance Criteria**:
1. WHEN converting ToolsCallResultMessage THEN emit one event per ToolCallResult
2. WHEN converting ToolCallResult THEN `data.toolCallId` SHALL match the original call ID
3. WHEN converting ToolCallResult THEN `data.result` SHALL contain the result string
4. WHEN result indicates error THEN `data.isError` SHALL be true
5. WHEN all results are emitted THEN emit `tool-result-complete` event

---

#### ToolsCallAggregateMessage → Multiple Events
```csharp
// LmCore
ToolsCallAggregateMessage {
    ToolsCallMessage: ToolsCallMessage,
    ToolsCallResult: ToolsCallResultMessage
}

// AG-UI (emits both call and result events)
// First: tool-call-chunk events (one per call)
// Then: tool-result-chunk events (one per result)
```

**Acceptance Criteria**:
1. WHEN converting ToolsCallAggregateMessage THEN emit tool-call-chunk events first
2. WHEN tool-call-chunk events complete THEN emit tool-result-chunk events
3. WHEN all events are emitted THEN maintain ordering (calls before results)
4. WHEN multiple aggregates occur THEN maintain chronological order

---

#### ReasoningMessage → thought-chunk
```csharp
// LmCore
ReasoningMessage {
    Reasoning: string,
    Role: Role,
    FromAgent: string,
    GenerationId: string
}

// AG-UI
{
    "type": "thought-chunk",
    "sessionId": "{session-id}",
    "messageId": "{generation-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "thought": "{reasoning}",
        "isComplete": false
    }
}
```

**Acceptance Criteria**:
1. WHEN converting ReasoningMessage THEN `data.thought` SHALL contain the reasoning text
2. WHEN reasoning is streaming THEN `isComplete` SHALL be false
3. WHEN reasoning is complete THEN emit `thought-complete` event
4. WHEN reasoning is disabled THEN no events SHALL be emitted

---

#### UsageMessage → metadata-update
```csharp
// LmCore
UsageMessage {
    Usage: Usage
}

Usage {
    PromptTokens: int,
    CompletionTokens: int,
    TotalTokens: int
}

// AG-UI
{
    "type": "metadata-update",
    "sessionId": "{session-id}",
    "messageId": "{message-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "usage": {
            "promptTokens": {prompt-tokens},
            "completionTokens": {completion-tokens},
            "totalTokens": {total-tokens}
        }
    }
}
```

**Acceptance Criteria**:
1. WHEN converting UsageMessage THEN `data.usage` SHALL contain all token counts
2. WHEN converting UsageMessage THEN property names SHALL use camelCase
3. WHEN usage is updated THEN emit metadata-update event
4. WHEN multiple usage messages occur THEN emit multiple metadata-update events

---

#### CompositeMessage → Recursive Conversion
```csharp
// LmCore
CompositeMessage {
    Messages: ImmutableList<IMessage>,
    Role: Role,
    FromAgent: string,
    GenerationId: string
}

// AG-UI
// Each contained message converts to its respective events
```

**Acceptance Criteria**:
1. WHEN converting CompositeMessage THEN iterate through all contained messages
2. WHEN converting each message THEN apply the appropriate conversion rule
3. WHEN emitting events THEN maintain the order of contained messages
4. WHEN nested CompositeMessages exist THEN recursively convert them

---

## WebSocket Protocol Specification

### Connection Lifecycle

#### Connection Establishment
```
Client                          Server
  |                               |
  |--- WebSocket Handshake ----> |
  |<-- 101 Switching Protocols -- |
  |                               |
  |<---- session-started -------- |
  |                               |
```

**Acceptance Criteria**:
1. WHEN client connects THEN server SHALL accept WebSocket upgrade
2. WHEN connection established THEN server SHALL emit `session-started` event
3. WHEN `session-started` emitted THEN it SHALL include session ID
4. WHEN connection fails THEN server SHALL return appropriate HTTP error

---

#### Message Exchange
```
Client                          Server
  |                               |
  |--- RunAgentInput ----------> |
  |                               |
  |<---- text-chunk ------------- |
  |<---- text-chunk ------------- |
  |<---- tool-call-chunk -------- |
  |<---- tool-result-chunk ------- |
  |<---- text-chunk ------------- |
  |<---- agent-response-complete - |
  |                               |
```

**Acceptance Criteria**:
1. WHEN client sends RunAgentInput THEN server SHALL validate it
2. WHEN input is valid THEN server SHALL start agent processing
3. WHEN agent produces messages THEN server SHALL stream events
4. WHEN agent completes THEN server SHALL emit `agent-response-complete`
5. WHEN errors occur THEN server SHALL emit `agent-error`

---

#### Connection Termination
```
Client                          Server
  |                               |
  |<---- session-ended ---------- |
  |                               |
  |--- WebSocket Close ---------- |
  |<-- WebSocket Close Ack ------ |
  |                               |
```

**Acceptance Criteria**:
1. WHEN conversation ends THEN server SHALL emit `session-ended`
2. WHEN client closes connection THEN server SHALL clean up session
3. WHEN server closes connection THEN client SHALL receive close frame
4. WHEN connection drops THEN server SHALL detect and clean up

---

### Message Format

#### Client to Server: RunAgentInput
```json
{
    "type": "run-agent",
    "sessionId": "optional-session-id",
    "data": {
        "messages": [
            {
                "role": "user",
                "content": "Hello, how can you help me?"
            }
        ],
        "options": {
            "modelId": "openai/gpt-4",
            "temperature": 0.7,
            "maxTokens": 2048
        }
    }
}
```

**Acceptance Criteria**:
1. WHEN client sends message THEN it SHALL include `type: "run-agent"`
2. WHEN client sends message THEN it MAY include `sessionId` to continue existing session
3. WHEN client sends message THEN `data.messages` SHALL contain conversation history
4. WHEN client sends message THEN `data.options` MAY include generation options

---

#### Server to Client: AG-UI Events
All events follow this structure:
```json
{
    "type": "{event-type}",
    "sessionId": "{session-id}",
    "messageId": "{message-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        // Event-specific data
    }
}
```

**Acceptance Criteria**:
1. WHEN server emits event THEN it SHALL include all required fields
2. WHEN server emits event THEN `type` SHALL be one of the 16 AG-UI event types
3. WHEN server emits event THEN `timestamp` SHALL be ISO 8601 format
4. WHEN server emits event THEN `data` SHALL conform to event-specific schema

---

### Error Handling Protocol

#### Agent Error Event
```json
{
    "type": "agent-error",
    "sessionId": "{session-id}",
    "messageId": "{message-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "error": {
            "code": "AGENT_EXECUTION_ERROR",
            "message": "Error description",
            "details": {
                // Additional error context
            }
        }
    }
}
```

**Acceptance Criteria**:
1. WHEN agent error occurs THEN emit `agent-error` event
2. WHEN emitting error THEN include error code and message
3. WHEN emitting error THEN MAY include additional details
4. WHEN error is emitted THEN session MAY continue or end based on severity

---

#### Session Error Event
```json
{
    "type": "session-error",
    "sessionId": "{session-id}",
    "timestamp": "{ISO-8601}",
    "data": {
        "error": {
            "code": "SESSION_ERROR",
            "message": "Session-level error",
            "isFatal": true
        }
    }
}
```

**Acceptance Criteria**:
1. WHEN session error occurs THEN emit `session-error` event
2. WHEN error is fatal THEN `data.error.isFatal` SHALL be true
3. WHEN error is fatal THEN emit `session-ended` after session-error
4. WHEN error is non-fatal THEN session MAY continue

---

## State Management Requirements

### REQ-SM-1: Session State
**User Story**: As a protocol implementer, I need session state management so that I can track active sessions and their context.

#### Acceptance Criteria:
1. WHEN a session starts THEN create SessionState with:
   - Unique session ID (GUID)
   - Creation timestamp
   - Last activity timestamp
   - Conversation history
   - Current agent instance
   - Configuration options

2. WHEN messages are exchanged THEN update last activity timestamp
3. WHEN session is idle beyond timeout THEN clean up session
4. WHEN session ends THEN persist history (if configured)
5. WHEN retrieving session THEN return current state or null if not found

---

### REQ-SM-2: Conversation History
**User Story**: As a developer, I need conversation history management so that I can maintain context across multiple interactions.

#### Acceptance Criteria:
1. WHEN user message arrives THEN add to history with:
   - Message ID
   - Timestamp
   - Role
   - Content
   - Metadata

2. WHEN agent responds THEN add to history
3. WHEN tool calls occur THEN add to history with linkage to results
4. WHEN retrieving history THEN return in chronological order
5. WHEN history exceeds limit THEN apply truncation strategy (configurable)

---

### REQ-SM-3: Tool Call Correlation
**User Story**: As a protocol implementer, I need tool call correlation so that I can match tool calls with their results.

#### Acceptance Criteria:
1. WHEN tool call begins THEN create ToolCallState with:
   - Tool call ID
   - Tool name
   - Arguments
   - Start timestamp
   - Status (pending/completed/failed)

2. WHEN tool result arrives THEN update ToolCallState with:
   - Result data
   - End timestamp
   - Status

3. WHEN multiple tools run in parallel THEN track each independently
4. WHEN retrieving tool state THEN include duration
5. WHEN tool call times out THEN mark as failed

---

### REQ-SM-4: Session Timeout
**User Story**: As a system administrator, I need session timeout so that inactive sessions are cleaned up automatically.

#### Acceptance Criteria:
1. WHEN session is created THEN set timeout timer (default: 30 minutes)
2. WHEN activity occurs THEN reset timeout timer
3. WHEN timeout expires THEN emit `session-ended` and clean up
4. WHEN timeout is configured THEN use configured value
5. WHEN session is explicitly closed THEN cancel timeout timer

---

## API Specifications

### ASP.NET Core Integration API

#### Service Registration
```csharp
public static IServiceCollection AddAgUi(
    this IServiceCollection services,
    Action<AgUiOptions> configure = null)
{
    // Register AG-UI services
}

public class AgUiOptions
{
    public string EndpointPath { get; set; } = "/api/ag-ui";
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);
    public int MaxMessageHistorySize { get; set; } = 100;
    public Func<IServiceProvider, IStreamingAgent> AgentFactory { get; set; }
}
```

**Acceptance Criteria**:
1. WHEN calling AddAgUi THEN register all required services
2. WHEN providing options THEN apply them to registered services
3. WHEN AgentFactory is provided THEN use it to create agents
4. WHEN AgentFactory is null THEN throw configuration exception

---

#### Middleware Registration
```csharp
public static IApplicationBuilder UseAgUi(
    this IApplicationBuilder app)
{
    // Configure AG-UI middleware
}

public static IEndpointRouteBuilder MapAgUi(
    this IEndpointRouteBuilder endpoints,
    string pattern = "/api/ag-ui")
{
    // Map AG-UI WebSocket endpoint
}
```

**Acceptance Criteria**:
1. WHEN calling UseAgUi THEN configure WebSocket support
2. WHEN calling MapAgUi THEN register WebSocket endpoint at specified path
3. WHEN endpoint pattern is custom THEN use custom pattern
4. WHEN services not registered THEN throw exception

---

### Protocol Handler API

#### IAgUiProtocolHandler Interface
```csharp
public interface IAgUiProtocolHandler
{
    Task<string> CreateSessionAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<AgUiEvent> ProcessRequestAsync(
        string sessionId,
        RunAgentInput input,
        CancellationToken cancellationToken = default);

    Task EndSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}
```

**Acceptance Criteria**:
1. WHEN CreateSessionAsync called THEN return new session ID
2. WHEN ProcessRequestAsync called THEN stream AG-UI events
3. WHEN EndSessionAsync called THEN clean up session
4. WHEN cancellation requested THEN stop processing and clean up

---

### Session Manager API

#### IAgUiSessionManager Interface
```csharp
public interface IAgUiSessionManager
{
    Task<AgUiSession> GetOrCreateSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task<AgUiSession?> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    Task UpdateSessionAsync(
        AgUiSession session,
        CancellationToken cancellationToken = default);

    Task RemoveSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);
}

public class AgUiSession
{
    public string SessionId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public List<AgUiMessage> ConversationHistory { get; set; }
    public Dictionary<string, object> Metadata { get; set; }
}
```

**Acceptance Criteria**:
1. WHEN GetOrCreateSessionAsync called THEN return existing or create new
2. WHEN GetSessionAsync called THEN return existing or null
3. WHEN UpdateSessionAsync called THEN persist session state
4. WHEN RemoveSessionAsync called THEN delete session and clean up

---

## Sample Application Requirements

### Project Structure
```
AG-UI.Sample/
├── Controllers/
│   └── AgUiController.cs (if using controller-based approach)
├── wwwroot/
│   ├── index.html (test client)
│   └── js/
│       └── agui-client.js
├── Agents/
│   ├── SimpleAgent.cs
│   ├── ToolsAgent.cs
│   └── ReasoningAgent.cs
├── Tools/
│   ├── WeatherTool.cs
│   └── CalculatorTool.cs
├── Program.cs
├── appsettings.json
└── README.md
```

**Acceptance Criteria**:
1. WHEN project is opened THEN structure SHALL match above
2. WHEN built THEN it SHALL compile without errors
3. WHEN run THEN it SHALL start on port 5000 (configurable)
4. WHEN navigated to root THEN test client SHALL be displayed

---

### Configuration (appsettings.json)
```json
{
  "AgUi": {
    "EndpointPath": "/api/ag-ui",
    "SessionTimeout": "00:30:00",
    "MaxMessageHistorySize": 100,
    "Endpoints": {
      "Chat": {
        "Path": "/chat",
        "AgentType": "SimpleAgent",
        "ModelId": "openai/gpt-4"
      },
      "Tools": {
        "Path": "/tools",
        "AgentType": "ToolsAgent",
        "ModelId": "openai/gpt-4",
        "EnableTools": true
      },
      "Reasoning": {
        "Path": "/reasoning",
        "AgentType": "ReasoningAgent",
        "ModelId": "openai/gpt-4",
        "EnableReasoning": true
      }
    }
  }
}
```

**Acceptance Criteria**:
1. WHEN configuration is loaded THEN it SHALL populate AgUiOptions
2. WHEN endpoints are configured THEN they SHALL be mapped correctly
3. WHEN agent types are specified THEN correct agents SHALL be created
4. WHEN settings are invalid THEN startup SHALL fail with clear error

---

## Dependencies & Technical Considerations

### NuGet Package Dependencies

#### AG-UI.DataObjects
- `System.Text.Json` (>= 8.0.0) - JSON serialization
- `System.Collections.Immutable` (>= 8.0.0) - Immutable collections

#### AG-UI.Protocol
- `AG-UI.DataObjects` (project reference)
- `LmCore` (project reference)
- `Microsoft.Extensions.Logging.Abstractions` (>= 8.0.0) - Logging

#### AG-UI.AspNetCore
- `AG-UI.Protocol` (project reference)
- `Microsoft.AspNetCore.WebSockets` (>= 8.0.0) - WebSocket support
- `Microsoft.Extensions.DependencyInjection.Abstractions` (>= 8.0.0) - DI

#### AG-UI.Sample
- `AG-UI.AspNetCore` (project reference)
- `LmCore` (project reference)
- `OpenAIProvider` or `AnthropicProvider` (project reference)

**Acceptance Criteria**:
1. WHEN packages are restored THEN all dependencies SHALL resolve
2. WHEN targeting .NET 8.0 THEN all packages SHALL be compatible
3. WHEN new versions are available THEN compatibility SHALL be verified
4. WHEN packages have vulnerabilities THEN they SHALL be updated

---

### .NET Version Requirements
- **Target Framework**: .NET 8.0
- **Language Version**: C# 12

**Acceptance Criteria**:
1. WHEN projects are created THEN they SHALL target .NET 8.0
2. WHEN C# 12 features are used THEN they SHALL compile correctly
3. WHEN deployed THEN .NET 8.0 runtime SHALL be available
4. WHEN LmCore updates to new .NET version THEN AG-UI SHALL follow

---

### Performance Considerations

#### Streaming Performance
**Acceptance Criteria**:
1. WHEN streaming events THEN memory allocation SHALL be minimized
2. WHEN streaming events THEN use `IAsyncEnumerable` for efficiency
3. WHEN multiple clients connected THEN each SHALL stream independently
4. WHEN high message volume occurs THEN backpressure SHALL be applied

---

#### WebSocket Buffer Sizes
**Acceptance Criteria**:
1. WHEN WebSocket is configured THEN buffer size SHALL be 4KB (default)
2. WHEN large messages occur THEN buffer SHALL expand automatically
3. WHEN buffer exceeds limit THEN message SHALL be chunked
4. WHEN memory pressure occurs THEN buffer SHALL be released

---

#### Session Memory Management
**Acceptance Criteria**:
1. WHEN sessions are inactive THEN they SHALL be cleaned up
2. WHEN history grows large THEN truncation strategy SHALL apply
3. WHEN memory threshold reached THEN oldest sessions SHALL be removed
4. WHEN session is removed THEN all resources SHALL be released

---

### Security Considerations

#### Authentication & Authorization
**Acceptance Criteria**:
1. WHEN authentication is configured THEN it SHALL be enforced on WebSocket connections
2. WHEN authorization is required THEN it SHALL be checked before session creation
3. WHEN authentication fails THEN connection SHALL be rejected with 401
4. WHEN authorization fails THEN connection SHALL be rejected with 403

---

#### Input Validation
**Acceptance Criteria**:
1. WHEN RunAgentInput is received THEN it SHALL be validated
2. WHEN validation fails THEN `session-error` event SHALL be emitted
3. WHEN message size exceeds limit THEN it SHALL be rejected
4. WHEN malformed JSON received THEN it SHALL be handled gracefully

---

#### Rate Limiting
**Acceptance Criteria**:
1. WHEN rate limiting is configured THEN it SHALL be enforced per session
2. WHEN rate limit exceeded THEN requests SHALL be throttled
3. WHEN throttling occurs THEN client SHALL be notified
4. WHEN rate limit is global THEN it SHALL apply across all sessions

---

### Logging & Diagnostics

**Acceptance Criteria**:
1. WHEN AG-UI components execute THEN they SHALL log to ILogger
2. WHEN errors occur THEN they SHALL be logged with stack traces
3. WHEN sessions start/end THEN they SHALL be logged
4. WHEN tool calls occur THEN they SHALL be logged with timing
5. WHEN logging is configured THEN it SHALL respect log levels

---

### Testing Considerations

#### Unit Testing Requirements
**Acceptance Criteria**:
1. WHEN AG-UI.DataObjects is tested THEN conversion logic SHALL be covered
2. WHEN AG-UI.Protocol is tested THEN protocol handler SHALL be covered
3. WHEN AG-UI.AspNetCore is tested THEN middleware SHALL be covered
4. WHEN tests run THEN code coverage SHALL be >= 80%

---

#### Integration Testing Requirements
**Acceptance Criteria**:
1. WHEN integration tests run THEN they SHALL use TestServer
2. WHEN testing WebSocket THEN real WebSocket connections SHALL be used
3. WHEN testing agents THEN mock agents SHALL be available
4. WHEN testing end-to-end THEN full pipeline SHALL be validated

---

## Future Considerations

### Potential Enhancements
1. **Server-Sent Events (SSE) Support**: Alternative to WebSocket for simpler scenarios
2. **gRPC Support**: For high-performance scenarios
3. **Persistent Sessions**: Store sessions in database for multi-server scenarios
4. **Metrics & Monitoring**: Prometheus metrics, health checks
5. **Advanced Rate Limiting**: Token bucket, sliding window algorithms
6. **Compression**: WebSocket message compression for bandwidth optimization
7. **Batching**: Batch multiple events for efficiency

---

## Glossary

- **AG-UI**: Agentic UI protocol for standardized agent communication
- **CopilotKit**: React framework for building AI-powered applications
- **IStreamingAgent**: LmCore interface for streaming agents
- **Middleware**: Pipeline component that processes messages
- **Session**: Stateful conversation between client and agent
- **Tool Call**: Agent's invocation of an external function/tool
- **Event**: AG-UI protocol message sent from server to client

---

## References

1. **AG-UI Repository**: https://github.com/ckpearson/ag-ui.git
2. **AG-UI Documentation**: https://docs.ag-ui.com/concepts/architecture
3. **CopilotKit**: https://copilotkit.ai
4. **WebSocket Protocol**: RFC 6455
5. **LmCore Reference**: D:\Source\repos\LmDotnetTools\src\LmCore
6. **Example Implementation**: D:\Source\repos\LmDotnetTools\example\ExamplePythonMCPClient\Program.cs
