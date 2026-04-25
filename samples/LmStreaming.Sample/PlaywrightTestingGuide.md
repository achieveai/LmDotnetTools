# Playwright Testing Guide for LmStreaming.Sample

This guide provides ready-to-use Playwright scripts for **manual exploratory testing** of the
LmStreaming Chat Client via the MCP Playwright tools in Claude Code.

> **Automated browser E2E tests** live in
> [`tests/LmStreaming.Sample.Browser.E2E.Tests/`](../../tests/LmStreaming.Sample.Browser.E2E.Tests/)
> and run the same chat client under headless Chromium + a scripted SSE backend (no real
> LLM calls). Those tests cover AC1-AC6 of issue #8 and are the authoritative regression
> suite; this guide remains useful for ad-hoc checks and when adding new scenarios.

## Prerequisites

- App running at `http://127.0.0.1:5050` (or adjust `BASE_URL`)
- Playwright MCP server configured in Claude Code

## UI Element Reference

### Layout
| Region | Selector | Description |
|--------|----------|-------------|
| Sidebar | `.conversation-sidebar` | Left panel with conversation list |
| Main area | `.chat-main` | Right panel with chat view |
| Header | `.chat-header` | Top bar with title, mode selector, clear button |
| Message list | `.message-list` | Scrollable message container |
| Input area | `.chat-input` | Bottom bar with textarea and send button |

### Key Elements
| Element | Playwright Selector | Alternative |
|---------|---------------------|-------------|
| Mode selector button | `.selector-btn` | `text=Mode:` |
| Mode dropdown | `.dropdown-menu` | |
| Mode item | `.menu-item` | `.menu-item:has-text("Mode Name")` |
| New Chat button | `.new-chat-btn` | `text=+ New Chat` |
| Clear button | `.clear-btn` | `text=Clear` |
| Sidebar toggle | `.toggle-btn` | |
| Send button | `.chat-input button` | `role=button[name="Send"]` |
| Message textarea | `.chat-input textarea` | `role=textbox[name="Type a message..."]` |
| User messages | `.user-message-wrapper` | |
| Assistant messages | `.assistant-message-wrapper` | |
| Thinking pills | `.pill-item:has-text("Thinking:")` | |
| Tool pills | `.pill-item:has(.item-label:not(:has-text("Thinking:")))` | |
| Conversation items | `.conversation-item` | |
| Delete conversation | `.conversation-item .delete-btn` | |
| Manage modes | `.manage-item` | `text=Manage Modes...` |

### Keyboard Shortcuts
| Key | Context | Action |
|-----|---------|--------|
| `Enter` | Textarea focused | Send message |
| `Shift+Enter` | Textarea focused | Newline (no send) |
| `Escape` | Dropdown open | Close dropdown |

---

## Scripts

### 1. Basic Navigation and Snapshot

```
Navigate to http://127.0.0.1:5050
Take a snapshot
```

**Verifies:** App loads, all UI elements render.

---

### 2. Switch Chat Mode

```
Navigate to http://127.0.0.1:5050
Click on the mode selector button (.selector-btn)
Take a snapshot                               # See all available modes
Click on the menu item "Medical Knowledge Assistant"
Take a snapshot                               # Mode switched, header updated
```

**Selectors:**
```
page.locator('.selector-btn').click()
page.locator('.menu-item').filter({ hasText: 'Medical Knowledge Assistant' }).click()
```

---

### 3. Send a Message and Wait for Response

```
Navigate to http://127.0.0.1:5050
Fill the textarea with "What is 2 + 2?"
Click the Send button
Wait for the assistant message wrapper to appear (.assistant-message-wrapper)
Wait for the text bubble to appear (.text-bubble)
Take a snapshot
```

**Selectors:**
```
page.locator('.chat-input textarea').fill('What is 2 + 2?')
page.locator('.chat-input button').click()
page.locator('.assistant-message-wrapper').waitFor()
page.locator('.text-bubble').waitFor()
```

**Tip:** For streaming responses, wait for `.text-bubble` rather than just `.assistant-message-wrapper` because the wrapper appears immediately but content streams in gradually.

---

### 4. Multi-Turn Conversation

```
Navigate to http://127.0.0.1:5050

# Turn 1
Fill the textarea with "Hello, what can you help me with?"
Click the Send button
Wait for .text-bubble to appear
Take a snapshot

# Turn 2
Fill the textarea with "Tell me a joke"
Click the Send button
Wait for a second .text-bubble to appear
Take a snapshot
```

**Verifies:** Multi-turn context is maintained across messages.

---

### 5. Medical Knowledge Mode with Tool Calls

This is the critical e2e test for thinking + tool calls.

```
Navigate to http://127.0.0.1:5050

# Switch to Medical Knowledge mode
Click .selector-btn
Click the menu item "Medical Knowledge Assistant"

# Ask a question that triggers tool calls
Fill the textarea with "What is hematosis? Find references in the books"
Click the Send button

# Wait for response (may take 30-60s with thinking + tool calls)
Wait for .text-bubble to appear (timeout: 120s)
Take a snapshot

# Verify thinking pills appeared
Check that .pill-item elements exist

# Turn 2 - This tests the thinking/reasoning preservation fix
Fill the textarea with "Can you explain more about the gas exchange process?"
Click the Send button
Wait for a new .text-bubble to appear (timeout: 120s)
Take a snapshot
```

**What to verify:**
- Turn 1: Thinking pills + tool call pills + text response
- Turn 2: Should succeed without errors (tests ReasoningMessage preservation)
- If turn 2 fails with an error, the thinking/reasoning fix is broken

---

### 6. Expand/Collapse Thinking and Tool Pills

```
Navigate to http://127.0.0.1:5050

# (After a response with thinking/tool pills exists)

# Expand a thinking pill
Click on the first .pill-item .item-header
Take a snapshot                               # Thinking content visible

# Collapse it
Click on the same .pill-item .item-header
Take a snapshot                               # Thinking content hidden

# Expand/collapse the pill group
Click on .pill-header (the group header)
Take a snapshot
```

**Selectors:**
```
page.locator('.pill-item .item-header').first().click()
page.locator('.pill-header').first().click()
```

---

### 7. Create New Conversation

```
Navigate to http://127.0.0.1:5050

# Send a message first
Fill the textarea with "Hello"
Click the Send button
Wait for .text-bubble

# Create new conversation
Click the .new-chat-btn button
Take a snapshot                               # Empty state, no messages

# Verify previous conversation appears in sidebar
Check that .conversation-item exists in sidebar
```

---

### 8. Switch Between Conversations

```
Navigate to http://127.0.0.1:5050

# Create first conversation
Fill the textarea with "First conversation"
Click the Send button
Wait for .text-bubble

# Create second conversation
Click .new-chat-btn
Fill the textarea with "Second conversation"
Click the Send button
Wait for .text-bubble

# Switch back to first conversation
Click the first .conversation-item in the sidebar
Take a snapshot                               # Should show "First conversation"

# Switch to second conversation
Click the second .conversation-item
Take a snapshot                               # Should show "Second conversation"
```

---

### 9. Delete a Conversation

```
Navigate to http://127.0.0.1:5050

# Create a conversation
Fill the textarea with "Conversation to delete"
Click the Send button
Wait for .text-bubble

# Delete it
Click the .delete-btn inside the .conversation-item
Take a snapshot                               # Conversation removed from sidebar
```

---

### 10. Clear Conversation

```
Navigate to http://127.0.0.1:5050

# Send a message
Fill the textarea with "This will be cleared"
Click the Send button
Wait for .text-bubble

# Clear
Click the .clear-btn button
Take a snapshot                               # Messages gone, empty state
```

---

### 11. Toggle Sidebar

```
Navigate to http://127.0.0.1:5050
Take a snapshot                               # Sidebar visible

# Collapse sidebar
Click the .toggle-btn button
Take a snapshot                               # Sidebar hidden, hamburger visible

# Expand sidebar
Click the hamburger button (text "=")
Take a snapshot                               # Sidebar visible again
```

---

### 12. Manage Modes Dialog

```
Navigate to http://127.0.0.1:5050

# Open mode dropdown
Click .selector-btn

# Open Manage Modes
Click .manage-item (text "Manage Modes...")
Take a snapshot                               # Modal with System Modes list

# Close modal
Click .close-btn (the x button)
Take a snapshot                               # Modal closed
```

---

### 13. Create a Custom Mode

```
Navigate to http://127.0.0.1:5050

# Open Manage Modes
Click .selector-btn
Click .manage-item

# Click Create New Mode
Click the "Create New Mode" button
Take a snapshot                               # Editor form visible

# Fill in the form
Fill "Name *" textbox with "My Test Mode"
Fill "Description" textbox with "A custom test mode"
Fill "System Prompt *" textbox with "You are a test assistant. Be brief."

# Select specific tools
Click "Deselect All" button
Check the "calculate" checkbox
Take a snapshot                               # Form filled, 1 tool selected

# Save
Click the "Save" button
Take a snapshot                               # Back to Manage Modes, custom mode visible
```

**Selectors:**
```
page.getByRole('button', { name: 'Create New Mode' }).click()
page.getByRole('textbox', { name: 'Name *' }).fill('My Test Mode')
page.getByRole('textbox', { name: 'Description' }).fill('A custom test mode')
page.getByRole('textbox', { name: 'System Prompt *' }).fill('You are a test assistant.')
page.getByRole('button', { name: 'Deselect All' }).click()
page.getByRole('checkbox', { name: /calculate/ }).check()
page.getByRole('button', { name: 'Save' }).click()
```

---

### 14. Copy an Existing Mode

```
Navigate to http://127.0.0.1:5050

# Open Manage Modes
Click .selector-btn
Click .manage-item

# Copy a system mode
Click the "Copy" button next to "Research Assistant"
Take a snapshot                               # Editor pre-filled with Research Assistant's config

# Modify name
Clear and fill "Name *" with "My Research Mode"

# Save
Click "Save"
Take a snapshot
```

---

### 15. Verify Recording Files (Post-Test)

After running any script that sends messages, check recordings:

```bash
ls -lt samples/LmStreaming.Sample/recordings/ | head -10
```

Recording files follow the pattern:
- `{threadId}_{timestamp}.ws.jsonl` — WebSocket messages (full conversation)
- `{threadId}_{timestamp}.llm.request.txt` — First LLM request
- `{threadId}_{timestamp}.llm.1.request.txt` — Second LLM request
- `{threadId}_{timestamp}.llm.response.txt` — LLM response

To inspect a recording for thinking blocks:
```bash
grep -c "thinking" samples/LmStreaming.Sample/recordings/*request*.txt
```

---

### 16. Check Logs with DuckDB (Post-Test)

Query structured Serilog logs for errors or specific events:

```sql
-- Find errors during a test
SELECT "@t" as Time, "@l" as Level, SourceContext, "@mt" as Template, "@x" as Exception
FROM read_json('C:/Users/gautamb/source/repos/MLProjects/autogen.net/LmDotnet/samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE "@l" = 'Error'
ORDER BY "@t" DESC
LIMIT 20;

-- Trace thinking/reasoning flow
SELECT "@t" as Time, SourceContext, "@mt" as Template
FROM read_json('C:/Users/gautamb/source/repos/MLProjects/autogen.net/LmDotnet/samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE "@mt" LIKE '%Reasoning%' OR "@mt" LIKE '%thinking%'
ORDER BY "@t" DESC
LIMIT 20;

-- Check token usage per run
SELECT "@t" as Time, PromptTokens, CompletionTokens, Duration
FROM read_json('C:/Users/gautamb/source/repos/MLProjects/autogen.net/LmDotnet/samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-*.jsonl')
WHERE "@mt" LIKE '%completed%' AND PromptTokens IS NOT NULL
ORDER BY "@t" DESC
LIMIT 10;
```

---

## Common Patterns

### Wait for Streaming Response to Complete

Streaming responses arrive in chunks. The `.text-bubble` appears once text starts streaming, but the response may not be complete. To wait for completion:

```
# Wait for the active-conversation-spacer which is always last
# OR wait for a specific text content
Wait for .active-conversation-spacer to be visible
```

For tool-call responses, the pattern is:
1. `.pill-item` with thinking appears first
2. `.pill-item` with tool call appears
3. More `.pill-item` entries may follow (multi-turn tool calls)
4. `.text-bubble` appears with the final text response

### Count Messages

```
page.locator('.user-message-wrapper').count()       // user messages
page.locator('.assistant-message-wrapper').count()   // assistant messages
page.locator('.pill-item').count()                   // thinking/tool entries
page.locator('.text-bubble').count()                 // text response segments
```

### Extract Response Text

```
page.locator('.assistant-content .markdown-content').last().textContent()
```

### Check Current Mode

```
page.locator('.mode-name').textContent()    // Returns e.g. "General Assistant"
```

---

## Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Send button disabled | Textarea empty | Fill textarea before clicking Send |
| No response after send | WebSocket disconnected | Check app is running, refresh page |
| Mode dropdown won't open | Already open or UI blocked | Press Escape first, then click |
| Thinking pills missing | Mode doesn't use thinking model | Switch to Medical Knowledge or test-anthropic mode |
| Second turn fails with error | ReasoningMessage lost in history | Check MessageTransformationMiddleware fix is applied |
| Recording files not created | Dump disabled for thread | Check `RequestResponseDumpFileName` in options |
