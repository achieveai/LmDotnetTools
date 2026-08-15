# Sandbox Per-Plugin Selection — Design Spec

Date: 2026-08-11
Status: Approved design, ready for implementation planning.

Repos involved:
- **Gateway repo**: `SandboxedOstoolsMcpServer` (Rust). Owns sandbox creation, marketplace/plugin
  discovery, and (new) per-plugin filtering + capability signaling.
- **App repo**: `LmDotnetTools`, specifically `samples/LmStreaming.Sample` and
  `src/LmAgentInfra/Sandbox`. Owns workspace model, session lifecycle, and UI.

Every section below states which repo owns the change. Gateway work ships and is verified
**first**. App work depends on it.

---

## 1. Executive summary

Today a workspace selects whole **marketplaces**. A user cannot pick individual plugins across
marketplaces. This spec adds **per-plugin selection**, scoped to the workspace, with immediate
sandbox restart when the selection changes.

Key design choices (approved product decisions):
- Selection lives on the **workspace**, not the conversation.
- Plugin identity is structured: `{ marketplace, plugin }`. Display form is `plugin@marketplace`.
- Selection is tri-state: missing = legacy "all plugins"; empty list = "no plugins"; non-empty =
  explicit subset.
- The gateway resolves plugin dependencies and reports the effective set.
- Changing selection **replaces** the workspace's sandbox sessions using a **prepare-then-replace**
  flow: old sessions stay live while new ones are built; nothing is destroyed until every
  replacement succeeds.
- The flow **waits for active runs to go idle** before restarting, bounded and cancellable.

Two verified defects are also fixed as part of this work — required for the above to be safe, not
separate product decisions:
- A known bug — sandbox recreation reusing a stale `WorkspaceRef` — is fixed (Section 4.2).
- A known gap — the REST/S2S dispatch path skipping the agent-refresh gate that WebSocket already
  has — is fixed (Section 4.3).

## 2. Current state (short version)

- A workspace has one flat `Marketplaces: string[]` field. No per-plugin selection exists.
- Sandbox sessions are cached per `(workspaceId, callerAppId)`, not per plugin selection.
- The gateway's sandbox-create contract accepts marketplace aliases only.
- The marketplace preview (used for the read-only browser) already returns nested
  marketplace → plugin → skills/agents, but this is discovery data, not proof the gateway can
  filter by plugin.
- There is no optimistic-concurrency field on the workspace model or store today.
- WebSocket dispatch already re-checks "is my agent still on the live session" before each
  message. The REST/S2S dispatch path does not call the same check.

Full file:line evidence is in Section 4.

## 3. Goals and non-goals

### Goals
- Let a user select a subset of plugins, drawn from any enabled marketplace, per workspace.
- Keep provenance unambiguous (`{marketplace, plugin}`), never a bare plugin name.
- Restart the workspace's live sandbox(es) immediately and safely when selection changes.
- Never lose or orphan a live session during a migration: each partition either moves to the new
  selection or is left completely untouched, decided by a per-partition compare-and-swap against the
  cache slot observed at snapshot time. The batch itself is **not** atomic across partitions — a
  reader can observe one partition already migrated and another still on the old selection (Section 7
  step 6b) — but no session is ever silently dropped. (A process crash mid-swap is a separate,
  narrower case — see Section 7 step 6b — recoverable, not silently inconsistent, but not
  "atomic" in the transactional sense.)
- Never silently claim plugin isolation the gateway cannot actually enforce.
- Keep the UI change small: nested checkboxes in the existing create/edit form.

### Non-goals
- No hot-reload of a sandbox without restart.
- No redesign of `MarketplaceBrowser.vue` (it stays read-only, unchanged).
- No cross-marketplace plugin renaming/aliasing scheme beyond `{marketplace, plugin}`.
- No change to how marketplaces themselves are enabled/disabled (that flow is unchanged; plugin
  selection is an additional, narrower layer on top of it).
- No attempt to migrate historical conversations retroactively; only the workspace's live sessions
  are affected going forward.

### Limitations — everything after the commit point is in-process only

Every step after persistence lives in process memory and is not journalled anywhere: the
per-workspace migration gate that serializes two updates to the same workspace, the bounded
post-commit retirement grace (Section 7 step 7c), and the single reconcile pass (Section 7 step 2).

If the process crashes between `_store.UpdateAsync` committing and retirement/reconcile finishing,
**the persisted selection is still correct** — that write already landed and is durable — but the
sandbox side can be left in one of two states:
- an orphaned gateway container: a session nothing references anymore and nothing will ever delete;
  or
- a session still published in the registry under the **old** plugin set, which stays that way until
  something recreates it (the gateway-404 recreate path of Section 4.2, or the next process start,
  which begins with an empty session cache).

There is **no persisted work queue and no crash recovery** for these steps, and adding one is out of
scope for this spec. The blast radius is bounded and non-silent in the persisted layer: a leaked
container costs resources until it is reaped by hand, a stale-plugin session serves the previous
selection until it is recreated. Neither corrupts the stored workspace. Operators recovering from a
crash mid-migration should treat orphaned sandbox containers as expected debris rather than as
evidence of a bug.

## 4. Verified current behavior (file:line)

All paths are under `B:\sources\LmDotnetTools\`.

### 4.1 Workspace model — flat marketplace list only

`samples/LmStreaming.Sample/Models/Workspace.cs`:
- `Workspace.Marketplaces` — `IReadOnlyList<string>` (line 29). No plugin field. No revision field.
- `WorkspaceCreate.Marketplaces` — nullable list, null treated as empty (lines 62-65).
- `WorkspaceUpdate.Marketplaces` — replacement list, no revision/ETag parameter (lines 71-77).

`samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs` — `CreateAsync`/`UpdateAsync` take no
concurrency token (lines 33, 44). Confirms: **no optimistic concurrency exists today.**

### 4.2 Session partitioning and `WorkspaceRef`

`src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs`:
- `WorkspaceRef` record — `Id`, `DirectoryRelPath?`, `Marketplaces?` (lines 55-58).
- `SandboxEstablishedBinding` record — carries `WorkspaceRef`, credentials, `SessionId?`
  (lines 77-82).
- Sessions are cached by `(WorkspaceId, AppId)` (lines 209-212; rationale comment 136-141).
- `ResolveThreadWorkspaceSessionAsync` (lines 450-493) calls
  `GetOrCreateLiveSessionAsync(binding.WorkspaceRef, ...)` at line 491 — using the **captured**
  `WorkspaceRef` from the binding, not a freshly reloaded one.
- `GetOrCreateLiveSessionAsync` (lines 859-905): probes liveness (891); on a definitive 404,
  invalidates and recreates via `GetOrCreateSessionAsync(effectiveRef, ...)` at line 904 — reusing
  the **same** `effectiveRef` captured earlier. **This is the stale-`WorkspaceRef` bug.** If the
  workspace's marketplaces/plugins changed since the ref was captured, the recreated sandbox still
  uses the old selection.
- `IsSessionAliveAsync` (lines 907-963): only a definitive 404 counts as "not alive." Any other
  failure is treated as alive (fail-open on liveness — unrelated to the fail-closed plugin-capability
  rule in Section 8).
- `CreateSessionAsync` (lines 1076-1321): marketplace resolution (1112-1116) — workspace marketplaces
  override global config when non-empty, else falls back to `SandboxGatewayOptions.Marketplaces`.
  Request built (1125-1131) as `SandboxCreateRequest(workspaceRelPath, marketplaces, authProviders,
  network, discovery)` — **only `marketplaces: string[]`, no plugin field.**
- `DestroyWorkspaceSessionAsync(workspaceId, ...)` (lines 1336-1385): destroys **every** session
  cached for that workspace id, across **all** caller app ids (line 1341). Not active-run aware.
- `InvalidateSessionAsync` / `EvictSessionStateAsync` (lines 965-1006, 1008-1065): shared cleanup
  helpers used by both the liveness-eviction path and explicit destroy.

`samples/LmStreaming.Sample/Program.cs`: the pooled-agent factory builds a fresh `WorkspaceRef` from
the current store on **first** agent creation for a thread (lines 880-888:
`workspaceStore.GetAsync(effectiveWorkspaceId)` → `new WorkspaceRef(...)`). This confirms the stale
data is not in this factory — it is in the **registry's recreate path** (4.2 above), which reuses a
ref captured earlier rather than calling back into the store.

### 4.3 Agent-refresh gate exists, but only one dispatch path calls it

`src/LmAgentInfra/Agents/MultiTurnAgentPool.cs`:
- `EnsureCurrentAgentAsync(threadId, callerCredential?, ct, replace = true)` (lines 970-1057+)
  already exists. It compares the pooled agent's bound `SessionId` (994, 1001) to the registry's
  current live session id, and — if different — transactionally swaps in a fresh `AgentEntry`
  (1006-1049), but **never interrupts an active run**; an active run is "checked again before the
  next message" (doc comment, lines 966-968).
- `GetOrCreateAgent(...)` (lines 353-498) freezes the caller's credential `AppId` at first creation
  and throws `SandboxCredentialConflictException` on a later mismatch (doc comment 379-386).

Only `samples/LmStreaming.Sample/WebSocket/ChatWebSocketManager.cs` calls
`EnsureCurrentAgentAsync` — once on connect (line 177) and once per inbound message with
`replace: false` (line 897).

`samples/LmStreaming.Sample/Controllers/ConversationsController.cs`'s `SendMessage` action (REST/S2S
path) calls **plain** `agentPool.GetOrCreateAgent(...)` at line 687 and never calls
`EnsureCurrentAgentAsync`. **This confirms the research finding exactly**: REST/S2S can keep using a
pooled agent that is bound to an old, replaced sandbox session, while WebSocket already self-heals.

### 4.4 Workspace CRUD and marketplace compatibility (unchanged surface, extended below)

`samples/LmStreaming.Sample/Controllers/WorkspacesController.cs` (full file read):
- `POST /api/workspaces` and `PUT /api/workspaces/{id}` call
  `WorkspaceCatalogCompatibilityService.ValidateForMutationAsync` before persisting, and
  `EvaluateAsync` to build the response view.
- Error mapping already established: `UnsupportedWorkspaceMarketplacesException` → 400
  `unsupported_marketplaces`; `WorkspaceGatewayCatalogUnavailableException` → 503
  `gateway_catalog_unavailable`; `WorkspaceCatalogCorruptException` → 503
  `workspace_catalog_unavailable`.

`samples/LmStreaming.Sample/Controllers/MarketplacesController.cs` (full file read): single `GET`,
proxies the gateway's read-only preview, degrades to 503 `marketplace_gateway_unavailable`. No
mutation endpoint.

`samples/LmStreaming.Sample/Models/MarketplaceCatalog.cs` (full file read): `CatalogPlugin` has no
enable/disable state — purely informational nesting. **Confirms: catalog nesting is not a capability
signal.**

`samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue` (partially read, lines
1-120): `createMarketplaces`/`editMarketplaces` are flat `ref<string[]>` (lines 45, 49) driving
checkboxes sourced from `GET /api/marketplaces` (lines 56-71). The selector locks to a read-only
badge once `lockedWorkspaceId` is set (props, lines 16-21; `isLocked`, line 73) — i.e. after the
workspace's first message, matching the research note.

### 4.5 Gateway capability negotiation — does not exist yet

No file in this repo exposes a gateway capability flag for plugin filtering. This must be added on
the gateway side (Section 8) and is labeled **proposed** everywhere below.

## 5. Wire and data contracts

Contracts confirmed against real code are marked **(existing)**. Everything else is **(proposed)**
— not yet implemented, not yet an agreed exact endpoint name, subject to final review during the
gateway PR.

### 5.1 Plugin identity (proposed, app + gateway shared shape)

```json
{ "marketplace": "community", "plugin": "pdf-tools" }
```

Display-only compact form: `pdf-tools@community`. The compact form is **never** parsed back into
its parts; it is derived from the structured object only.

### 5.2 Workspace persistence — new tri-state field (proposed, app repo)

```json
{
  "id": "ws_123",
  "name": "My Workspace",
  "directoryRelPath": "my-workspace",
  "marketplaces": ["core", "community"],
  "pluginSelection": [
    { "marketplace": "community", "plugin": "pdf-tools" },
    { "marketplace": "community", "plugin": "csv-tools" }
  ],
  "pluginsRevision": 4,
  "isSystemDefined": false,
  "createdAt": 0,
  "updatedAt": 0
}
```

Tri-state semantics of `pluginSelection`:
- **Absent / `null`** — legacy behavior, all plugins in the selected marketplaces are available.
  Existing workspaces (today's `Workspace.Marketplaces`-only shape) deserialize this way — no
  migration needed.
- **`[]`** — intentionally zero plugins enabled.
- **Non-empty** — the requested subset. The gateway resolves dependencies and returns the effective
  set (Section 5.3); the workspace persists the **requested** set, not the resolved one.

`pluginsRevision` is new: an optimistic-concurrency counter, since none exists today (Section 4.1).
`WorkspaceUpdate` must carry the revision it read; a mismatch is rejected (Section 7).

### 5.3 Sandbox create request (proposed, gateway repo)

Extends the existing `marketplaces: string[]` field (Section 4.2) with an optional `pluginSelection`
array using the same tri-state rule:

```json
{
  "workspaceRelPath": "my-workspace",
  "marketplaces": ["core", "community"],
  "pluginSelection": [
    { "marketplace": "community", "plugin": "pdf-tools" }
  ]
}
```

Gateway response adds a resolution block:

```json
{
  "sessionId": "sess_abc",
  "pluginResolution": {
    "supported": true,
    "requested": [
      { "marketplace": "community", "plugin": "pdf-tools" }
    ],
    "effective": [
      { "marketplace": "community", "plugin": "pdf-tools" },
      { "marketplace": "community", "plugin": "pdf-tools-deps" }
    ],
    "failed": []
  }
}
```

`pluginResolution.requested` mirrors the request's `pluginSelection` tri-state exactly rather than
collapsing it to a list: **`null`** when the request's `pluginSelection` was absent/`null` (all
plugins — there is no finite requested set to echo), **`[]`** when the request explicitly selected
zero plugins, and the requested list itself for an explicit subset (as in the example above).
`pluginResolution.effective` is always a concrete list — the actual resolved plugin set for the
session, after dependency expansion — regardless of what `requested` was.

`pluginResolution.supported: false` means the gateway build in use cannot filter by plugin at all;
the app must fail closed (Section 8) rather than silently create an all-plugins sandbox.

An unknown `{marketplace, plugin}` pair (an id that does not exist under that marketplace) is
rejected as a 400 failure of the whole create request — no `pluginResolution` body is returned.
The `failed` array above is reserved for ids that exist but could not be resolved for another
reason (e.g., a dependency that itself failed to install); it never contains unknown ids.

### 5.4 Gateway capability signal (proposed, gateway repo)

The capability signal is added to the existing marketplace preview response
(`GET /api/v1/marketplaces/preview`, proxied today by `MarketplacesController`) — this exact
endpoint is settled, not an open question; both the gateway and app implementation plans build
against it:

```json
{
  "capabilities": { "pluginFiltering": true },
  "selected": ["core", "community"],
  "marketplaces": [ /* existing nested shape, unchanged */ ]
}
```

The app must treat a missing `capabilities.pluginFiltering` field the same as `false` (fail closed —
never assume support from silence).

### 5.5 Workspace API error additions (proposed, app repo)

New codes alongside the existing `unsupported_marketplaces` / `gateway_catalog_unavailable`
(Section 4.4):
- `unsupported_plugins` (400) — a listed plugin does not exist under its marketplace.
- `gateway_plugin_filtering_unsupported` (503) — gateway capability check returned `false`, or was
  unconfirmed; the mutation is rejected rather than silently downgraded to all-plugins.
- `workspace_revision_conflict` (409) — `pluginsRevision` on the update did not match the stored
  value.
- `sandbox_restart_timeout` (503) — the bounded wait for active runs to idle expired; no selection
  or sandbox state changed.
- `sandbox_replacement_failed` (502) — prepare phase failed for at least one live partition; no
  selection or sandbox state changed.

One rejection deliberately gets **no new code**: a plugin-selection update targeting a
**system-defined workspace** (the seeded built-in `default`). It keeps the exact 400 the store
already produces today — `InvalidOperationException("Cannot update system-defined workspace
'{id}'.")` from `FileWorkspaceStore.cs:145` / `:161`, mapped by the trailing `catch` at
`WorkspacesController.cs:240`. Status, message, and body are byte-identical to today's. What changes
is only *when* it is raised: the orchestrator raises it before any sandbox work (Section 7 step 1b)
instead of letting the flow reach the store, so the side effects disappear while the response does
not move.

## 6. Component responsibilities

| Component | Repo | Responsibility |
|---|---|---|
| Sandbox gateway (Rust) | `SandboxedOstoolsMcpServer` | Accept `pluginSelection` on create, resolve dependencies, report requested/effective sets, expose a capability signal; reject the whole create request with a 400 when it references an unknown plugin id (distinct from resolution failures reported via `pluginResolution.failed`). |
| `Workspace` model + `IWorkspaceStore` | `LmDotnetTools` (app) | Persist tri-state `pluginSelection` + `pluginsRevision`. |
| `WorkspaceCatalogCompatibilityService` | app | Extend validation to check plugin ids against the live catalog (not just marketplace aliases), and to read the gateway capability signal. |
| `WorkspacesController` | app | Accept `pluginSelection`/`pluginsRevision` on create/update; map new error codes (Section 5.5). |
| `SandboxSessionRegistry` | app | Implement the prepare-then-replace flow (Section 7); fix the stale-`WorkspaceRef` recreate path (4.2) to reload current workspace config first. |
| `MultiTurnAgentPool` | app | No new public surface; `EnsureCurrentAgentAsync` is reused as-is. |
| `ConversationsController` (REST/S2S) | app | Call `EnsureCurrentAgentAsync` the same way `ChatWebSocketManager` already does, before dispatching a message. |
| `WorkspaceSelector.vue` | app (Vue) | Add nested plugin checkboxes under each selected marketplace, in the existing create/edit form. |
| `MarketplaceBrowser.vue` | app (Vue) | Unchanged — stays read-only. |

## 7. Prepare-then-replace flow (exact steps)

Triggered when a workspace's `pluginSelection` (or `marketplaces`) selection changes via
`PUT /api/workspaces/{id}`. Owned by the app repo, primarily in
`SandboxSessionRegistry` + `WorkspacesController`.

1. **Validate, and reject everything decidable from the request alone.** All three checks below run
   before a single gateway call and before any snapshot.
   a. **Catalog + capability.** Check the new selection against the live catalog (plugin ids exist
      under their marketplace) and against the gateway capability signal (Section 5.4). Fail the
      request with the Section 5.5 codes before touching any state if validation fails.
   b. **System-defined workspace.** A plugin-selection update targeting a system-defined workspace —
      the seeded built-in `default` — is rejected here: **after** 1a's catalog/capability
      validation, and **before** the revision pre-check (1c), the snapshot (step 2), the idle wait
      (step 3), and candidate creation (step 4).

      *Why this position and not later.* The store already refuses the write — the only two
      system-defined guards that exist are `FileWorkspaceStore.cs:145` and `:161`, both
      `InvalidOperationException("Cannot update system-defined workspace '{id}'.")`, surfaced as a
      400 by the trailing `catch` at `WorkspacesController.cs:240`. But that refusal lands *inside*
      step 6a, after the flow has already waited for idle and created **real** gateway sessions,
      which the abort path then has to tear down again. The seeded default carries
      `PluginsRevision = 0`, so such a request passes validation and the revision compare-and-swap
      cleanly and reaches the gateway. The resulting failure is also unstable: with a busy run the
      request times out first and returns 503 `sandbox_restart_timeout`; with a stale revision it
      returns 409 — three different answers to one request that is permanently invalid no matter
      what the sandbox or the revision counter is doing.

      *Precedence is deliberate.* 1a still runs first, so a request that is both against the default
      workspace **and** names an unknown plugin still returns 400 `unsupported_plugins` with its
      existing `code` and payload. This early-out changes nothing about that response.

      *One rule, one implementation.* The "is this workspace system-defined?" predicate is extracted
      into a **single shared helper**, called by three sites: this early-out and both existing store
      guards (`FileWorkspaceStore.cs:145`, `:161`). Same exception type, byte-identical message,
      still 400 — the early-out **relocates** the existing rejection, it does not add a second rule.
      The in-code comment beside the revision check in the orchestrator already states the reason:
      keeping two copies of one rule is precisely how the marketplace-resolution bug happened.
   c. **Revision pre-check.** Compare the caller-supplied `pluginsRevision` (Section 5.2) against the
      stored value and reject a mismatch with 409 `workspace_revision_conflict` before any gateway
      work. The store repeats this check atomically with the write at step 6a and remains the
      authority — that check is atomic with the write, this one is only an early-out — but without
      it a doomed request would first build candidate sessions it then has to clean up. Both sites
      call the same shared rule, for exactly the reason spelled out in 1b.
2. **Snapshot — bounded settle, plus exactly one post-commit reconcile pass.** List every live
   session partition keyed to this `workspaceId` (all `(workspaceId, callerAppId)` entries — same
   enumeration `DestroyWorkspaceSessionAsync` already does at `SandboxSessionRegistry.cs:1341`), and
   the retained credential for each.

   A purely synchronous, completed-only snapshot is not sufficient.
   `SnapshotPluginSelectionPartitions` (`SandboxSessionRegistry.PluginSelection.cs:61-94`) skips any
   cache slot that is `!IsValueCreated || !IsCompletedSuccessfully` — that is, every session whose
   creation is still in flight. A creation that completes one millisecond after that filter runs is
   invisible to the entire migration: it never gets a candidate, is never swapped, is never retired,
   and it keeps serving the **old** plugin set indefinitely. Nothing afterwards distinguishes it
   from a correctly migrated partition, so the defect is not even diagnosable after the fact. That
   silent-drop is the failure this step is designed against.

   So the snapshot is a **bounded async settle**: `SnapshotPluginSelectionPartitionsAsync`
   (`SandboxSessionRegistry.PluginSelection.cs:139-210`) wraps the synchronous
   `SnapshotPluginSelectionPartitions` and returns a `PluginSelectionSnapshot`
   (`SandboxSessionRegistry.PluginSelection.cs:112-115`) carrying two things: the partitions that
   settled, and the keys that did not (`Unsettled`). Two properties are load-bearing:
   - The settle budget is **shared across all entries, not per entry**. Total added latency is
     bounded by one budget regardless of how many creations are in flight, and one wedged creation
     cannot hold every other partition hostage behind its own timeout.
   - Anything that settles inside the budget is folded into the normal migration and is thereafter
     indistinguishable from a partition that was already complete at call time.

   **`Unsettled` is an explicit set difference, not a filter over the pre-wait pending list.** The
   method captures the settled partitions again, after the wait, and computes `Unsettled` as
   "every key that was pending before the wait minus every key present in that post-wait capture" —
   it does not simply filter the list of keys that were pending *before* the wait started down to
   the ones still incomplete. The distinction matters because a creation can **start** during the
   wait itself: such a key was never in the pre-wait pending list (it did not exist yet), so a
   filter over that list would place it in neither the settled set nor `Unsettled` — silently
   dropping it from both, which is precisely the silent-drop failure this whole step exists to
   prevent. The set-difference formulation has no such gap: whatever isn't captured after the wait
   is unsettled, regardless of when its creation began.

   Whatever did not settle is handled after persist and swap (steps 6a/6b) by **exactly one**
   reconcile pass. It re-enumerates the workspace's partitions and, for any partition whose session
   is still on the old selection, uses only the primitives the main flow already has — create a
   candidate, then the same per-partition compare-and-swap — under four rules:
   - **Fail closed via `ReflectsPluginSelection`.** `ReflectsPluginSelection`
     (`SandboxSessionRegistry.PluginSelection.cs:240-256`) is the single fail-closed comparison used
     to decide whether a session already carries a given selection: a session whose
     `PluginResolution` is missing cannot *prove* it is current, so it is treated as stale and
     migrated. The comparison is **tri-state-aware**: `null` (legacy "all plugins") matches only
     `null`, `[]` (explicitly none) matches only `[]`, and the two must never cross-match —
     collapsing them would make a migration *to* an explicit empty selection read as "already
     current" against a legacy `null` session, silently skipping it. A non-empty selection is
     compared as an order-and-duplicate-insensitive structural set over `(marketplace, plugin)`
     pairs, against `Requested` (not `Effective`). Re-migrating an already-current session costs one
     sandbox create; leaving a stale one published silently violates the selection the user just
     saved.
   - **On a lost compare-and-swap, reconcile retires ITS OWN candidate, never the winner.** The
     winner is published and serving traffic; the loser's candidate is the session nothing
     references.
   - **One pass, no retry loop, with a logged residual.** A partition still unsettled after that
     single pass is never retried — a retry loop here would reintroduce exactly the unbounded wait
     that step 3's timeout and step 7c's grace exist to prevent — but it is not silently dropped
     either: any owed partition key still absent from a fresh, zero-budget re-snapshot taken at the
     start of the pass is logged as a single warning naming the workspace, the residual count, and
     the specific keys. Without that log there would be no trace anywhere that the persisted
     selection and a live session have diverged.
   - **Gated by `CommittedRevision`.** The pass only ever acts for the migration that scheduled it.
     `PostCommitWork.CommittedRevision` records the workspace's `pluginsRevision` at the moment step
     6a's persist committed; the pass re-reads the workspace and returns immediately if it is gone or
     its `PluginsRevision` no longer matches `CommittedRevision` — meaning a later migration has
     already superseded this one. That revision re-read happens **before** the zero-budget
     re-snapshot, and nothing is awaited between that re-snapshot and each partition's
     compare-and-swap: an await in that gap would let a newer writer republish a partition this pass
     then judges against a compare-and-swap witness it no longer holds.

   Reconcile runs after the commit point, so it is best-effort by construction: any failure inside
   it is logged and **never** turns a committed, persisted update into a failed request (see the
   retire-eventually note under step 7).
3. **Wait for idle.** For every conversation bound to an affected partition with an active run,
   wait for it to go idle. The wait is **bounded** and **cancellation-aware**. On timeout, stop and
   return `sandbox_restart_timeout`; **no** selection or sandbox state has changed at this point.

   **How "bound to an affected partition" is discovered** is load-bearing, not incidental — it
   decides whether this wait can be silently skipped. Discovery goes through a single registry
   method, `GetBoundThreads(sessionId)` (`SandboxSessionRegistry.cs:2104-2123`) — the orchestrator
   calls it **alone** and never separately calls `GetThreads` and unions the two itself.
   `GetBoundThreads` computes that union **internally**, seeding a set from `_sessionThreads` (via
   the existing `GetThreads(sessionId)`, `SandboxSessionRegistry.cs:2072-2083`, which reads
   `_sessionThreads` and nothing else) and then adding every `SandboxEstablishedBinding` whose
   `SessionId` matches that partition's session.

   The union is required, not defensive breadth. `_sessionThreads` is populated only when sub-agent
   options are present, whereas the established binding is documented as "the ONLY authoritative
   signal that a conversation has an established sandbox workspace" and is **deliberately kept
   separate** from `_sessionThreads` (`SandboxSessionRegistry.cs:86-93`). A conversation known only
   through its binding is therefore absent from `GetThreads`, and an unknown thread reads as
   **idle** — the agent pool returns `IsInProgress: false` for any thread id it has no entry for
   (`MultiTurnAgentPool.cs:1105-1113`). If this flow read `GetThreads` directly instead of going
   through `GetBoundThreads`, such a conversation would never stall the wait, the wait would return
   "idle" vacuously, and step 7 would tear that conversation's session down in the middle of its
   run — which is exactly why `GetBoundThreads` is `internal` and the only entry point this flow
   uses; reading only `_sessionThreads` would make the whole bounded wait a no-op for precisely the
   conversations it exists to protect.
4. **Prepare.** For each snapshotted partition, create a **new** sandbox session with the new
   selection, using that partition's retained credential. This is a **real side effect** — a new
   sandbox session actually exists at the gateway from this point on — but it is an inert one: old
   sessions stay live and reachable throughout, and no conversation is ever pointed at a candidate
   before it commits (step 6b). Collect gateway `pluginResolution` per partition.
5. **Abort on prepare failure.** If any candidate session fails to create (network, gateway
   rejection, capability regression), delete every candidate created so far in step 4. Persisted
   workspace state and all live registry entries are left exactly as they were before step 1; old
   live sessions are untouched throughout. Return `sandbox_replacement_failed` with per-partition
   detail.
6. **Commit.** If every candidate succeeded:
   a. Persist the new `pluginSelection` + incremented `pluginsRevision`, with an optimistic-
      concurrency check comparing the caller-supplied `pluginsRevision` (Section 5.2) against the
      store's current value at commit time. **If this check fails** (409
      `workspace_revision_conflict`) **or persistence itself fails**, this is also an abort: every
      candidate created in step 4 is deleted before the error is returned, exactly as in step 5 —
      the candidates were real sessions, so a rejected commit must clean them up, not just the
      persisted state. Old live sessions remain unchanged throughout this path too.
   b. Only once step 6a's persist has succeeded: swap each partition's registry entry to point at
      its new session, one partition at a time, via a compare-and-swap
      (`ConcurrentDictionary.TryUpdate`) against the exact cache slot observed at snapshot time — no
      lock is taken, and no reader of the registry is ever blocked. The batch itself is deliberately
      **not** atomic: a reader resolving partition B while this loop sits between B and C observes B
      already migrated and C still on the old selection. That is acceptable — both sessions are live
      and serve the same workspace, and any single caller only ever observes its own partition, which
      flips in one reference write. What the compare-and-swap does guarantee is that no session is
      ever lost: if a partition's slot no longer holds the snapshotted entry — most commonly because
      the gateway-404 recreate path (Section 4.2) invalidated the slot and republished a brand-new
      session while this candidate was being created — that partition is skipped and its candidate is
      returned to the caller, who must retire it; it is a live gateway session nothing in the cache
      references anymore. Step 7 then retires the old session only for the partitions that actually
      committed, leaving a skipped partition's (still-current) session untouched. If the process
      crashes mid-loop, the untouched old entries are still live and valid (their sessions aren't
      retired until step 7), so a crash yields an interrupted-but-live state, not a broken one — the
      next request, or the lazy-recreate path (Section 4.2 fix), completes the migration using the
      selection already persisted in step 6a.

**Everything in step 7, plus step 2's reconcile pass, is dispatched off the HTTP request via an
injectable `postCommitScheduler`, not run inline before returning.** The default implementation
schedules this work on a background scheduler (`Task.Run`), so the request returns as soon as step
6b's swap commits, without waiting for retirement grace, teardown, or reconcile to finish. The
scheduler is an explicit constructor seam — not a hard-coded `Task.Run` — precisely so tests can
substitute an inline/synchronous scheduler and assert the post-commit behavior deterministically,
instead of racing real background timing.
7. **Retire, per outcome.** The swap in step 6b returns the candidates it could *not* commit, which
   partitions the retirement into two disjoint sets — both are retired, every time:
   a. **Committed partitions** retire their **old** session — invalidate/evict
      (`InvalidateSessionAsync`/`EvictSessionStateAsync`, `SandboxSessionRegistry.cs:965-1065`) and
      ask the gateway to destroy it, subject to the bounded post-commit grace in 7c. This is
      deliberately **committed-only**: a partition whose compare-and-swap lost still has its old
      session referenced by the cache and serving live traffic, so retiring it here would destroy
      the session that partition is still using.
   b. **Uncommitted candidates** — the ones step 6b handed back — retire the **new** session
      instead, and **immediately**: no grace applies to them. Nothing references them at any point —
      the cache slot moved on (typically the gateway-404 recreate path of Section 4.2 republished a
      fresh session mid-flight), so no conversation can be mid-run against a candidate. Delaying
      their teardown would only widen the window in which a leaked container exists.
   c. **Bounded post-commit retirement grace (committed partitions only).** Step 3's idle wait
      happens *before* step 4, and step 4 is sequential gateway I/O measured in seconds. Nothing
      re-checks idleness between them, so a run can legitimately start after the wait passed and
      still be in flight when the swap commits. Retiring the old session at that instant kills a
      live run.

      So a committed partition's **old** session is not destroyed the moment the swap lands. It
      first gets a **bounded** idle grace, re-checking the same union of bound conversations step 3
      used. Three properties are deliberate:

      - **The grace observes `CancellationToken.None`, never the caller's token.** Once persistence
        has committed, a caller who disconnects or cancels must not be able to skip deletion of a
        superseded session — that would leak a gateway container for the lifetime of the process.
        This matches the existing teardown path, which already runs entirely under
        `CancellationToken.None` and swallows every failure
        (`SandboxSessionRegistry.PluginSelection.cs:457-473`).
      - **On expiry, retire anyway.** When the grace budget is exhausted with a run still in
        progress, log a warning naming the session id and the elapsed wait, then destroy the session
        regardless. A permanently-busy conversation must never be able to pin a container forever;
        an unbounded grace would convert "a run that never ends" into "a container that is never
        reclaimed".
      - **Honest residual: the grace narrows the window, it does not close it.** If a run outlasts
        the grace, its session **is** torn down underneath it, and that run will fail. The window is
        smaller than retiring immediately and it is bounded rather than unbounded — but it is not a
        guarantee, and this spec does not claim one. Closing it entirely would require either an
        unbounded wait (a leak by another name) or the ability to interrupt a run in flight, which
        `EnsureCurrentAgentAsync` deliberately does not do (`MultiTurnAgentPool.cs`, doc comment
        966-968). A bounded window with a logged, attributable failure is the chosen trade.

      **Retirement runs OUTSIDE the per-workspace migration gate.** The gate exists to stop two
      migrations of the same workspace interleaving; holding it across a multi-second grace window
      would make an unrelated later edit to that workspace queue behind some other conversation's
      teardown. Post-commit cleanup blocks nothing.
   **A cleanup failure in either set does not revert the swap** — the committed partitions' new
   sessions are already authoritative per step 6b; cleanup is best-effort logging, not a rollback
   trigger.

   **After step 6a's persist, the contract is retire-eventually, not rollback.** The rollback
   language in steps 5 and 6a is exact — and exact *only* while the flow is still before
   `_store.UpdateAsync`. Once persistence and the swap have both committed there is no rollback and
   no compensating action anywhere in this design: every remaining obligation — the 7c grace, both
   retirement sets, and step 2's single reconcile pass — is **best-effort and eventually
   consistent**. Their failures are logged, never surfaced to the caller, because the update the
   caller asked for has already succeeded: the persisted selection is the new one, and the registry
   already points at the new sessions. Reporting a cleanup failure as a request failure would tell
   the caller to retry an update that already committed. What "eventually" does *not* promise is
   also stated plainly: if the process dies before these steps finish, they simply do not run —
   there is no queue that resumes them (see Section 3, "Limitations").
8. **Lazy agent rebuild.** Do not proactively touch pooled agents. Each conversation's next
   dispatch (WebSocket already does this via `EnsureCurrentAgentAsync`; REST/S2S gains it per
   Section 4.3/6) notices its bound session id no longer matches the registry's live session and
   rebuilds transactionally, without interrupting a run already in flight.

This directly implements the approved decision: "if replacement creation fails, roll back" —
here "rollback" means every candidate is deleted and nothing persisted or swapped survives the
failure, whether the failure happens at step 5 (prepare) or step 6a (commit/persist). Steps 1-3
have no side effects at all; step 4 does create real sandbox sessions, but they stay inert
candidates — unreachable by any conversation — until step 6b's swap, and both failure paths above
tear every candidate down before returning. Old live sessions are never touched until step 7, well
after the point of no return.

**Scope of the paragraph above: the pre-commit phase only.** Every guarantee it states is bounded by
`_store.UpdateAsync` succeeding. Past that line the design switches contracts — see the
retire-eventually note under step 7 — and nothing in this spec rolls a committed migration back.

## 8. Concurrency, active-run semantics, and fail-closed behavior

- **Active-run wait** (step 3) applies per affected conversation, not per workspace as a whole; a
  conversation with no active run proceeds immediately.
- **Discovery of affected conversations goes through `GetBoundThreads` alone, and its internal
  union is the correctness property** (step 3): `GetBoundThreads` (`SandboxSessionRegistry.cs:2104-2123`)
  seeds a set from `GetThreads` (`_sessionThreads`, `SandboxSessionRegistry.cs:2072-2083`) and adds
  every established binding whose `SessionId` matches — the orchestrator calls `GetBoundThreads`
  and never separately calls `GetThreads` and unions the two itself; doing so would be a bug. A
  conversation present only in the binding map is absent from `GetThreads`, and an unknown thread
  is reported idle (`MultiTurnAgentPool.cs:1105-1113`) — so calling `GetThreads` alone would pass
  the wait vacuously and let the retirement step destroy a session mid-run. The two maps are
  deliberately separate (`SandboxSessionRegistry.cs:86-93`); `GetBoundThreads` is what folds them
  together, and it is the only method this flow calls for discovery.
- **Idleness is re-checked after the commit, not only before the prepare** (step 7c): candidate
  creation is seconds of sequential gateway I/O, so a run can start after step 3 passed. Committed
  partitions' old sessions therefore get a bounded post-commit grace before teardown. The grace runs
  under `CancellationToken.None` — a cancelled caller must not be able to skip deletion — and on
  expiry the session is retired anyway, with a warning naming the session and the elapsed time. A
  run that outlasts the grace **is** interrupted; the grace bounds that window rather than removing
  it.
- **In-flight session creations are settled, bounded, then reconciled exactly once** (step 2): the
  completed-only snapshot filter (`SandboxSessionRegistry.PluginSelection.cs:61-94`) would otherwise
  silently strand a session that finishes creating a moment later on the old plugin set forever. The
  settle budget is shared across entries, so one wedged create cannot block the rest; reconcile is a
  single pass, fails closed when a session's `PluginResolution` cannot prove it is current, and
  retires its own losing candidate rather than the compare-and-swap winner.
- **Bounded + cancellation-aware**: the wait uses a fixed timeout and observes the request's
  cancellation token; a caller-cancelled request or a timeout both leave state untouched (same
  guarantee, different trigger). This applies to the **pre-commit** wait (step 3) only — the
  post-commit grace deliberately ignores the caller's token, per step 7c.
- **Concurrent workspace edits** are serialized by `pluginsRevision` (Section 5.2, 5.5, 7.6a) and,
  in-process, by a per-workspace migration gate so two migrations of the same workspace never
  interleave. Two concurrent `PUT`s: the second to commit sees a revision mismatch and is rejected,
  not silently overwritten — and per Section 7 step 6a, its own already-created candidate sessions
  are deleted before the rejection is returned, so a losing `PUT` never leaves a dangling sandbox
  session behind. Retirement (step 7) runs **outside** that gate, so a grace window can never block
  a later edit to the same workspace.
- **Fail-closed on capability**: if the gateway capability signal (Section 5.4) is missing, `false`,
  or the probe itself fails, plugin-scoped mutations are rejected with
  `gateway_plugin_filtering_unsupported`. Marketplace-only workspaces (legacy tri-state "all
  plugins") are unaffected — this restriction applies only when a caller supplies a non-null
  `pluginSelection` list.
- **Destroy is no longer active-run-blind for this path**: `DestroyWorkspaceSessionAsync` itself is
  unchanged (still used for explicit workspace deletion), but the new replace flow never calls it
  directly against a live partition without first passing step 3's idle wait.

## 9. Backward compatibility and rollout

- Existing workspaces have no `pluginSelection` field on disk → deserializes as `null` → legacy "all
  plugins in selected marketplaces" behavior, byte-for-byte what happens today. No migration script
  needed.
- Existing `marketplaces`-only create/update calls are unaffected; `pluginSelection` is purely
  additive.
- Gateway repo ships first: add `pluginSelection` field acceptance + capability signal + dependency
  resolution, deploy, and verify against a real running gateway before the app repo PR merges. Until
  the gateway ships, the app repo's plugin UI and validation stay behind the capability check
  (Section 8) and no-op to today's behavior.
- Rollout order: (1) gateway PR merged + deployed + verified, (2) app repo PR (model, store,
  registry replace flow, REST/S2S gate fix, UI checkboxes), (3) stale sandbox lifecycle docs
  updated (Section 11).

## 10. Security

- Plugin selection changes go through the same `WorkspaceCatalogCompatibilityService` validation
  gate as marketplace changes today — no new unauthenticated surface.
- Credential handling in the prepare-then-replace flow reuses the existing per-partition retained
  credential (`SandboxCredential`) already tracked by the registry; no credential is ever widened
  across partitions.
- `EnsureCurrentAgentAsync`'s existing `SandboxCredentialConflictException` check
  (`MultiTurnAgentPool.cs`, doc comment 379-386) is unchanged and still applies on the REST/S2S path
  once that path starts calling it — cross-actor resume protection (issue #153) is preserved, not
  weakened.
- Fail-closed capability handling (Section 8) is itself a security property: it prevents the app
  from claiming plugin-level isolation the gateway cannot actually enforce.

## 11. Documentation changes (app repo)

- Update the stale "sandbox is globally app-wide" narrative doc(s) referenced in the research (the
  registry has been `(workspaceId, callerAppId)`-partitioned since before this change; this spec
  does not alter partitioning, only what is created per partition).
- Document the new tri-state `pluginSelection` field and `pluginsRevision` next to the existing
  `Marketplaces` documentation in the workspace API reference.
- Document the REST/S2S `EnsureCurrentAgentAsync` call as a fix, cross-referencing issue tracking
  for the previously-missing parity with WebSocket.

## 12. Tests

### Gateway repo (`SandboxedOstoolsMcpServer`)
- Sandbox create with `pluginSelection: null` → legacy all-plugins behavior unchanged.
- Sandbox create with `pluginSelection: []` → zero plugins active, marketplaces still resolve for
  discovery only.
- Sandbox create with a non-empty subset → effective set includes resolved dependencies.
- Sandbox create referencing an unknown `{marketplace, plugin}` pair → rejected (400-equivalent),
  not silently dropped.
- Capability signal reports `pluginFiltering: false` on a build without filtering support.
- Effective-inventory endpoint/field reflects the actually-active plugin set, distinguishable from
  the requested set.
- Backward compatibility: an old-format create request (marketplaces only, no `pluginSelection` key)
  behaves identically to before this change.

### App repo (`LmDotnetTools`)
- Workspace persistence: `pluginSelection` tri-state round-trips (null/empty/subset); `pluginsRevision`
  increments on every successful plugin-affecting update.
- `PUT` with a stale `pluginsRevision` → 409 `workspace_revision_conflict`; store and live sessions
  unchanged.
- Plugin picker UI: selecting/deselecting a plugin under a marketplace updates the pending edit
  state; submit sends the structured `{marketplace, plugin}` list.
- Validation: unknown plugin id under a known marketplace → `unsupported_plugins` (400); gateway
  capability `false`/missing → `gateway_plugin_filtering_unsupported` (503).
- Active-run wait: an in-flight run delays the restart until idle; a bounded timeout with a run that
  never idles → `sandbox_restart_timeout`, and both persisted selection and live sessions are
  provably unchanged afterward.
- All-partitions coverage: a workspace shared by two `callerAppId`s — both partitions are prepared
  and swapped together, not just the editor's own partition.
- Candidate failure: force one of two candidate sessions to fail creation → both candidates are
  deleted, persisted selection and both live sessions are unchanged, `sandbox_replacement_failed`
  returned.
- Concurrency: two overlapping `PUT`s on the same workspace → exactly one commits; the other gets a
  revision conflict, never a silent overwrite.
- WebSocket + REST/S2S refresh parity: after a successful replace, both a WebSocket-driven
  conversation and a REST/S2S-driven conversation on the same workspace rebuild their agent on next
  dispatch — this closes the gap identified in Section 4.3.
- Stale-config reload: change a workspace's selection, force a 404-triggered recreate path, and
  assert the recreated sandbox uses the **current** selection, not the one captured when the binding
  was first established — this is the regression test for the Section 4.2 bug fix.
- System-defined early-out (Section 7 step 1b): a plugin-selection `PUT` against the built-in
  `default` workspace returns 400 with the same message as the store's existing guard, and
  **provably creates zero sandbox sessions** — assert the gateway saw no create call at all, not
  merely that the request failed. Both failure modes the early-out removes get their own case: with
  a run in flight the response is still that 400, never 503 `sandbox_restart_timeout`; with a stale
  `pluginsRevision` it is still that 400, never 409.
- System-defined precedence (Section 7 step 1b): a `PUT` against the `default` workspace that also
  names an unknown plugin returns 400 `unsupported_plugins` — validation ordering is unchanged by
  the early-out.
- Shared system-defined rule: the orchestrator's early-out and both `FileWorkspaceStore.UpdateAsync`
  guards produce the identical exception type and identical message (assert the strings match, so a
  future edit to one site cannot silently diverge).
- Discovery union (Section 7 step 3): a conversation bound to a partition **only** through
  `SandboxEstablishedBinding` (never registered in `_sessionThreads`) with a run in progress must
  stall the idle wait to `sandbox_restart_timeout`. This test fails RED against a `GetThreads`-only
  discovery — that is the whole point of it.
- Bounded settle (Section 7 step 2): a session whose creation completes *after* the synchronous
  snapshot filter would have run but *within* the settle budget is migrated in the initial batch,
  not left on the old selection.
- Shared settle budget: two in-flight creations where one never completes — the other still settles
  and migrates, and total wait stays within one budget rather than two.
- Single reconcile pass (Section 7 step 2): a session that completes after the settle budget is
  migrated by the one reconcile pass; a session that is *still* unsettled after that pass is left
  alone and logged as a residual, with no retry loop.
- Reconcile fail-closed: a session with a missing `PluginResolution` is treated as stale and
  migrated, not assumed current.
- Reconcile CAS loss: when reconcile's compare-and-swap loses, **its own** candidate is retired and
  the winner's published session survives untouched.
- Reconcile never fails the request: force reconcile to throw after a successful commit — the `PUT`
  still returns success with the new persisted selection, and the failure is logged.
- Reconcile residual log: a partition still absent from the zero-budget re-snapshot at the end of
  the single pass produces exactly one warning naming the workspace, the residual count, and the
  specific unreconciled keys — not silence, and not a retry.
- `CommittedRevision` supersession (Section 7 step 2): after this migration's persist commits, let a
  second migration on the same workspace commit before the first's reconcile pass runs — the first
  pass returns immediately without touching any partition, because `PostCommitWork.CommittedRevision`
  no longer matches the workspace's current `pluginsRevision`. Asserts the reconcile pass never acts
  on a workspace state it no longer owns.
- Off-request post-commit scheduling (Section 7, `postCommitScheduler`): substituting an inline
  scheduler makes retirement and reconcile run synchronously and deterministically assertable;
  separately, asserting the production default is the background scheduler proves the `PUT` response
  is never delayed by the retirement grace or the reconcile pass.
- Post-commit grace (Section 7 step 7c): a run that starts *after* the idle wait passed but before
  the swap delays retirement of the old session; once it goes idle within the grace, the old session
  is retired.
- Grace expiry (Section 7 step 7c): a run that never goes idle → the old session is retired **anyway**
  after the bounded grace, and a warning naming the session id and elapsed time is emitted. Assert the
  container is destroyed — a grace that leaks on a stuck run is the failure this bound exists to
  prevent.
- Grace ignores caller cancellation: cancel the caller's token after the persist commits — the old
  session is still destroyed. A cancelled caller must never be able to skip deletion.
- Uncommitted candidates retire immediately: a candidate handed back by a lost compare-and-swap is
  destroyed without waiting for any grace.
- Grace does not hold the migration gate: while one workspace's retirement grace is running, a
  second plugin-selection `PUT` on that same workspace proceeds rather than blocking behind it.

## 13. Acceptance criteria

- A workspace can select a subset of plugins across multiple marketplaces, persisted as structured
  `{marketplace, plugin}` pairs with tri-state semantics.
- Changing the selection restarts every live sandbox partition for that workspace, waiting for
  active runs to idle first; a partial/half-migrated outcome across independent partitions **is**
  observable (Section 7 step 6b's per-partition compare-and-swap, not a batch lock), but no session
  is ever lost or leaked — every partition either migrates to the new selection or is left
  untouched, and any candidate that could not be committed is retired — and even a process crash
  mid-swap leaves only an interrupted-but-live state that a subsequent request or lazy recreate
  completes, never silent corruption.
- A failed replacement leaves the workspace exactly as it was (selection and live sessions both
  unchanged) and reports which partition failed.
- The gateway's capability signal is checked before any plugin-scoped mutation; an unconfirmed or
  absent signal blocks the mutation rather than silently running all-plugins.
- REST/S2S dispatch calls the same agent-refresh gate WebSocket already calls.
- A plugin-selection update against a system-defined workspace is rejected **before** any sandbox
  session is created — same 400 and same message as the store's existing guard
  (`FileWorkspaceStore.cs:145`/`:161`), just raised earlier — and the answer no longer changes to a
  503 or a 409 depending on whether a run happens to be active or a revision happens to be stale.
  Catalog/plugin validation still precedes it, so `unsupported_plugins` (400) is unchanged.
- The system-defined predicate exists in exactly one place: one shared helper called by the
  orchestrator early-out and by both store guards. No second copy of the rule.
- The idle wait considers every conversation bound to an affected partition through **either**
  `_sessionThreads` **or** an established binding. A conversation visible only through its binding
  can stall the wait; it can no longer be treated as idle by omission and have its session destroyed
  mid-run.
- In-flight session creations are settled under a **shared, bounded** budget and, if they land too
  late for that, are handled by **exactly one** reconcile pass. No live session is left silently on
  the old plugin set with no record; a partition still unsettled after that pass is a logged
  residual, not an unbounded retry.
- Reconcile fails closed (a session that cannot prove its `PluginResolution` is current is
  re-migrated), retires only its own losing candidate, and can never turn a committed update into a
  failed request.
- The single reconcile pass is gated by `CommittedRevision`: it re-reads the workspace before acting
  and no-ops the moment the workspace is gone or its `pluginsRevision` no longer matches the
  revision this migration committed, so a pass scheduled by a superseded migration can never act on
  a newer one's partitions.
- Retirement and reconcile are dispatched off the request through an injectable
  `postCommitScheduler` (background by default), so the `PUT` response is never delayed by the
  post-commit grace or the reconcile pass, and tests can substitute an inline scheduler for
  deterministic assertions.
- After the persist commits, the contract is **retire-eventually, not rollback**: the grace, both
  retirements, and reconcile are best-effort and eventually consistent, and their failures are
  logged rather than returned. No committed migration is ever rolled back.
- A committed partition's old session is retired only after a **bounded** post-commit idle grace
  that runs under `CancellationToken.None`; on expiry it is retired anyway with a warning naming the
  session and elapsed time, so no permanently-busy run can leak a container. Uncommitted candidates
  are retired immediately, and retirement never holds the per-workspace migration gate. A run that
  outlasts the grace is still interrupted — this is a narrowed window, explicitly not a guarantee.
- The in-process limitation is documented, not implied: a crash between persist and the completion
  of retirement/reconcile can orphan a container or leave a stale-plugin session published, with no
  persisted queue and no recovery (Section 3, "Limitations").
- The stale-`WorkspaceRef` recreate bug (Section 4.2) is fixed: a 404-triggered recreate reloads
  current workspace configuration first.
- All tests in Section 12 pass in both repos, gateway PR merged/deployed/verified before the app PR.
- No visual redesign: the UI change is nested checkboxes in the existing create/edit form;
  `MarketplaceBrowser.vue` is untouched.

## 14. Open questions

None on the product/behavioral decisions: workspace scope, tri-state semantics, immediate restart,
wait-for-idle, gateway dependency resolution, no-partial-migration, lazy agent rebuild, capability
fail-closed, and prepare-then-replace were confirmed by the user during research (decision log:
`.claude/scratchpad/conversation_memories/lmstreaming-multi-marketplace-plugins/research.md`).

The wire-level details in Section 5 — field names (`pluginsRevision`), exact JSON shapes, and the
new error codes (Section 5.5) — are this spec's proposed elaboration of those decisions, not
independently user-confirmed; they remain **(proposed)** throughout Section 5, including the
app-repo-owned 5.2 and 5.5, same as the gateway-owned 5.3/5.4, and are open to review during
implementation and the gateway repo's own PR review.
