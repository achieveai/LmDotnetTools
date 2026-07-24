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

### Backgrounded sub-agent parked on a long wait (webhook-routing fixture)

Fixture for the directory-context routing feature (#198): background-spawns a **named** sub-agent
(`name: "ctx-probe"`) with `run_in_background: true`, whose nested chain immediately arms a **30s**
`Wait`. The parent chain returns right away while `ctx-probe` stays **Running-but-parked** — a stable
window in which a synthetic `context_discovery` webhook carrying `agent_id: "ctx-probe"` (with
`ContextDiscovery:RouteToOpeningSubAgent=true`) can be routed into that sub-agent's own conversation
instead of the primary. Requires `test` / `test-anthropic` mode with the `Agent` + `Wait` tools wired.

<|instruction_start|>{"instruction_chain":[{"id":"spawn-probe","id_message":"Background-spawn ctx-probe parked on a 30s wait","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","name":"ctx-probe","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"probe-arm\",\"messages\":[{\"tool_call\":[{\"name\":\"Wait\",\"args\":{\"kind\":\"timer\",\"args\":{\"delay\":\"30s\"},\"timeout\":\"60s\",\"label\":\"ctx-probe-park\"}}]}]},{\"id\":\"probe-done\",\"messages\":[{\"text\":\"ctx-probe resumed after its wait\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent-ack","id_message":"Parent continues while ctx-probe is parked","messages":[{"text":"ctx-probe spawned in the background and parked on its wait — ready to receive routed directory context."}]}]}<|instruction_end|>

Expected behavior:
1. An `Agent` tool-call pill for `ctx-probe` returns immediately (background spawn); the parent's final
   text lands while `ctx-probe` is still parked on its 30s timer.
2. POST a synthetic `context_discovery` webhook for this session with an item carrying
   `agent_id: "ctx-probe"` while the flag is on ⇒ the injector routes it into `ctx-probe`'s conversation
   (the primary store gets no context turn); `GET /api/diagnostics/context-discovery` shows the
   `routing.routed` counter increment.
3. After ~30s the timer fires and `ctx-probe` resumes — proof it was genuinely parked, not finished.

### Multi-level nested delegation (every `Agent` wrapped in the next chain)

> ⚠ **Only the FIRST level actually executes.** A sub-agent does **not** inherit the `Agent` tool —
> `MultiTurnAgentLoop` registers the `Agent`/`CheckAgent`/`SendMessage` tools *after* snapshotting the
> parent's tools, and `SubAgentManager.CreateSubAgentAsync` builds each sub-agent loop **without a
> `SubAgentManager`**. That is a deliberate **recursion guard** ("preventing unbounded recursive
> delegation"). So a sub-agent **cannot spawn a sub-agent**: the prompt below is valid and the mock
> accepts it, but the level-2 `Agent` call inside `stage-1-lead` resolves to an unknown tool and the
> tree stops one level deep. Verified by `SubAgentEmbeddedChainTests` (1-level works) +
> `SubAgentRecursionGuardTests` (level-2 never runs). **For genuine multi-level orchestration use the
> `StartWorkflowAgent` workflow feature** — a workflow controller IS a nested-root loop that
> re-registers `Agent`, so its delegates run under Workspace Agent mode (real provider + sandbox).

The builder below is kept as an illustration of the *shape* of a deep chain (and of why the escaping
forces a programmatic builder) — but remember it collapses to one executed level. A deep delegation
tree WOULD look like: the root spawns `stage-1-lead`, whose chain spawns `stage-2-researcher`, whose
chain spawns `stage-3-leaf` — i.e. **each `Agent` tool-call's `prompt` is the *next level's* complete
instruction chain**.

**Do NOT hand-escape this.** The nesting escapes quotes exponentially (`\"` → `\\\"` → `\\\\\\\"`
per level). Build it with nested `JSON.stringify` so the escaping is correct by construction (the
same trick `playwright-scripts/subagent-tabs.mjs` uses):

```js
const wrap  = (chain) => `<|instruction_start|>${JSON.stringify(chain)}<|instruction_end|>`;
const agent = (type, name, chain) =>
  ({ name: 'Agent', args: { subagent_type: type, name, run_in_background: false, prompt: wrap(chain) } });

const L3 = { instruction_chain: [
  { id: 'l3-work', reasoning: { length: 20 }, messages: [{ tool_call: [
    { name: 'calculate', args: { a: 6, operation: 'multiply', b: 7 } },
    { name: 'get_weather', args: { location: 'Reykjavik' } } ] }] },
  { id: 'l3-done', messages: [{ text: 'L3 leaf done: 6*7=42, weather checked.' }] } ] };
const L2 = { instruction_chain: [
  { id: 'l2-spawn', reasoning: { length: 25 }, messages: [
    { tool_call: [{ name: 'web_search', args: { query: 'deepest nested delegation', numResults: 2 } }] },
    { tool_call: [ agent('general-purpose', 'stage-3-leaf', L3) ] } ] },
  { id: 'l2-done', messages: [{ text: 'L2 done: stage-3-leaf returned.' }] } ] };
const L1 = { instruction_chain: [
  { id: 'l1-spawn', reasoning: { length: 25 }, messages: [
    { tool_call: [{ name: 'calculate', args: { a: 40, operation: 'add', b: 2 } }] },
    { tool_call: [ agent('researcher', 'stage-2-researcher', L2) ] } ] },
  { id: 'l1-done', messages: [{ text: 'L1 done: stage-2-researcher returned.' }] } ] };
const L0 = { instruction_chain: [
  { id: 'l0-plan', reasoning: { length: 30 },
    messages: [{ tool_call: [ agent('general-purpose', 'stage-1-lead', L1) ] }] },
  { id: 'l0-done', messages: [{ text: 'Root done: 4-level nested delegation completed.' }] } ] };
const PROMPT = wrap(L0); // ← paste this
```

The exact 4-level literal it produces (paste into a `test`/`test-anthropic` chat with the `Agent` +
`calculate` + `get_weather` + `web_search` tools wired):

<|instruction_start|>{"instruction_chain":[{"id":"l0-plan","id_message":"Root: orchestrate a 4-level delegation","reasoning":{"length":30},"messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","name":"stage-1-lead","run_in_background":false,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"l1-spawn\",\"id_message\":\"L1: compute then delegate to L2\",\"reasoning\":{\"length\":25},\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":40,\"operation\":\"add\",\"b\":2}}]},{\"tool_call\":[{\"name\":\"Agent\",\"args\":{\"subagent_type\":\"researcher\",\"name\":\"stage-2-researcher\",\"run_in_background\":false,\"prompt\":\"<|instruction_start|>{\\\"instruction_chain\\\":[{\\\"id\\\":\\\"l2-spawn\\\",\\\"id_message\\\":\\\"L2: research then delegate to L3\\\",\\\"reasoning\\\":{\\\"length\\\":25},\\\"messages\\\":[{\\\"tool_call\\\":[{\\\"name\\\":\\\"web_search\\\",\\\"args\\\":{\\\"query\\\":\\\"deepest nested delegation\\\",\\\"numResults\\\":2}}]},{\\\"tool_call\\\":[{\\\"name\\\":\\\"Agent\\\",\\\"args\\\":{\\\"subagent_type\\\":\\\"general-purpose\\\",\\\"name\\\":\\\"stage-3-leaf\\\",\\\"run_in_background\\\":false,\\\"prompt\\\":\\\"<|instruction_start|>{\\\\\\\"instruction_chain\\\\\\\":[{\\\\\\\"id\\\\\\\":\\\\\\\"l3-work\\\\\\\",\\\\\\\"id_message\\\\\\\":\\\\\\\"L3 leaf: compute + weather\\\\\\\",\\\\\\\"reasoning\\\\\\\":{\\\\\\\"length\\\\\\\":20},\\\\\\\"messages\\\\\\\":[{\\\\\\\"tool_call\\\\\\\":[{\\\\\\\"name\\\\\\\":\\\\\\\"calculate\\\\\\\",\\\\\\\"args\\\\\\\":{\\\\\\\"a\\\\\\\":6,\\\\\\\"operation\\\\\\\":\\\\\\\"multiply\\\\\\\",\\\\\\\"b\\\\\\\":7}},{\\\\\\\"name\\\\\\\":\\\\\\\"get_weather\\\\\\\",\\\\\\\"args\\\\\\\":{\\\\\\\"location\\\\\\\":\\\\\\\"Reykjavik\\\\\\\"}}]}]},{\\\\\\\"id\\\\\\\":\\\\\\\"l3-done\\\\\\\",\\\\\\\"messages\\\\\\\":[{\\\\\\\"text\\\\\\\":\\\\\\\"L3 leaf done: 6*7=42, weather checked.\\\\\\\"}]}]}<|instruction_end|>\\\"}}]}]},{\\\"id\\\":\\\"l2-done\\\",\\\"messages\\\":[{\\\"text\\\":\\\"L2 done: stage-3-leaf returned.\\\"}]}]}<|instruction_end|>\"}}]}]},{\"id\":\"l1-done\",\"messages\":[{\"text\":\"L1 done: stage-2-researcher returned.\"}]}]}<|instruction_end|>"}}]}]},{"id":"l0-done","messages":[{"text":"Root done: 4-level nested delegation completed."}]}]}<|instruction_end|>

Expected behavior (ACTUAL, given the recursion guard): the parent shows an `Agent` pill for
`stage-1-lead`, and `stage-1-lead` runs its chain — reasoning + `calculate` — and then its **level-2
`Agent` call for `stage-2-researcher` resolves to an unknown tool and is a no-op**. So the tree stops
at `stage-1-lead`; `stage-2-researcher`/`stage-3-leaf` never run and their text never reaches the UI.
This is the invariant `SubAgentRecursionGuardTests` locks in.

#### The working 1-level embedded chain (what the mock DOES support)

Parent spawns ONE sub-agent whose `prompt` is a leaf instruction chain (a tool + a text). The
sub-agent runs the nested chain and its final text becomes the synchronous `Agent` tool result — this
is the deepest nesting the plain `Agent` tool supports, and it's covered by `SubAgentEmbeddedChainTests`:

<|instruction_start|>{"instruction_chain":[{"id":"parent","id_message":"Delegate to sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"sub-tool\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":2,\"operation\":\"add\",\"b\":3}}]}]},{\"id\":\"sub-text\",\"messages\":[{\"text\":\"hi from agent\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent2","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent finished."}]}]}<|instruction_end|>

Expand the `Agent` pill — its **Result** shows `hi from agent` (the sub-agent's nested-chain output),
proving the embedded chain executed end to end. To surface sub-agents as CENTER-PANE TABS, flip the
spawn to `run_in_background:true` (see "Sub-Agent Tabs" below).

---

## Sub-Agent Tabs (center-pane, colored)

Drive the **center-pane tab strip**: a sub-agent conversation opens as a tab next to `main`, each
sub-agent is assigned a stable color (in discovery order — 1st sub-agent → hue 0, 2nd → hue 1, …) that
tints **both its tab and its inline `Agent`/`SendMessage` call pills** in the parent conversation.
Selecting a tab swaps the center pane to that child's transcript; `main` returns to the parent.

Requires `test` / `test-anthropic` mode and **General Assistant** (Agent + calculate wired). Prefer
`run_in_background: true` so each spawn returns a receipt immediately (its `agent_id` lets the pill
resolve its color exactly) and the child's transcript persists as the tab's replay source.

**Authoring tip:** build these nested-chain prompts with `JSON.stringify` rather than hand-escaping —
`playwright-scripts/subagent-tabs.mjs` constructs the exact prompt below programmatically, so the
inner-quote escaping (`\"`) and literal `<|instruction_start|>` tags are correct by construction.

### Two sub-agents → two distinct colored tabs (validated)

One parent turn spawns **two** background sub-agents (`alpha` = a researcher that replies with text;
`beta` = a general-purpose worker that runs `calculate` then replies). Two colored tabs (`alpha`,
`beta`) appear alongside `main`; both inline `Agent` pills are tinted to match their tab.

<|instruction_start|>{"instruction_chain":[{"id":"spawn-two","id_message":"Spawn two background workers","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"researcher","name":"alpha","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"a1\",\"messages\":[{\"text\":\"Alpha reporting: I found three fresh AI papers today.\"}]}]}<|instruction_end|>"}},{"name":"Agent","args":{"subagent_type":"general-purpose","name":"beta","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"b1\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":40,\"operation\":\"add\",\"b\":2}}]}]},{\"id\":\"b2\",\"messages\":[{\"text\":\"Beta reporting: 40 + 2 = 42.\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent-done","id_message":"Wrap up","messages":[{"text":"Spawned alpha and beta in the background."}]}]}<|instruction_end|>

Expected UI:
1. Within ~3s (the sub-agent poll) a `main` tab plus an `alpha` and a `beta` tab appear; the two
   sub-agent tab dots have **different** colors.
2. In the parent conversation, each `Agent` tool-call pill has a colored **left border** matching its
   tab (background spawns resolve the exact `agent_id` from the receipt). The two
   `Sub-agent completed ← Agent …` notification pills are tinted the same way.
3. Click `alpha` → the center pane shows alpha's transcript ("Alpha reporting: …"); click `beta` →
   beta's transcript (its `calculate 40 add 2 = 42` pill + "Beta reporting: 40 + 2 = 42."); click
   `main` → back to the parent conversation.

Known cosmetic quirk (pre-existing, not tab-specific): a sub-agent's own *user* message renders the
raw nested `<|instruction_start|>…` chain (the parent hides it as "Test instruction sent"; the
sub-agent transcript does not).

### Richer per-tab transcript (reasoning + multiple tools + text)

A single background sub-agent whose nested chain reasons, calls two tools, then summarizes — so its
tab holds a substantial transcript (a MetadataPill with a thinking row + two tool pills, then text).

<|instruction_start|>{"instruction_chain":[{"id":"spawn-analyst","id_message":"Spawn a background analyst","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","name":"analyst","run_in_background":true,"prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"r1\",\"reasoning\":{\"length\":30},\"messages\":[{\"tool_call\":[{\"name\":\"get_weather\",\"args\":{\"location\":\"Tokyo\"}}]}]},{\"id\":\"r2\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":18,\"operation\":\"multiply\",\"b\":2}}]}]},{\"id\":\"r3\",\"messages\":[{\"text\":\"Analyst: Tokyo checked, 18x2=36. Analysis complete.\"}]}]}<|instruction_end|>"}}]}]},{"id":"analyst-ack","id_message":"Wrap up","messages":[{"text":"Analyst spawned in the background."}]}]}<|instruction_end|>

The browser-observable slice of the two-tab scenario is locked in CI by
`tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/SubAgentTabsTests.cs`.

---

## Wait / Trigger (Park-and-Wake)

Requires `test` or `test-anthropic` mode. The `Wait` / `CancelWait` / `ListWaits` tools are wired into
LmStreaming.Sample's agent construction **for the mock providers only** (see `Program.cs` —
`triggerOptions: isTestMode ? new TriggerOptions() : null`). The built-in one-shot `timer` source is
registered automatically.

When the parent chain calls `Wait`, the run **parks** (the tool returns `Deferred()`): the `Wait`
tool-call result is persisted with `is_deferred: true` and no further provider request is made until
the timer fires. Once the `timer` elapses (here after 3s), the runtime resolves the deferred result
and the loop **auto-resumes** into the next chain turn — no new user message is needed.

Park on a 3-second timer, then auto-resume with a text message:
<|instruction_start|>{"instruction_chain":[{"id":"arm-wait","id_message":"Arming a 3s timer","messages":[{"tool_call":[{"name":"Wait","args":{"kind":"timer","args":{"delay":"3s"},"timeout":"30s","label":"demo-timer"}}]}]},{"id":"after-wait","id_message":"Resumed after wait","messages":[{"text":"Timer fired — the run resumed automatically after the wait."}]}]}<|instruction_end|>

Expected behavior:
1. A `Wait` tool-call pill appears; while parked, `GET /api/conversations/{threadId}/messages` shows
   the `Wait` tool result with `is_deferred: true` (this is the parked state a headless poller asserts).
2. After ~3s the timer fires, the deferred result resolves (status `fired`), and the run resumes.
3. Final assistant text: `Timer fired — the run resumed automatically after the wait.`

`timeout` is the required safety ceiling — if it elapses before the timer's `delay`, the wait resolves
with status `timed_out` instead of `fired`. Keep `delay` short (a few seconds) in demos so the parked
window is observable but the run still completes promptly.

Sub-agent delegation that itself parks on a wait (parent delegates → sub-agent arms a 2s timer, resumes,
replies) — combines the nested-chain escaping rule above with the `Wait` tool:
<|instruction_start|>{"instruction_chain":[{"id":"parent-wait","id_message":"Delegate to a waiting sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"sub-arm\",\"messages\":[{\"tool_call\":[{\"name\":\"Wait\",\"args\":{\"kind\":\"timer\",\"args\":{\"delay\":\"2s\"},\"timeout\":\"20s\",\"label\":\"sub-timer\"}}]}]},{\"id\":\"sub-done\",\"messages\":[{\"text\":\"sub-agent resumed after its wait\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent-done","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent parked, resumed, and replied."}]}]}<|instruction_end|>

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

## Usage banner (#196) UI tests

Prompts for the conversation-wide token-usage banner
([`playwright-scripts/usage-banner.mjs`](playwright-scripts/usage-banner.mjs) and the C# scenario
`Scenarios/UsageBannerTests.cs`). Use the **`test-anthropic`** mock: its scripted SSE emits a fixed
**100 input / 50 output** tokens per generation, so the banner totals are exact and deterministic.
The banner (`data-testid="usage-banner"`) renders once cumulative total tokens > 0 and reads
`Total: N | In: N | Out: N [| Cached: N] [| Cache created: N]`.

### Single reply (one generation → Total 150 / In 100 / Out 50)

<|instruction_start|>{"instruction_chain":[{"id":"u1","id_message":"Reply","messages":[{"text_message":{"length":20}}]}]}<|instruction_end|>

Send it twice in one conversation to accumulate to **Total 300 / In 200 / Out 100** (additive, not
max'd), then reload the page: the banner is restored to 300 from the persisted aggregate.

### Sub-agent delegation (descendant tokens fold into the SAME banner total)

Parent delegates via the `Agent` tool; the nested chain drives the sub-agent (`calculate` then text),
whose usage is relayed into the root conversation's ledger — so the banner total exceeds the 300 the
two parent turns alone would produce. Requires a mode where `calculate` + `Agent` are available
(**General Assistant**). Escaping rule: inner `<|instruction_start|>`/`<|instruction_end|>` stay
literal; only the inner JSON quotes are escaped.

<|instruction_start|>{"instruction_chain":[{"id":"parent","id_message":"Delegate to sub-agent","messages":[{"tool_call":[{"name":"Agent","args":{"subagent_type":"general-purpose","prompt":"<|instruction_start|>{\"instruction_chain\":[{\"id\":\"sub-tool\",\"messages\":[{\"tool_call\":[{\"name\":\"calculate\",\"args\":{\"a\":2,\"operation\":\"add\",\"b\":3}}]}]},{\"id\":\"sub-text\",\"messages\":[{\"text\":\"hi from agent\"}]}]}<|instruction_end|>"}}]}]},{"id":"parent2","id_message":"Wrap up","messages":[{"text":"Parent done: sub-agent finished."}]}]}<|instruction_end|>

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
