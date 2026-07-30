# Plural Agent Control Tools Design

**Date:** 2026-07-29
**Status:** Implemented
**Scope:** `LmMultiTurn` sub-agent observation tools and their repository-wide consumers

## Objective

Replace the singular `CheckAgent` tool with batch-oriented `CheckAgents`, and add a new batch-oriented `WaitForAgents` tool. The plural tools reduce repeated model/tool calls during fan-out, give controllers a non-polling way to wait for long-running delegates, and explicitly remind callers that sub-agents commonly take 5–15 minutes.

This is an intentional breaking tool-surface change. New singular aliases will not be retained. Historical persisted singular tool calls must remain displayable in the client, but attempting to execute `CheckAgent` after the migration is unsupported.

## Current State

`SubAgentToolProvider` currently exposes `Agent`, `SendMessage`, and `CheckAgent`. `CheckAgent` resolves one canonical agent ID and delegates to `SubAgentManager.TryPeek`, returning identity, status, template/task, recent turns, last result, and parent-relay failure information.

No `WaitForAgent` or `WaitForAgents` tool exists. The core manager already exposes `ObserveCompletionAsync(agentId, cancellationToken)`, a non-destructive observation primitive: cancelling observation does not cancel the sub-agent. The sample application's `SubAgentCompletionTriggerSource` and LmWorkflow's `WaitWorkflow` provide related, but separate, lifecycle and timeout patterns.

## Public Tool API

Only these observation tools will be exposed after migration:

- `CheckAgents`
- `WaitForAgents`

`Agent` and `SendMessage` are unchanged.

### Shared target semantics

Both plural tools accept:

```json
{
  "targets": ["agent-id", "readable-name"]
}
```

Requirements:

- `targets` is required and must be a non-empty array of non-blank strings.
- Each target resolves as a canonical agent ID first, then as an exact readable agent name.
- Every input target, including duplicates, produces exactly one response entry in the same position. Two inputs resolving to the same canonical agent produce two independently captured snapshot entries with the same `agent_id` and `name`.
- Each successful resolution returns canonical `agent_id` and `name` regardless of the input form.
- ID and name matching use the manager's existing ordinal, case-sensitive dictionary semantics. Agent names are already guaranteed non-blank and unique among tracked agents; a later spawn reusing a live name is rejected.
- Unknown targets do not fail the batch. They produce an ordered entry containing the original `target` and `status: "not_found"`.

### `CheckAgents`

`CheckAgents` performs a non-blocking snapshot of every target.

Request:

```json
{
  "targets": ["researcher", "9c2a7b34f810-ab12cd34"]
}
```

Response shape:

```json
{
  "agents": [
    {
      "target": "researcher",
      "agent_id": "9c2a7b34f810-ab12cd34",
      "name": "researcher",
      "status": "running",
      "template": "general-purpose",
      "task": "...",
      "recent_turns": [],
      "last_result": null,
      "send_to_parent_failed": false,
      "send_to_parent_error": null
    },
    {
      "target": "missing-agent",
      "agent_id": null,
      "name": null,
      "status": "not_found"
    }
  ],
  "summary": {
    "requested": 2,
    "running": 1,
    "terminal": 0,
    "not_found": 1
  },
  "guidance": "Use WaitForAgents instead of repeatedly polling. Agents commonly take 5–15 minutes. Wait at least 30 seconds per call."
}
```

For valid targets, the existing singular snapshot fields and meanings are preserved. Terminal statuses are `completed`, `error`, and `stopped`; `running` remains non-terminal.

The guidance appears in both the tool description and every successful response so the model sees it before and after polling:

- Prefer `WaitForAgents` over repeatedly calling `CheckAgents`.
- Agents may take 5–15 minutes.
- Use waits of at least 30 seconds.

This is behavioral steering, not a technical prohibition on repeated checks. The same preference is also migrated into controller/sample prompts; models may still call `CheckAgents` when they need an immediate non-blocking snapshot.

### `WaitForAgents`

Request:

```json
{
  "targets": ["review-one", "review-two"],
  "mode": "all",
  "timeout_seconds": 900
}
```

Parameters:

- `targets`: shared semantics above.
- `mode`: optional string enum `"all" | "any"`; omitted values default to `"all"` in the handler.
- `timeout_seconds`: optional integer; omitted values default to `900`; the schema and handler enforce `minimum: 30` and `maximum: 900`, inclusive.

Wait modes:

- `all`: return when every valid target is terminal, or when timeout/caller cancellation occurs. Unknown targets are already settled and do not block.
- `any`: return when the first valid target becomes terminal, or when timeout/caller cancellation occurs. Unknown targets do not satisfy `any`.
- If all inputs are unknown, return immediately with `outcome: "no_valid_targets"`, preserving all requested `not_found` entries. This outcome takes precedence over the vacuous truth of `all` and is neither completion nor timeout.

Response shape reuses the complete `CheckAgents.agents` snapshot schema—including recent turns, final result, and relay-error fields—and `summary`, and adds:

```json
{
  "wait": {
    "mode": "all",
    "outcome": "completed",
    "timeout_seconds": 900,
    "elapsed_seconds": 37.2
  }
}
```

`wait.outcome` is one of:

- `completed`: the selected completion condition was met.
- `partial`: `mode: "any"` completed while other valid agents remain non-terminal.
- `timeout`: the timeout elapsed before the selected completion condition.
- `no_valid_targets`: every target was `not_found`.

A timeout is normal data, not a tool error. The response includes a freshly captured snapshot for every target. `WaitForAgents` does not repeat the `guidance` field; its tool description already tells callers to use 30-second-or-longer waits and that agents may take 5–15 minutes.

## Architecture

### `SubAgentManager`

The manager owns reusable, transport-independent batch behavior:

1. Resolve each input target by ID, then exact name.
2. Capture immutable snapshots in input order.
3. Start non-destructive completion observations concurrently for valid, non-terminal agents.
4. Apply `all` or `any` completion semantics.
5. Apply the observation timeout without cancelling sub-agents.
6. Re-resolve/re-snapshot each target immediately before returning. This is a best-effort ordered batch view, not a globally atomic transaction across agents; each individual snapshot uses the existing thread-safe state reads, and concurrent agent/relay activity may advance between entries.

Manager result types represent target resolution, snapshots, wait mode/outcome, and elapsed time. They do not contain model-facing guidance strings or JSON-specific naming.

Existing `TryPeek` and `ObserveCompletionAsync` remain usable internally and by existing non-tool consumers. Batch APIs compose these primitives rather than duplicating lifecycle logic.

### `SubAgentToolProvider`

The provider remains the tool boundary and owns:

- JSON schemas/descriptions.
- Parsing and validating `targets`, `mode`, and `timeout_seconds`.
- Mapping manager results to stable snake_case JSON.
- Computing response summary counts.
- Adding the model-facing wait guidance.

It exposes `CheckAgents` and `WaitForAgents`; it no longer exposes or handles `CheckAgent`.

### Non-destructive waiting

`WaitForAgents` observes only. It must not:

- Cancel, stop, or dispose an agent on timeout.
- Change `NotifyParentOnCompletion`.
- Suppress or duplicate the existing background completion notification path.
- Consume a completion result such that another observer cannot read it.

All observations begin concurrently. The implementation must not await targets serially.

### Cancellation and races

- The caller-provided cancellation token remains authoritative. Caller cancellation propagates through the tool call and is not converted to `timeout`.
- A separate linked timeout token controls observation only.
- If completion races timeout, the manager performs a final snapshot. Any target already terminal in that final snapshot is reported terminal.
- At target resolution, each observation captures the currently registered `SubAgentState.Completion.Task`. That completion latch represents the run generation active at that moment. If `SendMessage` restarts the agent while the wait is active, the original wait continues observing the captured latch and does not follow the replacement generation; callers issue a new `WaitForAgents` call for restarted work.

## Validation and Error Policy

Tool-level validation errors reject the call with a descriptive recoverable tool error that names the invalid field and accepted values:

- Missing or empty `targets`.
- Non-string or blank target entries.
- `mode` outside `all|any`.
- A provided `timeout_seconds < 30` or `> 900`; omitted values default to 900 and are valid.

Per-target conditions remain successful batch data:

- Unknown target: `not_found`.
- Agent failure: `error` snapshot.
- Explicitly stopped agent: `stopped` snapshot.
- Parent relay failure: preserved through existing snapshot fields.

## Breaking Migration

Rename active tool usage repository-wide:

- `CheckAgent` → `CheckAgents` with one-element `targets` where batching is not useful.
- Repeated status polling → one `WaitForAgents` call where completion is required.
- Controller/workflow prompts should tell models to batch known agents and wait rather than poll.

Update exact tool lists and descriptions in:

- LmMultiTurn descriptors, handlers, and tests.
- Workflow controller restrictions and tool inheritance/exclusion tests.
- LmStreaming prompts, prompt examples, integration tests, and E2E scenarios.
- LmWorkflow documentation.
- Client tool-name formatting tests and current fixtures.

Historical compatibility is display-only: the client retains recognition/formatting for persisted `CheckAgent` tool messages. Stored history is not rewritten. The server no longer advertises or executes the singular tool.

The sample's `SubAgentCompletionTriggerSource` remains unchanged. It serves trigger-driven workflows and should not be coupled to the new core batch wait.

## Testing Strategy

Implementation follows RED→GREEN TDD.

### Tool schema and descriptor tests

- `CheckAgents` and `WaitForAgents` are exposed; `CheckAgent` is absent.
- `targets` is a required string array.
- Wait schema documents `all|any`, default `all`, default timeout 900, and valid range 30..900.
- `CheckAgents` description contains the `WaitForAgents`, 30-second minimum, and 5–15-minute guidance.

### Batch check tests

- Multiple IDs and names.
- Input ordering and duplicates.
- Mixed running/completed/error/stopped/not-found targets.
- Existing recent turns, result, and relay-error fields remain unchanged for valid targets.
- Empty arrays, blank targets, and non-string entries reject the call.

### Batch wait tests

Create focused `SubAgentManagerBatchObservationTests` for manager semantics and extend `SubAgentToolProviderTests` for schema, parsing, serialization, guidance, and recoverable validation errors.

- `all` waits for every valid target.
- `any` returns on the first valid terminal target.
- Omitted mode defaults to `all`.
- Timeout returns partial snapshots and leaves agents running.
- Caller cancellation propagates and leaves agents running.
- Unknown-only input returns `no_valid_targets` immediately.
- Mixed unknown/running behavior follows mode semantics.
- Bounds: 29 and 901 reject; 30 and 900 succeed.
- Completion racing timeout reports final terminal state accurately.
- Observations are concurrent, not serial.
- Existing automatic parent completion notifications remain unchanged.

### Migration and regression tests

- Exact server tool sets contain plural names only.
- Controller restrictions and recursion guards use plural names.
- Prompts instruct batching and `WaitForAgents` rather than repeated polls.
- Client rendering recognizes plural tool names while historical singular fixtures still render.
- Existing `Agent`, `SendMessage`, defer-queue, continuation, trigger, workflow, and completed-tab replay tests remain green.

## Verification Gates

1. Focused new RED→GREEN tests.
2. Full `LmMultiTurn.Tests`.
3. Relevant `LmWorkflow.Tests` and LmStreaming sample test filters.
4. Client unit tests for tool labels and persisted fixtures.
5. `dotnet build LmDotnetTools.sln`.
6. `dotnet format whitespace LmDotnetTools.sln --verify-no-changes`.

A live browser or paid LLM run is not required unless deterministic tests reveal an integration gap.

## Out of Scope

- Changing `Agent` or `SendMessage` request/response semantics.
- Changing sub-agent concurrency limits or defer-queue behavior.
- Replacing the sample's trigger system.
- Automatically following unlimited future `SendMessage` restarts within one wait.
- Rewriting persisted singular tool-call history.
