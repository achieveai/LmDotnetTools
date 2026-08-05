# LmStreaming.Sample Output-Token Policy Design

## Status

Approved for implementation on 2026-07-31.

## Problem

`LmStreaming.Sample` hardcodes `MaxToken = 8192` for primary conversations. The value was originally chosen around a fixed 2,048-token thinking budget and is no longer appropriate for current adaptive-thinking, tool-heavy models. In a real Claude Opus 5 conversation, two turns exhausted all 8,192 output tokens and truncated `Write` tool arguments before the file content was emitted.

The generic `LmMultiTurn` library also has an 8,192 unset-value floor. Other library consumers may rely on that behavior, so this change must not alter the global default.

## Policy

Within `LmStreaming.Sample`:

- Primary conversation loops default to 24,576 output tokens.
- Sample-created delegated loops default to 16,384 output tokens.
- An explicit template or caller `MaxToken` remains authoritative.
- Other `LmMultiTurn` consumers retain the existing 8,192 library fallback.

## Configuration

Add typed sample configuration bound from:

```json
{
  "AgentOutputTokens": {
    "Primary": 24576,
    "Delegated": 16384
  }
}
```

The options type validates both values as positive integers and requires `Primary >= Delegated`. Invalid startup configuration fails clearly rather than silently restoring an unsafe ceiling.

Environment-specific configuration can override the normal .NET configuration keys, including `AgentOutputTokens__Primary` and `AgentOutputTokens__Delegated`.

## Data Flow

### Primary conversation

The conversation factory reads the validated options and supplies `Primary` to the root `MultiTurnAgentLoop` through `GenerateReplyOptions.MaxToken`.

### Ordinary subagents

The sample normalizes its subagent templates before passing them to `SubAgentManager`: when a template has no explicit `DefaultOptions.MaxToken`, it receives `Delegated`; explicit values are unchanged. This makes the host policy independent of the global library fallback.

### Workflow controllers

`WorkflowManager.ControllerDefaultOptions.MaxToken` receives `Delegated`. A per-run model/provider override changes only the model and reasoning transport metadata, preserving the configured token budget.

### Workflow delegates

Workflow delegates inherit the workflow controller's effective `MaxToken` through the existing `SubAgentManager.ResolveSubAgentOptions` path. A delegate template with an explicit `MaxToken` continues to win.

## Compatibility

- No public `LmCore` or `LmMultiTurn` API changes are required.
- `MultiTurnAgentBase.DefaultMaxTokenFloor` remains 8,192.
- Direct callers that explicitly set smaller budgets remain unchanged.
- Provider request conversion remains unchanged; it receives the larger host-selected value through existing `GenerateReplyOptions`.
- All requests are already streamed, so 24K does not introduce a non-streaming timeout concern.

## Testing

Add focused tests that prove:

1. Default configuration binds to 24,576 primary and 16,384 delegated tokens.
2. Invalid non-positive values fail validation.
3. `Primary < Delegated` fails validation.
4. The root conversation construction path uses the configured primary value.
5. Sample template normalization fills only missing delegated budgets.
6. Explicit template budgets are preserved.
7. Workflow controller defaults use the configured delegated value.
8. Existing `LmMultiTurn` budget tests remain unchanged, proving the global library default was not modified.

Where direct construction of the large `Program.cs` closure is impractical, extract a small internal sample policy/helper that applies the options to `GenerateReplyOptions` and templates; test that helper directly. The helper must remain narrowly scoped to token-policy application rather than becoming a general options builder.

## Non-goals

- Dynamic model-capability discovery.
- Raising or clamping explicit caller budgets.
- Changing defaults for other samples or library consumers.
- Introducing per-model token mappings.
- Changing thinking or effort configuration.
