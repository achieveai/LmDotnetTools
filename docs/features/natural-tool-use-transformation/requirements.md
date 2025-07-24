# Feature Specification: Natural Tool Use Transformation

## High-Level Overview

This feature enables the SDK to maintain a consistent interface while supporting different LLM models with varying tool calling capabilities. The Natural Tool Use Transformation creates a reverse transformation of the existing NaturalToolUseParserMiddleware, converting ToolsCallAggregateMessage back to a natural language format with embedded XML-style tool calls and responses.

## High Level Requirements

1. **SDK Interface Consistency**: Maintain the same SDK interface regardless of the underlying model's tool calling capabilities
2. **Bidirectional Transformation**: Provide the reverse operation of NaturalToolUseParserMiddleware to transform structured tool calls back to natural text format
3. **Message Chain Consolidation**: Combine message chains (TextMessage + ToolsCallAggregateMessage + TextMessage) back into a single TextMessage
4. **Natural Tool Use Format**: Transform tool calls and results into human-readable XML-style format embedded within text
5. **Provider Compatibility**: Enable models that prefer natural language tool representations to work seamlessly with the existing SDK

## Existing Solutions

### Current Forward Transformation (NaturalToolUseParserMiddleware)

The existing `NaturalToolUseParserMiddleware` performs the following transformation:

```
Natural Text with <tool_call> blocks → ToolsCallMessage + ToolsCallResultMessage
```

**Input Format:**
```
I'll check the weather for you.

<tool_call name="GetWeather">
```json
{
  "location": "San Francisco, CA",
  "unit": "celsius"
}
```
</tool_call>

Based on the results, here's the forecast...
```

**Output:** Separate TextMessage and ToolsCallMessage objects

### Related Message Types

- **ToolsCallAggregateMessage**: Combines ToolsCallMessage and ToolsCallResultMessage
- **ToolsCallMessage**: Contains structured tool call data
- **ToolsCallResultMessage**: Contains tool execution results
- **TextMessage**: Contains natural language text

## Current Implementation

### Key Components

1. **ToolsCallAggregateMessage** (`src/LmCore/Messages/ToolCallAggregateMessage.cs`)
   - Combines tool call and result into single message
   - Contains `ToolsCallMessage` and `ToolsCallResultMessage`
   - Has `Role.Assistant` and combined metadata

2. **NaturalToolUseParserMiddleware** (`src/LmCore/Middleware/NaturalToolUseParserMiddleware.cs`)
   - Parses natural tool use from text
   - Validates JSON against schemas
   - Creates structured ToolsCallMessage objects

3. **FunctionCallMiddleware** (`src/LmCore/Middleware/FunctionCallMiddleware.cs`)
   - Executes tool calls and creates ToolsCallAggregateMessage
   - Combines tool call with execution results

### Current Message Flow

```
User Input → NaturalToolUseParserMiddleware → ToolsCallMessage → FunctionCallMiddleware → ToolsCallAggregateMessage
```

## Detailed Requirements

### Requirement 1: Reverse Transformation Middleware
- **User Story**: As a developer using models that prefer natural tool use format, I want the SDK to automatically transform structured tool calls back to natural language format so that I can maintain consistent SDK interfaces across different model types.

#### Acceptance Criteria:
1. **WHEN** a message chain contains ToolsCallAggregateMessage **THEN** the middleware **SHALL** transform it back to natural tool use format
2. **WHEN** transforming tool calls **THEN** the middleware **SHALL** preserve all tool call metadata and results
3. **WHEN** multiple tool calls exist **THEN** the middleware **SHALL** handle them sequentially with proper separators

### Requirement 2: Message Chain Consolidation
- **User Story**: As a developer, I want message chains that were split during parsing to be recombined into single messages so that the output matches what the original model would have produced.

#### Acceptance Criteria:
1. **WHEN** a message sequence contains TextMessage + ToolsCallAggregateMessage + TextMessage **THEN** the transformer **SHALL** combine them into a single TextMessage
2. **WHEN** consolidating messages **THEN** the transformer **SHALL** preserve all text content in proper order
3. **WHEN** no surrounding text messages exist **THEN** the transformer **SHALL** create a message containing only the tool call/response format

### Requirement 3: XML-Style Tool Format
- **User Story**: As a developer, I want tool calls and results formatted in a clear, human-readable XML-style format so that the output is easy to read, debug, and process.

#### Acceptance Criteria:
1. **WHEN** transforming tool calls **THEN** the output **SHALL** follow this format:
   ```
   <tool_call name="FunctionName">
   TOOL_CALL_ARGUMENT_JSON
   </tool_call>
   <tool_response name="FunctionName">
   TOOL_CALL_RESPONSE
   </tool_response>
   ```
2. **WHEN** multiple tool call/response pairs exist **THEN** they **SHALL** be separated by `---`
3. **WHEN** tool response is simple text/number/boolean **THEN** it **SHALL** be rendered as plain text
4. **WHEN** tool response is complex JSON **THEN** it **SHALL** be rendered as formatted JSON

### Requirement 4: Result Message Role Transformation
- **User Story**: As a developer, I want tool results to be properly attributed to the "user" role so that the conversation flow matches standard LLM interaction patterns.

#### Acceptance Criteria:
1. **WHEN** transforming ToolCallResult **THEN** the containing message **SHALL** have Role.User
2. **WHEN** creating the consolidated message **THEN** tool responses **SHALL** be embedded within the natural text flow
3. **WHEN** no additional text surrounds the tool calls **THEN** the message role **SHALL** be determined by the dominant content type

### Requirement 5: Integration Points
- **User Story**: As a developer, I want the transformation to integrate seamlessly with existing middleware pipelines so that I can selectively apply it based on model capabilities.

#### Acceptance Criteria:
1. **WHEN** integrated with provider adapters **THEN** the transformer **SHALL** be configurable per provider
2. **WHEN** processing message streams **THEN** the transformer **SHALL** support both streaming and non-streaming modes
3. **WHEN** errors occur during transformation **THEN** the transformer **SHALL** provide meaningful error messages with context

### Requirement 6: Preservation of Metadata
- **User Story**: As a developer, I want all message metadata to be preserved during transformation so that tracing, debugging, and analytics continue to work correctly.

#### Acceptance Criteria:
1. **WHEN** transforming messages **THEN** all metadata from source messages **SHALL** be preserved in the output
2. **WHEN** combining multiple messages **THEN** metadata **SHALL** be merged with appropriate precedence rules
3. **WHEN** generation IDs exist **THEN** they **SHALL** be maintained in the transformed output
