# Codex App Server Manual Testing Failures (2026-02-17)

## Scope
- Workspace: `worktrees/codex-sdk-provider`
- Sample app: `samples/LmStreaming.Sample`
- Validation target: Codex App Server streaming, turn lifecycle, MCP/dynamic tool execution
- Manual run timestamp window: 2026-02-17 01:40 UTC to 01:47 UTC

## Environment used
- App URL: `http://localhost:5921`
- MCP port: `39221`
- Provider diagnostics showed:
  - `providerMode=codex`
  - `model=gpt-5.3-codex`
  - `codexCliDetected=true`
  - `appServerHandshakeOk=true`

## Failure Register
| # | Failure | Status | Notes |
|---|---|---|---|
| 1 | Run lifecycle completion stuck/inconsistent | Fixed in code, pending manual re-verify | Turn watchdog now applies `timeout -> interrupt -> fail` and logs timeout telemetry. |
| 2 | Mode switching blocked (`mode_switch_while_streaming`) due stale run state | Fixed in code, pending manual re-verify | Run-state check now uses active task + agent state; stale state is detectable. |
| 3 | Tool execution observability gap | Fixed in code, pending manual re-verify | Added dynamic tool lifecycle logs and unmapped item telemetry. |
| 4 | Queued follow-up delay when run is unresolved | Fixed in code, pending manual re-verify | Unresolved runs now fail deterministically instead of hanging. |
| 5 | Shutdown-time secondary run error | Fixed in code, pending manual re-verify | Shutdown now attempts interrupt first and suppresses expected stop noise. |
| 6 | MCP port collision at startup | Fixed in code, pending manual re-verify | MCP listener now falls back to an available local port. |
| 7 | Browser automation launch issue | Open (infra-specific) | Keep API-level fallback validation until environment issue is resolved. |
| 8 | Local CLI below minimum version | Open by design | Fail-fast behavior is intentional; upgrade CLI or lower min version explicitly. |

## Evidence locations
- Structured logs:
  - `samples/LmStreaming.Sample/bin/Debug/net9.0/logs/lmstreaming-20260216.jsonl`
- JSON-RPC trace logs (session opt-in):
  - `samples/LmStreaming.Sample/recordings/<session>.codex.rpc.jsonl`
- Debug query pack:
  - `docs/CodexAppServer-DebugQueries.sql`
- Relevant source paths involved in lifecycle and routing:
  - `src/CodexSdkProvider/Transport/CodexAppServerTransport.cs`
  - `src/CodexSdkProvider/Agents/CodexSdkClient.cs`
  - `src/CodexSdkProvider/Tools/CodexDynamicToolBridge.cs`
  - `src/LmMultiTurn/CodexAgentLoop.cs`
  - `samples/LmStreaming.Sample/Agents/MultiTurnAgentPool.cs`
  - `samples/LmStreaming.Sample/Program.cs`

## Post-fix validation criteria
1. For each `codex.bridge.run.started`, exactly one terminal `codex.bridge.run.completed` or `codex.bridge.run.failed` appears within timeout + interrupt grace.
2. `mode_switch_while_streaming` appears only for genuinely active runs.
3. Tool lifecycle logs show both requested and terminal states for MCP + dynamic tool calls.
4. `record=1` sessions generate correlated WS + provider dump + Codex RPC trace files.
5. MCP startup succeeds even when the configured/default port is occupied.

## Additional failures observed (2026-02-17 07:06 UTC to 07:08 UTC)
1. MCP port collision prevents sample startup in Codex mode
- Symptom: app startup failed with `Failed to bind to address http://127.0.0.1:39200: address already in use`.
- Impact: Codex mode cannot start unless `CODEX_MCP_PORT` is overridden.
- Workaround used: run with `CODEX_MCP_PORT=49200`.

2. Browser automation could not launch for UI validation
- Symptom: Playwright launch failed with Chrome output `Opening in existing browser session` and immediate process exit.
- Impact: direct browser-driven UI validation was blocked in this environment.
- Workaround: API-level manual checks were performed with `curl` against the running sample.
- Classification: infra-only repro blocker; not a Codex provider runtime bug.

3. Local Codex CLI version below migration default
- Observed local CLI: `codex-cli 0.100.0-alpha.10`.
- Migration default: minimum `0.101.0`.
- Impact: run-start requests will fail under default gating until local CLI is upgraded or `CODEX_CLI_MIN_VERSION` is lowered explicitly.
