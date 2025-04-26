# Natural Tool Use Middleware

## Overview

Agents can now invoke tools *inline* within a single LLM message—no more back-and-forth function‐call handoff.  
This middleware pair:

1. **Injects** your function contracts into the system prompt (as Markdown)  
2. **Parses** any inline tool calls out of the LLM's reply  
3. **Splits** that reply into `TextMessage` + `ToolCallMessage`  
4. **Delegates** actual tool invocation to `FunctionCallMiddleware`

Benefits:
- Captures chain-of-thought in one pass  
- Reduces prompt tokens  
- Maintains full JSON-schema validation

---

## Composition

```txt
NaturalToolUseMiddleware
     ├─► NaturalToolUseParserMiddleware
     └─► FunctionCallMiddleware (downstream)
```

### NaturalToolUseMiddleware

Implements `IMiddleware` & `IStreamingMiddleware`.

Constructor:
```csharp
public NaturalToolUseMiddleware(
    IEnumerable<FunctionContract> functions,
    IDictionary<string, Func<string, Task<string>>> functionMap,
    IAgent fallbackParser,
    string? name = null)
```

Responsibilities:
1. Creates both `NaturalToolUseParserMiddleware` and `FunctionCallMiddleware`.
2. Combines both of them into a single middleware.
3. Internally use combined middleware for both `InvokeAsync` and `InvokeStreamingAsync`.
---

### NaturalToolUseParserMiddleware

Implements `IMiddleware` & `IStreamingMiddleware`.

Constructor:
```csharp
public NaturalToolUseParserMiddleware(
    IEnumerable<FunctionContract> functions,
    JsonSchemaValidator schemaValidator,
    IAgent fallbackParser)
```

Responsibilities:
0. **Prompt Injection**:
   - Render `functions` as Markdown and append to system prompt
1. **Parsing**:
   - Buffer streaming tokens until end-of-message
   - Detect fenced `<tool_name>`..`</tool_name>` blocks (note, only blocks with tool names are valid).
   - Extract JSON payload from the block
2. **Validation**:
   - Run `schemaValidator.Validate(json, contract.Schema)`  
   - On success:  
     a. Create `TextMessage` with all text before the call  
     b. Create `ToolCallMessage` with `FunctionName` + `Arguments`
   - On validation failure:
     - If `fallbackParser` provided, re-prompt it to emit a valid call
     - Otherwise throw `ToolUseParsingException`
3. **Edge-cases**:
   - Multiple calls in one reply → handle sequentially  
   - Partial JSON across streams → buffer and reassemble  
4. **Pass-through** any text with no tool calls untouched

---

## Algorithm (pseudo)

1. **On InvokeAsync/InvokeStreamingAsync** (user→LLM)
   - If first invocation:  
     a. `md = RenderContractsToMarkdown(functionContracts)`  
     b. `context.SystemMessage = md + "\n\n" + context.SystemMessage`
   - `await parserMiddleware.InvokeAsync(context, agent, cancellationToken)`

2. **On Processing Response** (LLM→agent)
   - For streaming:
     - As new tokens arrive, continuously parse for the start of a tool call (e.g., detect opening `<tool_name>` tag)
     - If start of tool call detected, begin buffering tokens until the end of the tool call (e.g., closing `</tool_name>` tag)
     - If not buffering for a tool call, stream messages out as update messages
     - Once tool call is complete:
       &nbsp;&nbsp;`parsed = JsonDocument.Parse(json)`  
       &nbsp;&nbsp;`Validate(parsed)`  
       &nbsp;&nbsp;`return new[] { new TextMessage(prefix), new ToolCallMessage(parsed) };`  
   - For non-streaming:
     - `raw = await ReadAllTokens()`  
     - `if (MatchToolCall(raw, out prefix, out json, out suffix)) {`  
       &nbsp;&nbsp;`parsed = JsonDocument.Parse(json)`  
       &nbsp;&nbsp;`Validate(parsed)`  
       &nbsp;&nbsp;`return new[] { new TextMessage(prefix), new ToolCallMessage(parsed) };`  
     `}`  
     - `else return new[] { new TextMessage(raw) };`

---

## Examples of Natural Tool Use

### Weather Query Tool

Tool Name: `GetWeather`

**Schema Definition:**
```json
{
  "type": "object",
  "properties": {
    "location": {
      "type": "string",
      "description": "The city and state, e.g. San Francisco, CA"
    },
    "unit": {
      "type": "string",
      "enum": ["celsius", "fahrenheit"],
      "description": "The temperature unit to use"
    }
  },
  "required": ["location"]
}
```

**Example User Input:**
```
What's the weather like in San Francisco today?
```

**Expected Tool Call Output (JSON):**

I'll get the weather for San Francisco today in Fahrenheit.

<GetWeather>
```json
{
  "location": "San Francisco, CA",
  "unit": "fahrenheit"
}
```
</GetWeather>

**Explanation:**
The language model interprets the user's natural language query and extracts the relevant information into a structured JSON format based on the provided schema. The middleware then processes this JSON to invoke the appropriate tool or API to fetch the weather data for San Francisco in Fahrenheit.

### Restaurant Booking Tool

ToolName: `BookRestaurant`

**Schema Definition:**
```json
{
  "type": "object",
  "properties": {
    "restaurantName": {
      "type": "string",
      "description": "Name of the restaurant"
    },
    "date": {
      "type": "string",
      "description": "Date of booking in YYYY-MM-DD format"
    },
    "time": {
      "type": "string",
      "description": "Time of booking in HH:MM format"
    },
    "numberOfPeople": {
      "type": "integer",
      "description": "Number of people for the reservation"
    }
  },
  "required": ["restaurantName", "date", "time", "numberOfPeople"]
}
```

**Example User Input:**
```
Book a table at Chez Paul for 4 people on 2025-05-15 at 7 PM.
```

**Expected Tool Call Output (JSON):**

I'll book a restaurant reservation for Chez Paul for 4 people on 2025-05-15 at 7 PM.

<BookRestaurant>
```json
{
  "restaurantName": "Chez Paul",
  "date": "2025-05-15",
  "time": "19:00",
  "numberOfPeople": 4
}
```
</BookRestaurant>

**Explanation:**
The model parses the user's request and fills in the structured JSON according to the schema. This JSON data is then used by the middleware to call the appropriate booking tool or API with the exact parameters needed for the reservation.

These examples demonstrate how natural language input from users can be converted into structured data that tools can use, leveraging JSON schemas to define the expected structure of tool call data. This approach allows for flexible and powerful tool interactions driven by natural language.

---

## Special Cases & Fallback

- **LLM never emits JSON**: use a small LLM (`fallbackParser`) to "rewrite" its reply purely as a function call JSON.  
- **Invalid schema**: attempt 1x fallback‐rewrite or error.  
- **Streaming split**: accumulate buffer until you see closing `</tool_name>` tag or closing `}` for JSON content.

---

## Markdown Schema for Function Contracts

The Markdown representation of function contracts follows a structured format as implemented in `FunctionContractExtensions.cs`:
- **Heading**: Uses the function name as an H2 heading (e.g., `## functionName`)
- **Description**: Includes a brief description if provided (e.g., `Description: This function does...`)
- **Parameters**: Lists parameters with their name, required/optional status, and description. Complex schemas are included as indented JSON code blocks.
- **Returns**: Specifies return type and description if available.
- **Example**: Provides an example usage with the function name as a tag (e.g., `<functionName>`) containing a JSON code block with sample parameter values.

This schema ensures that function contracts are clearly documented in the system prompt for the LLM to understand and use for inline tool calls.

---

## Testing

- **Unit**:  
  - Valid single call  
  - Multiple calls in one text  
  - Streaming boundary (JSON split across tokens)  
  - Invalid JSON → fallback invoked  
  - No calls → passthrough
- **Integration**: round-trip through `FunctionCallMiddleware` to actual tool stub

---

## Usage

```csharp
// Create function map for tool invocation
var functionMap = new Dictionary<string, Func<string, Task<string>>>
{
    { "tool_name", async (args) => await InvokeToolAsync(args) }
};

// Create the natural tool use middleware
var natural = new NaturalToolUseMiddleware(
    contracts,
    functionMap,
    "NaturalToolUse");

// Wire into your agent pipeline
var agent = new YourAgent();
var result = await natural.InvokeAsync(context, agent, cancellationToken);

// For streaming
var streamingAgent = new YourStreamingAgent();
var streamingResult = await natural.InvokeStreamingAsync(context, streamingAgent, cancellationToken);