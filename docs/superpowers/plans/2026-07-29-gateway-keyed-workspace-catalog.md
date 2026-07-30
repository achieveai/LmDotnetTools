# Gateway-Keyed Workspace Catalog Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Isolate LmStreaming.Sample workspace catalogs by canonical sandbox gateway URL + process AppId, archive ambiguous legacy data safely, validate marketplace compatibility before persistence/session creation, and prevent the client from showing stale workspaces when the backend is unavailable.

**Architecture:** A versioned `GatewayWorkspaceCatalogIdentity` derives a SHA-256 filesystem key and validated manifest. A cross-process-safe migration/resolver chooses one scoped `FileWorkspaceStore`; a compatibility service enriches workspace API results and gates mutations/session recreation using the existing remote marketplace client. The Vue client consumes a gateway-aware envelope, clears stale state on API failure, and disables incompatible/unknown workspace execution.

**Tech Stack:** C#/.NET 8–9, ASP.NET Core, `System.Uri`, SHA-256, JSON file persistence, xUnit/FluentAssertions/Moq, Vue 3/TypeScript, Vitest, Playwright browser E2E.

## Global Constraints

- Implement on current `fix-as-we-go`; keep this feature completely separate from the plural-agent-control worktree/diff.
- Catalog identity is canonical `SandboxGatewayOptions.BaseUrl` + process-wide `SandboxGatewayOptions.AppId`; never per-request `SandboxCredential.AppId` and never AppKey.
- Hash material is exactly `gateway-workspace-catalog:v1\0<canonical-base-url>\0<AppId>`, SHA-256 UTF-8, lowercase hex.
- AppKey must never be read by identity code, stored, logged, hashed, or appear in tests/snapshots.
- Per-gateway layout is `workspaces/gateways/<hash>/{gateway.json,workspaces.json}`; legacy data is archived under `workspaces/legacy/` and never auto-imported.
- Migration uses a cross-process `FileShare.None` lock and pending/completed markers; it is atomic, idempotent, retry-safe, and never overwrites catalogs/archives.
- Marketplace drift is never silently mutated or ignored: incompatible records remain visible and block remote execution until explicitly corrected.
- Gateway unavailable means compatibility `unknown`; correctly scoped records remain visible, but new remote sessions/validated edits are disabled.
- Workspace API/backend failure clears client workspaces and selection; never retain another response's in-memory list.
- Session compatibility gate lives in LmStreaming.Sample `Program.cs` before `SandboxSessionRegistry.GetOrCreateLiveSessionAsync`; do not add catalog dependencies to shared LmAgentInfra or CodeReviewDaemon.
- Existing REST history remains readable; remote execution/recreation may be blocked with an actionable message.
- Follow RED→GREEN TDD. Use `dotnet format whitespace` as formatting gate.
- Do not commit/push without explicit user authorization. Do not add Co-Authored-By or AI signatures.
- Approved spec is `docs/superpowers/specs/2026-07-29-gateway-keyed-workspace-catalog-design.md` and is currently uncommitted.

---

## File Structure

### New server components

- Create `samples/LmStreaming.Sample/Persistence/GatewayWorkspaceCatalogIdentity.cs`
  - Canonical URI, versioned hash material/key, manifest record, equality validation.
- Create `samples/LmStreaming.Sample/Persistence/GatewayWorkspaceCatalogResolver.cs`
  - Directory resolution, manifest creation/validation, lock-file and crash-safe legacy migration.
- Create `samples/LmStreaming.Sample/Services/WorkspaceCatalogCompatibilityService.cs`
  - 30-second single-flight remote catalog cache, compatibility evaluation, mutation/session validation.
- Modify `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`
  - Strict corruption failure (no silent empty fallback); retain atomic scoped writes.
- Modify `samples/LmStreaming.Sample/Models/Workspace.cs`
  - API-only enriched records/envelope/gateway status/error DTOs; persisted `Workspace` shape remains unchanged.
- Modify `samples/LmStreaming.Sample/Controllers/WorkspacesController.cs`
  - Enriched list/get; compatibility-validated create/update and backward-compatible errors.
- Modify `samples/LmStreaming.Sample/Program.cs`
  - Resolve scoped store at startup, register compatibility service, gate session creation/recreation.

### Server tests

- Create `tests/LmStreaming.Sample.Tests/Persistence/GatewayWorkspaceCatalogIdentityTests.cs`
- Create `tests/LmStreaming.Sample.Tests/Persistence/GatewayWorkspaceCatalogResolverTests.cs`
- Modify `tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs`
- Create `tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs`
- Modify `tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs`
- Modify `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerWorkspaceTests.cs`
- Modify `tests/LmStreaming.Sample.Tests/WorkspaceWorkflowWiringTests.cs`

### Client

- Modify `samples/LmStreaming.Sample/ClientApp/src/types/workspace.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/api/workspacesApi.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue`
- Modify `samples/LmStreaming.Sample/ClientApp/src/components/ChatLayout.vue`
- Create `samples/LmStreaming.Sample/ClientApp/src/__tests__/composables/useWorkspaces.test.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/__tests__/components/WorkspaceSelector.test.ts`
- Modify `samples/LmStreaming.Sample/ClientApp/src/__tests__/components/ChatLayout.test.ts`

### Browser/live verification

- Modify `tests/LmStreaming.Sample.Browser.E2E.Tests/Infrastructure/BrowserWebAppFactory.cs`
- Create `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/GatewayWorkspaceIsolationTests.cs`
- Add reusable live verification script under `samples/LmStreaming.Sample/playwright-scripts/gateway-workspace-catalog.mjs` only if deterministic API/browser verification cannot cover the real gateway through existing scripts.

---

### Task 1: Canonical Gateway Identity and Manifest

**Files:**
- Create: `samples/LmStreaming.Sample/Persistence/GatewayWorkspaceCatalogIdentity.cs`
- Create: `tests/LmStreaming.Sample.Tests/Persistence/GatewayWorkspaceCatalogIdentityTests.cs`

**Interfaces:**
- Produces:
  - `GatewayWorkspaceCatalogIdentity.Create(string baseUrl, string appId)`.
  - Properties `CanonicalBaseUrl`, `AppId`, `CatalogKey`, `SchemaVersion=1`, `DerivationVersion=1`.
  - `GatewayWorkspaceCatalogManifest` plus `ValidateManifest`.

- [ ] **Step 1: Write failing canonicalization/key tests**

Cover exact cases:

```csharp
[Theory]
[InlineData("HTTP://Example.COM:80/", "http://example.com")]
[InlineData("https://Example.COM:443/", "https://example.com")]
[InlineData("http://example.com:3000/", "http://example.com:3000")]
[InlineData("https://example.com/base/", "https://example.com/base")]
[InlineData("https://example.com/base//", "https://example.com/base")]
public void Create_CanonicalizesExpectedUrl(string input, string expected) { ... }
```

Also assert different non-default ports, internal paths, query text/order, AppIds, and `localhost` vs `127.0.0.1` yield different keys. Assert relative, FTP, and user-info URLs throw. Compute the expected SHA-256 in the test from the exact versioned material. Do not expose any AppKey parameter.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~GatewayWorkspaceCatalogIdentityTests" --nologo
```

Expected: compile failure because identity/manifest types do not exist.

- [ ] **Step 3: Implement identity and manifest validation**

Use `UriBuilder`/`System.Uri`; strip trailing `/` characters from the path, preserve query serialization, reject fragments/user-info/non-HTTP(S), and derive lowercase SHA-256 hex. `ValidateManifest` must compare every version/identity field and throw `InvalidOperationException` on mismatch.

- [ ] **Step 4: Run GREEN and format check**

Run the focused test; expected all pass. Run `dotnet format whitespace ... --include` on the two files or manually conform, then `git diff --check`.

- [ ] **Step 5: Commit checkpoint only if authorized**

```powershell
git add samples/LmStreaming.Sample/Persistence/GatewayWorkspaceCatalogIdentity.cs tests/LmStreaming.Sample.Tests/Persistence/GatewayWorkspaceCatalogIdentityTests.cs
git commit -m "feat(sample): derive gateway workspace catalog identity"
```

---

### Task 2: Scoped Catalog Resolver and Crash-Safe Legacy Migration

**Files:**
- Create: `samples/LmStreaming.Sample/Persistence/GatewayWorkspaceCatalogResolver.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs:14-37,206-234`
- Create: `tests/LmStreaming.Sample.Tests/Persistence/GatewayWorkspaceCatalogResolverTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs`

**Interfaces:**
- Consumes `GatewayWorkspaceCatalogIdentity`.
- Produces `GatewayWorkspaceCatalogResolver.ResolveAsync(rootDirectory, identity, ct)` returning scoped directory/path and migration result.
- Produces strict `WorkspaceCatalogCorruptException` from `FileWorkspaceStore` reads.

- [ ] **Step 1: Write failing isolation/manifest tests**

Use one temp root and identities A/B. Resolve both, construct stores in returned directories, create distinct workspaces, reopen, and prove no cross-catalog records. Write a mismatched `gateway.json` under a derived hash and assert resolver fails without modifying it.

- [ ] **Step 2: Write failing migration tests**

Test:

- Flat `workspaces.json` moves to one `legacy/workspaces.<timestamp>.json`; active scoped catalog begins Default-only.
- `migration.pending.json` records initiating canonical URL/AppId before move.
- Restart after completed marker creates no second archive.
- Simulated interruption after pending write and after move resumes safely.
- Changing active identity during recovery preserves initiating identity in completed marker.
- Existing scoped catalog is never populated from flat legacy data.
- Corrupt legacy JSON fails migration and preserves bytes.
- Two concurrent resolver calls serialize and produce one archive/marker.

Inject clock/archive-name generation and a migration-stage test hook so interruption tests are deterministic, not sleeps.

- [ ] **Step 3: Run RED**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --filter "FullyQualifiedName~GatewayWorkspaceCatalogResolverTests|FullyQualifiedName~FileWorkspaceStoreTests" --nologo
```

Expected: missing resolver/strict corruption APIs and existing corruption behavior fails new assertion.

- [ ] **Step 4: Implement lock, markers, atomic migration, and strict reads**

Hold `legacy/migration.lock` using `FileStream(..., FileShare.None)` across the transaction with bounded cancellation-aware retry. Write pending/completed markers through temp+atomic move. Validate archive by deserializing `Workspace[]`; never call the store's corruption fallback. Change scoped `FileWorkspaceStore.LoadUserWorkspacesAsync` to throw `WorkspaceCatalogCorruptException` on JSON errors and never overwrite corrupted data.

- [ ] **Step 5: Run GREEN repeatedly**

Run focused suite, then resolver tests 10 consecutive times to detect lock/race flakes. Expected all pass.

- [ ] **Step 6: Commit only if authorized**

Suggested message: `feat(sample): isolate workspace catalogs by gateway`.

---

### Task 3: Marketplace Compatibility Service and Cache

**Files:**
- Create: `samples/LmStreaming.Sample/Services/WorkspaceCatalogCompatibilityService.cs`
- Modify: `samples/LmStreaming.Sample/Models/Workspace.cs`
- Create: `tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs`

**Interfaces:**
- Consumes `IMarketplaceCatalogClient.GetCatalogAsync` and active identity.
- Produces:
  - `WorkspaceCompatibility` enum/string mapping (`compatible`, `incompatible`, `unknown`).
  - `WorkspaceCompatibilityResult` with deterministic unsupported aliases and available aliases.
  - `EvaluateAsync(workspace, ct)`.
  - `ValidateForMutationAsync(marketplaces, ct)` and `ValidateForSessionAsync(workspace, ct)` throwing typed compatibility/unavailable exceptions.

- [ ] **Step 1: Write failing compatibility tests**

Cover empty aliases, all supported, mixed unsupported preserving stored order after ordinal dedupe, gateway unavailable→unknown, and exceptions carrying supported/unsupported lists.

- [ ] **Step 2: Write failing cache/single-flight tests**

Inject `TimeProvider` and a fake catalog client. Assert concurrent evaluations make one call, success cached 30 seconds, failures expire no later than success, cancellation of one waiter does not cancel shared refresh for others, and separate service/identity instances never share catalog data.

- [ ] **Step 3: Run RED**

Run compatibility tests; expected missing types.

- [ ] **Step 4: Implement service**

Use one protected in-flight task and timestamped cache per service instance. Never persist catalog. Available aliases derive from `MarketplaceCatalog.Marketplaces[].Alias`, ordinal distinct. Empty explicit list means defaults and compatible only when catalog call succeeds.

- [ ] **Step 5: Run GREEN**

Run tests and `MarketplaceCatalogClientTests`; expected pass.

---

### Task 4: Scoped DI Wiring and Workspace API Envelope

**Files:**
- Modify: `samples/LmStreaming.Sample/Program.cs:195-293,503-510`
- Modify: `samples/LmStreaming.Sample/Controllers/WorkspacesController.cs`
- Modify: `samples/LmStreaming.Sample/Models/Workspace.cs`
- Modify: `tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs`
- Modify: `tests/LmStreaming.Sample.Tests/WorkspaceWorkflowWiringTests.cs`
- Modify: `tests/LmStreaming.Sample.Browser.E2E.Tests/Infrastructure/BrowserWebAppFactory.cs:199-217`

**Interfaces:**
- Consumes resolver and compatibility service.
- Produces `WorkspaceListResponse`, `WorkspaceView`, `WorkspaceGatewayView` JSON contract.
- Keeps create/update error `error` string plus typed `code`/details.

- [ ] **Step 1: Rewrite controller tests to RED**

Update construction with compatibility fake. Assert list envelope/gateway fields, enriched get, compatible/incompatible/unknown views. Assert create/update unsupported→400 with `{ error, code, unsupportedMarketplaces, availableMarketplaces }`; unavailable→503; store unchanged in both; empty aliases accepted.

- [ ] **Step 2: Add wiring tests to RED**

Build service provider with two BaseUrl/AppId combinations over same temp root; resolve `IWorkspaceStore`; prove distinct scoped paths/manifests. Assert production registration derives identity from `SandboxGatewayOptions.AppId`, not request credential/AppKey. Update browser factory to replace the resolver root/identity cleanly rather than hardcoding a flat store.

- [ ] **Step 3: Run RED**

Run controller and wiring filters; expected constructor/shape failures.

- [ ] **Step 4: Implement DI and controller envelope**

Resolve scoped path before registering `IWorkspaceStore`; register identity/resolver/compatibility singleton. Controller uses compatibility results without mutation. Preserve existing `{ error }` fields and add typed details.

- [ ] **Step 5: Run GREEN**

Run `FileWorkspaceStoreTests`, identity/resolver, compatibility, controller, wiring, and browser factory compile tests.

---

### Task 5: Gate Every LmStreaming Session Creation/Recreation

**Files:**
- Modify: `samples/LmStreaming.Sample/Program.cs:696-716`
- Modify: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerWorkspaceTests.cs`
- Add focused agent-factory/session compatibility tests in the closest existing Program wiring test class.

**Interfaces:**
- Consumes `WorkspaceCatalogCompatibilityService.ValidateForSessionAsync`.
- Produces local actionable `WorkspaceCompatibilityException`/`SandboxSessionUnavailableException` mapping before remote registry call.

- [ ] **Step 1: Write failing compatible/incompatible/unknown tests**

Use fake compatibility service and capturing sandbox handler/registry seam. Assert:

- compatible calls `GetOrCreateLiveSessionAsync`/remote create once;
- incompatible returns actionable error and remote create count remains zero;
- unknown/unavailable returns 503/sandbox_unavailable and remote create count zero;
- persisted history endpoint remains readable without compatibility call;
- recreating after pool/app/provider/mode path re-enters validation.

- [ ] **Step 2: Run RED**

Expected remote create still occurs for invalid workspace.

- [ ] **Step 3: Insert gate in Program agent-creation factory**

After workspace resolution and before registry call, synchronously bridge the service following existing factory patterns. Do not modify shared registry or daemon.

- [ ] **Step 4: Run GREEN plus session-registry regressions**

Run targeted tests and existing workspace/session registry tests.

---

### Task 6: Client Envelope, Compatibility UX, and Stale-State Clearing

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/types/workspace.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/api/workspacesApi.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/ChatLayout.vue`
- Create: `samples/LmStreaming.Sample/ClientApp/src/__tests__/composables/useWorkspaces.test.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/__tests__/components/WorkspaceSelector.test.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/__tests__/components/ChatLayout.test.ts`

**Interfaces:**
- Consumes list envelope and typed errors.
- Produces gateway state, scoped list, compatibility display, and `canStartWorkspaceAgent`/selection guards.

- [ ] **Step 1: Write composable RED tests**

Mock fetch/API. Assert success replaces list/gateway; second different-gateway response removes old names; backend/network error clears list and selected ID; incompatible cannot select; unknown during gateway unavailable stays visible but start-disabled; fallback to compatible Default only.

- [ ] **Step 2: Write component RED tests**

Assert gateway URL/AppId displayed, incompatible alias warning, unavailable banner, disabled workspace options/start controls, and catalog-unavailable empty state.

- [ ] **Step 3: Run RED**

```powershell
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run src/__tests__/composables/useWorkspaces.test.ts src/__tests__/components/WorkspaceSelector.test.ts src/__tests__/components/ChatLayout.test.ts
```

- [ ] **Step 4: Implement typed API and state**

`listWorkspaces()` returns `WorkspaceListResponse`. Parse typed error details while preserving `error` fallback. In catch, set `workspaces.value=[]` and `selectedWorkspaceId.value=null` before error. Expose compatibility-derived guards to ChatLayout.

- [ ] **Step 5: Run GREEN and full client tests**

Run focused tests, then full Vitest and TypeScript check/build command from package scripts.

---

### Task 7: Deterministic Browser Isolation and Live Remote Verification

**Files:**
- Modify: `tests/LmStreaming.Sample.Browser.E2E.Tests/Infrastructure/BrowserWebAppFactory.cs`
- Create: `tests/LmStreaming.Sample.Browser.E2E.Tests/Scenarios/GatewayWorkspaceIsolationTests.cs`
- Optionally create: `samples/LmStreaming.Sample/playwright-scripts/gateway-workspace-catalog.mjs`

**Interfaces:**
- Consumes completed server/client feature.
- Produces deterministic proof of no cross-gateway catalog leakage and live remote proof.

- [ ] **Step 1: Add deterministic browser RED scenario**

Use two factory instances or controlled restart identities sharing one catalog root. Gateway A creates `Only-A`; Gateway B must show Default only and never `Only-A`; switching back to A restores `Only-A`. Simulate workspace API 502 and assert selector clears rather than retaining list. Assert incompatible/unknown disabled.

- [ ] **Step 2: Run RED, implement factory identity/root seams, run GREEN**

Run only `GatewayWorkspaceIsolationTests`, then relevant workspace browser scenarios. Avoid fixed sleeps; use existing state waits/testids.

- [ ] **Step 3: Run server/client/full build gates**

```powershell
dotnet test tests/LmStreaming.Sample.Tests/LmStreaming.Sample.Tests.csproj --nologo
npm --prefix samples/LmStreaming.Sample/ClientApp test -- --run
dotnet build LmDotnetTools.sln -bl:.logs/build.binlog
dotnet format whitespace LmDotnetTools.sln --verify-no-changes
git diff --check
```

- [ ] **Step 4: Prepare live migration safely**

Before launching the changed publish, record SHA-256 of the current flat catalog and existing operational backup. Do not delete either. Start the app against `192.168.11.139:3000`; verify logs identify scoped key/path and legacy archive.

- [ ] **Step 5: Live remote verification**

Using the real API/UI:

1. Confirm scoped list initially contains Default only.
2. Create `LmDotNettools` with `claude-plugins,superpowers`.
3. Provision/send a harmless Workspace Agent message and verify remote session creation succeeds.
4. Start a test instance with a different AppId on a separate port and same catalog root; confirm `LmDotNettools` absent.
5. Reopen original identity; confirm it returns.
6. Verify legacy archive bytes equal pre-migration hash.

Use a single existing/new Playwright script if browser interaction is required; otherwise prefer REST plus logs. Never print AppKey.

- [ ] **Step 6: Final review and commit only if authorized**

Review all diff, logs, tests, and migration artifacts. If authorized, commit without AI signatures; never push without separate request.
