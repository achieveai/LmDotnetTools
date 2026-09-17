# Sandbox env in LmStreaming.Sample — plan

**Spec:** `docs/superpowers/specs/2026-09-14-sandbox-env-app-design.md` (approved 2026-09-14)
**Upstream:** SandboxedOstoolsMcpServer PR #183 (merged; released in `v0.1.11`). Wire: `env` on create; `GET`/`PATCH /api/v1/sandboxes/{id}/env`.

**Done when:** a Workspace Agent conversation and an S2S-provisioned conversation both see
`Workspace < Mode < Provision` env in `Bash`, a workspace or mode edit changes a live session
on the next tool call, and no env value appears in any log.

## Rules that hold everywhere

- Layers: Workspace (base) < ChatMode < S2S provision. Later wins. Shared session follows the last
  activated thread.
- Validate locally with the gateway's rules (key regex, sizes, case-dupes, protected names; `PATH` ok).
  Errors name keys and layer, never values. Never log values.
- No appsettings default. No S2S mutate-env endpoint. CI image pin → `0.1.11`.
- No new `SandboxSessionRegistry` ctor params. `SandboxException` ctor unchanged.

## Steps (one commit each)

1. **SDK** (`src/Sandbox`): `SandboxCreateRequest.Env` → wire `env` (omit when empty).
   New `SandboxClient.Env.cs`: `GetEnvAsync`, `PatchEnvAsync` (flat body, explicit nulls kept).
   `SandboxErrorKind.InvalidEnv` + `SandboxException.InvalidKeys`; map `error_code: invalid_env`.
   Create path never read a 400 body: add a bounded 400-only read so `keys` surface.
   Tests: wire shape, omit-when-empty, PATCH null, `invalid_env` → keys.

2. **Rules + registry** (`src/LmAgentInfra/Sandbox`): `SandboxEnvRules` — `Validate(env, layer)`,
   `Merge(layers…)`, `Diff(lastApplied, desired)`. `WorkspaceRef.Env` → create request.
   New partial `SandboxSessionRegistry.Env.cs`: per-session `lastApplied` + `lastActivatedThreadId`;
   `EnsureSessionEnvAsync(sessionId, desired, threadId)` = seed (from create, or one `GET /env` after a
   restart) → diff → PATCH → store result. Bare 404 on `/env` = old gateway → feature off, one warning.
   Tests: rules table, create sends env, diff PATCH with nulls, restart probe, unsupported gateway.

3. **App model** (`samples/LmStreaming.Sample`):
   Workspace `Env` (model/store/view; `WorkspaceUpdate.Env` as `Optional<>` = replace when present).
   ChatMode `Env` (+ `EnvIsSet`, YAML `env:` on system modes, fail startup if invalid).
   Provision `Env` → thread property `sample.sandboxEnv`; capabilities `sandboxEnv: true`.
   Controllers return `400 invalid_env { keys, layer }`; nothing stored on rejection.
   Tests: store round-trips, YAML parse, provision property round-trip, 400 shape.

4. **Apply** (`Services/SandboxEnvApplier.cs` + `Program.cs`): `ComputeEffective(thread)` merges the
   three layers. Agent factory: pass merged env on first create, then `EnsureSessionEnvAsync` after the
   session resolves (covers 404-recreate and mode switch, which re-enters the factory).
   Workspace `PUT` / mode `PUT` with env set → re-ensure each live session via its last-activated thread.
   Tests: precedence, workspace reapply, mode reapply scoped to that mode, unsupported gateway no-throw.

5. **UI** (`ClientApp`): shared `EnvEditor.vue` (key/value rows, inline key check) in the workspace
   form and `ModeEditor.vue`; send `env` only when changed. Types + one Vitest spec.

6. **Docs, CI, gate**: `SandboxWorkspaceGuide.md` section (layers, when it applies, PowerShell sticky
   caveat, plain-text/never-logged). Pin `sandbox-contract.yml` to `0.1.11` and add one live
   create→GET→PATCH→Bash→unset→protected-key test. Classify new tests in `scripts/test-priorities.ndjson`.
   Manual E2E against the `v0.1.11` gateway image: Modes path and S2S path show the layered values;
   grep the JSONL log for a value string → no hits.

## Risks

- Upstream #183 is merged and `v0.1.11` is published (verified 2026-09-14), so the contract job and
  local dev can both run on the released image.
- `WorkspaceView` is positional and `ConversationCapabilitiesResponse` props are `required`: adding a
  field breaks other constructors; fix at build.
- Two threads in different modes on one workspace flip shared keys on each activation (accepted).
