# Tool-render test fixtures (#199)

Real, extracted per-family samples for the per-tool rendering system. Each `persisted/*.json`
was extracted from the app's persisted conversation store
(`samples/LmStreaming.Sample/bin/Debug/net9.0/conversations/<thread>/messages.json`) — the exact
REST rehydration shape — by correlating a `tool_call` with its `tool_call_result` on `tool_call_id`.

## Shape
```jsonc
{
  "family": "read",              // normalized ToolFamily this sample exercises
  "sourceThread": "thread-…",    // provenance
  "toolName": "sandbox-Read",    // RAW wire name as persisted (keeps sandbox- prefix / case)
  "toolCallId": "…",
  "functionArgs": "{…}",         // raw function_args string (may be "" )
  "result": "…",                 // raw result string — the encoding-variance crux
  "isError": false               // ToolCallResultMessage.is_error (often unreliable)
}
```

## Encoding classes covered (top of the manifest)
- **double-encoded** → `weather.doubleenc`, `calculate.doubleenc`
- **single-obj**     → `checkagent.obj`, `taskoutput.obj` (structured exit_code/stdout/stderr)
- **single-array**   → `websearch.array` (encrypted_content stripped — unusable + bloat)
- **single-string**  → `updatetask.str`
- **plain string**   → `bash.plain`, `read.numbered`, `glob.paths`, `write.confirm`, `edit.diff`,
  `grep.matches`, `shell.exitcode`, `skill.body`

## Error semantics
- `shell.exitcode` — real non-zero `[Exit code: 22]` with **is_error=false** (failure lives in payload)
- `mcperror.silentfail` — `"Error executing MCP tool …"` with **is_error=false** (MCP-prefix detection)
- `agent.overlimit` — `{ "error": … }` with **is_error=true** (truthy error key)

## Notes
- `grep.matches` and `websearch.array` were TRIMMED to a small representative slice (`_trimmed`/3 items)
  so fixtures stay small; structure preserved.
- Edit renders its diff from `functionArgs.old_string`/`new_string` — the `result` is only a
  confirmation string (`Successfully edited …`), so DiffRich must not depend on the result.
- Token-scrub gate (`ghp_|github_pat_|sk-|Bearer |AKIA|PRIVATE KEY`) passed over every fixture.
- `synthetic/` holds the two hand-authored fixtures for families with zero persisted data.
