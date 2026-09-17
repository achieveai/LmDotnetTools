# Per-sandbox environment variables in LmStreaming.Sample — design

**Date:** 2026-09-14
**Upstream:** achieveai/SandboxedOstoolsMcpServer PR #183 (merged; released in `v0.1.11`). Adds `env` on
`POST /api/v1/sandboxes` and `GET`/`PATCH /api/v1/sandboxes/{id}/env`.
**Owner decisions (2026-09-14, HITL):** env lives on **Workspace + Mode + S2S provision** (no
appsettings default); live sessions are **PATCHed**; values are **plain text, never logged**.
**Status:** approved 2026-09-14 (HITL ReviewPlan, revision 1); §10 records the resolved decisions.

## 1. Summary

Three layers feed one per-session env map. The app computes the effective map, sends it at
create time, and PATCHes the live session whenever a layer changes. The gateway persists the
result; a change is visible on the next tool call, no container rebuild.

```
Workspace.Env  (base, persisted with workspace)
   ⊕ ChatMode.Env  (per mode, persisted with mode)
      ⊕ provision Env  (per S2S conversation, thread-metadata property)
   = effective env  → create `env` | PATCH {set…, null…}
```

Higher layer wins on key collision. One sandbox session is shared by every thread on the same
`(workspace, app id)`; the session's env follows the **most recently activated thread** (see §5).

## 2. Current state (verified, file:line)

- Sandbox created lazily in the agent factory `samples/LmStreaming.Sample/Program.cs:968-1055`,
  same path for browser and S2S. Only `callerCredential` differs.
- `BuildWorkspaceRef` (`Program.cs:4945`) is the single Workspace→`WorkspaceRef` seam, used by first
  create and by the 404-recreate reload (`Program.cs:514-521`).
- `WorkspaceRef` (`src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs:77`) → `SandboxCreateRequest`
  (`SandboxSessionRegistry.cs:1409`) → `SandboxClient.CreateAsync` (`src/Sandbox/SandboxClient.Lifecycle.cs:18`).
- SDK has no mutable session sub-resource yet. `env` map naming already exists for operations
  (`src/Sandbox/Wire/SandboxWireDtos.cs:221`, `IReadOnlyDictionary<string,string>`, key `env`).
- Modes own no sandbox config (`Models/ChatMode.cs`); Workspace owns dir/marketplaces/pluginSelection
  (`Models/Workspace.cs`). Provision stores optional fields as thread-metadata properties
  (`Controllers/ConversationsController.cs:335-390`).
- Session id is in-memory only (`SandboxSessionRegistry._sessions` keyed `(WorkspaceId, AppId)`);
  the 404-recreate path rebuilds from `WorkspaceRef`, so create-time env must be derivable from it.
- ADR 0001: all gateway calls go through the Sandbox SDK.

## 3. Goals / non-goals

**Goals**
1. `SandboxClient` gains `env` on create plus `GetEnvAsync` / `PatchEnvAsync` (session-scoped).
2. Workspace, Mode, and S2S provision each carry an optional `Env` map, persisted where the owner is.
3. Effective env is applied at create and re-applied by PATCH on: workspace edit, mode edit,
   mode switch, provision, agent (re)build.
4. Local validation mirrors gateway rules and names keys, never values. Gateway `400 invalid_env`
   is surfaced with its `keys`.
5. Values never appear in app logs. Log key names and counts only.

**Non-goals**
- appsettings-level default env (owner declined).
- Per-operation env on `SandboxCommand` (SDK field stays unexposed).
- Masking / secret flags in UI or API.
- Per-thread isolation of env on a shared session (impossible without per-thread sessions).

## 4. Contracts

### 4.1 Sandbox SDK (`src/Sandbox`)

```csharp
// SandboxCreateRequest — new optional ctor param (7th)
IReadOnlyDictionary<string,string>? Env   // null or empty ⇒ field omitted
// CreateSandboxRequestDto — [JsonPropertyName("env")] IReadOnlyDictionary<string,string>? Env

// New session-scoped calls (SandboxClient.Env.cs), via SendDirectAsync
Task<IReadOnlyDictionary<string,string>> GetEnvAsync(string sessionId, CancellationToken ct);
Task<IReadOnlyDictionary<string,string>> PatchEnvAsync(
    string sessionId, IReadOnlyDictionary<string,string?> changes, CancellationToken ct);
// body: {"K":"v","OLD":null}; response: {"env":{...}} → EnvResponseDto
```

Errors: gateway `400 invalid_env` → `SandboxException` with `Kind = InvalidEnv` (new enum member)
and `Keys` populated from the error body. `404` → existing not-found kind.

### 4.2 Shared validator (`src/LmAgentInfra/Sandbox/SandboxEnvRules.cs`)

Pure static class, used by Workspace/Mode/Provision validation and by the merge:
- key `[A-Za-z_][A-Za-z0-9_]*`, ≤ 256 B; no NUL in key/value; value ≤ 32 KiB;
- ≤ 256 keys, ≤ 128 KiB total; no case-duplicate keys (`OrdinalIgnoreCase`);
- protected names rejected, case-insensitive: `SANDBOX_ALLOWED_PATHS`, `SANDBOX_WORKSPACE`,
  `SANDBOX_HOME`, `PWSH_STATE_DIR`, `HTTP_PROXY`, `HTTPS_PROXY`, `NO_PROXY`,
  `NODE_TLS_REJECT_UNAUTHORIZED`, `PYTHONHTTPSVERIFY`, `GIT_SSL_NO_VERIFY`, `REQUESTS_CA_BUNDLE`,
  `SSL_CERT_FILE`, `CURL_CA_BUNDLE`, `NODE_EXTRA_CA_CERTS`, `NODE_USE_ENV_PROXY`. `PATH` allowed.
- `Merge(workspace, mode, provision)` → effective map (ordinal keys; later layer wins).
- `Diff(lastApplied, desired)` → PATCH body: changed/new keys set; keys in `lastApplied` but not in
  `desired` → `null`. Empty diff ⇒ no PATCH.
- Validation errors name keys and the layer (`workspace`, `mode`, `provision`), never values.

### 4.3 Workspace (`samples/LmStreaming.Sample/Models/Workspace.cs`)

- `Workspace.Env : IReadOnlyDictionary<string,string>` default empty (no tri-state needed; empty = none).
- `WorkspaceCreate.Env?` (null ⇒ empty). `WorkspaceUpdate.Env` as `Optional<IReadOnlyDictionary<string,string>>`
  (omitted = unchanged; present = **replacement** map, same rule as `Marketplaces`).
- `WorkspaceView.Env` echoed. Persisted by `FileWorkspaceStore` (JSON; absent ⇒ empty, old rows grandfather).
- `WorkspaceRef` gains `Env` (5th param). `BuildWorkspaceRef` maps it. Registry passes it to
  `SandboxCreateRequest` **merged** with mode/provision layers (see §5).
- `PUT api/workspaces/{id}` with `Env` set → store, then `SandboxEnvApplier.ReapplyForWorkspaceAsync`.

### 4.4 Mode (`Models/ChatMode.cs`, `Prompts.yaml`)

- `ChatMode.Env : IReadOnlyDictionary<string,string>?` (null = none). `ChatModeCreateUpdate.Env` with
  `EnvIsSet` presence flag, matching siblings. `Prompts.yaml` `chatModes[].env:` map, validated at
  load by `SystemChatModes` via `SandboxEnvRules` (fail startup on invalid, like other yaml checks).
- Mode edit (`ChatModesController` update) → store, then `ReapplyForModeAsync(modeId)` for every
  live thread currently in that mode.
- Mode switch (`POST api/conversations/{id}/mode`, and the WebSocket equivalent) → after the agent is
  rebuilt, `ReapplyForThreadAsync(threadId)`.

### 4.5 S2S provision (`Models/ConversationDtos.cs`)

- `ProvisionConversationRequest.Env : IReadOnlyDictionary<string,string>?`.
- Stored as thread-metadata property `ConversationSandboxEnv.PropertyKey = "sample.sandboxEnv"`
  (JSON object). Read by the agent factory when computing the effective map.
- Invalid → `400 invalid_env` with `keys` (same shape as gateway).
- No separate S2S env endpoint in this iteration. Callers change env by editing the workspace
  (`PUT api/workspaces/{id}`, already S2S-reachable) or re-provisioning. Open question §10.1.

### 4.6 Capabilities probe

`GET api/conversations/capabilities` adds `"sandboxEnv": true` so the daemon can feature-detect.

## 5. Apply model

### 5.1 Effective map for a thread

```
effective(thread) = Merge(workspace.Env, mode(thread).Env, provisionEnv(thread))
```

Computed by `SandboxEnvApplier` (new, `samples/LmStreaming.Sample/Services/`) from the stores plus the
thread's metadata. Mode for a thread = its current mode property; provision env = its property or none.

### 5.2 When applied

| Trigger | Action |
|---|---|
| Session create (agent factory / 404 recreate) | `SandboxCreateRequest.Env = effective(thread)` |
| Agent (re)build on an existing session | `PATCH Diff(lastApplied, effective)` |
| Mode switch | after rebuild: PATCH diff |
| Workspace edit with `Env` | for each live session on that workspace: pick its last-activated thread, PATCH diff |
| Mode edit with `Env` | for each live thread in that mode: PATCH diff on its session |
| Provision | only stores the property; first build applies it |

`lastApplied` per session id lives in `SandboxSessionRegistry` (in-memory, alongside `_sessionsById`),
seeded with the create-time map and replaced by each successful PATCH response. On 404-recreate it
is reseeded from the create request. It is never persisted: after a process restart the first PATCH
uses `GetEnvAsync` to seed `lastApplied` before diffing.

### 5.3 Shared-session rule

Threads on one `(workspace, app id)` share a session. The session env is set by the **most recently
activated thread** (build, mode switch, or explicit reapply). Two threads in different modes with
conflicting keys will flip the value on each activation. This is documented, logged at Information
with key names, and accepted for this iteration. The registry records `lastActivatedThreadId` per
session so workspace-edit reapply targets a deterministic thread.

### 5.4 PowerShell caveat

A PowerShell context that has set or removed a key keeps its own value; later PATCHes do not reach
it (upstream sticky-ownership rule). Bash follows the session. Document in the guide; no app logic.

## 6. Error handling

| Case | Result |
|---|---|
| Invalid env on Workspace/Mode/Provision write | 400 `invalid_env`, `keys[]`, `layer`; nothing stored |
| Gateway 400 on create | existing `SandboxSessionUnavailableException` path (400 + message with keys) |
| Gateway 400 on PATCH | log Warning with keys; store already updated; surface to the caller of the edit (400) |
| Gateway 404 on PATCH | session gone; drop `lastApplied`; next build recreates with full env |
| Gateway unreachable on PATCH | Warning; next activation retries (diff recomputed from `lastApplied`) |
| Gateway predates env (no route → 404/405 on PATCH; `env` ignored on create) | capability flag false; UI hides env editor; PATCH skipped with one Warning |

Gateway-version detection: probe `GET …/env` once per process on the first live session; 404 ⇒ unsupported.

## 7. UI (ClientApp)

- Workspace editor (`WorkspaceSelector.vue` edit dialog): key/value rows, add/remove, save → `PUT`.
- Mode editor (`ModeEditor.vue`): same component. Shared `EnvEditor.vue`.
- Validation errors shown per key. Hidden when `sandboxEnv` capability is false.

## 8. Security

- Values never logged (app or SDK). Log messages carry key names and counts only.
- Ownership: workspace/mode edits already require write access; PATCH runs under the session's
  effective credential, so a foreign app id gets the gateway's uniform 404.
- Protected-name check runs locally so a bad key never reaches the gateway.

## 9. Tests

**Sandbox.Tests**: exact wire shape with `env` (`SandboxClientLifecycleTests`), omit-when-empty,
`GetEnvAsync`/`PatchEnvAsync` bodies and null handling, `invalid_env` → `SandboxException.Keys`.

**LmStreaming.Sample.Tests**: `SandboxEnvRules` (regex, limits, protected, case-dup, merge order,
diff incl. null emission); `SandboxSessionRegistry` create passes merged env and seeds `lastApplied`;
404-recreate reproduces env; workspace update reapplies via fake client; mode switch PATCH diff;
provision property round-trip (`ProvisionedPropertyRoundTripTests` pattern); S2S provision 400 on
protected key; capabilities flag; `SystemChatModes` rejects invalid yaml env.

**Vitest**: `EnvEditor` add/remove/validate; hidden without capability.

**Contract job** (`.github/workflows/sandbox-contract.yml`): add an env round-trip scenario, pinned to
image tag `0.1.11` (see §10.2).

## 10. Resolved decisions and risks

1. **S2S mutable env endpoint — disallowed for now** (owner, 2026-09-14). No
   `PATCH api/conversations/{threadId}/sandbox-env`. S2S callers set env at provision or edit the
   workspace via `PUT api/workspaces/{id}`.
2. **Upstream release — pin CI to `0.1.11`** (owner). #183 ships in gateway + sandbox image
   `0.1.11`. The contract job (`.github/workflows/sandbox-contract.yml`, `IMAGE_TAG`) moves from
   `0.1.8` to `0.1.11` and gains the env round-trip scenario. `v0.1.11` is published (verified
   2026-09-14). The app still degrades cleanly against older gateways (§6, last row).
3. **Conflict policy — last activation wins** (owner). §5.3 stands as written.
