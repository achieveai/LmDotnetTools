# Sample Test Prompts

These prompts can be pasted into the chat to test tool calls and mode switching
using the mock LLM (TestSseMessageHandler).

## Format

The mock instruction format is:
<|instruction_start|>{"instruction_chain":[...]}<|instruction_end|>

---

## Tool Test Prompts

### Calculate Tool

Basic addition:
<|instruction_start|>{"instruction_chain":[{"id":"calc-add","id_message":"Adding numbers","messages":[{"tool_call":[{"name":"calculate","args":{"a":10,"operation":"add","b":5}}]}]}]}<|instruction_end|>

Multiplication:
<|instruction_start|>{"instruction_chain":[{"id":"calc-mul","id_message":"Multiplying","messages":[{"tool_call":[{"name":"calculate","args":{"a":7,"operation":"multiply","b":6}}]}]}]}<|instruction_end|>

Division:
<|instruction_start|>{"instruction_chain":[{"id":"calc-div","id_message":"Dividing","messages":[{"tool_call":[{"name":"calculate","args":{"a":100,"operation":"divide","b":4}}]}]}]}<|instruction_end|>

### Weather Tool

Get weather for a city:
<|instruction_start|>{"instruction_chain":[{"id":"weather-city","id_message":"Checking weather","messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}}]}]}]}<|instruction_end|>

### Web Search Tool

Search for a topic:
<|instruction_start|>{"instruction_chain":[{"id":"search-topic","id_message":"Searching","messages":[{"tool_call":[{"name":"web_search","args":{"query":"machine learning","numResults":3}}]}]}]}<|instruction_end|>

### Multiple Tool Calls in One Message

Calculator then weather:
<|instruction_start|>{"instruction_chain":[{"id":"multi-tools","id_message":"Using multiple tools","messages":[{"tool_call":[{"name":"calculate","args":{"a":32,"operation":"multiply","b":1.8}},{"name":"get_weather","args":{"location":"Tokyo"}}]}]}]}<|instruction_end|>

### Pill Overflow (>3 Items in MetadataPill)

MetadataPill shows "Show all N items" when there are more than 3 pill items.
Each reasoning block = 1 item. Each tool_call message entry = 1 item.
A single tool_call array with multiple tools = 1 item (labeled "Tools: N calls").

4 pill items (reasoning + 3 separate tool_call entries — triggers "Show all 4 items"):
<|instruction_start|>{"instruction_chain":[{"id":"pill-overflow-4","reasoning":{"length":30},"id_message":"Four pill items","messages":[{"tool_call":[{"name":"calculate","args":{"a":10,"operation":"add","b":5}}]},{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}}]},{"tool_call":[{"name":"calculate","args":{"a":7,"operation":"multiply","b":6}}]}]}]}<|instruction_end|>

5 pill items (reasoning + 4 tool_call entries including web_search — triggers "Show all 5 items"):
<|instruction_start|>{"instruction_chain":[{"id":"pill-overflow-5","reasoning":{"length":40},"id_message":"Five pill items","messages":[{"tool_call":[{"name":"calculate","args":{"a":100,"operation":"divide","b":4}}]},{"tool_call":[{"name":"get_weather","args":{"location":"London"}}]},{"tool_call":[{"name":"web_search","args":{"query":"temperature conversion","numResults":2}}]},{"tool_call":[{"name":"get_weather","args":{"location":"Paris"}}]}]}]}<|instruction_end|>

All three tool types in one turn (calculate + get_weather + web_search as separate pills):
<|instruction_start|>{"instruction_chain":[{"id":"all-three-tools","id_message":"All tool types","messages":[{"tool_call":[{"name":"calculate","args":{"a":42,"operation":"multiply","b":2}}]},{"tool_call":[{"name":"get_weather","args":{"location":"Chicago"}}]},{"tool_call":[{"name":"web_search","args":{"query":"best deep dish pizza","numResults":3}}]}]}]}<|instruction_end|>

### Reasoning-Only Messages

Tests the ThinkingPill (brain emoji, truncated preview, expand/collapse) without tool calls.

Short reasoning (20 words) followed by text:
<|instruction_start|>{"instruction_chain":[{"id":"reason-short","reasoning":{"length":20},"id_message":"Short thinking","messages":[{"text_message":{"length":30}}]}]}<|instruction_end|>

Long reasoning (100 words) followed by text:
<|instruction_start|>{"instruction_chain":[{"id":"reason-long","reasoning":{"length":100},"id_message":"Long thinking","messages":[{"text_message":{"length":50}}]}]}<|instruction_end|>

### Reasoning + Tool Calls (Mixed MetadataPill)

Tests reasoning and tool calls appearing as separate expandable rows in the same pill.

3 pill items — reasoning + 2 tool calls (no overflow):
<|instruction_start|>{"instruction_chain":[{"id":"mixed-3","reasoning":{"length":40},"id_message":"Mixed pill 3 items","messages":[{"tool_call":[{"name":"calculate","args":{"a":15,"operation":"add","b":25}}]},{"tool_call":[{"name":"get_weather","args":{"location":"Berlin"}}]}]}]}<|instruction_end|>

5 pill items — reasoning + 4 tool calls including web_search (with overflow):
<|instruction_start|>{"instruction_chain":[{"id":"mixed-5","reasoning":{"length":50},"id_message":"Mixed pill 5 items","messages":[{"tool_call":[{"name":"calculate","args":{"a":1,"operation":"add","b":1}}]},{"tool_call":[{"name":"web_search","args":{"query":"NYC weather history","numResults":2}}]},{"tool_call":[{"name":"get_weather","args":{"location":"NYC"}}]},{"tool_call":[{"name":"get_weather","args":{"location":"LA"}}]}]}]}<|instruction_end|>

### Long Text Responses

Tests streaming cursor, markdown rendering, and scroll behavior with large responses.

Long text response (200 words):
<|instruction_start|>{"instruction_chain":[{"id":"long-text","id_message":"Long response","messages":[{"text_message":{"length":200}}]}]}<|instruction_end|>

Multi-turn: tool call then long text summary (300 words):
<|instruction_start|>{"instruction_chain":[{"id":"long-turn1","id_message":"Gathering data","messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Tokyo"}}]}]},{"id":"long-turn2","id_message":"Long summary","messages":[{"text_message":{"length":300}}]}]}<|instruction_end|>

### Weather Emoji Conditions

The mock SampleTools.GetWeather returns random conditions from:
["Sunny", "Cloudy", "Partly Cloudy", "Rainy", "Clear"]

WeatherToolPill maps each to an emoji. Send this prompt repeatedly to see different conditions:
<|instruction_start|>{"instruction_chain":[{"id":"weather-emoji","id_message":"Weather check","messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Denver"}}]}]}]}<|instruction_end|>

### Multiturn Example (multiple turns with reasoning)

<|instruction_start|>{"instruction_chain":[{"id":"turn1_parallel_tools","reasoning":{"length":30},"messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}},{"name":"get_weather","args":{"location":"San Francisco"}}]}]},{"id":"turn2_final","reasoning":{"length":50},"messages":[{"tool_call":[{"name":"get_weather","args":{"location":"Seattle"}}]},{"text_message":{"length":50}}]},{"id":"turn3_summary","reasoning":{"length":10},"messages":[{"text_message":{"length":100}}]}]}<|instruction_end|>

---

## Sub-Agent Delegation (Nested Instruction Chains)

Drive the full parent → `Agent` tool → sub-agent flow from a SINGLE pasted message. The parent's
chain calls the `Agent` tool, and the `prompt` argument is itself a complete instruction chain that
drives the sub-agent. The sub-agent here calls `calculate`, then replies `hi from agent` — which
comes back as the `Agent` tool result.

Requires `test` or `test-anthropic` mode and a chat mode where `calculate` and the `Agent` tool are
available (e.g. **General Assistant** — the sub-agent inherits the parent's tools). Built-in
sub-agent types: `general-purpose`, `researcher`.

**Escaping rule (important):** the inner chain's `<|instruction_start|>` / `<|instruction_end|>`
tags stay **literal**; only the inner JSON **quotes** are escaped as `\"`. The parser matches tags
by depth, so the inner `<|instruction_end|>` no longer truncates the outer chain.

Parent delegates → sub-agent uses one tool then replies "hi from agent" → parent wraps up:
<|instruction_start|>{"instruction_chain":[{"id":"parent","id_message":"Delegate to sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"sub-tool\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":2,\"operation\":\"add\",\"b\":3}}]}]},{\"id\":\"sub-text\",\"messages\":[{\"text\":\"hi from agent\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent2","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent finished."}]}]}<|instruction_end|>

Expected UI:
1. An `Agent` tool-call pill. Expand it — **Arguments** shows the nested chain, **Result** shows
   `hi from agent` (the sub-agent's reply, returned synchronously as the tool result).
2. Final assistant text: `Parent done: sub-agent finished.`

The sub-agent's own `calculate` call runs inside the sub-agent and is not streamed to the parent
chat; the `hi from agent` result is the proof it executed the nested chain end to end.

Nested-tag handling lives in `src/LmTestUtils/TestMode/InstructionChainParser.cs`.

---

## Mode Filtering Test Prompts

Use these to verify that modes correctly restrict available tools.
Note: You can switch modes mid-conversation using the dropdown at the top right.
The new mode's tools and system prompt will apply to the very next message.

### Math Helper Mode (only `calculate` allowed)

Should SUCCEED - calculate is allowed:
<|instruction_start|>{"instruction_chain":[{"id":"math-ok","id_message":"Math mode calc test","messages":[{"tool_call":[{"name":"calculate","args":{"a":25,"operation":"add","b":17}}]}]}]}<|instruction_end|>

Should FAIL - get_weather is NOT allowed in Math Helper:
<|instruction_start|>{"instruction_chain":[{"id":"math-blocked","id_message":"Math mode weather test","messages":[{"tool_call":[{"name":"get_weather","args":{"location":"NYC"}}]}]}]}<|instruction_end|>

Should FAIL - web_search is NOT allowed in Math Helper:
<|instruction_start|>{"instruction_chain":[{"id":"math-blocked2","id_message":"Math mode search test","messages":[{"tool_call":[{"name":"web_search","args":{"query":"test"}}]}]}]}<|instruction_end|>

### Weather Assistant Mode (only `get_weather` allowed)

Should SUCCEED - get_weather is allowed:
<|instruction_start|>{"instruction_chain":[{"id":"weather-ok","id_message":"Weather mode weather test","messages":[{"tool_call":[{"name":"get_weather","args":{"location":"London"}}]}]}]}<|instruction_end|>

Should FAIL - calculate is NOT allowed in Weather Assistant:
<|instruction_start|>{"instruction_chain":[{"id":"weather-blocked","id_message":"Weather mode calc test","messages":[{"tool_call":[{"name":"calculate","args":{"a":5,"operation":"add","b":3}}]}]}]}<|instruction_end|>

### General Assistant Mode (all tools allowed)

All tools should SUCCEED:
<|instruction_start|>{"instruction_chain":[{"id":"general-all","id_message":"General mode all tools","messages":[{"tool_call":[{"name":"calculate","args":{"a":10,"operation":"multiply","b":3}},{"name":"get_weather","args":{"location":"Paris"}},{"name":"web_search","args":{"query":"hello world"}}]}]}]}<|instruction_end|>

---

## System Prompt Verification

Use these prompts to verify that the correct system prompt is being used for each mode.
The `system_prompt_echo` instruction returns the current system prompt as text.

### Echo System Prompt (works in any mode)

Returns the full system prompt for the current mode:
<|instruction_start|>{"instruction_chain":[{"id":"sys-prompt","id_message":"Echoing system prompt","messages":[{"system_prompt_echo":{}}]}]}<|instruction_end|>

### System Prompt with Explanation

Echo system prompt followed by a text message:
<|instruction_start|>{"instruction_chain":[{"id":"sys-prompt-explain","id_message":"System prompt check","messages":[{"system_prompt_echo":{}},{"text_message":{"length":50}}]}]}<|instruction_end|>

The system prompts per mode are:
- General Assistant: "You are a helpful assistant with access to weather, calculator, and web search tools..."
- Math Helper: "You are a math assistant. Help users with calculations..."
- Weather Assistant: "You are a weather assistant. Help users get weather information..."

---

## Tool Call List Verification

Use these prompts to verify which tools are available in the current mode.
The `tools_list` instruction returns the names of all visible tools.

### List Available Tools (works in any mode)

Returns the list of tools available in the current mode:
<|instruction_start|>{"instruction_chain":[{"id":"tools-list","id_message":"Listing available tools","messages":[{"tools_list":{}}]}]}<|instruction_end|>

### Tools List with Explanation

List tools followed by a text explanation:
<|instruction_start|>{"instruction_chain":[{"id":"tools-explain","id_message":"Checking tools","messages":[{"tools_list":{}},{"text_message":{"length":30}}]}]}<|instruction_end|>

### Combined: System Prompt + Tools List

Verify both system prompt and available tools together:
<|instruction_start|>{"instruction_chain":[{"id":"full-verification","id_message":"Full mode verification","messages":[{"system_prompt_echo":{}},{"tools_list":{}},{"text_message":{"length":20}}]}]}<|instruction_end|>

Expected tool lists per mode:
- General Assistant: calculate, get_weather, web_search
- Math Helper: calculate
- Weather Assistant: get_weather

---

## Tool Description & Parameter Verification

`tools_list` returns only tool *names*. To inspect what a tool actually does and what
arguments it takes, use `tools_echo` (names + descriptions) or `tool_schema` (a single
tool's description **and** full parameter schema).

### Echo All Tool Descriptions (`tools_echo`)

Returns the name AND description of every visible tool (no parameter schema):
<|instruction_start|>{"instruction_chain":[{"id":"tools-echo","id_message":"Echoing tool descriptions","messages":[{"tools_echo":{}}]}]}<|instruction_end|>

### Tool Schema — Description + Parameters (`tool_schema`)

Returns a single named tool's description and its parameter schema, rendered as Markdown
(`# name` / `## Description` / `## Schema` with an indented JSON block):
<|instruction_start|>{"instruction_chain":[{"id":"tool-schema-calc","id_message":"Calculate tool schema","messages":[{"tool_schema":{"name":"calculate"}}]}]}<|instruction_end|>

Weather tool schema:
<|instruction_start|>{"instruction_chain":[{"id":"tool-schema-weather","id_message":"Weather tool schema","messages":[{"tool_schema":{"name":"get_weather"}}]}]}<|instruction_end|>

Unknown tool name (returns a "not found" message):
<|instruction_start|>{"instruction_chain":[{"id":"tool-schema-missing","id_message":"Missing tool schema","messages":[{"tool_schema":{"name":"does_not_exist"}}]}]}<|instruction_end|>

Example output for `tool_schema` with `"name":"calculate"`:
```
# calculate

## Description

Perform basic arithmetic operations: add, subtract, multiply, divide

## Schema

```json
{
  "type": "object",
  "properties": {
    "a": { "type": "number", "description": "First number" },
    "operation": { "type": "string", "description": "Operation: 'add', 'subtract', 'multiply', or 'divide'" },
    "b": { "type": "number", "description": "Second number" }
  },
  "required": ["a", "operation", "b"]
}
```
```

Note: `tool_schema` and `tools_echo` resolve in the `test` (OpenAI) and `test-anthropic`
modes. The `codex` (OpenAI Responses) mock path supports `tools_list` and `system_prompt_echo`
but does **not** resolve `tool_schema` or `tools_echo`.

---

## Request Metadata Verification

Echo the outbound request details the mock handler received. Useful for confirming routing,
headers, and request parameters.

Request URL:
<|instruction_start|>{"instruction_chain":[{"id":"req-url","id_message":"Echo request URL","messages":[{"request_url_echo":{}}]}]}<|instruction_end|>

Request headers:
<|instruction_start|>{"instruction_chain":[{"id":"req-headers","id_message":"Echo request headers","messages":[{"request_headers_echo":{}}]}]}<|instruction_end|>

Selected request body params (omit `fields` to echo all):
<|instruction_start|>{"instruction_chain":[{"id":"req-params","id_message":"Echo request params","messages":[{"request_params_echo":{"fields":["model","max_tokens"]}}]}]}<|instruction_end|>

---

## Expected Behavior

When a tool is allowed in the current mode:
- The tool call executes and returns a result (weather data, calculation, etc.)

When a tool is blocked in the current mode:
- Server log: "No handler registered for function '{name}'. Available functions: [...]"
- Tool result: {"error":"Unknown function: {name}","available_functions":[...]}
- The LLM receives the error and responds with text (turn 2 has no tool calls)

---

## Server-Side Tool Examples (Anthropic Built-in Tools)

These require `test-anthropic` mode and the "Research Assistant" mode (or any mode with built-in tools).

### Web Search with Citations (single result)

<|instruction_start|>{"id":"test-server-tools","messages":[{"server_tool_use":{"name":"web_search","input":{"query":"quantum computing advances"}}},{"server_tool_result":{"name":"web_search","result":{"type":"web_search_result","search_results":[{"title":"Quantum Computing Breakthroughs 2026","url":"https://example.com/quantum","encrypted_content":"bW9jaw==","page_age":"3 days ago"}]}}},{"text_with_citations":{"text":"Recent advances in quantum computing include error correction breakthroughs and new qubit architectures.","citations":[{"type":"web_search_result_location","url":"https://example.com/quantum","title":"Quantum Computing Breakthroughs 2026","cited_text":"error correction breakthroughs and new qubit architectures"}]}}]}<|instruction_end|>

### Web Search with Multiple Citations

<|instruction_start|>{"id":"test-multi-citations","messages":[{"server_tool_use":{"name":"web_search","input":{"query":"AI trends 2026"}}},{"server_tool_result":{"name":"web_search","result":{"type":"web_search_result","search_results":[{"title":"AI Trends Report 2026","url":"https://example.com/ai-trends","encrypted_content":"bW9jaw==","page_age":"1 day ago"},{"title":"Machine Learning Advances","url":"https://example.org/ml","encrypted_content":"bW9jaw==","page_age":"5 days ago"}]}}},{"text_with_citations":{"text":"AI continues to evolve rapidly in 2026. Large language models have become more efficient and capable. New architectures are pushing the boundaries of what's possible.","citations":[{"type":"web_search_result_location","url":"https://example.com/ai-trends","title":"AI Trends Report 2026","cited_text":"Large language models have become more efficient"},{"type":"web_search_result_location","url":"https://example.org/ml","title":"Machine Learning Advances","cited_text":"New architectures are pushing the boundaries"}]}}]}<|instruction_end|>

### Multi-Turn: Mixed Function Tools + Server Tools (3 steps)

Step 1: calculate + get_weather (function tools) and web_search (server tool)
Step 2: web_search (server tool) and calculate (function tool)
Step 3: text with citations summarizing the search results

<|instruction_start|>{"instruction_chain":[{"id":"step1-calc-weather-search","id_message":"Step 1: Gathering data","messages":[{"tool_call":[{"name":"calculate","args":{"a":273.15,"operation":"add","b":100}},{"name":"get_weather","args":{"location":"Tokyo"}}]},{"server_tool_use":{"name":"web_search","input":{"query":"Tokyo climate data 2026"}}},{"server_tool_result":{"name":"web_search","result":{"type":"web_search_result","search_results":[{"title":"Tokyo Climate Report 2026","url":"https://example.com/tokyo-climate","encrypted_content":"bW9jaw==","page_age":"2 days ago"},{"title":"Japan Weather Patterns","url":"https://example.org/japan-weather","encrypted_content":"bW9jaw==","page_age":"1 week ago"}]}}}]},{"id":"step2-search-calc","id_message":"Step 2: Additional research","messages":[{"server_tool_use":{"name":"web_search","input":{"query":"Kelvin to Celsius conversion formula"}}},{"server_tool_result":{"name":"web_search","result":{"type":"web_search_result","search_results":[{"title":"Temperature Conversion Guide","url":"https://example.com/temp-conversion","encrypted_content":"bW9jaw==","page_age":"3 months ago"}]}}},{"tool_call":[{"name":"calculate","args":{"a":373.15,"operation":"add","b":-273.15}}]}]},{"id":"step3-summary","id_message":"Step 3: Summary with citations","messages":[{"text_with_citations":{"text":"Based on my research, Tokyo's current temperature is moderate. The calculation shows that 273.15 + 100 = 373.15 Kelvin, which converts to exactly 100 degrees Celsius. Tokyo's climate in 2026 continues to follow historical patterns with warm summers and mild winters.","citations":[{"type":"web_search_result_location","url":"https://example.com/tokyo-climate","title":"Tokyo Climate Report 2026","cited_text":"Tokyo's climate in 2026 continues to follow historical patterns"},{"type":"web_search_result_location","url":"https://example.com/temp-conversion","title":"Temperature Conversion Guide","cited_text":"373.15 Kelvin converts to exactly 100 degrees Celsius"}]}}]}]}<|instruction_end|>

### Expected UI Components for Server Tools
1. Web search pill - collapsible bar with magnifying glass icon showing "web_search: web_search"
2. Text with citations - response text rendered as a paragraph
3. Sources section - "Sources:" heading with bulleted list of clickable citation links
4. Token usage bar - "Tokens: 100 in / 50 out" at the bottom

---

## Normal Text Messages

Simple text message:
Some text message

Message with reasoning:
Some text message
Reason: Reason about something

---

## Reference

For instruction chain parser details, see:
src/LmTestUtils/TestMode/InstructionChainParser.cs

---

## Manual UI test prompts (conversation UX)

Prompts for the **manual/exploratory Playwright smoke checks** in
[`playwright-scripts/`](playwright-scripts/) (run one-shot via
`browser_run_code_unsafe({ filename: … })` — see CLAUDE.md "UI / browser testing"). These target the
conversation-UX features (provider switch, Queue button, conversation switching, streaming resume)
rather than a specific tool. Use two MOCK providers so there are no real LLM calls:
`test-anthropic` (streams text — the client keeps rendering, giving a **wide, reliable streaming
window**) and `claude-mock` (completes silently — proves a recreated agent runs a new turn).

### Long streamed reply (wide streaming window)

A ~300-word text reply keeps the composer in the streaming state for a few seconds — long enough to
observe "disabled while streaming", type a Queue follow-up, or attempt a mid-stream action. **Do not
use much larger lengths** (e.g. 2000): the client renders every word and the run can take >60s to go
idle, tripping wait timeouts.
<|instruction_start|>{"instruction_chain":[{"id":"long-text","id_message":"Long response","messages":[{"text_message":{"length":300}}]}]}<|instruction_end|>

### Short reply (fast completion)

For a quick turn (e.g. proving a switched-to provider runs a new turn):
<|instruction_start|>{"instruction_chain":[{"id":"short","id_message":"Short","messages":[{"text_message":{"length":20}}]}]}<|instruction_end|>

### Queue follow-up (plain text)

Any plain (non-instruction) text typed mid-stream is what the blue **Queue** button queues. The scripts
use a literal string, e.g. `a queued follow-up typed mid-stream`.

### What the scripts assert (deterministic, browser-observable)
- **provider-switch.mjs** — streaming ⇒ provider selector DISABLED + no `provider-locked-badge`; idle ⇒
  editable dropdown; switch when idle ⇒ `/api/conversations` shows the new `provider` + the label
  updates; a new turn runs on the recreated agent. (The 409-while-streaming / 503-unavailable HTTP
  codes are covered deterministically by `ConversationsControllerTests`, not the browser.)
- **queue-button.mjs** — streaming + empty box ⇒ red `stop-button`; streaming + typed text ⇒ blue
  `queue-button` replaces Stop; click Queue ⇒ box clears + message enters the `.pending-queue`
  "Waiting to send…" list + reverts to Stop.

> If a new manual scenario needs a new prompt, add it in this section and reference it from the script.

---

## Codex Mode Prompt Examples

These prompts are for `LM_PROVIDER_MODE=codex`.

### MCP Tool Usage

- "Use the calculate tool to multiply 19 by 27 and show only the numeric result."
- "Use get_weather for Seattle and summarize temperature, condition, and humidity in one sentence."

### Multi-Turn Coding Workflow

- "Create a short plan to add a new endpoint `/api/ping` to this sample app."
- "Now implement the plan and explain what files changed."
- "Run tests that are relevant and summarize any failures."

### Tool Restriction by Chat Mode

Switch to Math Helper mode, then ask:
- "Use get_weather for Tokyo."

Expected: the tool call should be rejected because the mode only enables `calculate`.
