# Deferred Tool Execution — Motivation

## Problem

`MultiTurnAgentLoop` executes tool calls synchronously: the run loop awaits every handler via `Task.WhenAll(pendingToolCalls.Values)` before starting the next turn. This works for fast tools (filesystem reads, in-process computation, quick HTTP calls) but breaks down whenever a tool's result depends on something the loop can't drive forward on its own.

Concrete scenarios that don't fit the current model:

- **Human-in-the-loop approval.** LLM asks to read a directory; a human must approve before it proceeds. Approval may take minutes to hours.
- **External async APIs with callbacks.** A bank verification API accepts a request and fires a webhook 10 minutes later. Nothing useful to await locally.
- **Long-running batch jobs.** Submit work to a queue or scheduler; result comes back via a separate channel.
- **Sub-agents with their own tool loops.** Already long-lived; modeling them as deferred calls is a natural fit.

In all of these the handler can't reasonably block. Today, doing so would:

- Pin a turn (and the entire run loop) for the full duration.
- Prevent the loop from draining new inputs from `SendAsync` until the slow tool returns (`TryDrainInputs` runs at turn boundaries).
- Provide no cooperative cancellation — handler signatures are `Func<string, Task<string>>` with no `CancellationToken`.
- Hide partial progress from the UI and the LLM.

## The generalizing insight

Approval is not a special case. The unifying primitive is **deferred tool execution**: a handler that returns a placeholder synchronously and is resolved later by *something* outside the loop. The loop should not care whether that "something" is a human, a webhook, a worker, or a timer.

Specializations collapse into thin integration layers:

| Use case | What "resolves" the call |
|---|---|
| HITL approval | UI calls `ResolveToolCallAsync` when the human acts |
| Bank webhook | Webhook receiver calls `ResolveToolCallAsync` |
| Long batch job | Worker calls `ResolveToolCallAsync` on completion |
| Timeout/cancellation | Host timer calls `ResolveToolCallAsync` with an error |

## Why a placeholder is required, not optional

Both Anthropic and OpenAI enforce that every `tool_use` (or `tool_call`) ID in an assistant message must have a matching `tool_result` before the next assistant inference. There is no "partially answered tool calls" state in either contract.

So while the loop is waiting on a deferred resolution, it must still place *some* `tool_result` in history for protocol validity. A placeholder string (`"PENDING ..."`) plus a flag like `IsDeferred = true` on the message satisfies both the contract and the loop's own state-tracking needs.

When resolution arrives, the placeholder is replaced in-place by the real result (mutating history by `ToolCallId`). Appending a second `tool_result` for the same ID is not a defined operation and would produce an invalid request payload.

## Design implications worth noting

These are flagged here as motivation context; the actual design lives in `design.md` (TBD).

- **History as the single source of truth.** Pending state lives on `ToolCallResultMessage` (e.g., `IsDeferred`), not in a parallel registry. Persistence via `IConversationStore` is then automatic.
- **Resolution is one API.** `ResolveToolCallAsync(toolCallId, result, isError)` — used identically by every integrator. Approvals, webhooks, and timeouts all funnel through it.
- **Idempotency matters once async is in scope.** Webhooks retry; resolution must be safe against double-delivery in a way approvals never required.
- **Restart durability is the hardest open question.** When the process dies mid-deferral, the in-flight outbound work the handler initiated (HTTP request, queue publish) is *not* persisted. Handlers must be responsible for their own outbound idempotency / recovery; the loop should expose enough metadata (deferred call inspection on startup) to support that, but cannot solve it generically.
- **Progress updates use the existing subscriber stream.** Tool results are write-once; partial progress flows as separate published events to UI subscribers without resolving the placeholder.

## Out of scope (for now)

- Cross-process resolution and durable workflow integration (Temporal, Durable Functions). The first cut targets in-process async; durable hand-off is an explicit future concern.
- Built-in timeout policy. Timeouts are host-driven (host calls `ResolveToolCallAsync` with an error after its own deadline) rather than enforced by the loop. Helpers may follow.
- Streaming partial tool results into LLM context. The LLM only sees a deferred call's result on final resolution.

## Recommendation

Build the primitive at the level of **deferred tool result**, not "approval." Approvals, webhooks, and long-running jobs all become thin convenience layers over the same `ResolveToolCallAsync` API and the same `IsDeferred` placeholder shape. The loop stays focused on driving turns; everything domain-specific lives outside it.
