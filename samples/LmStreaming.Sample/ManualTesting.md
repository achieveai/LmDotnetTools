# Manual Testing Guide

Manual testing uses browser tools (Chrome DevTools, Playwright) to validate UI behavior.
Use sub-agent / sub-task tools for individual test steps when automating.

## Prerequisites

Start the app:
```bash
# Default test provider (mock OpenAI format)
dotnet run --project samples/LmStreaming.Sample

# Specify provider mode
LM_PROVIDER_MODE=test dotnet run --project samples/LmStreaming.Sample
LM_PROVIDER_MODE=test-anthropic dotnet run --project samples/LmStreaming.Sample
LM_PROVIDER_MODE=codex dotnet run --project samples/LmStreaming.Sample
```

Provider modes:
| Mode | Description | Server Tools |
|------|-------------|--------------|
| `test` | Mock OpenAI SSE streaming | None |
| `test-anthropic` | Mock Anthropic SSE with server tools | web_search |
| `openai` | Real OpenAI API (gpt-4o) | None |
| `anthropic` | Real Anthropic API (claude-sonnet) | web_search |
| `codex` | OpenAI Codex SDK (multi-turn via Node bridge) | MCP `sample_tools` (calculate, get_weather) |

Chat modes (switch via dropdown, top-right):
| Mode | Enabled Tools |
|------|---------------|
| General Assistant | all (calculate, get_weather, web_search) |
| Math Helper | calculate only |
| Weather Assistant | get_weather only |
| Research Assistant | calculate, get_weather, web_search |

Prompts reference `PromptExamples.txt` in this directory. Test provider prompts use the
`<|instruction_start|>...<|instruction_end|>` format parsed by `InstructionChainParser`.

---

## 1. Basic Messaging

### 1.1 Send simple message

- **Provider:** `test` (or any)
- **Prompt:** Type `Hi` in the chat input. Press Enter or click Send.
- **Validation (test-provider):** A reply appears in the assistant bubble (left-aligned, gray background). User message "Hi" appears right-aligned in blue.
- **Validation (real provider):** An LLM response appears.

### 1.2 Send a reply message (scroll behavior)

- **Provider:** `test` (or any)
- **Prompt:** After 1.1, send `Hi` again.
- **Validation (test-provider):** The new user message smooth-scrolls to the top of the view (150ms animation). Previous messages scroll up. Same echo response appears.
- **Validation (real provider):** Same scroll behavior, different response content.

### 1.3 Chat input behavior

- **Provider:** any
- **Steps:**
  1. Type a message, press **Shift+Enter** — a newline is inserted (not sent).
  2. Press **Enter** (without Shift) — the message is sent.
  3. While a response is streaming, observe the input and Send button are disabled (grayed out).
- **Validation:** Shift+Enter = newline. Enter = submit. Input disabled during streaming.

### 1.4 Empty state

- **Provider:** any
- **Steps:** Open a fresh chat (or click Clear).
- **Validation:** "No messages yet. Send a message to start the conversation." appears centered.

---

## 2. Message Display & Styling

### 2.1 User message alignment

- **Provider:** any
- **Prompt:** Send `Hello world`.
- **Validation:** User message appears right-aligned. Blue avatar with person emoji to the right.

### 2.2 Assistant message alignment

- **Provider:** `test`
- **Prompt:** Send `Hello`.
- **Validation:** Assistant message appears left-aligned. Gray avatar with robot emoji to the left.

### 2.3 Message grouping

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Reasoning-Only Messages" > "Short reasoning followed by text"
- **Validation:** The reasoning pill and text message share a single robot avatar. Items stack vertically under the same avatar.

### 2.4 Markdown rendering

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Long Text Responses" > "Long text response (200 words)"
- **Validation:** Text renders with paragraph formatting. Code blocks (if generated) have gray background.

### 2.5 Streaming cursor

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Long Text Responses" > "Long text response (200 words)"
- **Validation:** A blinking blue cursor `|` appears at the end of text during streaming. Disappears when streaming completes.

---

## 3. Tool Call Pills

### 3.1 Web search function tool pill

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Web Search Tool" > "Search for a topic"
- **Validation:** A pill appears with magnifying glass icon (web_search is detected as a search tool). Shows function name "web_search" and argument summary. Spinner animation while loading. Expand arrow appears after result arrives. Expanding shows the query argument and search result JSON.

### 3.2 Calculator tool pill

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Calculate Tool" > "Basic addition"
- **Validation:** Calculator pill renders. Shows "10 + 5 = ..." while loading, then result when complete.

### 3.3 Weather tool pill

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Weather Tool" > "Get weather for a city"
- **Validation:**
  1. Shows location "Seattle" immediately from args, with "Loading..." text.
  2. Pulsing animation on weather icon while loading.
  3. After result: weather emoji changes based on condition, formatted temperature shown, forecast text shown.
  4. Sky-blue background.

### 3.4 Weather emoji conditions

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Weather Emoji Conditions" — send repeatedly (5-10 times)
- **Validation:** Over multiple sends, observe different weather emojis matching the random conditions:
  - Sunny/Clear → sun emoji
  - Cloudy → cloud emoji
  - Partly Cloudy → sun-behind-cloud emoji
  - Rainy → rain emoji

Note: Snow, Thunder, Fog emojis are not reachable via mock tools (not in the random pool).

### 3.5 Multiple tools in one message

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Multiple Tool Calls in One Message" > "Calculator then weather"
- **Validation:** A single pill item labeled "Tools: 2 calls" with wrench icon. Expanding shows both tool results with their arguments and JSON responses.

### 3.6 All three tool types as separate pills

- **Provider:** `test` (must be in General Assistant mode for web_search access)
- **Prompt:** PromptExamples.txt > "Pill Overflow" > "All three tool types in one turn"
- **Validation:** Three separate pill items appear, each with a different icon:
  1. Calculator pill (calculator icon or wrench)
  2. Weather pill (weather emoji, sky-blue)
  3. Web search pill (magnifying glass icon)
- Each pill shows its own arguments and results when expanded.

### 3.7 Tool call expand/collapse

- **Provider:** `test`
- **Prompt:** Any single tool call prompt (e.g., "Basic addition")
- **Steps:** After the tool call completes, click on the pill item row.
- **Validation:** Clicking toggles expansion. Expanded view shows "Arguments:" with formatted JSON and "Result:" with the tool result JSON. Arrow icon toggles between right-arrow and down-arrow.

---

## 4. MetadataPill Overflow (>3 Items)

The MetadataPill shows a "Show all N items" header when `items.length > 3`.

### 4.1 Exactly 3 items — no overflow

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Reasoning + Tool Calls" > "3 pill items — reasoning + 2 tool calls"
- **Validation:** No "Show all" header appears. All 3 items visible: 1 thinking pill + 2 tool call pills.

### 4.2 4+ items — overflow header appears

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Pill Overflow" > "4 pill items"
- **Validation:** "Show all 4 items" header appears with a right-arrow icon. The pill container has max-height ~150px with scrollbar.

### 4.3 Expand/collapse overflow (includes web_search)

- **Provider:** `test` (General Assistant mode for web_search access)
- **Prompt:** PromptExamples.txt > "Pill Overflow" > "5 pill items" (includes reasoning + calculate + get_weather + web_search + get_weather)
- **Steps:** Click the "Show all 5 items" header.
- **Validation:** Text changes to "Collapse". All 5 items visible — thinking pill, calculator pill, weather pill, web search pill (magnifying glass), and another weather pill. Clicking again collapses back to scrollable view.

---

## 5. Reasoning/Thinking Pills

### 5.1 Reasoning-only message

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Reasoning-Only Messages" > "Long reasoning (100 words)"
- **Validation:** A thinking pill appears with brain emoji. Label shows "Thinking:" followed by truncated preview (60 chars). Text message follows below the pill.

### 5.2 Reasoning expand to full text

- **Provider:** `test`
- **Prompt:** Same as 5.1
- **Steps:** Click on the thinking pill item.
- **Validation:** Full reasoning text appears in expanded content area with pre-formatted text.

### 5.3 Reasoning + tool calls in same MetadataPill

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Reasoning + Tool Calls" > "3 pill items"
- **Validation:** A single MetadataPill contains both the thinking item (brain emoji) and tool call items (wrench/weather emojis) as separate expandable rows.

---

## 6. Auto-Scroll Behavior

### 6.1 New user message scrolls to top

- **Provider:** `test`
- **Steps:** Send 3-4 simple messages to build up history. Then send another message.
- **Validation:** The new user message smooth-scrolls to the top of the visible area. Previous messages scroll above the fold.

### 6.2 Long response extends below viewport

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Long Text Responses" > "Long text response (200 words)"
- **Validation:** User message stays at top. Assistant response renders below and may extend beyond the viewport. User can manually scroll to see the full response.

---

## 7. Mode Selector & Tool Filtering

### 7.1 Mode dropdown display

- **Provider:** any
- **Steps:** Click the "Mode:" button in the header.
- **Validation:** Dropdown shows "System" section with General Assistant, Math Helper, Weather Assistant, Research Assistant. Checkmark next to current mode. "Manage Modes..." link at bottom.

### 7.2 Switch mode

- **Provider:** `test`
- **Steps:** Open mode dropdown. Click "Math Helper".
- **Prompt:** Then send PromptExamples.txt > "Echo System Prompt"
- **Validation:** Mode button shows "Mode: Math Helper". Echo response contains "You are a math assistant".

### 7.3 Allowed tool succeeds

- **Provider:** `test`
- **Steps:** Switch to "Math Helper" mode.
- **Prompt:** PromptExamples.txt > "Math Helper Mode" > "Should SUCCEED"
- **Validation:** Calculator tool executes and returns a result.

### 7.4 Blocked tool returns error

- **Provider:** `test`
- **Steps:** Stay in "Math Helper" mode.
- **Prompt:** PromptExamples.txt > "Math Helper Mode" > "Should FAIL - get_weather"
- **Validation:** Tool result shows error: `{"error":"Unknown function: get_weather","available_functions":[...]}`. The LLM responds with text in a follow-up turn (no more tool calls).

### 7.5 Tools list per mode

- **Provider:** `test`
- **Steps:** Switch to each mode and send the tools list prompt.
- **Prompt:** PromptExamples.txt > "List Available Tools"
- **Validation:**
  - General Assistant: calculate, get_weather, web_search
  - Math Helper: calculate
  - Weather Assistant: get_weather
  - Research Assistant: calculate, get_weather, web_search

### 7.6 Dropdown closes on outside click

- **Steps:** Open mode dropdown. Click somewhere outside it.
- **Validation:** Dropdown closes.

### 7.7 Dropdown closes on Escape

- **Steps:** Open mode dropdown. Press Escape.
- **Validation:** Dropdown closes.

---

## 8. Mode Management Modal

### 8.1 Open modal

- **Steps:** Open mode dropdown. Click "Manage Modes...".
- **Validation:** Modal opens with backdrop overlay. Shows "System Modes" (with Copy button only) and "Your Modes" (with Edit, Copy, Delete buttons or "No custom modes yet").

### 8.2 Create a new mode

- **Steps:** Click "Create New Mode". Fill in name, description, system prompt. Select tools. Click Save.
- **Validation:** New mode appears under "Your Modes" in the modal list and in the dropdown selector.

### 8.3 Copy a system mode

- **Steps:** Click "Copy" next to "Math Helper". Enter a new name. Click Copy.
- **Validation:** New mode appears under "Your Modes" with the same settings as Math Helper.

### 8.4 Edit a user mode

- **Steps:** Create or copy a mode. Click "Edit". Change name or tools. Click Save.
- **Validation:** Updated name appears in the list and dropdown.

### 8.5 Delete a user mode

- **Steps:** Click "Delete" on a user mode.
- **Validation:** Confirmation dialog appears. Clicking Delete removes the mode. Clicking Cancel cancels.

### 8.6 Close modal

- **Steps:** Click the dark backdrop area outside the modal.
- **Validation:** Modal closes.

---

## 9. Conversation Sidebar

### 9.1 New chat

- **Steps:** Click "+ New Chat" in the sidebar.
- **Validation:** Chat area clears. A new conversation thread is created.

### 9.2 Conversation appears after first message

- **Provider:** `test`
- **Prompt:** Send `Hello world` in a new chat.
- **Validation:** A new entry appears in the sidebar with the message text as title and a timestamp.

### 9.3 Switch between conversations

- **Provider:** `test`
- **Steps:** Create 2 conversations with different messages. Click between them.
- **Validation:** Active conversation has blue highlight and left blue border. Switching loads the correct message history.

### 9.4 Delete conversation

- **Steps:** Hover over a conversation item. Click the X button.
- **Validation:** Delete button appears on hover (red X). Confirmation dialog. Conversation removed after confirming.

### 9.5 Collapse/expand sidebar

- **Steps:** Click the toggle button in the sidebar header.
- **Validation:** Sidebar collapses to narrow width. "+ New Chat" button fades out. Click toggle again to expand.

---

## 10. Usage & Error Banners

### 10.1 Token usage banner

- **Provider:** `test`
- **Prompt:** Send any message and wait for response to complete.
- **Validation:** Green banner at bottom: "Tokens: XX in / YY out".

### 10.2 Error banner

- **Steps:** Trigger an error (e.g., network disconnect or malformed request).
- **Validation:** Red banner appears with error message text.

---

## 11. Server-Side Tools (test-anthropic provider)

These tests require `LM_PROVIDER_MODE=test-anthropic` and "Research Assistant" mode.

Note: `web_search` exists in two forms:
- **Function tool** (`test` provider): Registered as a user-defined function, returns mock JSON. Rendered with magnifying glass pill.
- **Server tool** (`test-anthropic` provider): Anthropic built-in tool with `server_tool_use`/`server_tool_result` blocks. Rendered with magnifying glass pill + citations/sources section.

Section 3.1 tests function-tool web_search. This section tests server-tool web_search.

### 11.1 Web search pill

- **Provider:** `test-anthropic`
- **Prompt:** PromptExamples.txt > "Server-Side Tool Examples" > "Web Search with Citations (single result)"
- **Validation:** A MetadataPill item appears with magnifying glass emoji. Shows "web_search" label.

### 11.2 Citations/sources display

- **Provider:** `test-anthropic`
- **Prompt:** Same as 11.1
- **Validation:** After the search pill, text message appears. A "Sources:" section with bulleted, clickable citation links at the bottom.

### 11.3 Multiple citations

- **Provider:** `test-anthropic`
- **Prompt:** PromptExamples.txt > "Server-Side Tool Examples" > "Web Search with Multiple Citations"
- **Validation:** Multiple sources in the Sources list. Each link points to a different URL.

### 11.4 Multi-turn mixed function + server tools

- **Provider:** `test-anthropic`
- **Prompt:** PromptExamples.txt > "Server-Side Tool Examples" > "Multi-Turn: Mixed Function Tools + Server Tools"
- **Validation:**
  - Step 1: Calculator and weather pills + web_search server tool pill
  - Step 2: Another web_search pill + calculator pill
  - Step 3: Text message with citations and Sources section
  - Token usage banner updates after each turn

---

## 12. Multi-Turn Conversations

### 12.1 Multi-turn with reasoning

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Multiturn Example (multiple turns with reasoning)"
- **Validation:**
  - Turn 1: Reasoning pill + 2 parallel weather calls (1 tool pill with "Tools: 2 calls")
  - Turn 2: Reasoning pill + 1 weather call + text message
  - Turn 3: Short reasoning + long text summary
  - Each turn's messages appear sequentially. Auto-scroll works on each new user message.

### 12.2 Multi-turn with long text

- **Provider:** `test`
- **Prompt:** PromptExamples.txt > "Long Text Responses" > "Multi-turn: tool call then long text summary"
- **Validation:**
  - Turn 1: Weather tool call pill, user sends follow-up
  - Turn 2: Long text response (300 words) with streaming cursor visible

---

## 13. Clear Functionality

### 13.1 Clear messages

- **Provider:** any
- **Steps:** Send a few messages. Click the "Clear" button (top-right, red).
- **Validation:** All messages removed. Empty state message appears. Usage banner disappears.

---

## 14. Codex Raw Streaming Verification

These checks require `LM_PROVIDER_MODE=codex`.

### 14.1 Expected UI behavior in raw mode

- **Setup:** Ensure `CODEX_EMIT_SYNTHETIC_MESSAGE_UPDATES=false`.
- **Prompt:** Ask for a longer response (e.g., a 6-8 paragraph explanation).
- **Validation:** If Codex emits bursty snapshots, text may appear mostly at once near turn completion. This is expected in raw provider mode.

### 14.2 Verify event timing with structured logs

- **Goal:** Confirm whether `text_update` events are truly incremental or arrive in a late burst.
- **Command:**
  ```bash
  duckdb -c "
  SELECT
    \"@t\" AS ts,
    event_type,
    event_status,
    provider_mode,
    thread_id,
    run_id,
    generation_id,
    bridge_request_id,
    bridge_event_type,
    event_sequence,
    latency_ms
  FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
  WHERE provider_mode = 'codex'
    AND event_type IN ('codex.bridge.event.received', 'codex.text_update.published', 'codex.text.published')
  ORDER BY ts, event_sequence;"
  ```
- **Validation:**
  - `codex.bridge.event.received` appears for each provider event.
  - `codex.text_update.published` appears only when upstream sends update events.
  - If updates are bursty, most `codex.text_update.published` rows cluster close to final `codex.text.published`.

### 14.3 Per-run burst check

- **Command:**
  ```bash
  duckdb -c "
  WITH stream AS (
    SELECT
      run_id,
      event_type,
      latency_ms
    FROM read_json_auto('samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
    WHERE provider_mode = 'codex'
      AND event_type IN ('codex.text_update.published', 'codex.text.published')
  )
  SELECT
    run_id,
    MIN(CASE WHEN event_type = 'codex.text_update.published' THEN latency_ms END) AS first_update_ms,
    MAX(CASE WHEN event_type = 'codex.text_update.published' THEN latency_ms END) AS last_update_ms,
    MAX(CASE WHEN event_type = 'codex.text.published' THEN latency_ms END) AS final_text_ms
  FROM stream
  GROUP BY run_id
  ORDER BY final_text_ms DESC;"
  ```
- **Validation:** `first_update_ms` and `last_update_ms` close to `final_text_ms` indicates bursty provider timing.

---

## Provider Coverage Matrix

| Test Area | `test` | `test-anthropic` | `openai` | `anthropic` |
|---|:---:|:---:|:---:|:---:|
| Basic messaging | Primary | Also test | Optional | Optional |
| Tool call pills | Primary | — | Optional | Optional |
| MetadataPill overflow | Primary | — | — | — |
| Reasoning pills | Primary | — | Optional | Optional |
| Mode selector/filtering | Primary | Also test | Optional | Optional |
| Server-side tools | — | Primary | — | Optional |
| Citations/sources | — | Primary | — | Optional |
| Sidebar, banners, UI | Primary | — | — | — |
