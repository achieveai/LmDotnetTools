# Sandbox Plugin Selection (App Layer) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the sample app's workspace UI/API select a subset of gateway plugins per workspace (tri-state: legacy-all / none / explicit subset), persist that selection with optimistic concurrency, and safely migrate live sandbox sessions onto the new selection without dropping in-flight agent runs.

**Architecture:** Add plugin-selection types to the low-level Sandbox SDK (`src/Sandbox`) purely as new nullable, null-preserving fields alongside existing ones (no behavior change when unused). Add a parallel, app-owned `pluginSelection`/`pluginsRevision` tri-state contract to the sample's `Workspace` persistence model, using a new `Optional<T>` JSON-presence wrapper to distinguish "field omitted" (unchanged) from "field explicit null" (legacy-all) from "field is a list" (subset/none). Extend `SandboxSessionRegistry` with a new partial class implementing prepare-then-replace session migration (snapshot partitions → wait bounded-idle → create candidates → CAS-persist workspace → lock-free swap → best-effort retire old sessions), aborting every already-created candidate and leaving old sessions completely untouched if candidate creation OR the CAS-persist step fails. A new sample-level orchestrator service owns the wait-for-idle policy (via a narrow `IAgentRunActivityProbe` seam implemented by `MultiTurnAgentPool`, not a direct concrete dependency) and wires persistence + registry migration together behind the controller through a narrow `IWorkspacePluginSelectionService` interface. Vue UI adds nested plugin checkboxes gated on a live gateway capability flag.

**Tech Stack:** C# / .NET (ASP.NET Core, xUnit, FluentAssertions, System.Text.Json), Vue 3 + TypeScript (Vitest), existing `Sandbox.Tests`, `LmAgentInfra.Tests`, `LmStreaming.Sample.Tests` test projects.

## Global Constraints

- Wire field for the app's workspace-persistence JSON contract is `pluginSelection` (camelCase), NOT `plugins` — the gateway's own sandbox-create wire contract already uses top-level `plugins` for volume/plugin mounts, so the app's persisted-workspace field must not collide with that name.
- Tri-state semantics for any nullable plugin-selection list, wherever it appears: `null` = legacy behavior (all plugins from configured marketplaces), `[]` = explicitly none, non-empty list = explicit subset.
- The app's `WorkspaceUpdate.PluginSelection` additionally needs a THIRD state — "omitted from the JSON body entirely" (leave existing selection unchanged) — distinct from explicit `null`. This is implemented via a new `Optional<T>` presence-tracking wrapper type; this is a backward-compatible plan decision (existing PUT callers that don't send the field must not have their selection wiped), not something a user separately confirmed.
- `pluginsRevision` (int) is an optimistic-concurrency token on `Workspace`. Only updates that touch `PluginSelection` (i.e. `dto.PluginSelection.IsSet`) require/check/increment it; marketplace-only updates are unaffected. **CAS is mandatory whenever `PluginSelection` is explicitly set**: if `dto.PluginSelection.IsSet` is true and `dto.PluginsRevision` is missing (`null`), the store MUST reject the update as a revision conflict (`WorkspaceRevisionConflictException`, surfaced as HTTP 409) rather than silently overwriting the persisted selection. There is no "optional CAS" path for an explicit selection change.
- SDK-layer capability/resolution fields (`SandboxMarketplaceCatalog.PluginFilteringSupported`, `SandboxPluginResolution.Requested`, `SandboxInfo.PluginResolution`) must preserve `null` when the gateway doesn't report them or the caller didn't request a subset — never default to `false`/an empty list/an empty object. `SandboxPluginResolution.Requested` is itself tri-state and nullable: `null` = the request field was absent/explicit-null (legacy "all plugins"), `[]` = the caller explicitly requested none, non-empty = an explicit subset — this mirrors the app-layer tri-state one section above and must round-trip through DTO/model mapping without collapsing `null` to `[]`. The sample/app layer is what fails closed (treats null/false/probe-failure as "not supported") and ONLY when the caller supplied a non-null explicit plugin selection.
- The app must never emit a `pluginSelection` value (in a sandbox-create request) unless the live capability probe returned `PluginFilteringSupported == true` for the workspace's configured marketplaces at the time of the request.
- **Gateway-first gate:** before any app-layer task that emits `pluginSelection` on a sandbox-create call may be considered complete, there must be a live-gateway verification record (per this repo's real-client-connectivity convention) showing `capabilities.pluginFiltering == true` for the target gateway/marketplace configuration. Merging/deploying app-layer plugin-selection emission ahead of that confirmed gateway capability is out of scope for this plan and must not happen.
- **Failure symmetry for the migration path:** if EITHER live-session candidate creation OR the CAS-persist step (`FileWorkspaceStore.UpdateAsync`) fails during a plugin-selection update, every already-created candidate session for that update is aborted/disposed, no partial swap occurs, and the original (old) sessions are left completely untouched — the caller sees the original failure (e.g. `WorkspaceRevisionConflictException` stays a 409) rather than a wrapped/swallowed error.
- No new per-workspace keyed lock is introduced for the persistence layer; `FileWorkspaceStore`'s existing `SemaphoreSlim(1,1) _lock` inside `UpdateAsync` is the sole correctness mechanism for the CAS revision check. No new lock is introduced in the registry either: the swap step (see Task 14) uses a per-entry `ConcurrentDictionary.TryUpdate` compare-and-swap against the cache slot observed at snapshot time, so a partition whose slot changed since the snapshot is skipped rather than overwritten — there is nothing for a lock to protect.
- Idle-run detection uses `MultiTurnAgentPool.IsRunInProgress(threadId)` combined with `SandboxSessionRegistry.GetBoundThreads(sessionId)` — a new method that computes the union INTERNALLY (seeded from the existing `GetThreads(sessionId)`, then adding every established binding whose `SessionId` matches), so a caller uses it alone rather than unioning two separate calls itself. **An earlier revision of this constraint claimed `GetThreads` alone was "sufficient because both are session-bound in-process state". That claim was wrong and is superseded.** It conflated "session-bound in-process state" with "the only session-bound in-process state": `GetThreads` reads `_sessionThreads` and nothing else (`SandboxSessionRegistry.cs:2072-2083`), and `_sessionThreads` is populated only when sub-agent options are present — while `SandboxEstablishedBinding` is documented as "the ONLY authoritative signal that a conversation has an established sandbox workspace" and is *deliberately* kept separate from `_sessionThreads` (`SandboxSessionRegistry.cs:86-93`). The second half of the old claim — "a thread absent from the pool is, by definition, idle" — is still true and is exactly what makes the omission dangerous: `GetRunStateInfo` returns `IsInProgress: false` for an unknown thread id (`MultiTurnAgentPool.cs:1105-1113`), so a binding-only conversation would not merely be missed, it would read as *affirmatively idle*, the bounded wait would pass vacuously, and its session would be torn down mid-run. `GetBoundThreads` folds both sources together so no caller can read only one of them.
- **Post-commit obligations are best-effort, not transactional.** Once `FileWorkspaceStore.UpdateAsync` has committed and the registry swap has run, nothing rolls back. The retirement grace, both retirement sets, and the single reconcile pass are eventually-consistent cleanup: their failures are logged and never converted into a failed request. The failure-symmetry constraint above governs the pre-commit phase only.
- Preexisting unrelated working-tree changes are never touched by any commit step in this plan — every `git add` lists exact files for that task only.
- REST/S2S conversation entry points must call `EnsureCurrentAgentAsync` on every message send, mirroring the existing WebSocket behavior, so a plugin-selection-triggered agent rebuild is observed by every transport.

---

## Limitations

These are accepted properties of this design, not backlog items. Implementers must not "fix" them by adding a persisted work queue — that is out of scope for this plan.

- **Everything after the commit point is in-process only.** The per-workspace migration gate (`_workspaceGates`, Task 15), the post-commit retirement grace (Task 15), and the single reconcile pass (Task 15) are all in-memory, and none of them are journalled.
- **A crash between persist and completion of retirement/reconcile is not recovered.** The persisted selection stays correct — that write already landed — but the sandbox side can be left with either an orphaned gateway container (referenced by nothing, deleted by nothing) or a session still published under the *old* plugin set until something recreates it (the gateway-404 recreate path, or the next process start, which begins with an empty session cache). There is no persisted work queue and no crash recovery for these steps.
- **The retirement grace narrows a window; it does not close it.** A run that outlasts the bounded grace has its session torn down underneath it (Task 15). Closing the window entirely would require an unbounded wait — i.e. a container that a stuck run can pin forever — or the ability to interrupt a run in flight, which `EnsureCurrentAgentAsync` deliberately does not do (`MultiTurnAgentPool.cs`, doc comment 966-968).
- **Reconcile is one pass, not a retry loop.** A partition still unsettled after that pass is a logged residual (Task 15).

---

## File Structure

**SDK layer (`src/Sandbox/`):**
- `SandboxPluginRef.cs` — NEW. `{Marketplace, Plugin}` identity type (SDK-owned, distinct from the sample's `PluginRef`).
- `SandboxCreateRequest.cs` — MODIFY. Add nullable `PluginSelection`.
- `SandboxInfo.cs` — MODIFY. Add nullable `PluginResolution`.
- `SandboxPluginResolution.cs` — NEW. `{Supported, Requested, Effective, Failed}`.
- `SandboxMarketplaceCatalog.cs` — MODIFY. Add nullable `PluginFilteringSupported`.
- `SandboxClient.Lifecycle.cs` — MODIFY. Wire mapping for create request/response.
- `SandboxClient.Catalog.cs` — MODIFY. Wire mapping for capability flag.
- `Wire/SandboxWireDtos.cs` — MODIFY. New `PluginRefDto`, `PluginResolutionDto`, `CapabilitiesDto`; extend `CreateSandboxRequestDto`, `CreateSandboxResponseDto`, `MarketplaceCatalogDto`.

**Sample app models/persistence (`samples/LmStreaming.Sample/`):**
- `Models/Optional.cs` — NEW. Presence-tracking wrapper struct.
- `Models/OptionalJsonConverter.cs` — NEW. `System.Text.Json` converter factory for `Optional<T>`.
- `Models/PluginRef.cs` — NEW. App-owned `{Marketplace, Plugin}` record (separate from SDK's `SandboxPluginRef`).
- `Models/Workspace.cs` — MODIFY. Add `PluginSelection`/`PluginsRevision` to `Workspace`/`WorkspaceCreate`/`WorkspaceUpdate`/`WorkspaceView`.
- `Models/MarketplaceCatalog.cs` — MODIFY. Add `MarketplaceCapabilities`.
- `Persistence/FileWorkspaceStore.cs` — MODIFY. CAS revision check + `WorkspaceRevisionConflictException`.
- `Persistence/IWorkspaceStore.cs` — MODIFY. XML doc `<exception>` tag.
- `Services/MarketplaceCatalogClient.cs` — MODIFY. Map capability passthrough.
- `Services/WorkspaceCatalogCompatibilityService.cs` — MODIFY. `ValidatePluginsForMutationAsync` + 2 new exceptions.
- `Controllers/WorkspacesController.cs` — MODIFY. 5 new catch blocks, orchestrator wiring.
- `Controllers/ConversationsController.cs` — MODIFY. `EnsureCurrentAgentAsync` parity call.

**Registry/orchestration layer (`src/LmAgentInfra/Sandbox/`, `samples/LmStreaming.Sample/Services/`):**
- `SandboxSessionRestartTimeoutException.cs` — NEW.
- `SandboxSessionReplacementFailedException.cs` — NEW.
- `SandboxSessionRegistry.cs` — MODIFY. Add `partial` keyword; extend `WorkspaceRef`/`SandboxSession` records; reload-callback constructor param; fix stale-ref recreate branch.
- `SandboxSessionRegistry.PluginSelection.cs` — NEW partial class. Prepare-then-replace migration primitives.
- `Services/WorkspacePluginSelectionService.cs` — NEW (sample layer). Wait-for-idle + validate + persist-CAS + registry-migration orchestration.
- `Program.cs` — MODIFY. DI wiring for reload callback + new orchestrator.

**Vue client (`samples/LmStreaming.Sample/ClientApp/src/`):**
- `types/workspace.ts` — MODIFY. `PluginRef`, `pluginSelection`, `pluginsRevision`.
- `types/marketplace.ts` — MODIFY. `capabilities` on `MarketplaceCatalog`.
- `services/workspacesApi.ts` — MODIFY. 409 handling.
- `composables/useWorkspaces.ts` — MODIFY. Reload-on-409.
- `components/WorkspaceSelector.vue` — MODIFY. Nested plugin checkboxes, capability gating.

**Docs:**
- `samples/LmStreaming.Sample/SandboxWorkspaceGuide.md` — MODIFY lines 291-294.

**Test files (new or extended, matching the above 1:1):**
- `tests/Sandbox.Tests/SandboxModelsTests.cs`, `SandboxClientLifecycleTests.cs`, `SandboxClientCatalogTests.cs` — extended.
- `tests/LmStreaming.Sample.Tests/Models/OptionalTests.cs` — new.
- `tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs` — extended.
- `tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs` — extended.
- `tests/LmStreaming.Sample.Tests/Services/WorkspacePluginSelectionServiceTests.cs` — new.
- `tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs`, `ConversationsControllerTests.cs` — extended.
- `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryPluginSelectionTests.cs` — new.
- `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs` — extended (stale-ref reload regression).
- `ClientApp/src/composables/useWorkspaces.test.ts`, `ClientApp/src/components/WorkspaceSelector.test.ts` — extended.

---

## Task 1: SDK `SandboxPluginRef`

**Files:**
- Create: `src/Sandbox/SandboxPluginRef.cs`
- Test: `tests/Sandbox.Tests/SandboxModelsTests.cs`

**Interfaces:**
- Produces: `public sealed class SandboxPluginRef { public SandboxPluginRef(string marketplace, string plugin); public string Marketplace { get; } public string Plugin { get; } }`

- [ ] **Step 1: Write the failing tests** — append to `tests/Sandbox.Tests/SandboxModelsTests.cs`:

```csharp
[Theory]
[InlineData("", "plugin")]
[InlineData("marketplace", "")]
public void SandboxPluginRef_BlankRequiredField_Throws(string marketplace, string plugin)
{
    var act = () => new SandboxPluginRef(marketplace, plugin);

    act.Should().Throw<ArgumentException>();
}

[Fact]
public void SandboxPluginRef_ValidFields_ExposesThem()
{
    var pluginRef = new SandboxPluginRef("official", "code-review");

    pluginRef.Marketplace.Should().Be("official");
    pluginRef.Plugin.Should().Be("code-review");
}

[Fact]
public void SandboxPluginRef_NullMarketplace_Throws()
{
    var act = () => new SandboxPluginRef(null!, "code-review");

    act.Should().Throw<ArgumentException>();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~SandboxPluginRef"`
Expected: FAIL — `SandboxPluginRef` does not exist (CS0246).

- [ ] **Step 3: Implement** — create `src/Sandbox/SandboxPluginRef.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Identifies a single plugin within a marketplace, as referenced by a workspace's explicit plugin
/// selection. Mirrors the gateway's <c>{marketplace, plugin}</c> wire pair (spec Section 5.1).
/// </summary>
public sealed class SandboxPluginRef
{
    public SandboxPluginRef(string marketplace, string plugin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(marketplace);
        ArgumentException.ThrowIfNullOrWhiteSpace(plugin);

        Marketplace = marketplace;
        Plugin = plugin;
    }

    public string Marketplace { get; }

    public string Plugin { get; }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~SandboxPluginRef"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sandbox/SandboxPluginRef.cs tests/Sandbox.Tests/SandboxModelsTests.cs
git commit -m "feat(sandbox-sdk): add SandboxPluginRef identity type"
```

---

## Task 2: SDK `SandboxCreateRequest.PluginSelection` + wire mapping

**Files:**
- Modify: `src/Sandbox/SandboxCreateRequest.cs`
- Modify: `src/Sandbox/Wire/SandboxWireDtos.cs`
- Modify: `src/Sandbox/SandboxClient.Lifecycle.cs`
- Test: `tests/Sandbox.Tests/SandboxModelsTests.cs`, `tests/Sandbox.Tests/SandboxClientLifecycleTests.cs`

**Interfaces:**
- Consumes: `SandboxPluginRef` (Task 1).
- Produces: `SandboxCreateRequest.PluginSelection` of type `IReadOnlyList<SandboxPluginRef>?` (null-preserving — NOT collapsed to `[]` when the caller passes null, unlike `Marketplaces`). New trailing constructor parameter `pluginSelection` after `discovery`.

- [ ] **Step 1: Write the failing tests** — append to `tests/Sandbox.Tests/SandboxModelsTests.cs`:

```csharp
[Fact]
public void SandboxCreateRequest_OmittedPluginSelection_IsNullNotEmpty()
{
    var request = new SandboxCreateRequest("ws");

    request.PluginSelection.Should().BeNull();
}

[Fact]
public void SandboxCreateRequest_ExplicitEmptyPluginSelection_StaysEmpty_NotNull()
{
    var request = new SandboxCreateRequest("ws", pluginSelection: []);

    request.PluginSelection.Should().NotBeNull();
    request.PluginSelection.Should().BeEmpty();
}

[Fact]
public void SandboxCreateRequest_PluginSelection_IsDefensivelyCopied()
{
    var refs = new List<SandboxPluginRef> { new("official", "code-review") };
    var request = new SandboxCreateRequest("ws", pluginSelection: refs);

    refs.Add(new SandboxPluginRef("official", "other"));

    request.PluginSelection.Should().HaveCount(1);
}
```

Append to `tests/Sandbox.Tests/SandboxClientLifecycleTests.cs`:

```csharp
[Fact]
public async Task CreateAsync_ExplicitPluginSelection_IncludedInWireBodyAsPluginSelectionField()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

    var request = new SandboxCreateRequest(
        "my-workspace",
        pluginSelection: [new SandboxPluginRef("official", "code-review")]
    );

    _ = await client.CreateAsync(request);

    var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
    var body = JsonDocument.Parse(sent.Body!).RootElement;
    var plugin = body.GetProperty("pluginSelection")[0];

    plugin.GetProperty("marketplace").GetString().Should().Be("official");
    plugin.GetProperty("plugin").GetString().Should().Be("code-review");
}

[Fact]
public async Task CreateAsync_ExplicitEmptyPluginSelection_SendsEmptyPluginSelectionArray_NotOmitted()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

    var request = new SandboxCreateRequest("my-workspace", pluginSelection: []);

    _ = await client.CreateAsync(request);

    var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
    var body = JsonDocument.Parse(sent.Body!).RootElement;

    body.GetProperty("pluginSelection").GetArrayLength().Should().Be(0);
}

[Fact]
public async Task CreateAsync_NullPluginSelection_OmitsPluginSelectionFieldFromWireBody()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

    _ = await client.CreateAsync(new SandboxCreateRequest("ws"));

    var sent = handler.Requests.Single(r => r.Method == HttpMethod.Post);
    var body = JsonDocument.Parse(sent.Body!).RootElement;

    body.TryGetProperty("pluginSelection", out _).Should().BeFalse();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginSelection"`
Expected: FAIL — no `pluginSelection` parameter on `SandboxCreateRequest` (CS1739), and no `pluginSelection` wire field. NOTE: `pluginSelection` is deliberately NOT named `plugins` on the wire — the gateway's sandbox-create contract already reserves the top-level `plugins` key for volume/plugin MOUNT data (see Global Constraints); reusing it here for plugin-selection would silently collide with that reserved field.

- [ ] **Step 3: Implement.** Modify `src/Sandbox/SandboxCreateRequest.cs` — add trailing parameter and property, without collapsing null to empty:

```csharp
public SandboxCreateRequest(
    string workspace,
    IReadOnlyList<string>? marketplaces = null,
    IReadOnlyList<SandboxAuthProvider>? authProviders = null,
    IReadOnlyList<SandboxNetworkRule>? networkRules = null,
    SandboxDiscoverySettings? discovery = null,
    IReadOnlyList<SandboxPluginRef>? pluginSelection = null
)
{
    ArgumentNullException.ThrowIfNull(workspace);

    Workspace = workspace;
    Marketplaces = marketplaces is null ? [] : [.. marketplaces];
    AuthProviders = authProviders is null ? [] : [.. authProviders];
    NetworkRules = networkRules is null ? [] : [.. networkRules];
    Discovery = discovery;
    // Unlike Marketplaces/AuthProviders/NetworkRules, null and [] are semantically different here
    // (tri-state plugin selection): null must stay null, not collapse to an empty list.
    PluginSelection = pluginSelection is null ? null : [.. pluginSelection];
}

public IReadOnlyList<SandboxPluginRef>? PluginSelection { get; }
```

Modify `src/Sandbox/Wire/SandboxWireDtos.cs` — add new DTO and extend the request DTO:

```csharp
internal sealed record PluginRefDto(
    [property: JsonPropertyName("marketplace")] string Marketplace,
    [property: JsonPropertyName("plugin")] string Plugin
);
```

Extend `CreateSandboxRequestDto` with a new trailing property:

```csharp
internal sealed record CreateSandboxRequestDto(
    AppRefDto App,
    string Workspace,
    IReadOnlyList<AuthProviderDto>? AuthProviders,
    NetworkDto? Network,
    DiscoveryDto? Discovery,
    IReadOnlyList<string>? Marketplaces,
    [property: JsonPropertyName("pluginSelection")] IReadOnlyList<PluginRefDto>? PluginSelection = null
);
```

Modify `src/Sandbox/SandboxClient.Lifecycle.cs` — extend `ToWireDto` and add a mapping helper:

```csharp
private static CreateSandboxRequestDto ToWireDto(SandboxCreateRequest request, string appId)
{
    return new CreateSandboxRequestDto(
        App: new AppRefDto(appId),
        Workspace: request.Workspace,
        AuthProviders: request.AuthProviders.Count > 0 ? [.. request.AuthProviders.Select(ToDto)] : null,
        Network: request.NetworkRules.Count > 0 ? new NetworkDto([.. request.NetworkRules.Select(ToDto)]) : null,
        Discovery: request.Discovery is null ? null : ToDto(request.Discovery),
        Marketplaces: request.Marketplaces.Count > 0 ? [.. request.Marketplaces] : null,
        PluginSelection: request.PluginSelection is null ? null : [.. request.PluginSelection.Select(ToPluginRefDto)]
    );
}

private static PluginRefDto ToPluginRefDto(SandboxPluginRef pluginRef) =>
    new(pluginRef.Marketplace, pluginRef.Plugin);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginSelection"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sandbox/SandboxCreateRequest.cs src/Sandbox/Wire/SandboxWireDtos.cs src/Sandbox/SandboxClient.Lifecycle.cs tests/Sandbox.Tests/SandboxModelsTests.cs tests/Sandbox.Tests/SandboxClientLifecycleTests.cs
git commit -m "feat(sandbox-sdk): thread explicit plugin selection into sandbox create requests"
```

---

## Task 3: SDK `SandboxInfo.PluginResolution` + `SandboxPluginResolution`

**Files:**
- Create: `src/Sandbox/SandboxPluginResolution.cs`
- Modify: `src/Sandbox/SandboxInfo.cs`
- Modify: `src/Sandbox/Wire/SandboxWireDtos.cs`
- Modify: `src/Sandbox/SandboxClient.Lifecycle.cs`
- Test: `tests/Sandbox.Tests/SandboxModelsTests.cs`, `tests/Sandbox.Tests/SandboxClientLifecycleTests.cs`

**Interfaces:**
- Produces: `SandboxPluginResolution { bool Supported; IReadOnlyList<SandboxPluginRef>? Requested; IReadOnlyList<SandboxPluginRef> Effective; IReadOnlyList<SandboxPluginRef> Failed; }`; `SandboxInfo.PluginResolution` of type `SandboxPluginResolution?`, never defaulted (stays `null` when the gateway didn't report one). `Requested` is itself null-preserving tri-state, mirroring the app-layer `pluginSelection` tri-state (Global Constraints): `null` = the request field was absent/explicit-null on the wire (legacy "all plugins"), `[]` = the caller explicitly requested none, non-empty = an explicit subset. Unlike `Effective`/`Failed` (which always default to `[]`), `Requested` must NOT collapse `null` to `[]` anywhere in the constructor or the DTO mapping.

- [ ] **Step 1: Write the failing tests** — append to `tests/Sandbox.Tests/SandboxModelsTests.cs`:

```csharp
[Fact]
public void SandboxInfo_OmittedPluginResolution_IsNull()
{
    var info = new SandboxInfo("sess-1");

    info.PluginResolution.Should().BeNull();
}

[Fact]
public void SandboxPluginResolution_ExposesFields()
{
    var resolution = new SandboxPluginResolution(
        supported: true,
        requested: [new SandboxPluginRef("official", "code-review")],
        effective: [new SandboxPluginRef("official", "code-review")],
        failed: []
    );

    resolution.Supported.Should().BeTrue();
    resolution.Requested.Should().ContainSingle();
    resolution.Failed.Should().BeEmpty();
}

[Fact]
public void SandboxPluginResolution_NullRequested_StaysNull_NotEmpty()
{
    var resolution = new SandboxPluginResolution(
        supported: true,
        requested: null,
        effective: [],
        failed: []
    );

    resolution.Requested.Should().BeNull();
}
```

Append to `tests/Sandbox.Tests/SandboxClientLifecycleTests.cs`:

```csharp
[Fact]
public async Task CreateAsync_ResponseWithPluginResolution_ParsesIntoSandboxInfo()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(
        HttpMethod.Post,
        "/api/v1/sandboxes",
        """
        {"session_id":"sess-1","container_id":"container-1",
         "volumes":{"workspace":{"container_path":"/workspace","read_only":false}},
         "pluginResolution":{"supported":true,
           "requested":[{"marketplace":"official","plugin":"code-review"}],
           "effective":[{"marketplace":"official","plugin":"code-review"}],
           "failed":[]}}
        """
    );

    var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

    info.PluginResolution.Should().NotBeNull();
    info.PluginResolution!.Supported.Should().BeTrue();
    info.PluginResolution.Effective.Should().ContainSingle(r => r.Plugin == "code-review");
}

[Fact]
public async Task CreateAsync_ResponseWithoutPluginResolution_LeavesItNull()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(HttpMethod.Post, "/api/v1/sandboxes", CreateResponseJson);

    var info = await client.CreateAsync(new SandboxCreateRequest("ws"));

    info.PluginResolution.Should().BeNull();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginResolution"`
Expected: FAIL — `SandboxPluginResolution`/`PluginResolution` do not exist.

- [ ] **Step 3: Implement.** Create `src/Sandbox/SandboxPluginResolution.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.Sandbox;

/// <summary>
/// Reports how the gateway resolved a sandbox's requested plugin selection (spec Section 5.3).
/// <see cref="Supported"/> is false when the gateway accepted the create call but does not support
/// plugin filtering; callers must not infer capability from an absent <see cref="SandboxInfo.PluginResolution"/> —
/// that means the gateway is old enough to not report resolution at all, a stronger "unknown" signal.
/// </summary>
public sealed class SandboxPluginResolution
{
    public SandboxPluginResolution(
        bool supported,
        IReadOnlyList<SandboxPluginRef>? requested = null,
        IReadOnlyList<SandboxPluginRef>? effective = null,
        IReadOnlyList<SandboxPluginRef>? failed = null
    )
    {
        Supported = supported;
        // Unlike Effective/Failed, Requested is tri-state and must not collapse null to []:
        // null means the wire request field was absent/explicit-null (legacy "all plugins").
        Requested = requested is null ? null : [.. requested];
        Effective = effective is null ? [] : [.. effective];
        Failed = failed is null ? [] : [.. failed];
    }

    public bool Supported { get; }

    public IReadOnlyList<SandboxPluginRef>? Requested { get; }

    public IReadOnlyList<SandboxPluginRef> Effective { get; }

    public IReadOnlyList<SandboxPluginRef> Failed { get; }
}
```

Modify `src/Sandbox/SandboxInfo.cs` — add a new trailing parameter, NOT defaulted like `Inventory`:

```csharp
public SandboxInfo(
    string sessionId,
    string? containerId = null,
    string? workspaceContainerPath = null,
    long? workspaceMountId = null,
    string? status = null,
    SandboxInventory? inventory = null,
    SandboxPluginResolution? pluginResolution = null
)
{
    ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

    SessionId = sessionId;
    ContainerId = containerId;
    WorkspaceContainerPath = workspaceContainerPath;
    WorkspaceMountId = workspaceMountId;
    Status = status;
    Inventory = inventory ?? SandboxInventory.Unavailable(SandboxInventoryUnavailableReasons.NotRequested);
    // Unlike Inventory, this stays null when the gateway did not report it — null is a distinct,
    // stronger "capability unknown" signal than a false Supported flag.
    PluginResolution = pluginResolution;
}

public SandboxPluginResolution? PluginResolution { get; }
```

Modify `src/Sandbox/Wire/SandboxWireDtos.cs` — add DTOs:

```csharp
internal sealed record PluginResolutionDto(
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("requested")] IReadOnlyList<PluginRefDto>? Requested,
    [property: JsonPropertyName("effective")] IReadOnlyList<PluginRefDto>? Effective,
    [property: JsonPropertyName("failed")] IReadOnlyList<PluginRefDto>? Failed
);
```

Extend `CreateSandboxResponseDto` with a new trailing optional parameter:

```csharp
internal sealed record CreateSandboxResponseDto(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("container_id")] string? ContainerId,
    [property: JsonPropertyName("volumes")] IReadOnlyDictionary<string, VolumeDto>? Volumes,
    string? Status = null,
    SandboxInventoryDto? Inventory = null,
    [property: JsonPropertyName("pluginResolution")] PluginResolutionDto? PluginResolution = null
);
```

Modify `src/Sandbox/SandboxClient.Lifecycle.cs` — extend `ToSandboxInfo` and add a mapping helper:

```csharp
private static SandboxInfo ToSandboxInfo(CreateSandboxResponseDto dto)
{
    var workspaceVolume = dto.Volumes is not null && dto.Volumes.TryGetValue("workspace", out var volume) ? volume : null;

    return new SandboxInfo(
        dto.SessionId!,
        dto.ContainerId,
        workspaceVolume?.ContainerPath,
        status: dto.Status,
        inventory: ToInventory(dto.Inventory),
        pluginResolution: ToPluginResolution(dto.PluginResolution)
    );
}

private static SandboxPluginResolution? ToPluginResolution(PluginResolutionDto? dto)
{
    if (dto is null)
    {
        return null;
    }

    return new SandboxPluginResolution(
        dto.Supported,
        dto.Requested?.Select(ToPluginRef).ToArray(),
        dto.Effective?.Select(ToPluginRef).ToArray(),
        dto.Failed?.Select(ToPluginRef).ToArray()
    );
}

private static SandboxPluginRef ToPluginRef(PluginRefDto dto) => new(dto.Marketplace, dto.Plugin);
```

(NOTE: verify the exact current signature/body of `ToSandboxInfo`/`ToInventory` in `SandboxClient.Lifecycle.cs` before editing — the snippet above matches the fields already confirmed in Task research; keep the existing `workspaceMountId` argument passthrough unchanged if present.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginResolution"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sandbox/SandboxPluginResolution.cs src/Sandbox/SandboxInfo.cs src/Sandbox/Wire/SandboxWireDtos.cs src/Sandbox/SandboxClient.Lifecycle.cs tests/Sandbox.Tests/SandboxModelsTests.cs tests/Sandbox.Tests/SandboxClientLifecycleTests.cs
git commit -m "feat(sandbox-sdk): parse gateway plugin resolution on sandbox create"
```

---

## Task 4: SDK `SandboxMarketplaceCatalog.PluginFilteringSupported`

**Files:**
- Modify: `src/Sandbox/SandboxMarketplaceCatalog.cs`
- Modify: `src/Sandbox/Wire/SandboxWireDtos.cs`
- Modify: `src/Sandbox/SandboxClient.Catalog.cs`
- Test: `tests/Sandbox.Tests/SandboxClientCatalogTests.cs`

**Interfaces:**
- Produces: `SandboxMarketplaceCatalog.PluginFilteringSupported` of type `bool?`, never defaulted — `null` means the gateway didn't report a capability block at all.

- [ ] **Step 1: Write the failing tests** — append to `tests/Sandbox.Tests/SandboxClientCatalogTests.cs`:

```csharp
[Fact]
public async Task PreviewMarketplacesAsync_ResponseWithCapabilities_ParsesPluginFilteringSupported()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(
        HttpMethod.Get,
        "/api/v1/marketplaces/preview",
        """{"selected":["official"],"marketplaces":[],"capabilities":{"pluginFiltering":true}}"""
    );

    var catalog = await client.PreviewMarketplacesAsync(["official"]);

    catalog.PluginFilteringSupported.Should().BeTrue();
}

[Fact]
public async Task PreviewMarketplacesAsync_ResponseWithoutCapabilities_LeavesPluginFilteringSupportedNull()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(
        HttpMethod.Get,
        "/api/v1/marketplaces/preview",
        """{"selected":["official"],"marketplaces":[]}"""
    );

    var catalog = await client.PreviewMarketplacesAsync(["official"]);

    catalog.PluginFilteringSupported.Should().BeNull();
}

[Fact]
public async Task PreviewMarketplacesAsync_CapabilitiesWithFalsePluginFiltering_ParsesAsFalse_NotNull()
{
    var (client, handler) = TestSupport.CreateBorrowedClient();
    handler.OnJson(
        HttpMethod.Get,
        "/api/v1/marketplaces/preview",
        """{"selected":["official"],"marketplaces":[],"capabilities":{"pluginFiltering":false}}"""
    );

    var catalog = await client.PreviewMarketplacesAsync(["official"]);

    catalog.PluginFilteringSupported.Should().BeFalse();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginFilteringSupported"`
Expected: FAIL — `PluginFilteringSupported` does not exist on `SandboxMarketplaceCatalog`.

- [ ] **Step 3: Implement.** Modify `src/Sandbox/SandboxMarketplaceCatalog.cs` — add trailing constructor parameter:

```csharp
public SandboxMarketplaceCatalog(
    IReadOnlyList<string>? selected,
    IReadOnlyList<SandboxMarketplaceEntry>? marketplaces,
    bool? pluginFilteringSupported = null
)
{
    Selected = selected is null ? [] : [.. selected];
    Marketplaces = marketplaces is null ? [] : [.. marketplaces];
    PluginFilteringSupported = pluginFilteringSupported;
}

public bool? PluginFilteringSupported { get; }
```

Modify `src/Sandbox/Wire/SandboxWireDtos.cs` — add:

```csharp
internal sealed record CapabilitiesDto(
    [property: JsonPropertyName("pluginFiltering")] bool? PluginFiltering
);
```

Extend `MarketplaceCatalogDto` with a trailing property:

```csharp
internal sealed record MarketplaceCatalogDto(
    IReadOnlyList<string>? Selected,
    IReadOnlyList<MarketplaceEntryDto>? Marketplaces,
    [property: JsonPropertyName("capabilities")] CapabilitiesDto? Capabilities = null
);
```

Modify `src/Sandbox/SandboxClient.Catalog.cs` — in `PreviewMarketplacesAsync`, pass the capability through to the constructed catalog:

```csharp
return new SandboxMarketplaceCatalog(
    payload.Selected,
    marketplaceEntries,
    payload.Capabilities?.PluginFiltering
);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/Sandbox.Tests --filter "FullyQualifiedName~PluginFilteringSupported"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/Sandbox/SandboxMarketplaceCatalog.cs src/Sandbox/Wire/SandboxWireDtos.cs src/Sandbox/SandboxClient.Catalog.cs tests/Sandbox.Tests/SandboxClientCatalogTests.cs
git commit -m "feat(sandbox-sdk): surface gateway plugin-filtering capability on marketplace preview"
```

---

## Task 5: App `Optional<T>` presence wrapper + JSON converter

**Files:**
- Create: `samples/LmStreaming.Sample/Models/Optional.cs`
- Create: `samples/LmStreaming.Sample/Models/OptionalJsonConverter.cs`
- Test: `tests/LmStreaming.Sample.Tests/Models/OptionalTests.cs`

**Interfaces:**
- Produces: `readonly struct Optional<T> { bool IsSet; T? Value; static Optional<T> Unset; }`; `[JsonConverter(typeof(OptionalJsonConverterFactory))]` attribute usable on any `Optional<T>` property.

- [ ] **Step 1: Write the failing tests** — create `tests/LmStreaming.Sample.Tests/Models/OptionalTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Tests.Models;

public class OptionalTests
{
    private sealed record Payload
    {
        [JsonConverter(typeof(OptionalJsonConverterFactory))]
        public Optional<IReadOnlyList<string>?> Selection { get; init; }
    }

    [Fact]
    public void Unset_IsNotSet_AndValueIsDefault()
    {
        var optional = Optional<string>.Unset;

        optional.IsSet.Should().BeFalse();
        optional.Value.Should().BeNull();
    }

    [Fact]
    public void Constructed_IsSet_WithGivenValue()
    {
        var optional = new Optional<string>("hello");

        optional.IsSet.Should().BeTrue();
        optional.Value.Should().Be("hello");
    }

    [Fact]
    public void Deserialize_OmittedProperty_LeavesUnset()
    {
        var payload = JsonSerializer.Deserialize<Payload>("{}");

        payload!.Selection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void Deserialize_ExplicitNullProperty_IsSetWithNullValue()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":null}""");

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().BeNull();
    }

    [Fact]
    public void Deserialize_ExplicitListProperty_IsSetWithList()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":["a","b"]}""");

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().Equal("a", "b");
    }

    [Fact]
    public void Deserialize_ExplicitEmptyListProperty_IsSetWithEmptyList()
    {
        var payload = JsonSerializer.Deserialize<Payload>("""{"selection":[]}""");

        payload!.Selection.IsSet.Should().BeTrue();
        payload.Selection.Value.Should().BeEmpty();
    }

    [Fact]
    public void Serialize_SetValue_WritesValue()
    {
        var payload = new Payload { Selection = new Optional<IReadOnlyList<string>?>(["a"]) };

        var json = JsonSerializer.Serialize(payload);

        json.Should().Contain("\"selection\":[\"a\"]");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~OptionalTests"`
Expected: FAIL — `Optional<T>`/`OptionalJsonConverterFactory` do not exist (CS0246).

- [ ] **Step 3: Implement.** Create `samples/LmStreaming.Sample/Models/Optional.cs`:

```csharp
namespace LmStreaming.Sample.Models;

/// <summary>
/// Distinguishes "property omitted from the JSON body" (<see cref="IsSet"/> false, meaning "leave
/// unchanged") from "property present with value <paramref name="value"/>" — including an explicit
/// JSON <c>null</c>, which is itself a meaningful tri-state value for
/// <see cref="WorkspaceUpdate.PluginSelection"/> (legacy-all), distinct from omission (unchanged).
/// Requires <see cref="OptionalJsonConverterFactory"/> via <c>[JsonConverter]</c> to populate
/// correctly — <see cref="System.Text.Json"/> never calls a converter's Read for an absent property,
/// which is exactly the mechanism this type relies on to detect omission.
/// </summary>
public readonly struct Optional<T>
{
    public Optional(T? value)
    {
        IsSet = true;
        Value = value;
    }

    public static Optional<T> Unset => default;

    public bool IsSet { get; }

    public T? Value { get; }
}
```

Create `samples/LmStreaming.Sample/Models/OptionalJsonConverter.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LmStreaming.Sample.Models;

/// <summary>Factory for <see cref="OptionalJsonConverter{T}"/>, resolving the closed generic per property.</summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var innerType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(OptionalJsonConverter<>).MakeGenericType(innerType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>
/// Reads/writes <see cref="Optional{T}"/>. Read is only ever invoked by <see cref="System.Text.Json"/>
/// when the property is PRESENT in the JSON (including explicit <c>null</c>) — an omitted property
/// leaves the field at its default (<c>Optional&lt;T&gt;.Unset</c>), which is exactly what distinguishes
/// "unchanged" from "explicit null".
/// </summary>
public sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = JsonSerializer.Deserialize<T>(ref reader, options);
        return new Optional<T>(value);
    }

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value.Value, options);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~OptionalTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Models/Optional.cs samples/LmStreaming.Sample/Models/OptionalJsonConverter.cs tests/LmStreaming.Sample.Tests/Models/OptionalTests.cs
git commit -m "feat(sample-app): add Optional<T> JSON-presence wrapper for tri-state PUT semantics"
```

---

## Task 6: App `PluginRef` model

**Files:**
- Create: `samples/LmStreaming.Sample/Models/PluginRef.cs`
- Test: `tests/LmStreaming.Sample.Tests/Models/PluginRefTests.cs`

**Interfaces:**
- Produces: `public sealed record PluginRef(string Marketplace, string Plugin);` — the app-owned type, deliberately separate from the SDK's `AchieveAi.LmDotnetTools.Sandbox.SandboxPluginRef` (Task 1), since the app's copy is a plain JSON-serializable record for workspace persistence, with no validation constructor.

- [ ] **Step 1: Write the failing test** — create `tests/LmStreaming.Sample.Tests/Models/PluginRefTests.cs`:

```csharp
namespace LmStreaming.Sample.Tests.Models;

public class PluginRefTests
{
    [Fact]
    public void RoundTrips_ThroughJson_WithCamelCaseFields()
    {
        var pluginRef = new PluginRef("official", "code-review");

        var json = JsonSerializer.Serialize(pluginRef, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        json.Should().Contain("\"marketplace\":\"official\"");
        json.Should().Contain("\"plugin\":\"code-review\"");

        var roundTripped = JsonSerializer.Deserialize<PluginRef>(json, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        roundTripped.Should().Be(pluginRef);
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new PluginRef("official", "code-review");
        var b = new PluginRef("official", "code-review");

        a.Should().Be(b);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~PluginRefTests"`
Expected: FAIL — `PluginRef` does not exist.

- [ ] **Step 3: Implement.** Create `samples/LmStreaming.Sample/Models/PluginRef.cs`:

```csharp
namespace LmStreaming.Sample.Models;

/// <summary>
/// A single plugin identity as persisted in a workspace's explicit selection. Deliberately a
/// separate type from the Sandbox SDK's <c>SandboxPluginRef</c> (which is a validating, SDK-owned
/// type): this record is the app's own JSON-persistence shape, mapped to/from the SDK type at the
/// registry boundary rather than shared across layers.
/// </summary>
public sealed record PluginRef(string Marketplace, string Plugin);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~PluginRefTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Models/PluginRef.cs tests/LmStreaming.Sample.Tests/Models/PluginRefTests.cs
git commit -m "feat(sample-app): add app-level PluginRef persistence model"
```

---

## Task 7: `Workspace`/`WorkspaceCreate`/`WorkspaceUpdate`/`WorkspaceView` plugin-selection fields

**Files:**
- Modify: `samples/LmStreaming.Sample/Models/Workspace.cs`
- Test: `tests/LmStreaming.Sample.Tests/Models/WorkspaceTests.cs` (new)

**Interfaces:**
- Consumes: `PluginRef` (Task 6), `Optional<T>`/`OptionalJsonConverterFactory` (Task 5).
- Produces: `Workspace.PluginSelection` (`IReadOnlyList<PluginRef>?`, tri-state, defaults `null`), `Workspace.PluginsRevision` (`int`, defaults `0`), `WorkspaceCreate.PluginSelection` (`IReadOnlyList<PluginRef>?`, nullable, defaults `null`), `WorkspaceUpdate.PluginSelection` (`Optional<IReadOnlyList<PluginRef>?>`, defaults `Optional<IReadOnlyList<PluginRef>?>.Unset`), `WorkspaceView.PluginSelection`/`PluginsRevision`.

- [ ] **Step 1: Write the failing tests** — create `tests/LmStreaming.Sample.Tests/Models/WorkspaceTests.cs`:

```csharp
namespace LmStreaming.Sample.Tests.Models;

public class WorkspaceTests
{
    [Fact]
    public void Workspace_DefaultsPluginSelectionToNull_AndRevisionToZero()
    {
        var workspace = new Workspace
        {
            Id = "id",
            Name = "name",
            DirectoryRelPath = "dir",
            IsSystemDefined = false,
            CreatedAt = 0,
            UpdatedAt = 0,
        };

        workspace.PluginSelection.Should().BeNull();
        workspace.PluginsRevision.Should().Be(0);
    }

    [Fact]
    public void WorkspaceUpdate_DefaultsPluginSelectionToUnset()
    {
        var update = new WorkspaceUpdate();

        update.PluginSelection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_OmittedPluginSelection_StaysUnset()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeFalse();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_ExplicitNullPluginSelection_IsSetToNull()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"],"pluginSelection":null}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeTrue();
        update.PluginSelection.Value.Should().BeNull();
    }

    [Fact]
    public void WorkspaceUpdate_Deserialize_ExplicitPluginList_IsSetToList()
    {
        var update = JsonSerializer.Deserialize<WorkspaceUpdate>(
            """{"marketplaces":["a"],"pluginSelection":[{"marketplace":"official","plugin":"code-review"}]}""",
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }
        );

        update!.PluginSelection.IsSet.Should().BeTrue();
        update.PluginSelection.Value.Should().ContainSingle(p => p.Plugin == "code-review");
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspaceTests"`
Expected: FAIL — `PluginSelection`/`PluginsRevision` do not exist on `Workspace`/`WorkspaceUpdate`.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Models/Workspace.cs`:

Add to `Workspace` record (alongside existing `Marketplaces` property):

```csharp
public IReadOnlyList<PluginRef>? PluginSelection { get; init; }

public int PluginsRevision { get; init; }
```

Add to `WorkspaceCreate`:

```csharp
public IReadOnlyList<PluginRef>? PluginSelection { get; init; }
```

Replace `WorkspaceUpdate` (add `using System.Text.Json.Serialization;` at the top of the file) with:

```csharp
public record WorkspaceUpdate
{
    public IReadOnlyList<string> Marketplaces { get; init; } = [];

    [JsonConverter(typeof(OptionalJsonConverterFactory))]
    public Optional<IReadOnlyList<PluginRef>?> PluginSelection { get; init; } = Optional<IReadOnlyList<PluginRef>?>.Unset;

    public int? PluginsRevision { get; init; }
}
```

Add to `WorkspaceView` (positional record — append two trailing parameters) and update `WorkspaceViewMapping.ToView` to pass them through from the source `Workspace`.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspaceTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Models/Workspace.cs tests/LmStreaming.Sample.Tests/Models/WorkspaceTests.cs
git commit -m "feat(sample-app): add tri-state plugin selection fields to workspace models"
```

---

## Task 8: `FileWorkspaceStore` CAS revision check + `WorkspaceRevisionConflictException`

**Files:**
- Modify: `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs`
- Modify: `samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs`
- Test: `tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs`

**Interfaces:**
- Consumes: `WorkspaceUpdate.PluginSelection`/`PluginsRevision` (Task 7).
- Produces: `WorkspaceRevisionConflictException(string workspaceId, int expectedRevision, int actualRevision)`. `FileWorkspaceStore.UpdateAsync` now bumps `PluginsRevision` only when `dto.PluginSelection.IsSet`, and throws `WorkspaceRevisionConflictException` whenever `dto.PluginSelection.IsSet` is true AND EITHER `dto.PluginsRevision` is missing (`null`) OR its value does not match `existing.PluginsRevision`. CAS is mandatory for any explicit plugin-selection change — there is no optional/bypassable revision check; an omitted `PluginsRevision` on an explicit selection change is rejected exactly like a stale one, using sentinel `expectedRevision: -1` (a value no real revision can ever equal, since revisions start at `0` and only increment) to distinguish "revision omitted entirely" from "revision stale" in the exception payload.
- Also produces: `SystemDefinedWorkspaceRule.ThrowIfSystemDefined(string workspaceId, bool isSystemDefined)` — the SHARED system-defined rule, appended as a static class at the end of the existing `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs` (shipped as part of that file, not a new file — see the shipped-vs-planned note at Step 3 below). `FileWorkspaceStore.UpdateAsync` has exactly two system-defined guards today (`FileWorkspaceStore.cs:145`, the default-id check before the lock, and `:161`, the `existing.IsSystemDefined` check after load); both are replaced by calls to this one helper. The orchestrator's early-out (Task 15) calls the SAME helper. It keeps throwing `InvalidOperationException` with the byte-identical message `Cannot update system-defined workspace '{id}'.` — the type is load-bearing (`WorkspacesController.cs:240`'s trailing `catch (InvalidOperationException)` is what produces the 400) and so is the string (it becomes the response body). Do NOT introduce a new derived exception type: `WorkspaceCatalogCorruptException` already derives from `InvalidOperationException` and the controller's catch ordering comment (`WorkspacesController.cs:235-237`) exists because of exactly that hazard.

- [ ] **Step 1: Write the failing tests** — append to `tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs`:

```csharp
[Fact]
public async Task UpdateAsync_OmittedPluginSelection_LeavesExistingSelectionUnchanged()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj", PluginSelection = [new PluginRef("official", "code-review")] });

    var updated = await store.UpdateAsync(created.Id, new WorkspaceUpdate { Marketplaces = ["a"] });

    updated.PluginSelection.Should().ContainSingle(p => p.Plugin == "code-review");
    updated.PluginsRevision.Should().Be(created.PluginsRevision);
}

[Fact]
public async Task UpdateAsync_ExplicitNullPluginSelection_ClearsToLegacyAll_AndBumpsRevision()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj", PluginSelection = [new PluginRef("official", "code-review")] });

    var updated = await store.UpdateAsync(
        created.Id,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>(null), PluginsRevision = created.PluginsRevision }
    );

    updated.PluginSelection.Should().BeNull();
    updated.PluginsRevision.Should().Be(created.PluginsRevision + 1);
}

[Fact]
public async Task UpdateAsync_ExplicitEmptyPluginSelection_SetsToNone_AndBumpsRevision()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

    var updated = await store.UpdateAsync(
        created.Id,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
    );

    updated.PluginSelection.Should().NotBeNull();
    updated.PluginSelection.Should().BeEmpty();
    updated.PluginsRevision.Should().Be(created.PluginsRevision + 1);
}

[Fact]
public async Task UpdateAsync_StalePluginsRevision_ThrowsRevisionConflict()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
    _ = await store.UpdateAsync(
        created.Id,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
    );

    var act = async () => await store.UpdateAsync(
        created.Id,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>(null), PluginsRevision = created.PluginsRevision }
    );

    await act.Should().ThrowAsync<WorkspaceRevisionConflictException>();
}

[Fact]
public async Task UpdateAsync_MarketplaceOnlyUpdate_DoesNotRequireOrCheckPluginsRevision()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

    var updated = await store.UpdateAsync(created.Id, new WorkspaceUpdate { Marketplaces = ["a"] });

    updated.Marketplaces.Should().Equal("a");
}

[Fact]
public async Task UpdateAsync_ExplicitPluginSelection_MissingRevision_ThrowsRevisionConflict()
{
    var store = new FileWorkspaceStore(NewTempDir());
    var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });

    // PluginSelection is explicitly set but PluginsRevision is omitted (null) — CAS is
    // mandatory for any explicit selection change, so this must reject, not silently overwrite.
    var act = async () => await store.UpdateAsync(
        created.Id,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]) }
    );

    await act.Should().ThrowAsync<WorkspaceRevisionConflictException>();
}

[Fact]
public async Task UpdateAsync_SystemDefinedWorkspace_ThrowsInvalidOperation_WithUnchangedMessage()
{
    var store = new FileWorkspaceStore(NewTempDir());

    var act = async () => await store.UpdateAsync(
        DefaultWorkspaceId,
        new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = 0 }
    );

    // The TYPE and the MESSAGE are both contract. WorkspacesController.cs:240's trailing
    // `catch (InvalidOperationException)` is what makes this a 400, and its body is `ex.Message` —
    // so introducing a new derived exception type or rewording the string would change an API
    // response. Asserted literally here AND in the orchestrator's early-out test (Task 15): two
    // call sites, one string, so a future edit to one cannot silently diverge.
    (await act.Should().ThrowAsync<InvalidOperationException>())
        .WithMessage($"Cannot update system-defined workspace '{DefaultWorkspaceId}'.");
}

[Fact]
public void ThrowIfSystemDefined_NonSystemDefinedWorkspace_DoesNotThrow()
{
    var act = () => SystemDefinedWorkspaceRule.ThrowIfSystemDefined("ws-1", isSystemDefined: false);

    act.Should().NotThrow();
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~FileWorkspaceStoreTests"`
Expected: FAIL — `WorkspaceCreate.PluginSelection`/`WorkspaceRevisionConflictException` do not resolve, and the CAS behavior does not exist yet.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs` — in `UpdateAsync`, between the existing `var existing = userWorkspaces[index];` / `if (existing.IsSystemDefined)` lines and the `updatedWorkspace` construction, insert:

```csharp
if (dto.PluginSelection.IsSet)
{
    WorkspaceRevisionConflictException.ThrowIfMismatch(id, dto.PluginsRevision, existing.PluginsRevision);
}
```

**Shipped-vs-planned note:** the two-branch "missing revision" / "stale revision" logic below is not
inlined at the call site — it lives inside a shared static `WorkspaceRevisionConflictException.ThrowIfMismatch(string workspaceId, int? suppliedRevision, int actualRevision)` method, appended to the bottom of `WorkspaceRevisionConflictException` itself (in this same file). The store above is only ONE of its two callers: the plugin-selection orchestrator (Task 15) calls the SAME method in its own early-out, before it snapshots partitions or waits for idle, for the same reason the system-defined rule is shared — a doomed request must be rejected before it does any of that work, not just at persist time. `ThrowIfMismatch`'s body is:

```csharp
public static void ThrowIfMismatch(string workspaceId, int? suppliedRevision, int actualRevision)
{
    // An omitted revision is ambiguous ("caller didn't know it" vs "caller doesn't care") and must
    // never silently overwrite a concurrent change — reject it exactly like a stale revision, using
    // sentinel -1 (no real revision can equal it) so the payload still distinguishes "omitted" from
    // "stale".
    if (suppliedRevision is not int expected)
    {
        throw new WorkspaceRevisionConflictException(workspaceId, expectedRevision: -1, actualRevision);
    }

    if (expected != actualRevision)
    {
        throw new WorkspaceRevisionConflictException(workspaceId, expected, actualRevision);
    }
}
```

Change the `updatedWorkspace` construction to:

```csharp
var updatedWorkspace = existing with
{
    Marketplaces = dto.Marketplaces ?? [],
    PluginSelection = dto.PluginSelection.IsSet ? dto.PluginSelection.Value : existing.PluginSelection,
    PluginsRevision = dto.PluginSelection.IsSet ? existing.PluginsRevision + 1 : existing.PluginsRevision,
    UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
};
```

Also update `CreateAsync` to persist `dto.PluginSelection` onto the newly created `Workspace` (defaulting `PluginsRevision` to `0`), matching how `Marketplaces` is already seeded there.

At the bottom of `FileWorkspaceStore.cs`, add the new exception. It is placed here — the file that throws it — rather than beside `WorkspaceCatalogCorruptException` (which lives in `GatewayWorkspaceCatalogResolver.cs`, a different file from where it's thrown), because this codebase does not have one single consistent convention for exception placement; co-locating with the throw site is the more locally consistent choice for a new exception:

```csharp
/// <summary>
/// Thrown when a workspace update with an explicit <c>PluginSelection</c> supplies a stale or
/// missing <c>pluginsRevision</c>. CAS is mandatory for any explicit plugin-selection change: a
/// missing revision is reported with <see cref="ExpectedRevision"/> equal to the sentinel <c>-1</c>
/// (no real revision can ever equal it) to distinguish "revision omitted entirely" from "revision
/// stale" (a real, mismatched, non-negative value). Only raised for updates that touch
/// <see cref="WorkspaceUpdate.PluginSelection"/>; marketplace-only updates never check the revision.
/// </summary>
public sealed class WorkspaceRevisionConflictException : Exception
{
    public WorkspaceRevisionConflictException(string workspaceId, int expectedRevision, int actualRevision)
        : base($"Workspace '{workspaceId}' plugins revision conflict: expected {expectedRevision}, actual {actualRevision}.")
    {
        WorkspaceId = workspaceId;
        ExpectedRevision = expectedRevision;
        ActualRevision = actualRevision;
    }

    public string WorkspaceId { get; }

    public int ExpectedRevision { get; }

    public int ActualRevision { get; }
}
```

**Shipped-vs-planned note:** this rule did not ship as its own file. It is appended as a static class at the end of the existing `samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs` — right after `WorkspaceRevisionConflictException` (see the note at Step 3 above) — so a reader expecting a separate `SystemDefinedWorkspaceGuard.cs` should not take its absence as a sign the file is missing.

The rule itself, as it appears at the bottom of `FileWorkspaceStore.cs`:

```csharp
namespace LmStreaming.Sample.Persistence;

/// <summary>
/// The one place that decides a workspace may not be mutated because it is system-defined, and the
/// one place that words the rejection.
/// <para>
/// Both halves are contract, not style. The type is what makes this a 400 —
/// <c>WorkspacesController</c>'s trailing <c>catch (InvalidOperationException)</c> is the mapping —
/// and the message becomes the response body verbatim. A new derived exception type would also sit
/// under <c>WorkspaceCatalogCorruptException</c>'s catch-ordering hazard, which that controller
/// already carries a comment about.
/// </para>
/// <para>
/// It is a shared helper rather than three copies of one <c>if</c> because the callers are far
/// apart — two store guards and an orchestrator early-out that must reject the same request before
/// it creates any gateway session — and duplicating a single rule across distant call sites is
/// precisely how the marketplace-resolution bug happened.
/// </para>
/// </summary>
internal static class SystemDefinedWorkspaceRule
{
    public static void ThrowIfSystemDefined(string workspaceId, bool isSystemDefined)
    {
        if (isSystemDefined)
        {
            throw new InvalidOperationException($"Cannot update system-defined workspace '{workspaceId}'.");
        }
    }
}
```

Then replace BOTH existing guards in `FileWorkspaceStore.UpdateAsync` with calls to it — `FileWorkspaceStore.cs:145` becomes
`SystemDefinedWorkspaceRule.ThrowIfSystemDefined(id, string.Equals(id, _defaultWorkspace.Id, StringComparison.Ordinal));`
and `:161` becomes
`SystemDefinedWorkspaceRule.ThrowIfSystemDefined(id, existing.IsSystemDefined);`.
Keep both sites — they answer different questions (the seeded default id before the file is loaded, and a persisted `IsSystemDefined` flag after). Only the rule and its wording are shared.

Modify `samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs` — add to `UpdateAsync`'s XML doc:
```csharp
/// <exception cref="WorkspaceRevisionConflictException">
/// <paramref name="dto"/> sets an explicit <c>PluginSelection</c> with a <c>PluginsRevision</c> that
/// does not match the workspace's current revision.
/// </exception>
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~FileWorkspaceStoreTests"`
Expected: PASS (all tests in the file, including the 8 new ones — 6 revision-CAS cases plus the 2 shared system-defined-rule cases).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Persistence/FileWorkspaceStore.cs samples/LmStreaming.Sample/Persistence/IWorkspaceStore.cs tests/LmStreaming.Sample.Tests/Persistence/FileWorkspaceStoreTests.cs
git commit -m "feat(sample-app): optimistic concurrency for workspace plugin-selection updates"
```

---

## Task 9: App `MarketplaceCatalog.Capabilities` passthrough

**Files:**
- Modify: `samples/LmStreaming.Sample/Models/MarketplaceCatalog.cs`
- Modify: `samples/LmStreaming.Sample/Services/MarketplaceCatalogClient.cs`
- Test: `tests/LmStreaming.Sample.Tests/Services/MarketplaceCatalogClientTests.cs`

**Interfaces:**
- Consumes: `SandboxMarketplaceCatalog.PluginFilteringSupported` (Task 4).
- Produces: `MarketplaceCapabilities(bool? PluginFiltering)`; `MarketplaceCatalog.Capabilities` of type `MarketplaceCapabilities`.

- [ ] **Step 1: Write the failing test** — create/extend `tests/LmStreaming.Sample.Tests/Services/MarketplaceCatalogClientTests.cs` with:

```csharp
[Fact]
public void Map_PropagatesPluginFilteringSupported_IntoCapabilities()
{
    var sdkCatalog = new SandboxMarketplaceCatalog(["official"], [], pluginFilteringSupported: true);

    var mapped = MarketplaceCatalogClient.Map(sdkCatalog);

    mapped.Capabilities.PluginFiltering.Should().BeTrue();
}

[Fact]
public void Map_NullPluginFilteringSupported_PropagatesAsNull()
{
    var sdkCatalog = new SandboxMarketplaceCatalog(["official"], []);

    var mapped = MarketplaceCatalogClient.Map(sdkCatalog);

    mapped.Capabilities.PluginFiltering.Should().BeNull();
}
```

(If `MarketplaceCatalogClientTests.cs` does not yet exist, create it with `namespace LmStreaming.Sample.Tests.Services;` and the two tests above as its full content.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~MarketplaceCatalogClientTests"`
Expected: FAIL — `MarketplaceCatalog.Capabilities` does not exist.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Models/MarketplaceCatalog.cs` — add trailing parameter and new record:

```csharp
public sealed record MarketplaceCatalog(
    IReadOnlyList<string> Selected,
    IReadOnlyList<MarketplaceEntry> Marketplaces,
    MarketplaceCapabilities Capabilities
);

public sealed record MarketplaceCapabilities(bool? PluginFiltering);
```

Modify `samples/LmStreaming.Sample/Services/MarketplaceCatalogClient.cs` — in the existing `Map(SandboxMarketplaceCatalog catalog)` method, add the new argument to the constructed `MarketplaceCatalog`:

```csharp
return new MarketplaceCatalog(
    catalog.Selected,
    entries,
    new MarketplaceCapabilities(catalog.PluginFilteringSupported)
);
```

(Keep the existing `entries`-building logic unchanged; only the final `MarketplaceCatalog` construction gains the new trailing argument.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~MarketplaceCatalogClientTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Models/MarketplaceCatalog.cs samples/LmStreaming.Sample/Services/MarketplaceCatalogClient.cs tests/LmStreaming.Sample.Tests/Services/MarketplaceCatalogClientTests.cs
git commit -m "feat(sample-app): surface plugin-filtering capability on the app marketplace catalog"
```

---

## Task 10: `WorkspaceCatalogCompatibilityService.ValidatePluginsForMutationAsync` + new exceptions

**Files:**
- Modify: `samples/LmStreaming.Sample/Services/WorkspaceCatalogCompatibilityService.cs`
- Test: `tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs`

**Interfaces:**
- Consumes: `MarketplaceCatalogClient` (Task 9), `PluginRef` (Task 6).
- Produces: `Task ValidatePluginsForMutationAsync(IReadOnlyList<string> marketplaces, IReadOnlyList<PluginRef>? pluginSelection, CancellationToken ct = default)` — a no-op when `pluginSelection is null` (legacy-all is always valid); for non-null selection, throws `UnsupportedWorkspacePluginsException` if any `{Marketplace, Plugin}` pair is not in the resolved catalog, and `GatewayPluginFilteringUnsupportedException` if the gateway's `PluginFilteringSupported` is not `true`. New exceptions `UnsupportedWorkspacePluginsException(IReadOnlyList<PluginRef> unsupportedPlugins, IReadOnlyList<PluginRef> availablePlugins)` and `GatewayPluginFilteringUnsupportedException()`.

- [ ] **Step 1: Write the failing tests** — append to `tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs` (following the file's existing mocking pattern for `MarketplaceCatalogClient`):

```csharp
[Fact]
public async Task ValidatePluginsForMutationAsync_NullSelection_IsAlwaysValid_NoCatalogCallNeeded()
{
    var service = CreateService(catalogAvailable: false);

    var act = async () => await service.ValidatePluginsForMutationAsync(["official"], null);

    await act.Should().NotThrowAsync();
}

[Fact]
public async Task ValidatePluginsForMutationAsync_GatewayDoesNotSupportPluginFiltering_ThrowsGatewayPluginFilteringUnsupported()
{
    var service = CreateService(pluginFilteringSupported: false);

    var act = async () => await service.ValidatePluginsForMutationAsync(
        ["official"],
        [new PluginRef("official", "code-review")]
    );

    await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
}

[Fact]
public async Task ValidatePluginsForMutationAsync_GatewayCapabilityUnknown_ThrowsGatewayPluginFilteringUnsupported()
{
    // null capability is treated the same as false: fail closed.
    var service = CreateService(pluginFilteringSupported: null);

    var act = async () => await service.ValidatePluginsForMutationAsync(
        ["official"],
        [new PluginRef("official", "code-review")]
    );

    await act.Should().ThrowAsync<GatewayPluginFilteringUnsupportedException>();
}

[Fact]
public async Task ValidatePluginsForMutationAsync_UnknownPlugin_ThrowsUnsupportedWorkspacePlugins()
{
    var service = CreateService(
        pluginFilteringSupported: true,
        availablePlugins: [new PluginRef("official", "code-review")]
    );

    var act = async () => await service.ValidatePluginsForMutationAsync(
        ["official"],
        [new PluginRef("official", "unknown-plugin")]
    );

    await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
}

[Fact]
public async Task ValidatePluginsForMutationAsync_KnownPluginsAndSupportedGateway_Succeeds()
{
    var service = CreateService(
        pluginFilteringSupported: true,
        availablePlugins: [new PluginRef("official", "code-review")]
    );

    var act = async () => await service.ValidatePluginsForMutationAsync(
        ["official"],
        [new PluginRef("official", "code-review")]
    );

    await act.Should().NotThrowAsync();
}

[Fact]
public async Task ValidatePluginsForMutationAsync_ExplicitEmptySelection_Succeeds_WhenGatewaySupports()
{
    var service = CreateService(pluginFilteringSupported: true);

    var act = async () => await service.ValidatePluginsForMutationAsync(["official"], []);

    await act.Should().NotThrowAsync();
}
```

Add a private `CreateService(bool catalogAvailable = true, bool? pluginFilteringSupported = true, IReadOnlyList<PluginRef>? availablePlugins = null)` helper to the test class, mirroring the existing helper used by the file's marketplace-validation tests, but stubbing `MarketplaceCatalogClient.GetCatalogAsync` to return a `MarketplaceCatalog` whose `Capabilities.PluginFiltering == pluginFilteringSupported` and whose marketplace entries expose `availablePlugins` (empty by default) as their plugin list. If the existing test file's helper does not currently return plugin data, extend it rather than duplicating a second helper.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~ValidatePluginsForMutationAsync"`
Expected: FAIL — `ValidatePluginsForMutationAsync`/new exceptions do not exist.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Services/WorkspaceCatalogCompatibilityService.cs`:

Extend the private `CatalogSnapshot` record with two new fields:

```csharp
private sealed record CatalogSnapshot(
    bool Available,
    IReadOnlyList<string> Aliases,
    string? Error,
    DateTimeOffset FetchedAt,
    IReadOnlyList<PluginRef> AvailablePlugins,
    bool? PluginFilteringSupported
);
```

Update `RefreshAsync` to also project the plugin list and capability flag from the fetched catalog:

```csharp
var aliases = catalog.Marketplaces.Select(x => x.Alias).Distinct(StringComparer.Ordinal).ToArray();
var availablePlugins = catalog.Marketplaces
    .SelectMany(m => m.Plugins.Select(p => new PluginRef(m.Alias, p.Name)))
    .ToArray();

return new CatalogSnapshot(true, aliases, null, _timeProvider.GetUtcNow(), availablePlugins, catalog.Capabilities.PluginFiltering);
```

(Adjust `m.Plugins`/`p.Name` to the exact property names already used by this file's existing `MarketplaceEntry`/plugin projection — reuse whatever accessor the file's current marketplace-alias projection uses as its sibling, rather than inventing new ones.)

Add the new public method:

```csharp
/// <summary>
/// Validates an explicit, non-null plugin selection against the current catalog. A <c>null</c>
/// <paramref name="pluginSelection"/> (legacy-all) is always valid and never touches the catalog —
/// only explicit selections are checked, per spec Section 8's fail-closed rule.
/// </summary>
public async Task ValidatePluginsForMutationAsync(
    IReadOnlyList<string> marketplaces,
    IReadOnlyList<PluginRef>? pluginSelection,
    CancellationToken ct = default
)
{
    if (pluginSelection is null)
    {
        return;
    }

    var snapshot = await GetCatalogAsync(ct).ConfigureAwait(false);

    if (snapshot.PluginFilteringSupported != true)
    {
        throw new GatewayPluginFilteringUnsupportedException();
    }

    var unsupported = pluginSelection
        .Where(p => !snapshot.AvailablePlugins.Contains(p))
        .ToArray();

    if (unsupported.Length > 0)
    {
        throw new UnsupportedWorkspacePluginsException(unsupported, snapshot.AvailablePlugins);
    }
}
```

At the bottom of the file, alongside the existing two exceptions, add:

```csharp
/// <summary>Thrown when a workspace's explicit plugin selection references a plugin the catalog does not offer.</summary>
public sealed class UnsupportedWorkspacePluginsException : Exception
{
    public UnsupportedWorkspacePluginsException(IReadOnlyList<PluginRef> unsupportedPlugins, IReadOnlyList<PluginRef> availablePlugins)
        : base($"Unsupported plugins: {string.Join(", ", unsupportedPlugins.Select(p => $"{p.Marketplace}/{p.Plugin}"))}")
    {
        UnsupportedPlugins = unsupportedPlugins;
        AvailablePlugins = availablePlugins;
    }

    public IReadOnlyList<PluginRef> UnsupportedPlugins { get; }

    public IReadOnlyList<PluginRef> AvailablePlugins { get; }
}

/// <summary>Thrown when an explicit plugin selection is supplied but the gateway does not (or is not known to) support plugin filtering.</summary>
public sealed class GatewayPluginFilteringUnsupportedException : Exception
{
    public GatewayPluginFilteringUnsupportedException()
        : base("The gateway does not support plugin filtering; an explicit plugin selection cannot be applied.")
    {
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~ValidatePluginsForMutationAsync"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Services/WorkspaceCatalogCompatibilityService.cs tests/LmStreaming.Sample.Tests/Services/WorkspaceCatalogCompatibilityServiceTests.cs
git commit -m "feat(sample-app): fail-closed plugin-selection compatibility validation"
```

---

## Task 11: `SandboxSessionRegistry` `WorkspaceRef`/`SandboxSession` plugin fields + `CreateSessionAsync` threading

**Files:**
- Modify: `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs`
- Test: `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs`

**Interfaces:**
- Consumes: `SandboxPluginRef` (Task 1), `SandboxCreateRequest.PluginSelection` (Task 2), `SandboxInfo.PluginResolution` (Task 3).
- Produces: `WorkspaceRef.PluginSelection` (`IReadOnlyList<SandboxPluginRef>?`, new trailing init-only property, default `null`); `SandboxSession.PluginResolution` (`SandboxPluginResolution?`, new trailing parameter, default `null`).

**Precondition (gateway-first gate, see Global Constraints):** this is the first task in the plan that threads a real, non-null `WorkspaceRef.PluginSelection` through to a live sandbox-create call against the gateway. Before starting this task, confirm a live-gateway verification record showing `capabilities.pluginFiltering == true` for the target gateway/marketplace configuration exists (per this repo's real-client-connectivity convention). Do not proceed with wiring `pluginSelection` end-to-end against a gateway that has not been confirmed to support it.

- [ ] **Step 1: Write the failing test** — append to `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs` (following that file's existing fake-gateway-based session-creation test pattern):

```csharp
[Fact]
public async Task CreateSessionAsync_ExplicitPluginSelectionOnWorkspaceRef_IsSentOnWireRequest()
{
    var (registry, gateway) = CreateRegistryWithFakeGateway();
    var workspaceRef = new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("official", "code-review")]);

    _ = await registry.GetOrCreateSessionAsync(workspaceRef);

    var sent = gateway.Requests.Single(r => r.Method == HttpMethod.Post);
    var body = JsonDocument.Parse(sent.Body!).RootElement;
    body.GetProperty("pluginSelection")[0].GetProperty("plugin").GetString().Should().Be("code-review");
}

[Fact]
public async Task CreateSessionAsync_ResponsePluginResolution_IsStoredOnSandboxSession()
{
    var (registry, gateway) = CreateRegistryWithFakeGatewayReturningPluginResolution();

    var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

    session.PluginResolution.Should().NotBeNull();
    session.PluginResolution!.Supported.Should().BeTrue();
}
```

(Use this test file's existing helpers for constructing a `SandboxSessionRegistry` against a fake gateway — mirror whichever helper method the file's current marketplace-wire tests already use for `CreateRegistryWithFakeGateway`, adding a `ReturningPluginResolution` variant that scripts a `pluginResolution` block in the create response JSON.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~PluginSelection|FullyQualifiedName~PluginResolution"`
Expected: FAIL — `WorkspaceRef` has no `PluginSelection` parameter, `SandboxSession` has no `PluginResolution` parameter.

- [ ] **Step 3: Implement.** Modify `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs`:

Extend the `WorkspaceRef` record (currently `public sealed record WorkspaceRef(string Id, string? DirectoryRelPath = null, IReadOnlyList<string>? Marketplaces = null);`):

```csharp
public sealed record WorkspaceRef(
    string Id,
    string? DirectoryRelPath = null,
    IReadOnlyList<string>? Marketplaces = null,
    IReadOnlyList<SandboxPluginRef>? PluginSelection = null
);
```

Extend the `SandboxSession` record (currently `public sealed record SandboxSession(string WorkspaceId, string SessionId, string WorkspaceRelPath, string HostPath);`):

```csharp
public sealed record SandboxSession(
    string WorkspaceId,
    string SessionId,
    string WorkspaceRelPath,
    string HostPath,
    SandboxPluginResolution? PluginResolution = null
);
```

In the private `CreateSessionAsync` method, at the exact marketplace-resolution block (previously confirmed lines 1114-1116) and `SandboxCreateRequest` construction (lines 1125-1131), add the plugin selection as a new argument:

```csharp
var request = new SandboxCreateRequest(
    workspaceRelPath,
    marketplaces: marketplaces,
    authProviders: authProviders,
    networkRules: networkRules,
    discovery: discovery,
    pluginSelection: workspaceRef.PluginSelection
);
```

(Keep every other existing argument in this call exactly as currently written — only the new trailing `pluginSelection:` argument is added.)

At the session construction (previously confirmed line 1243), add the new trailing argument sourced from the gateway response:

```csharp
var session = new SandboxSession(workspaceId, info.SessionId, workspaceRelPath, hostPath, info.PluginResolution);
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~PluginSelection|FullyQualifiedName~PluginResolution"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs
git commit -m "feat(lmagentinfra): thread plugin selection and resolution through sandbox session creation"
```

---

## Task 12: Stale-`WorkspaceRef` reload-callback fix

**Files:**
- Modify: `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs`
- Test: `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs`

**Interfaces:**
- Consumes: `WorkspaceRef` (Task 11), `IWorkspaceStore.GetAsync` (existing).
- Produces: new constructor parameter `Func<string, CancellationToken, Task<WorkspaceRef?>>? reloadWorkspaceRef = null` on `SandboxSessionRegistry`; when non-null, `GetOrCreateLiveSessionAsync`'s recreate branch calls it to fetch a fresh `WorkspaceRef` instead of reusing the stale captured `effectiveRef`.

- [ ] **Step 1: Write the failing test** — append to `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs`:

```csharp
[Fact]
public async Task GetOrCreateLiveSessionAsync_SessionGone_ReloadsWorkspaceRef_BeforeRecreating()
{
    var reloadCalls = new List<string>();
    Func<string, CancellationToken, Task<WorkspaceRef?>> reload = (id, _) =>
    {
        reloadCalls.Add(id);
        return Task.FromResult<WorkspaceRef?>(new WorkspaceRef(id, Marketplaces: ["updated-marketplace"]));
    };

    var (registry, gateway) = CreateRegistryWithFakeGatewayThatReports404OnLivenessCheck(reloadWorkspaceRef: reload);
    var staleRef = new WorkspaceRef("ws-1", Marketplaces: ["stale-marketplace"]);

    _ = await registry.GetOrCreateLiveSessionAsync(staleRef);

    reloadCalls.Should().ContainSingle().Which.Should().Be("ws-1");
    var recreateRequest = gateway.Requests.Where(r => r.Method == HttpMethod.Post).Last();
    var body = JsonDocument.Parse(recreateRequest.Body!).RootElement;
    body.GetProperty("marketplaces")[0].GetString().Should().Be("updated-marketplace");
}

[Fact]
public async Task GetOrCreateLiveSessionAsync_NoReloadCallbackConfigured_FallsBackToOriginalRef()
{
    var (registry, gateway) = CreateRegistryWithFakeGatewayThatReports404OnLivenessCheck(reloadWorkspaceRef: null);
    var originalRef = new WorkspaceRef("ws-1", Marketplaces: ["original-marketplace"]);

    _ = await registry.GetOrCreateLiveSessionAsync(originalRef);

    var recreateRequest = gateway.Requests.Where(r => r.Method == HttpMethod.Post).Last();
    var body = JsonDocument.Parse(recreateRequest.Body!).RootElement;
    body.GetProperty("marketplaces")[0].GetString().Should().Be("original-marketplace");
}
```

(Add `CreateRegistryWithFakeGatewayThatReports404OnLivenessCheck(Func<string, CancellationToken, Task<WorkspaceRef?>>? reloadWorkspaceRef)` as a new private helper in the test file, following the existing constructor-wiring helper pattern for this test class, scripting a liveness GET that 404s and a subsequent create POST that succeeds.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~ReloadsWorkspaceRef|FullyQualifiedName~FallsBackToOriginalRef"`
Expected: FAIL — no `reloadWorkspaceRef` constructor parameter exists.

- [ ] **Step 3: Implement.** Modify `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs`:

Add a new field and extend the constructor (currently `public SandboxSessionRegistry(SandboxGatewayLifetime gateway, SandboxGatewayOptions options, ILogger<SandboxSessionRegistry> logger, HttpClient httpClient, AuthOptions authOptions, SessionSecretStore sessionSecretStore, PredefinedKeyRegistry? predefinedKeys = null, MultiTurnLifecycleServices? lifecycle = null)`):

```csharp
private readonly Func<string, CancellationToken, Task<WorkspaceRef?>>? _reloadWorkspaceRef;

public SandboxSessionRegistry(
    SandboxGatewayLifetime gateway,
    SandboxGatewayOptions options,
    ILogger<SandboxSessionRegistry> logger,
    HttpClient httpClient,
    AuthOptions authOptions,
    SessionSecretStore sessionSecretStore,
    PredefinedKeyRegistry? predefinedKeys = null,
    MultiTurnLifecycleServices? lifecycle = null,
    Func<string, CancellationToken, Task<WorkspaceRef?>>? reloadWorkspaceRef = null
)
{
    // ... existing assignments unchanged ...
    _reloadWorkspaceRef = reloadWorkspaceRef;
}
```

In `GetOrCreateLiveSessionAsync`'s recreate branch (previously confirmed: `var effectiveRef = workspaceRef with { Id = workspaceId };` at line 887, reused at both line 890 and line 904), change the SECOND use (the recreate-after-404 call at line 904) to reload first:

```csharp
var refreshedRef = _reloadWorkspaceRef is null
    ? effectiveRef
    : await _reloadWorkspaceRef(workspaceId, ct).ConfigureAwait(false) ?? effectiveRef;

return await GetOrCreateSessionAsync(refreshedRef, ct, credential).ConfigureAwait(false);
```

(Leave the FIRST use at line 890 — the initial liveness-probe path — untouched; only the post-404 recreate path needs the reload, since that is the one that can otherwise silently commit a stale `Marketplaces`/`PluginSelection` snapshot into a brand-new session.)

Modify `samples/LmStreaming.Sample/Program.cs` — locate the existing `SandboxSessionRegistry` DI registration and add the new argument, resolving an `IWorkspaceStore` from the container:

```csharp
reloadWorkspaceRef: async (workspaceId, ct) =>
{
    var store = serviceProvider.GetRequiredService<IWorkspaceStore>();
    var workspace = await store.GetAsync(workspaceId).ConfigureAwait(false);
    return workspace is null
        ? null
        : new WorkspaceRef(workspace.Id, workspace.DirectoryRelPath, workspace.Marketplaces, ToSandboxPluginRefs(workspace.PluginSelection));
}
```

(Add a small private static `ToSandboxPluginRefs(IReadOnlyList<PluginRef>? selection)` mapping helper near this registration, converting the app's `PluginRef` (Task 6) to the SDK's `SandboxPluginRef` (Task 1), preserving null.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~ReloadsWorkspaceRef|FullyQualifiedName~FallsBackToOriginalRef"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs samples/LmStreaming.Sample/Program.cs tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryMarketplacesTests.cs
git commit -m "fix(lmagentinfra): reload workspace ref before recreating a dead sandbox session"
```

---

## Task 13: New exceptions `SandboxSessionRestartTimeoutException` / `SandboxSessionReplacementFailedException`

**Files:**
- Create: `src/LmAgentInfra/Sandbox/SandboxSessionRestartTimeoutException.cs`
- Create: `src/LmAgentInfra/Sandbox/SandboxSessionReplacementFailedException.cs`
- Test: `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionExceptionsTests.cs` (new)

**Interfaces:**
- Produces: `SandboxSessionRestartTimeoutException(string workspaceId, TimeSpan waited)`; `SandboxSessionReplacementFailedException(string workspaceId, Exception innerException)`.

- [ ] **Step 1: Write the failing tests** — create `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionExceptionsTests.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

public class SandboxSessionExceptionsTests
{
    [Fact]
    public void SandboxSessionRestartTimeoutException_ExposesWorkspaceIdAndWaited()
    {
        var exception = new SandboxSessionRestartTimeoutException("ws-1", TimeSpan.FromSeconds(30));

        exception.WorkspaceId.Should().Be("ws-1");
        exception.Waited.Should().Be(TimeSpan.FromSeconds(30));
        exception.Message.Should().Contain("ws-1");
    }

    [Fact]
    public void SandboxSessionReplacementFailedException_ExposesWorkspaceIdAndInnerException()
    {
        var inner = new InvalidOperationException("boom");

        var exception = new SandboxSessionReplacementFailedException("ws-1", inner);

        exception.WorkspaceId.Should().Be("ws-1");
        exception.InnerException.Should().BeSameAs(inner);
    }
}
```

(Match this test project's existing namespace convention for `tests/LmAgentInfra.Tests/Sandbox/*` — use whatever namespace `SandboxSessionUnavailableException`'s own tests file uses, if one exists, or the project's default root namespace otherwise.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~SandboxSessionExceptionsTests"`
Expected: FAIL — both exception types do not exist.

- [ ] **Step 3: Implement.** Create `src/LmAgentInfra/Sandbox/SandboxSessionRestartTimeoutException.cs`, mirroring `SandboxSessionUnavailableException.cs`'s exact style:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a plugin-selection-driven session migration could not proceed because an active run
/// on the workspace's sandbox never went idle within the bounded wait window (spec Section 7, step 3).
/// </summary>
public sealed class SandboxSessionRestartTimeoutException : Exception
{
    public SandboxSessionRestartTimeoutException(string workspaceId, TimeSpan waited)
        : base($"Workspace '{workspaceId}' still had an active run after waiting {waited} for it to go idle.")
    {
        WorkspaceId = workspaceId;
        Waited = waited;
    }

    public string WorkspaceId { get; }

    public TimeSpan Waited { get; }
}
```

Create `src/LmAgentInfra/Sandbox/SandboxSessionReplacementFailedException.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

/// <summary>
/// Thrown when a candidate sandbox session could not be created (or committed) during a plugin-selection
/// migration, after any partially-created candidates were aborted (spec Section 7, step 5).
/// </summary>
public sealed class SandboxSessionReplacementFailedException : Exception
{
    public SandboxSessionReplacementFailedException(string workspaceId, Exception innerException)
        : base($"Failed to replace sandbox session(s) for workspace '{workspaceId}'.", innerException)
    {
        WorkspaceId = workspaceId;
    }

    public string WorkspaceId { get; }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~SandboxSessionExceptionsTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/LmAgentInfra/Sandbox/SandboxSessionRestartTimeoutException.cs src/LmAgentInfra/Sandbox/SandboxSessionReplacementFailedException.cs tests/LmAgentInfra.Tests/Sandbox/SandboxSessionExceptionsTests.cs
git commit -m "feat(lmagentinfra): add exceptions for plugin-selection session migration failures"
```

---

## Task 14: `SandboxSessionRegistry.PluginSelection.cs` — prepare-then-replace primitives

**Files:**
- Modify: `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs` (add `partial` keyword)
- Create: `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.PluginSelection.cs`
- Test: `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryPluginSelectionTests.cs` (new)

**Interfaces:**
- Consumes: `WorkspaceRef.PluginSelection`/`SandboxSession.PluginResolution` (Task 11), `SandboxSessionRestartTimeoutException`/`SandboxSessionReplacementFailedException` (Task 13), private `CreateSessionAsync`/`DestroySessionAsync`/`EvictSessionStateAsync` (existing).
- Produces: `internal sealed record PluginSelectionPartition((string WorkspaceId, string AppId) Key, Lazy<Task<SandboxSession>> Entry, SandboxSession Session, SandboxCredential? Credential)`; `IReadOnlyList<PluginSelectionPartition> SnapshotPluginSelectionPartitions(string workspaceId)`; `Task<SandboxSession> CreatePluginSelectionCandidateAsync(WorkspaceRef newRef, PluginSelectionPartition partition, CancellationToken ct)`; `Task AbortPluginSelectionCandidateAsync(SandboxSession candidate)`; `IReadOnlyList<SandboxSession> SwapPluginSelectionSessions(IReadOnlyList<(PluginSelectionPartition Old, SandboxSession New)> commits)`; `Task RetirePluginSelectionSessionsAsync(IReadOnlyList<SandboxSession> oldSessions)`. `PluginSelectionPartition.Entry` carries the `Lazy<Task<SandboxSession>>` cache slot observed at snapshot time — the compare-and-swap witness `SwapPluginSelectionSessions` uses so a slot some other code path already replaced is skipped, not overwritten, with its candidate returned to the caller to retire. `PluginSelectionPartition.Credential` is captured explicitly at snapshot time and carried through to candidate creation — it is NEVER re-derived from `_sessionCredentials`/`CredentialFor` at candidate-creation time (see Task 14 rationale below for why that conditional-recovery pattern is a bug).
- Also produces: `internal sealed record PluginSelectionSnapshot(IReadOnlyList<PluginSelectionPartition> Partitions, IReadOnlyList<(string WorkspaceId, string AppId)> Unsettled)` and `internal Task<PluginSelectionSnapshot> SnapshotPluginSelectionPartitionsAsync(string workspaceId, TimeSpan settleBudget, CancellationToken ct)` — the bounded async settle that wraps the synchronous snapshot. The synchronous `SnapshotPluginSelectionPartitions` stays exactly as it is and remains the underlying enumeration; the settle exists because that enumeration is completed-only.
- Also produces: `internal IReadOnlyList<string> GetBoundThreads(string sessionId)` — the full idle-wait discovery union, computed INSIDE this one method: a `HashSet<string>` seeded from `GetThreads(sessionId)` (the `_sessionThreads` routing map, populated only for sub-agent-enabled conversations), with every established-binding thread whose `SessionId` matches `sessionId` added on top. Callers use `GetBoundThreads` alone — they do not separately call `GetThreads` and concatenate; there is no orchestrator-level union helper. `GetThreads` (`SandboxSessionRegistry.cs:2072-2083`) is unchanged and still reads only `_sessionThreads`.

- [ ] **Step 1: Write the failing tests** — create `tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryPluginSelectionTests.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Tests.Sandbox;

public class SandboxSessionRegistryPluginSelectionTests
{
    [Fact]
    public async Task SnapshotPluginSelectionPartitions_ReturnsOnePartitionPerCallerAppId()
    {
        var (registry, _) = CreateRegistryWithFakeGateway();
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-b"));
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-2"), credential: CredentialFor("app-a"));

        var partitions = registry.SnapshotPluginSelectionPartitions("ws-1");

        partitions.Should().HaveCount(2);
        partitions.Select(p => p.Key.AppId).Should().BeEquivalentTo(["app-a", "app-b"]);
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitions_CapturesCallerCredentialExplicitly()
    {
        var (registry, _) = CreateRegistryWithFakeGateway();
        var credential = CredentialFor("app-a");
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: credential);

        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        // The partition carries the caller's credential explicitly captured at snapshot time —
        // candidate creation must use THIS value, not a fresh lookup derived from the session id.
        partition.Credential.Should().BeSameAs(credential);
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_Success_CreatesNewSandboxSession_LeavingOldSessionUntouched()
    {
        var (registry, gateway) = CreateRegistryWithFakeGateway();
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        var candidate = await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: [new SandboxPluginRef("official", "code-review")]),
            partition,
            CancellationToken.None
        );

        candidate.SessionId.Should().NotBe(partition.Session.SessionId);
        gateway.Requests.Count(r => r.Method == HttpMethod.Delete).Should().Be(0);
    }

    [Fact]
    public async Task CreatePluginSelectionCandidateAsync_GatewayCreateFails_ThrowsWithoutPartialState()
    {
        var (registry, _) = CreateRegistryWithFakeGatewayThatFailsCreate();
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();

        var act = async () => await registry.CreatePluginSelectionCandidateAsync(
            new WorkspaceRef("ws-1", PluginSelection: []),
            partition,
            CancellationToken.None
        );

        await act.Should().ThrowAsync<SandboxException>();
    }

    [Fact]
    public async Task AbortPluginSelectionCandidateAsync_DestroysAndEvictsCandidate_NeverThrows()
    {
        var (registry, gateway) = CreateRegistryWithFakeGateway();
        var candidate = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        var act = async () => await registry.AbortPluginSelectionCandidateAsync(candidate);

        await act.Should().NotThrowAsync();
        gateway.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task SwapPluginSelectionSessions_ReplacesEntriesViaPerPartitionCompareAndSwap()
    {
        var (registry, _) = CreateRegistryWithFakeGateway();
        var oldSession = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        var partition = registry.SnapshotPluginSelectionPartitions("ws-1").Single();
        var newSession = partition.Session with { SessionId = "candidate-session" };

        registry.SwapPluginSelectionSessions([(partition, newSession)]);

        var current = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        current.SessionId.Should().Be("candidate-session");
    }

    [Fact]
    public async Task RetirePluginSelectionSessionsAsync_DestroysThenEvicts_BestEffort_NeverThrows()
    {
        var (registry, gateway) = CreateRegistryWithFakeGatewayThatFailsDelete();
        var oldSession = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));

        var act = async () => await registry.RetirePluginSelectionSessionsAsync([oldSession]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitionsAsync_CreationCompletingAfterTheSyncFilter_IsStillIncluded()
    {
        // The exact silent-drop this method exists for: the synchronous snapshot skips any slot that
        // is `!IsValueCreated || !IsCompletedSuccessfully`, so a create that lands a moment later is
        // invisible to the whole migration and keeps serving the OLD plugin set forever, with
        // nothing to distinguish it from a correctly migrated partition afterwards.
        var (registry, gateway) = CreateRegistryWithFakeGateway();
        gateway.HoldCreateUntilReleased();
        var pending = registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-a"));

        registry.SnapshotPluginSelectionPartitions("ws-1").Should().BeEmpty(); // RED without the settle
        gateway.ReleaseCreate();

        var snapshot = await registry.SnapshotPluginSelectionPartitionsAsync(
            "ws-1",
            TimeSpan.FromSeconds(2),
            CancellationToken.None
        );

        _ = await pending;
        snapshot.Partitions.Should().ContainSingle();
        snapshot.Unsettled.Should().BeEmpty();
    }

    [Fact]
    public async Task SnapshotPluginSelectionPartitionsAsync_SharesOneBudgetAcrossEntries_OneWedgedCreateDoesNotBlockTheRest()
    {
        // The budget is SHARED, not per-entry: total added latency stays bounded no matter how many
        // creates are in flight, and a single wedged create cannot hold the other partitions
        // hostage behind its own timeout. Asserting elapsed < 2x budget is what proves "shared"
        // rather than "per entry" — a per-entry budget passes the count assertions below.
        var (registry, gateway) = CreateRegistryWithFakeGateway();
        gateway.HoldCreateUntilReleased(appId: "app-wedged");
        _ = registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-wedged"));
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"), credential: CredentialFor("app-ok"));

        var budget = TimeSpan.FromMilliseconds(300);
        var elapsed = Stopwatch.StartNew();
        var snapshot = await registry.SnapshotPluginSelectionPartitionsAsync("ws-1", budget, CancellationToken.None);
        elapsed.Stop();

        snapshot.Partitions.Should().ContainSingle(p => p.Key.AppId == "app-ok");
        snapshot.Unsettled.Should().ContainSingle(k => k.AppId == "app-wedged");
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(600));
    }

    [Fact]
    public async Task GetBoundThreads_ReturnsThreadsKnownOnlyThroughAnEstablishedBinding()
    {
        // GetThreads reads _sessionThreads only, which is populated just for sub-agent-bearing
        // conversations. A conversation known only through its SandboxEstablishedBinding is absent
        // there — and an absent thread reads as IDLE — so the idle wait needs this second source.
        var (registry, _) = CreateRegistryWithFakeGateway();
        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef("ws-1"));
        PublishEstablishedBinding(registry, threadId: "thread-1", session);

        registry.GetThreads(session.SessionId).Should().NotContain("thread-1");
        registry.GetBoundThreads(session.SessionId).Should().Contain("thread-1");
    }

    [Fact]
    public async Task GetBoundThreads_UnknownSession_ReturnsEmpty_NeverThrows()
    {
        var (registry, _) = CreateRegistryWithFakeGateway();

        registry.GetBoundThreads("no-such-session").Should().BeEmpty();
    }
}
```

(Reuse this test class's fake-gateway helper conventions (`CreateRegistryWithFakeGateway`, `CredentialFor`) from `SandboxSessionRegistryMarketplacesTests.cs`; add the two new failure-mode helper variants — `CreateRegistryWithFakeGatewayThatFailsCreate` (POST returns 500) and `CreateRegistryWithFakeGatewayThatFailsDelete` (DELETE returns 500) — as small private methods in this new file, following the same construction pattern as the existing helper. The settle tests additionally need the fake gateway to be able to *hold* a create until released — `HoldCreateUntilReleased([appId])` / `ReleaseCreate()` over a `TaskCompletionSource` — because "creation still in flight" is the only state that reproduces the silent drop, and a helper that cannot produce that state makes the settle tests pass vacuously against the synchronous snapshot. `PublishEstablishedBinding` goes through the registry's existing `ISandboxBindingSink` surface rather than reaching into private state, so the test exercises the same publication path the agent pool uses.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~SandboxSessionRegistryPluginSelectionTests"`
Expected: FAIL — none of `SnapshotPluginSelectionPartitions`/`CreatePluginSelectionCandidateAsync`/`AbortPluginSelectionCandidateAsync`/`SwapPluginSelectionSessions`/`RetirePluginSelectionSessionsAsync` exist.

- [ ] **Step 3: Implement.** Modify `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs` — change the class declaration (previously confirmed line 143) to:

```csharp
public sealed partial class SandboxSessionRegistry : IAsyncDisposable, ISandboxBindingSink, IWorkspaceFileBrowser
```

Create `src/LmAgentInfra/Sandbox/SandboxSessionRegistry.PluginSelection.cs`:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Sandbox;

public sealed partial class SandboxSessionRegistry
{
    /// <summary>
    /// A single (workspace, caller app) session partition captured before a plugin-selection migration
    /// begins.
    /// </summary>
    /// <param name="Key">The <c>(WorkspaceId, AppId)</c> cache key this session is published under.</param>
    /// <param name="Entry">
    /// The exact cache slot observed at snapshot time. <see cref="SwapPluginSelectionSessions"/> uses
    /// it as the compare-and-swap witness, so a slot that some other code path replaced while the
    /// candidate was being created is never overwritten.
    /// </param>
    /// <param name="Session">The live session as of snapshot time.</param>
    /// <param name="Credential">
    /// Captured explicitly at snapshot time (see <see cref="SnapshotPluginSelectionPartitions"/>) and
    /// carried through unchanged to <see cref="CreatePluginSelectionCandidateAsync"/> — it is never
    /// re-derived from <c>_sessionCredentials</c>/<c>CredentialFor</c> at candidate-creation time.
    /// </param>
    internal sealed record PluginSelectionPartition(
        (string WorkspaceId, string AppId) Key,
        Lazy<Task<SandboxSession>> Entry,
        SandboxSession Session,
        SandboxCredential? Credential
    );

    /// <summary>
    /// Captures every currently-live session partition for <paramref name="workspaceId"/> — one per
    /// distinct caller app id that has an active session — as of the moment of the call. Later
    /// candidate creation and swap steps operate on this snapshot, not on a live re-query, so that a
    /// concurrent new session created mid-migration is not accidentally retired. Each partition's
    /// caller credential is captured here, explicitly, from <c>_sessionCredentials</c> — NOT
    /// conditionally re-derived later — so that a credential rotation or eviction racing the migration
    /// cannot silently swap in a different (or absent, falling back to <c>_defaultCredential</c>)
    /// caller identity for the candidate session.
    /// </summary>
    internal IReadOnlyList<PluginSelectionPartition> SnapshotPluginSelectionPartitions(string workspaceId)
    {
        // Normalize exactly as the resolve paths do before they key `_sessions`. Without this a blank
        // id matches no key and the migration reports success having changed nothing — a silent no-op
        // is the worst outcome for a user who just edited their plugin list.
        var effectiveWorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId;

        var partitions = new List<PluginSelectionPartition>();

        foreach (var entry in _sessions)
        {
            if (!string.Equals(entry.Key.WorkspaceId, effectiveWorkspaceId, StringComparison.Ordinal) || !entry.Value.IsValueCreated)
            {
                continue;
            }

            if (entry.Value.Value.IsCompletedSuccessfully)
            {
                var session = entry.Value.Value.Result;
                // MUST be `null`, not `default`, on a miss: SandboxCredential is a readonly record
                // struct, so `TryGetValue(out var credential)` leaves a ZERO-VALUED struct on a miss,
                // and passing that to a `SandboxCredential?` parameter yields a non-null nullable
                // wrapping a blank app id — CreateSessionAsync's `credential ?? _defaultCredential`
                // fallback would then never fire, and the candidate would be created under an empty
                // identity the gateway rejects.
                SandboxCredential? credential = _sessionCredentials.TryGetValue(session.SessionId, out var tracked)
                    ? tracked
                    : null;
                partitions.Add(new PluginSelectionPartition(entry.Key, entry.Value, session, credential));
            }
        }

        return partitions;
    }

    /// <summary>
    /// A settled view of a workspace's partitions: the ones that are ready to migrate, and the keys
    /// that were still creating when the budget ran out.
    /// </summary>
    internal sealed record PluginSelectionSnapshot(
        IReadOnlyList<PluginSelectionPartition> Partitions,
        IReadOnlyList<(string WorkspaceId, string AppId)> Unsettled
    );

    /// <summary>
    /// <see cref="SnapshotPluginSelectionPartitions"/>, but bounded-async: awaits creations that are
    /// still in flight so they can join this migration, and reports the ones that did not finish.
    /// <para>
    /// The synchronous snapshot skips any slot that is not already completed successfully, which is
    /// correct for what it does and wrong as a migration input: a creation that completes a
    /// millisecond after that filter runs is never given a candidate, never swapped, never retired,
    /// and keeps serving the OLD plugin set indefinitely — indistinguishable afterwards from a
    /// partition that migrated correctly. A silent, undiagnosable drop is the worst available
    /// outcome, so the migration pays a bounded wait to shrink it and then reconciles the remainder
    /// exactly once (see the orchestrator, Task 15).
    /// </para>
    /// <para>
    /// The budget is deliberately SHARED across entries rather than applied per entry. Total added
    /// latency for the user's PUT is then one budget regardless of how many creations are in flight,
    /// and one wedged create cannot hold every other partition hostage behind its own timeout — it
    /// simply lands in <see cref="PluginSelectionSnapshot.Unsettled"/> and the reconcile pass picks
    /// it up if it ever completes.
    /// </para>
    /// </summary>
    internal async Task<PluginSelectionSnapshot> SnapshotPluginSelectionPartitionsAsync(
        string workspaceId,
        TimeSpan settleBudget,
        CancellationToken ct
    )
    {
        var effectiveWorkspaceId = string.IsNullOrWhiteSpace(workspaceId) ? DefaultWorkspaceId : workspaceId;

        var inFlight = _sessions
            .Where(e =>
                string.Equals(e.Key.WorkspaceId, effectiveWorkspaceId, StringComparison.Ordinal)
                && e.Value.IsValueCreated
                && !e.Value.Value.IsCompleted
            )
            .ToList();

        if (inFlight.Count > 0)
        {
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(settleBudget);

            // ONE WhenAll against ONE deadline — this is what makes the budget shared. Awaiting each
            // entry with its own timeout would multiply the wait by the number of in-flight creates.
            // Failures are irrelevant here: a creation that faults simply never becomes a partition,
            // which the completed-only filter below already handles.
            var all = Task.WhenAll(inFlight.Select(e => Swallow(e.Value.Value)));
            try
            {
                await all.WaitAsync(budgetCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller's own cancellation still propagates; the budget expiring does not — an
                // expired budget is a normal outcome that produces an Unsettled list, not an error.
                ct.ThrowIfCancellationRequested();
            }
        }

        var partitions = SnapshotPluginSelectionPartitions(effectiveWorkspaceId);
        var settledKeys = partitions.Select(p => p.Key).ToHashSet();
        var unsettled = inFlight.Select(e => e.Key).Where(k => !settledKeys.Contains(k)).ToList();

        return new PluginSelectionSnapshot(partitions, unsettled);

        static async Task Swallow(Task<SandboxSession> task)
        {
            try
            {
                _ = await task.ConfigureAwait(false);
            }
            catch
            {
                // Deliberate: a failed creation is not this method's problem to report. It never
                // becomes a partition, and the caller that owns it already sees its own exception.
            }
        }
    }

    /// <summary>
    /// Returns every conversation thread bound to <paramref name="sessionId"/> by EITHER index: the
    /// <c>_sessionThreads</c> routing map (see <see cref="GetThreads"/>) or a published
    /// <see cref="SandboxEstablishedBinding"/> naming this session. Empty when nothing is bound.
    /// <para>
    /// The union is the point, and it is computed HERE — callers use this method alone rather than
    /// unioning <see cref="GetThreads"/> and a separate bindings lookup themselves.
    /// <c>_sessionThreads</c> is populated only when sub-agent options are present, so a plain
    /// workspace-mode conversation can hold a live session while appearing nowhere in
    /// <see cref="GetThreads"/>. Any caller that asks "is anyone still using this session?" and
    /// consults only <see cref="GetThreads"/> therefore gets a false negative — and because a thread
    /// absent from the pool is idle by definition, that false negative reads as "idle", which is
    /// precisely the answer that lets a session be torn down out from under a running turn.
    /// </para>
    /// <para>
    /// Use this for lifecycle decisions (idle waits, retirement). <see cref="GetThreads"/> remains
    /// correct for its own callers, which route gateway callbacks to the threads registered for
    /// routing — a deliberately narrower question.
    /// </para>
    /// </summary>
    internal IReadOnlyList<string> GetBoundThreads(string sessionId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        var threads = new HashSet<string>(GetThreads(sessionId), StringComparer.Ordinal);

        foreach (var binding in _establishedBindings)
        {
            if (string.Equals(binding.Value.SessionId, sessionId, StringComparison.Ordinal))
            {
                _ = threads.Add(binding.Key);
            }
        }

        return [.. threads];
    }

    /// <summary>
    /// Creates a brand-new sandbox session for <paramref name="partition"/>'s caller app id under    /// <paramref name="newRef"/>'s (updated) plugin selection, WITHOUT touching the registry's existing
    /// cached entry for that partition. Uses <paramref name="partition"/>'s explicitly-captured
    /// <see cref="PluginSelectionPartition.Credential"/> directly — deliberately NOT
    /// <c>CredentialFor(partition.Session.SessionId) ?? _defaultCredential</c>, which would silently
    /// substitute a different (or default) caller identity if the live credential map changed between
    /// snapshot and candidate creation. The workspace directory and marketplace scope are likewise
    /// pinned to what the session being replaced actually ran with whenever <paramref name="newRef"/>
    /// omits them — both are "omit ⇒ fall back to global configuration" fields inside
    /// <c>CreateSessionAsync</c>, so a caller that passes a ref carrying only the new plugin selection
    /// would otherwise silently move the replacement onto the default workspace directory and the
    /// globally-configured marketplaces, changing far more than the plugin set this migration exists
    /// to change. On failure, the caller is responsible for aborting any sibling candidates already
    /// created for other partitions (see spec Section 7, step 5).
    /// </summary>
    internal async Task<SandboxSession> CreatePluginSelectionCandidateAsync(
        WorkspaceRef newRef,
        PluginSelectionPartition partition,
        CancellationToken ct
    )
    {
        var candidateRef = newRef with
        {
            Id = partition.Key.WorkspaceId,
            DirectoryRelPath = string.IsNullOrWhiteSpace(newRef.DirectoryRelPath)
                ? partition.Session.WorkspaceRelPath
                : newRef.DirectoryRelPath,
            Marketplaces = newRef.Marketplaces is { Count: > 0 } ? newRef.Marketplaces : partition.Session.Marketplaces,
        };

        var candidate = await CreateSessionAsync(candidateRef, ct, partition.Credential).ConfigureAwait(false);

        // Drain the creation stash here. This path deliberately bypasses AwaitAndEvictOnFailureAsync —
        // there is no cache slot to evict on failure, because a candidate is not published until the
        // swap — and that method is otherwise the ONLY drain of _unreportedCreations. Skipping it would
        // strand the entry for the lifetime of the registry and suppress SandboxCreated for a session
        // that genuinely was created. No-throw by contract.
        await PublishPendingCreationAsync(candidate).ConfigureAwait(false);
        return candidate;
    }

    /// <summary>
    /// Best-effort teardown of a candidate session that will never be committed (a sibling candidate's
    /// creation failed, or the swap lost its race). Never throws — an orphaned candidate container is a
    /// cleanup nuisance, not a correctness problem, and must never mask the original failure that
    /// triggered the abort.
    /// </summary>
    internal Task AbortPluginSelectionCandidateAsync(SandboxSession candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return TearDownBestEffortAsync(candidate, "abort");
    }

    /// <summary>
    /// The commit point: republishes each partition's cache entry to point at its candidate. Each
    /// entry is swapped with a compare-and-swap (<c>ConcurrentDictionary.TryUpdate</c>) against the
    /// <see cref="PluginSelectionPartition.Entry"/> observed at snapshot time — not a lock — because
    /// candidate creation is seconds of gateway I/O during which the slot can be legitimately replaced
    /// by someone else, most commonly the gateway-404 recreate path, which invalidates the slot and
    /// republishes a brand-new session. An unconditional write there would drop that session on the
    /// floor: unreachable through the cache, absent from this migration's retire list, and therefore
    /// never deleted on the gateway. So a partition whose slot no longer holds the snapshotted entry is
    /// SKIPPED and its candidate returned to the caller to retire. Per-entry atomicity comes from
    /// <c>TryUpdate</c> itself; no reader of <c>_sessions</c> ever takes a lock. The batch as a whole is
    /// deliberately NOT atomic — a reader resolving one partition while this loop is mid-run can observe
    /// it migrated and another partition not — which is acceptable because both sessions are live and
    /// serve the same workspace; what must never happen, and does not, is a lost session.
    /// </summary>
    /// <returns>
    /// The candidates that could NOT be committed because their partition had moved on. The caller must
    /// retire these — they are live gateway sessions that nothing references.
    /// </returns>
    internal IReadOnlyList<SandboxSession> SwapPluginSelectionSessions(
        IReadOnlyList<(PluginSelectionPartition Old, SandboxSession New)> commits
    )
    {
        var uncommitted = new List<SandboxSession>();

        foreach (var (old, replacement) in commits)
        {
            var republished = new Lazy<Task<SandboxSession>>(
                () => Task.FromResult(replacement),
                LazyThreadSafetyMode.ExecutionAndPublication
            );

            // Reference comparison against the snapshotted Lazy: succeeds only if nothing replaced the
            // slot since the snapshot.
            if (!_sessions.TryUpdate(old.Key, republished, old.Entry))
            {
                uncommitted.Add(replacement);
            }
        }

        return uncommitted;
    }

    /// <summary>
    /// Best-effort teardown of every superseded OLD session after a successful swap, mirroring
    /// <see cref="DestroyWorkspaceSessionAsync"/>'s existing destroy-then-evict order rather than the
    /// narrower <see cref="InvalidateSessionAsync"/> path — this avoids orphaning old sandbox
    /// containers on the gateway. Never throws: the swap already committed, so a retire failure here
    /// must never be surfaced as a migration failure.
    /// </summary>
    internal async Task RetirePluginSelectionSessionsAsync(IReadOnlyList<SandboxSession> oldSessions)
    {
        ArgumentNullException.ThrowIfNull(oldSessions);

        foreach (var session in oldSessions)
        {
            await TearDownBestEffortAsync(session, "retire").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Destroys <paramref name="session"/> on the gateway and drops its per-session state, swallowing
    /// every failure. Shared by the abort and retire paths, whose contracts both promise not to throw.
    /// </summary>
    /// <remarks>
    /// Two things here are load-bearing.
    /// <para>
    /// <b>Destroy BEFORE evict.</b> The gateway DELETE resolves this session's creating credential
    /// through <c>_sessionCredentials</c>, which the eviction clears. Reversing the two sends the DELETE
    /// under the process-default app id, which the gateway rejects — leaking the very container this
    /// method exists to remove.
    /// </para>
    /// <para>
    /// <b>The catch is required, not defensive padding.</b> <c>DestroySessionAsync</c> swallows its own
    /// failures, but <c>EvictSessionStateAsync</c> does not: it reaches
    /// <c>DecrementSessionRefAndMaybeDispose</c>, whose final <c>Client.Dispose()</c>/
    /// <c>Transport.Dispose()</c> pair is unguarded — the same pair that <c>DisposeAsync</c> wraps
    /// per-entry precisely because it can throw. Without this catch, one failing session would abort the
    /// caller's loop and skip every remaining session, leaking their containers too.
    /// </para>
    /// </remarks>
    private async Task TearDownBestEffortAsync(SandboxSession session, string phase)
    {
        try
        {
            await DestroySessionAsync(session, CancellationToken.None).ConfigureAwait(false);
            await EvictSessionStateAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Best-effort {Phase} of sandbox session {SessionId} failed; continuing.",
                phase,
                session.SessionId
            );
        }
    }
}
```

(NOTE for the implementer: the abort and retire contracts both promise "never throws", so neither may
call `DestroySessionAsync`/`EvictSessionStateAsync` directly — `DestroySessionAsync` swallows its own
failures but `EvictSessionStateAsync` does not, so both go through the shared `TearDownBestEffortAsync`
above, which also pins the destroy-before-evict order the gateway DELETE depends on. `CredentialFor` is
intentionally NOT called from this file — see the rationale on `CreatePluginSelectionCandidateAsync`
above. The swap step touches only `_sessions`, via `TryUpdate`; it does not read or write
`_sessionsById`/`_sessionCredentials` — the compare-and-swap witness is the `Lazy<>` carried on
`PluginSelectionPartition.Entry`, not a separate id/credential remap. `GetBoundThreads` merges
`_sessionThreads` (via `GetThreads`) WITH `_establishedBindings` itself — the union is assembled
inside this one accessor, not by the orchestrator (Task 15); the orchestrator calls `GetBoundThreads`
alone and never re-derives the union from `GetThreads` separately.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmAgentInfra.Tests --filter "FullyQualifiedName~SandboxSessionRegistryPluginSelectionTests"`
Expected: PASS (11 tests — the original 7, plus 2 settle cases and 2 `GetBoundThreads` cases).

- [ ] **Step 5: Commit**

```bash
git add src/LmAgentInfra/Sandbox/SandboxSessionRegistry.cs src/LmAgentInfra/Sandbox/SandboxSessionRegistry.PluginSelection.cs tests/LmAgentInfra.Tests/Sandbox/SandboxSessionRegistryPluginSelectionTests.cs
git commit -m "feat(lmagentinfra): add prepare-then-replace session migration primitives"
```

---

## Task 15: `WorkspacePluginSelectionService` orchestrator (wait-for-idle + persist + migrate)

**Files:**
- Create: `samples/LmStreaming.Sample/Services/WorkspacePluginSelectionService.cs`
- Test: `tests/LmStreaming.Sample.Tests/Services/WorkspacePluginSelectionServiceTests.cs` (new)

**Interfaces:**
- Consumes: `IWorkspaceStore` (Task 8), `WorkspaceCatalogCompatibilityService.ValidatePluginsForMutationAsync` (Task 10), `SandboxSessionRegistry.SnapshotPluginSelectionPartitions`/`CreatePluginSelectionCandidateAsync`/`AbortPluginSelectionCandidateAsync`/`SwapPluginSelectionSessions`/`RetirePluginSelectionSessionsAsync`/`GetBoundThreads` (Task 14), a new narrow `IAgentRunActivityProbe` interface (implemented by `MultiTurnAgentPool`, NOT a direct concrete `MultiTurnAgentPool` dependency — `MultiTurnAgentPool` is `sealed` with no virtual members, so it cannot be mocked; the narrow interface is the mockable seam).
- Produces: `IAgentRunActivityProbe { bool IsRunInProgress(string threadId); }` (new — shipped at `src/LmAgentInfra/Agents/IAgentRunActivityProbe.cs`, namespace `AchieveAi.LmDotnetTools.LmAgentInfra.Agents`, NOT in the sample app; see the shipped-vs-planned note at Step 3 below; implemented by `MultiTurnAgentPool : IAsyncDisposable, IAgentRunActivityProbe`); `IWorkspacePluginSelectionService { Task<Workspace> ApplyPluginSelectionUpdateAsync(string workspaceId, WorkspaceUpdate dto, CancellationToken ct = default); }` (new, in `samples/LmStreaming.Sample/Services/IWorkspacePluginSelectionService.cs` — the narrow seam `WorkspacesController` (Task 16) depends on and its tests mock, since `WorkspacePluginSelectionService` itself is `sealed` with no virtual members and cannot be proxied by Moq); `WorkspacePluginSelectionService : IWorkspacePluginSelectionService` implements `Task<Workspace> ApplyPluginSelectionUpdateAsync(string workspaceId, WorkspaceUpdate dto, CancellationToken ct = default)` — validates, waits bounded-idle, prepares candidates, persists via CAS, swaps, retires; on EITHER candidate-creation failure OR CAS-persist failure, aborts every already-created candidate and leaves old sessions untouched (candidate-creation failure surfaces as `SandboxSessionReplacementFailedException`; a CAS-persist failure is bare-rethrown unwrapped, so `WorkspaceRevisionConflictException` still reaches the caller as itself). The idle-wait timeout (default 30s) and poll interval (default 200ms) are constructor-injectable so tests can exercise the timeout path without a real 30-second wait.
- Also produces, on the same orchestrator, the three lifecycle-race behaviours this flow needs to be safe (detailed after the main implementation block below):
  1. **System-defined and revision-conflict early-outs.** `SystemDefinedWorkspaceRule.ThrowIfSystemDefined` (Task 8) is called immediately after `ValidatePluginsForMutationAsync`, and `WorkspaceRevisionConflictException.ThrowIfMismatch` (Task 8) is called right after that — BOTH before the settle/snapshot, the idle wait, and candidate creation. Same exceptions, same messages, same status codes as the store's own checks — raised before any gateway work exists to undo.
  2. **Bounded post-commit retirement grace** for committed partitions' old sessions, under `CancellationToken.None`, retiring anyway on expiry with a warning, and running OUTSIDE the per-workspace gate. Uncommitted candidates retire immediately.
  3. **Bounded settle + exactly one reconcile pass**, consuming `SnapshotPluginSelectionPartitionsAsync` (Task 14). Settle budget is constructor-injectable alongside the idle-wait knobs; the grace budget is too.
- Consumes additionally: `SandboxSessionRegistry.SnapshotPluginSelectionPartitionsAsync`/`GetBoundThreads` (Task 14), `SystemDefinedWorkspaceRule` (Task 8).

- [ ] **Step 1: Write the failing tests** — create `tests/LmStreaming.Sample.Tests/Services/WorkspacePluginSelectionServiceTests.cs`:

```csharp
namespace LmStreaming.Sample.Tests.Services;

public class WorkspacePluginSelectionServiceTests
{
    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_NoActiveRuns_PersistsAndSwapsImmediately()
    {
        var (service, store, registry, pool) = CreateService(runInProgress: false);
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));

        var updated = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "code-review")]), PluginsRevision = created.PluginsRevision }
        );

        updated.PluginSelection.Should().ContainSingle(p => p.Plugin == "code-review");
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_ActiveRunNeverGoesIdle_ThrowsRestartTimeout()
    {
        // Use a REAL live registry session for the workspace — a partition list built from a
        // workspace with no sessions would make WaitForIdleAsync return immediately (vacuously
        // "idle"), never reaching the timeout path this test claims to exercise. The activity
        // probe is stubbed to always report "in progress" for every thread on this session, and
        // the service is constructed with a short idle-wait timeout/poll interval (injected, NOT
        // the 30s/200ms production defaults) so the test exercises the REAL internal timeout
        // mechanism instead of faking it via the caller's own cancellation token.
        var (service, store, registry, _) = CreateService(
            runInProgress: true,
            idleWaitTimeout: TimeSpan.FromMilliseconds(150),
            idlePollInterval: TimeSpan.FromMilliseconds(20)
        );
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision },
            CancellationToken.None
        );

        await act.Should().ThrowAsync<SandboxSessionRestartTimeoutException>();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_CallerCancellation_DuringIdleWait_ThrowsOperationCanceled_NotRestartTimeout()
    {
        // Distinguishes the two classifications of a cancelled idle-wait delay: this test cancels
        // the CALLER's token while the internal idle-wait timeout is generous, so the thrown
        // exception must be OperationCanceledException (propagated as-is), never
        // SandboxSessionRestartTimeoutException — proving WaitForIdleAsync actually distinguishes
        // "caller cancelled" from "internal timeout fired" rather than always reporting whichever
        // one happens to unwind first out of Task.Delay.
        var (service, store, registry, _) = CreateService(
            runInProgress: true,
            idleWaitTimeout: TimeSpan.FromSeconds(30),
            idlePollInterval: TimeSpan.FromMilliseconds(20)
        );
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        using var callerCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision },
            callerCts.Token
        );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_CandidateCreationFails_AbortsSiblingCandidates_AndThrowsReplacementFailed()
    {
        var (service, store, registry, _) = CreateServiceWithFailingCandidateCreation();
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        await act.Should().ThrowAsync<SandboxSessionReplacementFailedException>();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_TwoOverlappingCallsSameWorkspace_ExactlyOneCommits()
    {
        var (service, store, registry, _) = CreateService(runInProgress: false);
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));

        var updateA = new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "a")]), PluginsRevision = created.PluginsRevision };
        var updateB = new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "b")]), PluginsRevision = created.PluginsRevision };

        var results = await Task.WhenAll(
            SafeApply(service, created.Id, updateA),
            SafeApply(service, created.Id, updateB)
        );

        results.Count(r => r.Succeeded).Should().Be(1);
        results.Count(r => r.Conflicted).Should().Be(1);

        // The losing call's CAS-persist failure (WorkspaceRevisionConflictException) must abort its
        // already-created candidate session — no orphaned candidate survives a losing concurrent
        // update. The registry must reflect exactly the winner's single committed session for this
        // workspace, not two (winner + an un-aborted loser candidate).
        registry.SnapshotPluginSelectionPartitions(created.Id).Should().ContainSingle();
    }

    private static async Task<(bool Succeeded, bool Conflicted)> SafeApply(WorkspacePluginSelectionService service, string id, WorkspaceUpdate dto)
    {
        try
        {
            _ = await service.ApplyPluginSelectionUpdateAsync(id, dto);
            return (true, false);
        }
        catch (WorkspaceRevisionConflictException)
        {
            return (false, true);
        }
    }

    // ---- (1) system-defined early-out ----

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_SystemDefinedWorkspace_RejectsBeforeCreatingAnySession()
    {
        var (service, store, registry, _) = CreateService(runInProgress: false);
        var gateway = GatewayFor(registry);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(DefaultWorkspaceId));
        var createsBefore = gateway.Requests.Count(r => r.Method == HttpMethod.Post);

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            DefaultWorkspaceId,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = 0 }
        );

        // Same type and same string as the store's guard (Task 8) — this is the SHARED rule, and the
        // 400 body is the message, so both are asserted literally at both call sites.
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"Cannot update system-defined workspace '{DefaultWorkspaceId}'.");

        // The point of the early-out: ZERO gateway work. Asserting only "it threw" would pass
        // against today's behaviour, where the same 400 arrives after real sessions were created and
        // torn down again.
        gateway.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(createsBefore);
        gateway.Requests.Should().NotContain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_SystemDefinedWorkspace_WithBusyRun_Still400_NotRestartTimeout()
    {
        // Today a busy run turns this permanently-invalid request into a 503 sandbox_restart_timeout,
        // because the idle wait expires before the store ever gets to refuse the write.
        var (service, store, registry, _) = CreateService(
            runInProgress: true,
            idleWaitTimeout: TimeSpan.FromMilliseconds(150),
            idlePollInterval: TimeSpan.FromMilliseconds(20)
        );
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(DefaultWorkspaceId));

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            DefaultWorkspaceId,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = 0 }
        );

        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().NotThrowAsync<SandboxSessionRestartTimeoutException>();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_SystemDefinedWorkspace_WithStaleRevision_Still400_NotConflict()
    {
        // And a stale revision turns it into a 409. Three answers to one invalid request; the
        // early-out collapses them to one.
        var (service, _, registry, _) = CreateService(runInProgress: false);
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(DefaultWorkspaceId));

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            DefaultWorkspaceId,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = 99 }
        );

        await act.Should().ThrowAsync<InvalidOperationException>();
        await act.Should().NotThrowAsync<WorkspaceRevisionConflictException>();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_SystemDefinedWorkspace_WithUnknownPlugin_StillReportsUnsupportedPlugins()
    {
        // Precedence: catalog/plugin validation runs BEFORE the system-defined early-out, so
        // unsupported_plugins (400, with its own code and payload) is unchanged by this work.
        var (service, _, registry, _) = CreateServiceRejectingPlugins();

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            DefaultWorkspaceId,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "nope")]), PluginsRevision = 0 }
        );

        await act.Should().ThrowAsync<UnsupportedWorkspacePluginsException>();
    }

    // ---- (2) bounded post-commit retirement grace ----

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_RunStartsAfterIdleWait_GraceDelaysRetirement_ThenRetires()
    {
        // The window this grace exists for: the idle wait runs BEFORE candidate creation, and
        // candidate creation is seconds of sequential gateway I/O. A run that starts in between is
        // mid-flight when the swap commits.
        var (service, store, registry, probe) = CreateService(runInProgress: false, gracePeriod: TimeSpan.FromSeconds(2));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        var old = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        BindThread(registry, "thread-1", old);
        probe.OnCandidateCreated = () => probe.SetRunInProgress("thread-1", true);

        var apply = service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        await GatewayFor(registry).WaitForDeleteObservationWindowAsync();
        GatewayFor(registry).Requests.Should().NotContain(r => r.Path.Contains(old.SessionId) && r.Method == HttpMethod.Delete);

        probe.SetRunInProgress("thread-1", false);
        _ = await apply;
        GatewayFor(registry).Requests.Should().Contain(r => r.Path.Contains(old.SessionId) && r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_RunOutlastsGrace_RetiresAnyway_AndWarns()
    {
        // The honest residual, asserted rather than hoped for: the run is STILL in progress and the
        // session is destroyed regardless. An unbounded grace would let one stuck conversation pin a
        // gateway container for the life of the process, which is strictly worse than one failed run.
        var (service, store, registry, probe) = CreateService(runInProgress: true, gracePeriod: TimeSpan.FromMilliseconds(150));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        var old = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        BindThread(registry, "thread-1", old);
        probe.SetRunInProgress("thread-1", false); // idle for the pre-flight wait...
        probe.OnCandidateCreated = () => probe.SetRunInProgress("thread-1", true); // ...busy forever after

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        GatewayFor(registry).Requests.Should().Contain(r => r.Path.Contains(old.SessionId) && r.Method == HttpMethod.Delete);
        LogsOf(service).Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains(old.SessionId));
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_CallerCancelsAfterCommit_OldSessionIsStillDestroyed()
    {
        // Post-commit cleanup observes CancellationToken.None. A caller who disconnects must not be
        // able to skip deletion of a session that is already superseded.
        var (service, store, registry, probe) = CreateService(runInProgress: false, gracePeriod: TimeSpan.FromMilliseconds(200));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        var old = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        using var cts = new CancellationTokenSource();
        probe.OnPersistCommitted = cts.Cancel;

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision },
            cts.Token
        );

        GatewayFor(registry).Requests.Should().Contain(r => r.Path.Contains(old.SessionId) && r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_UncommittedCandidate_IsRetiredImmediately_WithNoGrace()
    {
        // Nothing references a candidate whose CAS lost, so no run can be mid-flight against it —
        // waiting would only widen the window in which a leaked container exists.
        var (service, store, registry, probe) = CreateService(runInProgress: false, gracePeriod: TimeSpan.FromSeconds(30));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        probe.OnCandidateCreated = () => ReplaceCacheSlot(registry, created.Id); // force the CAS to lose

        var elapsed = Stopwatch.StartNew();
        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );
        elapsed.Stop();

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        GatewayFor(registry).Requests.Should().Contain(r => r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_GraceDoesNotHoldThePerWorkspaceGate()
    {
        // The gate serializes MIGRATIONS. Holding it across a multi-second grace would make an
        // unrelated later edit queue behind some other conversation's teardown.
        var (service, store, registry, probe) = CreateService(runInProgress: false, gracePeriod: TimeSpan.FromSeconds(5));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        var old = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        BindThread(registry, "thread-1", old);
        probe.OnCandidateCreated = () => probe.SetRunInProgress("thread-1", true);

        var first = service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );
        await probe.GraceStarted;

        var second = service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "x")]), PluginsRevision = created.PluginsRevision + 1 }
        );

        (await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2)))).Should().BeSameAs(second);
        _ = await first;
    }

    // ---- (3) bounded settle + exactly one reconcile pass ----

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_CreationSettlingWithinBudget_IsMigratedInTheInitialBatch()
    {
        var (service, store, registry, probe) = CreateService(runInProgress: false, settleBudget: TimeSpan.FromSeconds(2));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        GatewayFor(registry).HoldCreateUntilReleased();
        var pending = registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        GatewayFor(registry).ReleaseCreateAfter(TimeSpan.FromMilliseconds(100));

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        _ = await pending;
        registry.SnapshotPluginSelectionPartitions(created.Id).Single().Session.PluginResolution.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_CreationSettlingAfterBudget_IsMigratedByTheSingleReconcilePass()
    {
        var (service, store, registry, probe) = CreateService(runInProgress: false, settleBudget: TimeSpan.FromMilliseconds(50));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        GatewayFor(registry).HoldCreateUntilReleased();
        var pending = registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        probe.OnPersistCommitted = () => GatewayFor(registry).ReleaseCreate();

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        _ = await pending;
        // Without reconcile this partition keeps the OLD plugin set forever, with nothing to
        // distinguish it from one that migrated correctly.
        registry.SnapshotPluginSelectionPartitions(created.Id).Single().Session.PluginResolution.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_StillUnsettledAfterReconcile_IsLoggedAsResidual_NotRetried()
    {
        var (service, store, registry, probe) = CreateService(runInProgress: false, settleBudget: TimeSpan.FromMilliseconds(50));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        GatewayFor(registry).HoldCreateUntilReleased(); // never released

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        LogsOf(service).Should().Contain(l => l.Level == LogLevel.Warning && l.Message.Contains("residual"));
        probe.ReconcilePassCount.Should().Be(1); // exactly one pass, no retry loop
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_ReconcileSeesMissingPluginResolution_TreatsItAsStale_AndMigrates()
    {
        // Fail closed: a session that cannot PROVE it is current is migrated. Re-migrating a current
        // session costs one create; leaving a stale one published silently breaks the selection the
        // user just saved.
        var (service, store, registry, probe) = CreateService(runInProgress: false, settleBudget: TimeSpan.FromMilliseconds(50));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        GatewayFor(registry).OmitPluginResolutionOnCreate();
        GatewayFor(registry).HoldCreateUntilReleased();
        var pending = registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        probe.OnPersistCommitted = () => GatewayFor(registry).ReleaseCreate();

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        _ = await pending;
        probe.ReconcileMigratedKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_ReconcileLosesTheCas_RetiresItsOwnCandidate_NotTheWinner()
    {
        var (service, store, registry, probe) = CreateService(runInProgress: false, settleBudget: TimeSpan.FromMilliseconds(50));
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        GatewayFor(registry).HoldCreateUntilReleased();
        var pending = registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        probe.OnPersistCommitted = () => GatewayFor(registry).ReleaseCreate();
        probe.OnReconcileCandidateCreated = () => ReplaceCacheSlot(registry, created.Id); // winner appears

        _ = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        var winner = registry.SnapshotPluginSelectionPartitions(created.Id).Single().Session;
        GatewayFor(registry).Requests.Should().NotContain(r => r.Path.Contains(winner.SessionId) && r.Method == HttpMethod.Delete);
        GatewayFor(registry).Requests.Should().Contain(r => r.Method == HttpMethod.Delete);
        _ = await pending;
    }

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_ReconcileThrows_UpdateStillSucceeds()
    {
        // After the commit point the contract is retire-eventually, not rollback: a cleanup failure
        // must never tell the caller to retry an update that already committed.
        var (service, store, registry, probe) = CreateService(runInProgress: false);
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        _ = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        probe.ThrowFromReconcile = new InvalidOperationException("boom");

        var updated = await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision }
        );

        updated.PluginSelection.Should().BeEmpty();
        LogsOf(service).Should().Contain(l => l.Level == LogLevel.Warning);
    }

    // ---- discovery union ----

    [Fact]
    public async Task ApplyPluginSelectionUpdateAsync_ThreadKnownOnlyThroughABinding_StillStallsTheIdleWait()
    {
        // RED against a GetThreads-only discovery, and that is the entire point of the test: an
        // unknown thread is reported IDLE (MultiTurnAgentPool.cs:1105-1113), so a binding-only
        // conversation would not stall the wait at all and its session would be torn down mid-run.
        var (service, store, registry, probe) = CreateService(
            runInProgress: true,
            idleWaitTimeout: TimeSpan.FromMilliseconds(150),
            idlePollInterval: TimeSpan.FromMilliseconds(20)
        );
        var created = await store.CreateAsync(new WorkspaceCreate { Name = "Proj" });
        var session = await registry.GetOrCreateSessionAsync(new WorkspaceRef(created.Id));
        PublishEstablishedBindingOnly(registry, threadId: "binding-only-thread", session);
        registry.GetThreads(session.SessionId).Should().NotContain("binding-only-thread");

        var act = async () => await service.ApplyPluginSelectionUpdateAsync(
            created.Id,
            new WorkspaceUpdate { Marketplaces = [], PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = created.PluginsRevision },
            CancellationToken.None
        );

        await act.Should().ThrowAsync<SandboxSessionRestartTimeoutException>();
    }
}
```

(Add private `CreateService(bool runInProgress, TimeSpan? idleWaitTimeout = null, TimeSpan? idlePollInterval = null, TimeSpan? gracePeriod = null, TimeSpan? settleBudget = null)` and `CreateServiceWithFailingCandidateCreation()` factory helpers to this test class, constructing a real `FileWorkspaceStore` against a temp directory, a real `SandboxSessionRegistry` against a fake gateway (reusing `SandboxSessionRegistryPluginSelectionTests`' fake-gateway helper conventions), a lightweight FAKE class implementing the new `IAgentRunActivityProbe` interface (NOT a `Mock<MultiTurnAgentPool>` — `MultiTurnAgentPool` is `sealed` with no virtual members, so Moq cannot proxy it at all) stubbed to return `runInProgress` for every thread id, and a `WorkspaceCatalogCompatibilityService` stubbed to always succeed validation. Pass `idleWaitTimeout ?? TimeSpan.FromSeconds(30)` / `idlePollInterval ?? TimeSpan.FromMilliseconds(200)` through to the `WorkspacePluginSelectionService` constructor so most tests exercise the production defaults while the two timeout-classification tests above override them to short values.

`CreateService` returns `(service, store, registry, probe)`. The `probe` is the same fake that answers `IsRunInProgress`, extended with the observation seams the lifecycle-race tests need, each of which exists because the behaviour under test is a *timing* property that cannot be provoked from the outside:
- `SetRunInProgress(threadId, bool)` — per-thread, not global, so a test can make one conversation busy while the migration proceeds.
- `OnCandidateCreated` / `OnPersistCommitted` / `OnReconcileCandidateCreated` callbacks — the three instants that define the windows this work closes. Without a hook at *candidate created* there is no way to start a run strictly between the idle wait and the swap, which is the exact window the grace exists for; without one at *persist committed* there is no way to distinguish pre-commit rollback from post-commit retire-eventually.
- `GraceStarted` (a `Task`) — lets the gate test observe that the grace is running rather than sleeping and hoping.
- `ReconcilePassCount` / `ReconcileMigratedKeys` / `ThrowFromReconcile` — assert "exactly one pass" and "a failing reconcile does not fail a committed update" directly, instead of inferring them from side effects.

The fake gateway needs `HoldCreateUntilReleased()` / `ReleaseCreate()` / `ReleaseCreateAfter(TimeSpan)` (Task 14 already adds the first two) and `OmitPluginResolutionOnCreate()`. `LogsOf(service)` reads the captured `ILogger` sink; add an `ILogger<WorkspacePluginSelectionService>` constructor parameter for it — the residual/expiry warnings are part of the contract here (they are the only evidence an operator gets that a run was cut off or a partition was left stale), so they are asserted, not incidental.

NOTE for the implementer: `SandboxSessionRegistry.GetThreads(sessionId)` returns only threads recorded in `_sessionThreads`, which is populated when sub-agent options are present — a session created via `GetOrCreateSessionAsync` and bound through `ISandboxBindingSink` alone is NOT in it (`SandboxSessionRegistry.cs:86-93`, `:2072-2083`). That is no longer just a test-setup caveat: it is the bug fixed by the union with `GetBoundThreads` (Task 14), and `ApplyPluginSelectionUpdateAsync_ThreadKnownOnlyThroughABinding_StillStallsTheIdleWait` above pins it. So the helpers must be able to produce BOTH shapes independently — `BindThread(registry, threadId, session)` for the `_sessionThreads` path and `PublishEstablishedBindingOnly(registry, threadId, session)` for the binding-only path. A helper that always does both would make the union test pass against a `GetThreads`-only implementation, which is precisely the false green this test exists to prevent.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspacePluginSelectionServiceTests"`
Expected: FAIL — `WorkspacePluginSelectionService` does not exist.

- [ ] **Step 3: Implement.** Create `src/LmAgentInfra/Agents/IAgentRunActivityProbe.cs`:

**Shipped-vs-planned note:** this interface does not live under the sample app. The sample already
depends on `LmAgentInfra` for `MultiTurnAgentPool` itself, so the seam that abstracts over it belongs
in the same project, next to the concrete type it is extracted from — a dependency from
`LmAgentInfra` back down to `LmStreaming.Sample` would invert the existing reference direction. The
shipped file is `src/LmAgentInfra/Agents/IAgentRunActivityProbe.cs`, namespace
`AchieveAi.LmDotnetTools.LmAgentInfra.Agents`:

```csharp
namespace AchieveAi.LmDotnetTools.LmAgentInfra.Agents;

/// <summary>
/// Narrow seam over "is this thread's agent run currently in progress" — deliberately extracted
/// instead of depending on the concrete, sealed <c>MultiTurnAgentPool</c> directly, so this
/// orchestrator's tests can fake the signal without Moq (which cannot mock a sealed class with no
/// virtual members).
/// </summary>
public interface IAgentRunActivityProbe
{
    bool IsRunInProgress(string threadId);
}
```

Modify `MultiTurnAgentPool`'s class declaration to also implement this interface (its existing `IsRunInProgress(string threadId)` method already satisfies the shape — no method body changes needed):

```csharp
public sealed class MultiTurnAgentPool : IAsyncDisposable, IAgentRunActivityProbe
```

Create `samples/LmStreaming.Sample/Services/IWorkspacePluginSelectionService.cs`:

```csharp
using LmStreaming.Sample.Models;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Narrow seam over the end-to-end plugin-selection update flow — deliberately extracted instead
/// of depending on the concrete, sealed <c>WorkspacePluginSelectionService</c> directly, so
/// <c>WorkspacesController</c>'s tests (Task 16) can mock this flow without Moq (which cannot mock
/// a sealed class with no virtual members).
/// </summary>
public interface IWorkspacePluginSelectionService
{
    Task<Workspace> ApplyPluginSelectionUpdateAsync(string workspaceId, WorkspaceUpdate dto, CancellationToken ct = default);
}
```

Create `samples/LmStreaming.Sample/Services/WorkspacePluginSelectionService.cs`:

```csharp
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
/// Owns the end-to-end plugin-selection update flow for a workspace: validate the requested
/// selection, wait (bounded) for every live sandbox session on the workspace to go idle, prepare
/// replacement sessions, persist the new selection with optimistic concurrency, swap the registry
/// over to the new sessions, then best-effort retire the old ones (spec Section 7). If EITHER
/// candidate creation OR the CAS-persist step fails, every already-created candidate for this call
/// is aborted and the ORIGINAL old sessions are left completely untouched — a CAS conflict is
/// bare-rethrown unwrapped (so it still surfaces as a 409), while a candidate-creation failure is
/// wrapped in <see cref="SandboxSessionReplacementFailedException"/>. Old sessions are only ever
/// retired AFTER the CAS-persist step and the in-memory swap have both already committed.
/// </summary>
public sealed class WorkspacePluginSelectionService : IWorkspacePluginSelectionService
{
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceCatalogCompatibilityService _compatibility;
    private readonly SandboxSessionRegistry _registry;
    private readonly IAgentRunActivityProbe _activityProbe;
    private readonly TimeSpan _idleWaitTimeout;
    private readonly TimeSpan _idlePollInterval;

    public WorkspacePluginSelectionService(
        IWorkspaceStore workspaceStore,
        WorkspaceCatalogCompatibilityService compatibilityService,
        SandboxSessionRegistry registry,
        IAgentRunActivityProbe activityProbe,
        TimeSpan? idleWaitTimeout = null,
        TimeSpan? idlePollInterval = null
    )
    {
        _store = workspaceStore;
        _compatibility = compatibilityService;
        _registry = registry;
        _activityProbe = activityProbe;
        _idleWaitTimeout = idleWaitTimeout ?? TimeSpan.FromSeconds(30);
        _idlePollInterval = idlePollInterval ?? TimeSpan.FromMilliseconds(200);
    }

    public async Task<Workspace> ApplyPluginSelectionUpdateAsync(string workspaceId, WorkspaceUpdate dto, CancellationToken ct = default)
    {
        await _compatibility.ValidatePluginsForMutationAsync(dto.Marketplaces, dto.PluginSelection.Value, ct).ConfigureAwait(false);

        var partitions = _registry.SnapshotPluginSelectionPartitions(workspaceId);

        await WaitForIdleAsync(partitions, ct).ConfigureAwait(false);

        // Build a FULL ref, not just the changed field. `CreatePluginSelectionCandidateAsync` falls
        // back to each partition's own live session when `DirectoryRelPath`/`Marketplaces` are omitted,
        // but that fallback is a safety net for a caller that couldn't do better, not a licence for
        // this orchestrator — which already has `_store` — to pass a deliberately partial ref.
        var workspace = await _store.GetAsync(workspaceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");
        var newRef = new WorkspaceRef(
            workspaceId,
            DirectoryRelPath: workspace.DirectoryRelPath,
            Marketplaces: dto.Marketplaces,
            PluginSelection: ToSandboxPluginRefs(dto.PluginSelection.Value)
        );
        var candidates = new List<(SandboxSessionRegistry.PluginSelectionPartition Old, SandboxSession New)>();

        try
        {
            foreach (var partition in partitions)
            {
                var candidate = await _registry.CreatePluginSelectionCandidateAsync(newRef, partition, ct).ConfigureAwait(false);
                candidates.Add((partition, candidate));
            }
        }
        catch (Exception ex)
        {
            await AbortAllAsync(candidates).ConfigureAwait(false);
            throw new SandboxSessionReplacementFailedException(workspaceId, ex);
        }

        Workspace updated;

        try
        {
            updated = await _store.UpdateAsync(workspaceId, dto).ConfigureAwait(false);
        }
        catch
        {
            // The CAS-persist step failed (most commonly WorkspaceRevisionConflictException, a losing
            // concurrent update) AFTER candidates were already created. Abort every candidate so no
            // orphaned sandbox session survives, then bare-rethrow — the caller must see the ORIGINAL
            // exception type (a WorkspaceRevisionConflictException stays a 409), not a wrapped
            // SandboxSessionReplacementFailedException. Old sessions are untouched: nothing below this
            // point has run yet.
            await AbortAllAsync(candidates).ConfigureAwait(false);
            throw;
        }

        // SwapPluginSelectionSessions is a per-partition compare-and-swap, not a batch lock: a
        // partition whose cache slot changed since the snapshot (most commonly the gateway-404
        // recreate path racing this migration) is SKIPPED and its candidate is handed back here — it
        // is still a live gateway session nothing references. Ignoring the return value would leak
        // that candidate as an orphaned container forever, and retiring EVERY partition's old session
        // (including a skipped one's) would destroy a session that is still published in the cache and
        // still serving traffic — a use-after-free at the session level. So: retire the old session
        // only for partitions that actually committed, and separately retire every uncommitted
        // candidate.
        var uncommitted = _registry.SwapPluginSelectionSessions(candidates);
        var uncommittedIds = uncommitted.Select(session => session.SessionId).ToHashSet();
        var committedOldSessions = candidates
            .Where(commit => !uncommittedIds.Contains(commit.New.SessionId))
            .Select(commit => commit.Old.Session)
            .ToList();

        await _registry.RetirePluginSelectionSessionsAsync(committedOldSessions).ConfigureAwait(false);
        await _registry.RetirePluginSelectionSessionsAsync(uncommitted).ConfigureAwait(false);

        return updated;
    }

    private async Task AbortAllAsync(List<(SandboxSessionRegistry.PluginSelectionPartition Old, SandboxSession New)> candidates)
    {
        foreach (var (_, candidate) in candidates)
        {
            await _registry.AbortPluginSelectionCandidateAsync(candidate).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Polls every partition's threads for an in-progress run via <see cref="IAgentRunActivityProbe.IsRunInProgress"/>
    /// — the only "is this thread active" signal that exists, since there is no persisted running-agent
    /// concept independent of a live pool entry. Bounded by <see cref="_idleWaitTimeout"/> so a stuck
    /// run cannot hang a plugin-selection update forever.
    /// </summary>
    private async Task WaitForIdleAsync(IReadOnlyList<SandboxSessionRegistry.PluginSelectionPartition> partitions, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_idleWaitTimeout);
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var anyInProgress = partitions
                .SelectMany(p => _registry.GetThreads(p.Session.SessionId))
                .Any(_activityProbe.IsRunInProgress);

            if (!anyInProgress)
            {
                return;
            }

            try
            {
                await Task.Delay(_idlePollInterval, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                // timeoutCts is linked to BOTH the caller's token and our own CancelAfter deadline, so
                // IsCancellationRequested alone can't say which one fired. Classify explicitly against
                // the caller's own token: if the CALLER cancelled, propagate that cancellation as-is
                // (never reinterpreted as a restart timeout); otherwise it was our internal idle-wait
                // deadline, which is the actual "run never went idle" condition this method reports.
                if (ct.IsCancellationRequested)
                {
                    throw;
                }

                throw new SandboxSessionRestartTimeoutException(partitions[0].Key.WorkspaceId, waited.Elapsed);
            }
        }
    }

    private static IReadOnlyList<SandboxPluginRef>? ToSandboxPluginRefs(IReadOnlyList<PluginRef>? selection) =>
        selection?.Select(p => new SandboxPluginRef(p.Marketplace, p.Plugin)).ToArray();
}
```

**Then apply the three lifecycle-race behaviours to the block above.** They are written as deltas against that flow rather than as a rewritten class, so the diff against the already-reviewed prepare-then-replace ordering stays readable and so each ordering constraint is stated exactly where it binds. Apply them in method order.

**Delta 0 — the per-workspace migration gate.** Split the public method into a gate wrapper plus a private `MigrateAsync` carrying the body shown above:

```csharp
    /// <summary>
    /// One gate per workspace, so two migrations of the same workspace never interleave.
    /// <para>
    /// The store's compare-and-swap alone would keep the PERSISTED state correct, but only by letting
    /// the loser get all the way to the persist call — after it had already built replacement sessions
    /// for every partition. Serializing here means the loser's revision check fails while it still
    /// holds no gateway resources, which is what makes "a stale request creates no candidates" true
    /// rather than merely "a stale request cleans up after itself".
    /// </para>
    /// <para>
    /// Entries are never removed: the workspace count is small and bounded, whereas evicting a gate
    /// another caller is about to await is a genuine race for no real gain.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _workspaceGates = new(StringComparer.Ordinal);

    public async Task<Workspace> ApplyPluginSelectionUpdateAsync(string workspaceId, WorkspaceUpdate dto, CancellationToken ct = default)
    {
        var gate = _workspaceGates.GetOrAdd(workspaceId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        PostCommitWork retirement;
        Workspace updated;
        try
        {
            (updated, retirement) = await MigrateAsync(workspaceId, dto, ct).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Release();
        }

        // OUTSIDE the gate, and deliberately so — see delta 3.
        await CompletePostCommitAsync(workspaceId, retirement).ConfigureAwait(false);
        return updated;
    }
```

`MigrateAsync` returns the work that must happen after the gate is dropped instead of doing it inline. Its return type is `(Workspace Updated, PostCommitWork Work)`:

```csharp
    private sealed record PostCommitWork(
        IReadOnlyList<SandboxSession> Uncommitted,
        IReadOnlyList<SandboxSession> Superseded,
        WorkspaceRef NewRef,
        IReadOnlyList<(string WorkspaceId, string AppId)> Unsettled,
        int CommittedRevision
    );
```

- `Uncommitted` — candidates whose per-partition CAS lost; nothing references them, so they retire immediately.
- `Superseded` — old sessions whose partitions DID commit; these get the bounded retirement grace, since a run may have started on one after the pre-commit idle wait passed.
- `NewRef` — the ref the candidates were created from, reused by reconcile so it compares against the very selection the batch was built from rather than a second copy that could drift.
- `Unsettled` — the partition keys the settle budget didn't cover (`settled.Unsettled` from Delta 2 below); owed exactly one reconcile pass.
- `CommittedRevision` — the `Workspace.PluginsRevision` this migration persisted, so the reconcile pass — which runs after the gate is released and can find a LATER migration has already taken it — can tell whether it still owns these partitions.

A migration that failed before the commit point returns an empty `PostCommitWork`: its cleanup is rollback, it already ran on the failure path, and it must stay inside the gate where the "no partial swap" guarantee is enforced.

`PostCommitWork` is constructed at the tail of the block above, after the store update and the session swap have both already happened — replace the two immediate `RetirePluginSelectionSessionsAsync` calls and the bare `return updated;` with:

```csharp
        var uncommitted = _registry.SwapPluginSelectionSessions(candidates);
        var uncommittedIds = uncommitted.Select(session => session.SessionId).ToHashSet();
        var committedOldSessions = candidates
            .Where(commit => !uncommittedIds.Contains(commit.New.SessionId))
            .Select(commit => commit.Old.Session)
            .ToList();

        var work = new PostCommitWork(
            Uncommitted: uncommitted,
            Superseded: committedOldSessions,
            NewRef: newRef,
            Unsettled: settled.Unsettled,
            CommittedRevision: updated.PluginsRevision
        );

        return (updated, work);
    }
```

**Delta 1 — system-defined early-out.** Hoist the `_store.GetAsync` call that currently sits *after* `WaitForIdleAsync` up to immediately after validation, and guard on the flag it returns. This is a move, not a second read: `newRef` already consumes the same `workspace` object further down.

```csharp
        await _compatibility.ValidatePluginsForMutationAsync(dto.Marketplaces, dto.PluginSelection.Value, ct).ConfigureAwait(false);

        var workspace = await _store.GetAsync(workspaceId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workspace '{workspaceId}' not found.");

        // A system-defined workspace can NEVER accept this update — the store refuses it
        // unconditionally (FileWorkspaceStore.cs:145 / :161), so nothing about the request, the
        // revision, or the sandbox state can make it succeed. Deciding that here, before the
        // revision check and before any gateway work exists, is what turns "400 after we created and
        // tore down real sessions" into "400 with no side effects at all". It also stabilises the
        // ANSWER: today a busy run makes this same request time out as a 503 sandbox_restart_timeout
        // and a stale revision makes it a 409, because both of those checks sit in front of the
        // store's guard.
        //
        // Placed AFTER plugin validation on purpose: unsupported_plugins keeps its precedence, so a
        // request that is invalid in both ways reports the selection problem, which is the one the
        // caller can act on.
        //
        // Shared rule, not a copy — SystemDefinedWorkspaceRule (Task 8) is the single implementation
        // called from here and from both store sites. Two copies of one rule is exactly how the
        // marketplace-resolution bug happened.
        SystemDefinedWorkspaceRule.ThrowIfSystemDefined(workspaceId, workspace.IsSystemDefined);
```

**Delta 2 — bounded settle instead of a plain snapshot, and the union idle-wait.** Replace the `SnapshotPluginSelectionPartitions` call and the `GetThreads` projection:

```csharp
        // The synchronous snapshot filters to COMPLETED creations only, so a session that is still
        // being created is silently absent — and a partition that is absent is not migrated, gets no
        // candidate, and keeps the old plugin set indefinitely with nothing recording that it was
        // skipped. Settling bounds the wait for those in-flight creations instead of dropping them.
        var settled = await _registry
            .SnapshotPluginSelectionPartitionsAsync(workspaceId, _settleBudget, ct)
            .ConfigureAwait(false);
        var partitions = settled.Partitions;

        await WaitForIdleAsync(workspaceId, partitions, ct).ConfigureAwait(false);
```

```csharp
    private bool IsSessionBusy(SandboxSession session) =>
        // GetBoundThreads IS the union — it walks _sessionThreads (populated only when sub-agent
        // options are present) AND SandboxEstablishedBinding ("the ONLY authoritative signal that a
        // conversation has an established sandbox workspace", SandboxSessionRegistry.cs:86-93)
        // internally, so calling it alone is correct; concatenating GetThreads on top would only
        // double-count the same thread ids. Reading GetThreads alone instead is the real failure
        // mode this guards against: MultiTurnAgentPool.GetRunStateInfo reports IsInProgress:false for
        // a thread id it does not know (MultiTurnAgentPool.cs:1105-1113), so a binding-only
        // conversation would read as affirmatively IDLE, the bounded wait would pass vacuously, and
        // its session would be torn down mid-run.
        _registry.GetBoundThreads(session.SessionId).Any(_activityProbe.IsRunInProgress);
```

…and use `partitions.Select(p => p.Session).Any(IsSessionBusy)` in `WaitForIdleAsync`.

**Delta 3 — post-commit: bounded retirement grace, then exactly one reconcile pass.** Everything below runs after the gate is released and under `CancellationToken.None`:

```csharp
    private async Task CompletePostCommitAsync(string workspaceId, PostCommitWork work)
    {
        // Uncommitted candidates go FIRST and with NO grace. Their CAS lost, so they are published
        // nowhere and referenced by nothing — no run can be mid-flight against one, and waiting would
        // only widen the window in which a leaked container exists.
        await RunPostCommitStageAsync(work.Uncommitted).ConfigureAwait(false);

        // Committed partitions' OLD sessions get the grace. The idle wait ran BEFORE candidate
        // creation, and candidate creation is seconds of sequential gateway I/O; nothing re-checks
        // idleness in between, so a run can legitimately have started after the wait passed and still
        // be live now.
        await RetireAfterGraceAsync(work.Superseded).ConfigureAwait(false);

        await ReconcileUnsettledOnceAsync(workspaceId, work).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits (bounded by <see cref="_retirementGrace"/>) for the sessions' bound conversations to go
    /// idle, then destroys them — <em>whether or not</em> they went idle.
    /// <para>
    /// Note the token: <see cref="CancellationToken.None"/>, never the caller's. Past the commit
    /// point a caller who disconnects must not be able to skip deletion of a session that is already
    /// superseded, which would leak a gateway container for the lifetime of the process. This matches
    /// the existing teardown path, which already runs entirely under
    /// <c>CancellationToken.None</c> and swallows every failure.
    /// </para>
    /// <para>
    /// On expiry: log a warning naming the session and the elapsed wait, then retire anyway. This is
    /// the honest limit of the design — a run that outlasts the grace HAS its session torn down
    /// underneath it and WILL fail. The grace narrows that window and bounds it; it does not close
    /// it. An unbounded grace would trade "one failed run" for "a container a permanently-busy
    /// conversation can pin forever", and interrupting the run instead is not available:
    /// <c>EnsureCurrentAgentAsync</c> deliberately does not do it (<c>MultiTurnAgentPool.cs</c>, doc
    /// comment 966-968).
    /// </para>
    /// </summary>
    private async Task RetireAfterGraceAsync(IReadOnlyList<SandboxSession> oldSessions)
    {
        if (oldSessions.Count == 0)
        {
            return;
        }

        var waited = System.Diagnostics.Stopwatch.StartNew();
        var deadline = waited.Elapsed + _retirementGrace;

        foreach (var session in oldSessions)
        {
            while (IsSessionBusy(session))
            {
                if (waited.Elapsed >= deadline)
                {
                    _logger.LogWarning(
                        "Retirement grace expired for sandbox session {SessionId} after {ElapsedMs}ms with a run still in progress; retiring anyway. That run will fail.",
                        session.SessionId,
                        (long)waited.Elapsed.TotalMilliseconds
                    );
                    break;
                }

                await Task.Delay(_idlePollInterval, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await RunPostCommitStageAsync(oldSessions).ConfigureAwait(false);
    }

    /// <summary>
    /// One pass. Not a loop, not a retry schedule.
    /// <para>
    /// Rebuilds only what the settle could not: partitions whose creation was still in flight when
    /// the budget expired, plus any partition that cannot prove it is current. It uses nothing new —
    /// the same <c>CreatePluginSelectionCandidateAsync</c> and the same per-partition CAS the main
    /// flow uses, via <c>ReconcilePartitionAsync</c> below.
    /// </para>
    /// </summary>
    private async Task ReconcileUnsettledOnceAsync(string workspaceId, PostCommitWork work)
    {
        if (work.Unsettled.Count == 0)
        {
            return;
        }

        try
        {
            // A later migration may have taken the workspace gate since, committed its own selection
            // and swapped in its own sessions — those belong to it, not to this pass. Measuring them
            // against `work.NewRef` below would judge them stale and destroy them via the
            // compare-and-swap, leaving the store on the newer selection and the live session on THIS
            // migration's older one, with nothing left to self-heal it. One read, one comparison,
            // before anything is snapshotted. A workspace that has since been deleted (null) is
            // likewise not ours to reconcile.
            var current = await _store.GetAsync(workspaceId, CancellationToken.None).ConfigureAwait(false);
            if (current is null || current.PluginsRevision != work.CommittedRevision)
            {
                return;
            }

            var owed = new HashSet<(string WorkspaceId, string AppId)>(work.Unsettled);

            // Re-snapshot rather than reuse the original: the whole point is to pick up creations that
            // completed AFTER the settle budget expired. THIS CALL MUST STAY SYNCHRONOUS, and nothing
            // may be awaited between it and the CAS inside `ReconcilePartitionAsync` below — the
            // partitions captured here carry the per-partition compare-and-swap witnesses, and an
            // await inserted in between would let some other writer republish a partition before this
            // pass judges it against a witness it no longer holds. This ordering is load-bearing, not
            // an artifact to "clean up" into an async snapshot call.
            var resnapshot = _registry.SnapshotPluginSelectionPartitions(workspaceId);

            var late = resnapshot
                .Where(partition =>
                    owed.Contains(partition.Key)
                    // Fail-closed lives INSIDE ReflectsPluginSelection, which returns false when
                    // partition.Session.PluginResolution is null: a session that cannot PROVE it
                    // already carries the new selection is treated as stale. The cost of being wrong
                    // is one redundant recreate; the cost of the opposite default is a session left on
                    // the old plugin set that looks migrated.
                    && !SandboxSessionRegistry.ReflectsPluginSelection(partition.Session, work.NewRef.PluginSelection)
                )
                .ToList();

            // An owed key STILL absent from this zero-budget resnapshot never settled: its creation
            // was in flight when the settle budget expired and is in flight now. This is the ONLY pass
            // there will be, so this is the last moment that residual can be named — an ABSENCE check
            // against `resnapshot`, not a `PluginResolution is null` filter, because a key can be
            // present and merely stale (handled by `late` above) without being unsettled. Emitted
            // BEFORE the loop so a partition failing mid-loop cannot suppress it.
            var neverSettled = owed.Where(key => !resnapshot.Any(partition => partition.Key == key)).ToList();
            if (neverSettled.Count > 0)
            {
                _logger.LogWarning(
                    "Post-commit reconcile pass for workspace {WorkspaceId} left {UnreconciledCount} partition(s) "
                        + "unreconciled: {UnreconciledPartitions}. Their sandbox sessions were still being created "
                        + "when the settle budget expired and had not appeared by the single reconcile pass, so "
                        + "they keep serving the previous plugin selection until something recreates them.",
                    workspaceId,
                    neverSettled.Count,
                    string.Join(", ", neverSettled.Select(key => $"{key.WorkspaceId}/{key.AppId}"))
                );
            }

            foreach (var partition in late)
            {
                await ReconcilePartitionAsync(workspaceId, work.NewRef, partition).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            // NEVER rethrow. The update committed; telling the caller it failed would invite a retry
            // of a write that already landed, and would report a 5xx for a request that succeeded.
            _logger.LogWarning(ex, "Post-commit reconcile pass for workspace {WorkspaceId} failed. The plugin selection is persisted and the migrated sessions are live; any session still on the previous selection stays that way until it is recreated.", workspaceId);
        }
    }
```

`ReconcilePartitionAsync(workspaceId, newRef, partition)` is the single-partition body: create one candidate, CAS it in, and either retire the losing candidate immediately (lost the swap — nothing references it) or retire the superseded old session through the same `RetireAfterGraceAsync` grace the main flow uses (won the swap). It carries its own try/catch (log-and-continue) so one partition's failure cannot abandon the partitions after it in this one-and-only pass. `RunPostCommitStageAsync` is a thin wrapper over each of the three post-commit phases that logs and swallows — failures after the commit point are cleanup failures, and the constraint above says they never become request failures. `IsSessionBusy(session)` is the same `GetBoundThreads`-alone check from delta 2, taken by session id.

Add `_settleBudget` (default 5s), `_retirementGrace` (default 30s), an injectable `postCommitScheduler` (defaults to dispatching `CompletePostCommitAsync` via `Task.Run`, off the request) and an `ILogger<WorkspacePluginSelectionService>` to the constructor alongside the existing idle-wait knobs; both budgets and the scheduler are injectable for the same reason the idle-wait timeout is — otherwise every grace test costs 30 real seconds, and every post-commit test has to wait on a real background dispatch.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspacePluginSelectionServiceTests"`
Expected: PASS (21 tests — the original 5, plus 4 system-defined early-out cases, 5 retirement-grace cases, 6 settle/reconcile cases, and 1 discovery-union case).

- [ ] **Step 5: Commit**

```bash
git add src/LmAgentInfra/Agents/IAgentRunActivityProbe.cs samples/LmStreaming.Sample/Services/IWorkspacePluginSelectionService.cs samples/LmStreaming.Sample/Services/WorkspacePluginSelectionService.cs src/LmAgentInfra/Agents/MultiTurnAgentPool.cs tests/LmStreaming.Sample.Tests/Services/WorkspacePluginSelectionServiceTests.cs
git commit -m "feat(sample-app): add plugin-selection orchestrator with bounded idle-wait migration"
```

---

## Task 16: `WorkspacesController` wiring (5 new catch blocks + orchestrator routing)

**Files:**
- Modify: `samples/LmStreaming.Sample/Controllers/WorkspacesController.cs`
- Modify: `samples/LmStreaming.Sample/Program.cs` (DI registration mapping `IWorkspacePluginSelectionService` → `WorkspacePluginSelectionService`)
- Test: `tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs`

**Interfaces:**
- Consumes: `IWorkspacePluginSelectionService.ApplyPluginSelectionUpdateAsync` (Task 15 — the controller depends on the interface, never the concrete sealed `WorkspacePluginSelectionService`), all 5 new exceptions (Tasks 8, 10, 13).
- Produces: `WorkspacesController.Update` routes to `IWorkspacePluginSelectionService` when `dto.PluginSelection.IsSet`, otherwise calls the existing `IWorkspaceStore.UpdateAsync` path unchanged; returns `unsupported_plugins` (400), `gateway_plugin_filtering_unsupported` (503), `workspace_revision_conflict` (409), `sandbox_restart_timeout` (503), `sandbox_replacement_failed` (502).

- [ ] **Step 1: Write the failing tests** — append to `tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs`:

```csharp
[Fact]
public async Task Update_UnsupportedPluginsException_Returns400WithCode()
{
    var controller = CreateControllerThrowing(new UnsupportedWorkspacePluginsException([new PluginRef("official", "bad")], []));

    var result = await controller.Update("ws-1", new WorkspaceUpdate { PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([new PluginRef("official", "bad")]) });

    var objectResult = result.Should().BeOfType<ObjectResult>().Subject;
    objectResult.StatusCode.Should().Be(400);
}

[Fact]
public async Task Update_GatewayPluginFilteringUnsupportedException_Returns503WithCode()
{
    var controller = CreateControllerThrowing(new GatewayPluginFilteringUnsupportedException());

    var result = await controller.Update("ws-1", new WorkspaceUpdate { PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]) });

    result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(503);
}

[Fact]
public async Task Update_WorkspaceRevisionConflictException_Returns409WithCode()
{
    var controller = CreateControllerThrowing(new WorkspaceRevisionConflictException("ws-1", 1, 2));

    var result = await controller.Update("ws-1", new WorkspaceUpdate { PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]), PluginsRevision = 1 });

    result.Should().BeOfType<ConflictObjectResult>();
}

[Fact]
public async Task Update_SandboxRestartTimeoutException_Returns503WithCode()
{
    var controller = CreateControllerThrowing(new SandboxSessionRestartTimeoutException("ws-1", TimeSpan.FromSeconds(30)));

    var result = await controller.Update("ws-1", new WorkspaceUpdate { PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]) });

    result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(503);
}

[Fact]
public async Task Update_SandboxReplacementFailedException_Returns502WithCode()
{
    var controller = CreateControllerThrowing(new SandboxSessionReplacementFailedException("ws-1", new InvalidOperationException()));

    var result = await controller.Update("ws-1", new WorkspaceUpdate { PluginSelection = new Optional<IReadOnlyList<PluginRef>?>([]) });

    result.Should().BeOfType<ObjectResult>().Subject.StatusCode.Should().Be(502);
}

[Fact]
public async Task Update_PluginSelectionUnset_DoesNotInvokeOrchestrator_UsesExistingMarketplaceOnlyPath()
{
    var (controller, orchestratorMock, storeMock) = CreateControllerWithMocks();

    _ = await controller.Update("ws-1", new WorkspaceUpdate { Marketplaces = ["a"] });

    orchestratorMock.Verify(o => o.ApplyPluginSelectionUpdateAsync(It.IsAny<string>(), It.IsAny<WorkspaceUpdate>(), It.IsAny<CancellationToken>()), Times.Never);
    storeMock.Verify(s => s.UpdateAsync("ws-1", It.IsAny<WorkspaceUpdate>()), Times.Once);
}
```

(Add `CreateControllerThrowing(Exception exception)` and `CreateControllerWithMocks()` private helpers to this test class, following the file's existing `Moq`-based controller construction pattern, mocking `IWorkspacePluginSelectionService` (the narrow interface from Task 15, NOT the concrete sealed `WorkspacePluginSelectionService`, which Moq cannot proxy) to throw/succeed as directed.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspacesControllerTests"`
Expected: FAIL — no catch blocks for the 5 new exceptions, no orchestrator routing.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Controllers/WorkspacesController.cs` — inject `IWorkspacePluginSelectionService` via constructor (alongside the existing injected services), and change the body of `Update` to branch:

```csharp
[HttpPut("{id}")]
public async Task<IActionResult> Update(string id, [FromBody] WorkspaceUpdate dto)
{
    try
    {
        var updated = dto.PluginSelection.IsSet
            ? await _pluginSelectionService.ApplyPluginSelectionUpdateAsync(id, dto)
            : await _workspaceStore.UpdateAsync(id, dto);

        return Ok(updated.ToView());
    }
    catch (KeyNotFoundException)
    {
        return NotFound();
    }
    catch (UnsupportedWorkspaceMarketplacesException ex)
    {
        return StatusCode(400, new { code = "unsupported_marketplaces", unsupportedMarketplaces = ex.UnsupportedMarketplaces, availableMarketplaces = ex.AvailableMarketplaces });
    }
    catch (UnsupportedWorkspacePluginsException ex)
    {
        return StatusCode(400, new { code = "unsupported_plugins", unsupportedPlugins = ex.UnsupportedPlugins, availablePlugins = ex.AvailablePlugins });
    }
    catch (WorkspaceGatewayCatalogUnavailableException)
    {
        return StatusCode(503, new { code = "gateway_catalog_unavailable" });
    }
    catch (GatewayPluginFilteringUnsupportedException)
    {
        return StatusCode(503, new { code = "gateway_plugin_filtering_unsupported" });
    }
    catch (WorkspaceCatalogCorruptException)
    {
        return CatalogUnavailable();
    }
    catch (WorkspaceRevisionConflictException ex)
    {
        return Conflict(new { code = "workspace_revision_conflict", expectedRevision = ex.ExpectedRevision, actualRevision = ex.ActualRevision });
    }
    catch (SandboxSessionRestartTimeoutException)
    {
        return StatusCode(503, new { code = "sandbox_restart_timeout" });
    }
    catch (SandboxSessionReplacementFailedException)
    {
        return StatusCode(502, new { code = "sandbox_replacement_failed" });
    }
    catch (InvalidOperationException ex)
    {
        return BadRequest(new { error = ex.Message });
    }
}
```

(Preserve every existing catch block's exact current body/order for the ones already present — `UnsupportedWorkspaceMarketplacesException`, `WorkspaceGatewayCatalogUnavailableException`, `WorkspaceCatalogCorruptException`, `KeyNotFoundException`, `InvalidOperationException` — inserting the 5 new ones adjacent to their nearest existing analog as shown above, keeping more specific catches before broader ones.)

Modify `samples/LmStreaming.Sample/Program.cs` to register the pairing in DI: `services.AddSingleton<WorkspacePluginSelectionService>(); services.AddSingleton<IWorkspacePluginSelectionService>(sp => sp.GetRequiredService<WorkspacePluginSelectionService>());` (or scoped, matching how `WorkspaceCatalogCompatibilityService` is already registered in this file) — the controller resolves the interface only.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~WorkspacesControllerTests"`
Expected: PASS (all tests in the file, including the 6 new ones).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Controllers/WorkspacesController.cs samples/LmStreaming.Sample/Program.cs tests/LmStreaming.Sample.Tests/Controllers/WorkspacesControllerTests.cs
git commit -m "feat(sample-app): wire plugin-selection updates and error codes into WorkspacesController"
```

---

## Task 17: `ConversationsController.SendMessage` `EnsureCurrentAgentAsync` parity fix

**Files:**
- Modify: `samples/LmStreaming.Sample/Controllers/ConversationsController.cs:687`
- Test: `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerTests.cs`

**Interfaces:**
- Consumes: `MultiTurnAgentPool.EnsureCurrentAgentAsync(string threadId, SandboxCredential? callerCredential = null, CancellationToken ct = default, bool replace = true)` (existing, at `MultiTurnAgentPool.cs:970`). `MultiTurnAgentPool` is `sealed` with no virtual members, so its tests construct a REAL pool instance (via its plain `Func<>`-delegate constructors) with fake `agentFactory`/`liveSessionResolver` delegates rather than a Moq mock.
- Produces: `SendMessage` calls `EnsureCurrentAgentAsync(threadId, ct: ct, replace: false)` immediately after `GetOrCreateAgent`, mirroring `ChatWebSocketManager.cs`'s per-message call (lines 896-898).

- [ ] **Step 1: Write the failing test** — append to `tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerTests.cs`:

```csharp
[Fact]
public async Task SendMessage_CallsEnsureCurrentAgentAsync_WithReplaceFalse_LikeWebSocketPath()
{
    // MultiTurnAgentPool is sealed with no virtual members, so Moq cannot mock it. Its
    // constructors accept plain Func<> delegates for agent creation and live-session resolution
    // (MultiTurnAgentPool.cs:224-320), so this test constructs a REAL pool with fake delegates
    // and observes EnsureCurrentAgentAsync's effect via a call counter on the fake
    // liveSessionResolver, instead of asserting against a mock's Verify.
    var resolverCallCount = 0;
    var binding = new SandboxEstablishedBinding(new WorkspaceRef("ws-1"), new SandboxCredential("session-cred"));

    var pool = CreateRealPoolWithFakeDelegates(
        agentFactory: (threadId, ct) => Task.FromResult(new AgentCreationResult(CreateFakeAgent(threadId), StagedBinding: binding)),
        liveSessionResolver: (threadId, credential, ct) =>
        {
            resolverCallCount++;
            return Task.FromResult(binding);
        }
    );

    var controller = CreateControllerWithPool(pool);

    // Positive control: GetOrCreateAgent alone (the pre-fix call graph) already resolves the live
    // session once while creating and staging a brand-new agent's binding. This baseline proves
    // the fake delegates are wired correctly and gives a non-zero floor to compare against below —
    // without it, an assertion of "resolver was called" could pass vacuously off of this
    // pre-existing call alone, never actually exercising the new call site this task adds.
    _ = await pool.GetOrCreateAgent("thread-baseline", CancellationToken.None);
    var baselineCallCount = resolverCallCount;

    resolverCallCount = 0;
    _ = await controller.SendMessage("thread-1", new SendMessageRequest { Content = "hi" });

    // If SendMessage only called GetOrCreateAgent (today's bug), resolverCallCount here would
    // land at the same baseline captured above. The fix adds an explicit
    // EnsureCurrentAgentAsync(threadId, ct: ct, replace: false) call immediately afterward, which
    // invokes the live-session resolver AGAIN — so the count for a single SendMessage call must
    // exceed the GetOrCreateAgent-only baseline, proving the new call site actually ran.
    resolverCallCount.Should().BeGreaterThan(baselineCallCount);
}
```

(Add `CreateRealPoolWithFakeDelegates(...)`, `CreateFakeAgent(threadId)`, and `CreateControllerWithPool(pool)` helpers to this test class: `CreateRealPoolWithFakeDelegates` constructs a genuine `MultiTurnAgentPool` (not a mock) using this test file's existing pool-construction conventions, substituting the given `agentFactory`/`liveSessionResolver` delegates for the real ones and leaving every other constructor dependency as whatever lightweight fake/stub this file already uses elsewhere. `CreateFakeAgent` returns a minimal usable fake agent instance so `SendMessage`'s subsequent logic does not null-ref. NOTE for the implementer: confirm whether `GetOrCreateAgent` itself invokes `liveSessionResolver` zero or one times on a brand-new thread id — the baseline capture above is written to tolerate either, since it only asserts a strict increase relative to whatever that baseline turns out to be, not an absolute count.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~SendMessage_CallsEnsureCurrentAgentAsync"`
Expected: FAIL — `SendMessage` never calls `EnsureCurrentAgentAsync`, so `resolverCallCount` after `SendMessage` stays at (or below) `baselineCallCount` instead of exceeding it.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/Controllers/ConversationsController.cs` — immediately after the existing `agentPool.GetOrCreateAgent(...)` call at line 687, insert:

```csharp
var refresh = await agentPool.EnsureCurrentAgentAsync(threadId, ct: ct, replace: false).ConfigureAwait(false);
agent = refresh.Agent;
```

(Match the exact local variable name already bound to the agent by `GetOrCreateAgent`'s result at this call site — reassign it via `refresh.Agent` so every subsequent line in `SendMessage` that uses the agent automatically observes a freshly-rebuilt one when a plugin-selection swap occurred.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/LmStreaming.Sample.Tests --filter "FullyQualifiedName~SendMessage_CallsEnsureCurrentAgentAsync"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/Controllers/ConversationsController.cs tests/LmStreaming.Sample.Tests/Controllers/ConversationsControllerTests.cs
git commit -m "fix(sample-app): call EnsureCurrentAgentAsync on REST send-message like the WebSocket path"
```

---

## Task 18: TS type updates (`workspace.ts`, `marketplace.ts`)

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/types/workspace.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/types/marketplace.ts`

**Interfaces:**
- Produces: `interface PluginRef { marketplace: string; plugin: string; }`; `Workspace.pluginSelection?: PluginRef[] | null`; `Workspace.pluginsRevision: number`; `WorkspaceUpdatePayload.pluginSelection?: PluginRef[] | null` (optional key, matching the tri-state: omit the key entirely for "unchanged"); `MarketplaceCatalog.capabilities: { pluginFiltering: boolean | null }`.

- [ ] **Step 1: Verify current shape compiles** — this task has no separate unit test file; it is verified via the project's type-checker in Step 2 immediately after editing, and consumed/exercised by Tasks 19-20's Vitest suites.

- [ ] **Step 2: Implement.** Modify `samples/LmStreaming.Sample/ClientApp/src/types/workspace.ts` — add:

```typescript
export interface PluginRef {
  marketplace: string;
  plugin: string;
}
```

Add to the existing `Workspace` interface:

```typescript
pluginSelection?: PluginRef[] | null;
pluginsRevision: number;
```

Add to the existing workspace-update payload interface (whatever it's currently named in this file, e.g. `WorkspaceUpdatePayload`):

```typescript
pluginSelection?: PluginRef[] | null;
pluginsRevision?: number;
```

(The `pluginSelection` key being OPTIONAL on the TS payload type mirrors the wire tri-state: omitting the key from the object sent to `JSON.stringify` means "unchanged"; setting it to `null` means legacy-all; setting it to `[]` or a list means the explicit value — callers must delete the key rather than set it to `undefined`, since `JSON.stringify` also omits `undefined`-valued keys, which happens to line up correctly here.)

Modify `samples/LmStreaming.Sample/ClientApp/src/types/marketplace.ts` — add to the existing `MarketplaceCatalog` interface:

```typescript
capabilities: {
  pluginFiltering: boolean | null;
};
```

- [ ] **Step 3: Run type-check to verify no regressions**

Run: `npm --prefix samples/LmStreaming.Sample/ClientApp run type-check`
Expected: PASS (no new errors; existing call sites constructing a `Workspace`/`MarketplaceCatalog` object literal without the new fields will fail if those fields are non-optional — keep `pluginSelection`/`capabilities` optional or supply defaults at every existing construction site touched by this task).

- [ ] **Step 4: Commit**

```bash
git add samples/LmStreaming.Sample/ClientApp/src/types/workspace.ts samples/LmStreaming.Sample/ClientApp/src/types/marketplace.ts
git commit -m "feat(sample-app-client): add plugin-selection and capability types"
```

---

## Task 19: `workspacesApi.ts` / `useWorkspaces.ts` 409-conflict handling and reload

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/services/workspacesApi.ts`
- Modify: `samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.ts`
- Test: `samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.test.ts`

**Interfaces:**
- Produces: `workspacesApi.updateWorkspace` throws a typed `WorkspaceRevisionConflictError { expectedRevision: number; actualRevision: number }` on HTTP 409; `useWorkspaces`'s update handler catches it, refetches the workspace list, and re-throws a user-facing error so the caller (UI) can prompt a retry with the fresh revision.

- [ ] **Step 1: Write the failing test** — append to `useWorkspaces.test.ts`:

```typescript
it('reloads the workspace list and rethrows on a 409 revision conflict', async () => {
  const conflictError = new WorkspaceRevisionConflictError(1, 2);
  vi.mocked(workspacesApi.updateWorkspace).mockRejectedValueOnce(conflictError);
  vi.mocked(workspacesApi.listWorkspaces).mockResolvedValue([{ id: 'ws-1', name: 'Proj', pluginsRevision: 2 } as Workspace]);

  const { updateWorkspace, workspaces, error } = useWorkspaces();
  await expect(updateWorkspace('ws-1', { pluginsRevision: 1 })).rejects.toThrow(WorkspaceRevisionConflictError);

  expect(workspacesApi.listWorkspaces).toHaveBeenCalled();
  expect(workspaces.value.find((w) => w.id === 'ws-1')?.pluginsRevision).toBe(2);
});
```

- [ ] **Step 2: Run to verify failure**

Run: `npm --prefix samples/LmStreaming.Sample/ClientApp test -- useWorkspaces`
Expected: FAIL — `WorkspaceRevisionConflictError` does not exist; no reload-on-409 behavior.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/ClientApp/src/services/workspacesApi.ts` — add:

```typescript
export class WorkspaceRevisionConflictError extends Error {
  constructor(public expectedRevision: number, public actualRevision: number) {
    super(`Workspace plugins revision conflict: expected ${expectedRevision}, actual ${actualRevision}`);
    this.name = 'WorkspaceRevisionConflictError';
  }
}
```

In the existing `updateWorkspace` function's error-handling branch (wherever this file currently inspects the response status for other typed errors, e.g. `unsupported_marketplaces`), add:

```typescript
if (response.status === 409) {
  const body = await response.json();
  throw new WorkspaceRevisionConflictError(body.expectedRevision, body.actualRevision);
}
```

Modify `samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.ts` — in the composable's `updateWorkspace` wrapper, catch and reload:

```typescript
async function updateWorkspace(id: string, payload: WorkspaceUpdatePayload) {
  try {
    return await workspacesApi.updateWorkspace(id, payload);
  } catch (err) {
    if (err instanceof WorkspaceRevisionConflictError) {
      workspaces.value = await workspacesApi.listWorkspaces();
    }
    throw err;
  }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `npm --prefix samples/LmStreaming.Sample/ClientApp test -- useWorkspaces`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/ClientApp/src/services/workspacesApi.ts samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.ts samples/LmStreaming.Sample/ClientApp/src/composables/useWorkspaces.test.ts
git commit -m "feat(sample-app-client): reload workspace state on plugins-revision conflict"
```

---

## Task 20: `WorkspaceSelector.vue` nested plugin checkboxes + capability gating

**Files:**
- Modify: `samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue`
- Test: `samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.test.ts`

**Interfaces:**
- Consumes: `PluginRef`, `Workspace.pluginSelection`/`pluginsRevision` (Task 18), `MarketplaceCatalog.capabilities.pluginFiltering` (Task 18).
- Produces: a nested checkbox list of plugins under each selected marketplace in both the create form (existing insertion point, lines 399-423) and the edit form (lines 475-499), disabled/hidden entirely when `capabilities.pluginFiltering !== true`; toggling a plugin checkbox updates the pending `pluginSelection` array (or sets it to `null` if every plugin is re-checked back to "all", matching the legacy-all tri-state — an explicit choice this task encodes as "all checked = send `null`" rather than an equivalent full explicit list, to prefer the simpler wire payload).

- [ ] **Step 1: Write the failing tests** — append to `WorkspaceSelector.test.ts`:

```typescript
it('hides plugin checkboxes when the gateway capability is not true', async () => {
  const wrapper = mountWithCatalog({ pluginFiltering: false }, { plugins: [{ name: 'code-review' }, { name: 'linter' }] });

  // Positive control: the marketplace's plugin data DID make it into the mounted component
  // (its name renders in the tree) before asserting the checkboxes are absent — otherwise this
  // assertion would pass vacuously even if the whole marketplace block failed to render for a
  // reason unrelated to the capability gate.
  expect(wrapper.text()).toContain('official');
  expect(wrapper.find('[data-plugin-checkbox="true"]').exists()).toBe(false);
});

it('shows a plugin checkbox per plugin in the selected marketplace when capability is true', async () => {
  const wrapper = mountWithCatalog({ pluginFiltering: true }, { plugins: [{ name: 'code-review' }, { name: 'linter' }] });

  const checkboxes = wrapper.findAll('[data-plugin-checkbox="true"]');
  expect(checkboxes).toHaveLength(2);
});

it('unchecking one plugin sends an explicit subset pluginSelection on save', async () => {
  const wrapper = mountWithCatalog({ pluginFiltering: true }, { plugins: [{ name: 'code-review' }, { name: 'linter' }] });

  await wrapper.find('[data-testid="plugin-checkbox-linter"]').setValue(false);
  await wrapper.find('[data-testid="save-button"]').trigger('click');

  expect(updateWorkspaceMock).toHaveBeenCalledWith(
    expect.anything(),
    expect.objectContaining({ pluginSelection: [{ marketplace: 'official', plugin: 'code-review' }], pluginsRevision: expect.any(Number) })
  );
});

it('re-checking every plugin back to all sends pluginSelection: null', async () => {
  const wrapper = mountWithCatalog(
    { pluginFiltering: true },
    { plugins: [{ name: 'code-review' }, { name: 'linter' }] },
    { pluginSelection: [{ marketplace: 'official', plugin: 'code-review' }] }
  );

  await wrapper.find('[data-testid="plugin-checkbox-linter"]').setValue(true);
  await wrapper.find('[data-testid="save-button"]').trigger('click');

  expect(updateWorkspaceMock).toHaveBeenCalledWith(
    expect.anything(),
    expect.objectContaining({ pluginSelection: null, pluginsRevision: expect.any(Number) })
  );
});
```

(Add a `mountWithCatalog(capabilities, marketplaceOverrides?, workspaceOverrides?)` helper to this test file mirroring the file's existing `mount`-with-stubbed-store pattern. NOTE for the implementer: the file's existing 4th assertion style test (confirmed in a prior research pass to use `toEqual` for comparing the payload sent to `updateWorkspace`) may need its expected object literal updated to include the new `pluginSelection`/`pluginsRevision` keys if it asserts the FULL payload shape via `toEqual` rather than `objectContaining` — check that test before this task's changes land, and switch it to `expect.objectContaining` if a strict `toEqual` would otherwise start failing merely because the payload interface grew new optional fields.)

- [ ] **Step 2: Run to verify failure**

Run: `npm --prefix samples/LmStreaming.Sample/ClientApp test -- WorkspaceSelector`
Expected: FAIL — no `data-plugin-checkbox="true"` elements exist yet.

- [ ] **Step 3: Implement.** Modify `samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue`:

In the `<script setup>` block, add reactive state and helpers:

```typescript
const pendingPluginSelection = ref<PluginRef[] | null>(null);

function isPluginChecked(marketplace: string, plugin: string): boolean {
  if (pendingPluginSelection.value === null) return true;
  return pendingPluginSelection.value.some((p) => p.marketplace === marketplace && p.plugin === plugin);
}

function togglePlugin(marketplace: string, plugin: string, allPlugins: PluginRef[], checked: boolean) {
  const current = pendingPluginSelection.value ?? [...allPlugins];
  const next = checked
    ? [...current, { marketplace, plugin }]
    : current.filter((p) => !(p.marketplace === marketplace && p.plugin === plugin));

  pendingPluginSelection.value = next.length === allPlugins.length ? null : next;
}
```

At the confirmed insertion points (create form lines 399-423, edit form lines 475-499), inside each marketplace's template block, add (once per selected marketplace, iterating its plugin list from the marketplace catalog):

```vue
<div v-if="catalog.capabilities.pluginFiltering === true" class="plugin-checkbox-list">
  <label v-for="plugin in marketplace.plugins" :key="plugin.name" class="plugin-checkbox">
    <input
      type="checkbox"
      :data-testid="`plugin-checkbox-${plugin.name}`"
      class="plugin-checkbox-input"
      data-plugin-checkbox="true"
      :checked="isPluginChecked(marketplace.alias, plugin.name)"
      @change="togglePlugin(marketplace.alias, plugin.name, allSelectedPlugins, ($event.target as HTMLInputElement).checked)"
    />
    {{ plugin.name }}
  </label>
</div>
```

(Every rendered checkbox carries both a unique `data-testid="plugin-checkbox-<name>"`, for targeted per-plugin toggling in tests, and the shared literal marker `data-plugin-checkbox="true"`, which is what the generic existence/count assertions above query — `@vue/test-utils`'s `find`/`findAll` need a literal attribute value to match against, so the marker cannot be a per-instance name.)

In the save handler (wherever this component currently constructs the `WorkspaceUpdatePayload` sent to `updateWorkspace`), add the field conditionally so an untouched (never-toggled) selector still omits the key entirely (leaving `pluginSelection` unset, i.e. unchanged):

```typescript
const payload: WorkspaceUpdatePayload = {
  marketplaces: selectedMarketplaces.value,
  ...(pluginSelectionTouched.value ? { pluginSelection: pendingPluginSelection.value, pluginsRevision: workspace.pluginsRevision } : {}),
};
```

(Add a `pluginSelectionTouched` ref, set to `true` inside `togglePlugin`, so a user who never interacts with any plugin checkbox does not accidentally send an explicit `pluginSelection` and trigger the revision-conflict/migration path for a marketplace-only edit.)

MarketplaceBrowser.vue is explicitly unchanged by this task.

- [ ] **Step 4: Run to verify pass**

Run: `npm --prefix samples/LmStreaming.Sample/ClientApp test -- WorkspaceSelector`
Expected: PASS (all tests in the file, including the 4 new ones).

- [ ] **Step 5: Commit**

```bash
git add samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.vue samples/LmStreaming.Sample/ClientApp/src/components/WorkspaceSelector.test.ts
git commit -m "feat(sample-app-client): add capability-gated nested plugin selection to WorkspaceSelector"
```

---

## Task 21: Doc fix — `SandboxWorkspaceGuide.md:291-294`

**Files:**
- Modify: `samples/LmStreaming.Sample/SandboxWorkspaceGuide.md:291-294`

**Interfaces:** None (documentation-only).

- [ ] **Step 1: Read the stale paragraph** — open `samples/LmStreaming.Sample/SandboxWorkspaceGuide.md` at lines 291-294 and identify the exact stale text (it currently describes workspace marketplace selection as the only per-workspace customization axis, omitting plugin selection entirely — read the live file at this exact range before editing, since its precise current wording must be replaced verbatim, not paraphrased around).

- [ ] **Step 2: Rewrite the paragraph** to describe the new tri-state plugin-selection behavior, e.g.:

```markdown
A workspace's marketplace list controls which plugin catalogs are available to it. Within those
marketplaces, a workspace may additionally restrict itself to an explicit subset of plugins: set
`pluginSelection` to `null` (or omit the field) to use every plugin from the workspace's configured
marketplaces (legacy behavior), to `[]` to run with no plugins, or to a list of `{marketplace, plugin}`
pairs to select an explicit subset. Explicit plugin selection requires the connected gateway to report
`capabilities.pluginFiltering: true`; older gateways silently keep using the legacy all-plugins
behavior regardless of any selection the app might otherwise send.
```

- [ ] **Step 3: Verify no other doc references the stale paragraph's wording** — search this file and its neighbors for any other reference to marketplace-only behavior that would now be inaccurate:

Run: `grep -rn "marketplace" samples/LmStreaming.Sample/SandboxWorkspaceGuide.md`

Expected: any remaining marketplace-only claims either still accurate (marketplace selection is unchanged) or updated in this same step if they imply plugins can't be restricted.

- [ ] **Step 4: Commit**

```bash
git add samples/LmStreaming.Sample/SandboxWorkspaceGuide.md
git commit -m "docs(sample-app): document tri-state plugin selection in the workspace guide"
```

---

## Self-Review

**1. Spec coverage:**
- Section 5.1 (plugin identity) — Task 1 (SDK), Task 6 (app).
- Section 5.2 (workspace persistence tri-state, `pluginsRevision`) — Tasks 7, 8.
- Section 5.3 (sandbox create/response `pluginSelection`/`pluginResolution` — NOT the reserved gateway `plugins` mount-data key) — Tasks 2, 3, 11.
- Section 5.4 (gateway capability signal) — Task 4, 9.
- Section 5.5 (5 error codes) — Tasks 8, 10, 13, 16.
- Section 7 (prepare-then-replace 8-step flow, abort-all-candidates-on-any-failure symmetry) — Tasks 14, 15.
- Section 8 (concurrency/fail-closed, `pluginsRevision` CAS mandatory-when-set) — Tasks 8, 10, 15 (idle-wait), Global Constraints.
- Section 9 (backward compatibility) — Task 5 (`Optional<T>` omitted-vs-null), Task 8 (marketplace-only updates unaffected).
- Section 11 (docs) — Task 21.
- Section 12 (tests) — every task's own test file, plus Task 12's explicit stale-ref regression test.
- Stale-`WorkspaceRef` bug (Section 4.2) — Task 12.
- Agent-refresh gate gap (Section 4.3, `ConversationsController`) — Task 17.
- Gateway-first merge/deployment gate (a live-gateway verification record showing `capabilities.pluginFiltering: true` required before merging/deploying any task that emits `pluginSelection`) — Global Constraints, and as an explicit precondition on Task 11.
- No gap identified against the spec's numbered sections.

**2. Placeholder scan:** No "TBD"/"similar to Task N"/unshown code blocks remain; every step includes literal, runnable code or an exact command. Two explicit "verify exact signature before editing" notes were left in Tasks 3 and 14 rather than removed — these are intentional engineering cautions (the implementer must re-read a live file before a multi-line edit whose neighboring, unquoted code wasn't independently re-confirmed byte-for-byte in this pass), not placeholders for missing content; all code shown is concrete and complete as written. Task 20's `data-plugin-checkbox="true"` marker is a literal attribute value used verbatim by both the component and its tests, not a placeholder.

**3. Type consistency:** `SandboxPluginRef` (SDK, Task 1) vs `PluginRef` (app, Task 6) — kept deliberately distinct across all 21 tasks, with explicit mapping functions (`ToSandboxPluginRefs` in Tasks 12 and 15) at every layer boundary; no task accidentally uses one where the other is expected. `Optional<T>` (Task 5) is used consistently as `Optional<IReadOnlyList<PluginRef>?>` for `WorkspaceUpdate.PluginSelection` in Tasks 7, 8, 15, 16. `WorkspaceRevisionConflictException`'s constructor signature `(string workspaceId, int expectedRevision, int actualRevision)` (Task 8) matches its usage in Task 16's catch block and Task 15's orchestrator; its `expectedRevision: -1` sentinel (Task 8) distinguishes an omitted `pluginsRevision` from a real, stale one and is only ever produced by `FileWorkspaceStore.UpdateAsync`, never fabricated at a call site. `SnapshotPluginSelectionPartitions`/`CreatePluginSelectionCandidateAsync`/`AbortPluginSelectionCandidateAsync`/`SwapPluginSelectionSessions`/`RetirePluginSelectionSessionsAsync` (Task 14) are named and typed identically at their Task 15 call sites; `PluginSelectionPartition`'s added `Credential` field (Task 14) is threaded straight through to `CreatePluginSelectionCandidateAsync` in Task 15 with no conditional recovery. The two narrow mockable interfaces introduced to avoid mocking sealed concrete classes — `IAgentRunActivityProbe` (Task 15, implemented by `MultiTurnAgentPool`) and `IWorkspacePluginSelectionService` (Task 15/16, implemented by `WorkspacePluginSelectionService`) — are each consumed only through the interface at their one call site (`WorkspacePluginSelectionService` and `WorkspacesController` respectively), never through the concrete sealed type.

---

Plan complete. Two execution options:

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

### Critical Files for Implementation
- B:\sources\LmDotnetTools\src\LmAgentInfra\Sandbox\SandboxSessionRegistry.cs
- B:\sources\LmDotnetTools\samples\LmStreaming.Sample\Persistence\FileWorkspaceStore.cs
- B:\sources\LmDotnetTools\samples\LmStreaming.Sample\Controllers\WorkspacesController.cs
- B:\sources\LmDotnetTools\src\Sandbox\SandboxCreateRequest.cs
- B:\sources\LmDotnetTools\samples\LmStreaming.Sample\Models\Workspace.cs