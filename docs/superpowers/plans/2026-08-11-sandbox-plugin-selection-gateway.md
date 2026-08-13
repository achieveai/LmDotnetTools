# Sandbox Per-Plugin Selection (Gateway) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a sandbox-create caller narrow which plugins (not just which marketplaces) are mounted/exposed for a session, with structured `{marketplace, plugin}` identity, tri-state selection semantics, deterministic dependency resolution, a fail-closed capability signal, and restart-durable persistence — closing the two confirmed gateway gaps (no command allow-listing, no MCP-server locate/filter wiring) along the way.

**Architecture:** Add a new, top-level tri-state `pluginSelection` wire field on `CreateSandboxRequest` (distinct from the existing mount-data `plugins: Vec<MountEntry>` field), resolved against the plugin catalog and expanded via a standalone dependency resolver, then converted into a per-marketplace `AllowList<plugin-name>` (mirroring the existing `skill_allow` precedent) that is persisted on each `Marketplace` `MountRecord` and re-applied on every skill/agent/command/MCP-server surface, live and after a gateway restart.

**Tech Stack:** Rust (edition 2021+), Axum, `serde`/`serde_json`, `rusqlite` (via the existing `Database` wrapper), `tokio`, `reqwest` (for the final live-verification task only). Crate: `mcp-gateway` in `B:\sources\SandboxedOstoolsMcpServer`.

## Global Constraints

- Scope is the **gateway repo only** (`SandboxedOstoolsMcpServer`, crate `mcp-gateway`). The app repo (`LmDotnetTools`) is out of scope; per the design spec's rollout order, the gateway ships first.
- The existing top-level `CreateSandboxRequest.plugins: Vec<MountEntry>` field (and its `mounts.plugins` twin) is **mount data** relative to `PLUGINS_BASE_PATH` — it MUST NOT be renamed, reused, or repurposed. The new selection concept uses a **distinct wire key**: `pluginSelection`.
- Plugin identity is always the structured pair `{marketplace, plugin}` — never a bare plugin name — because plugin names are not unique across marketplaces.
- Tri-state selection semantics (matches the existing `AllowList` convention used for skills/agents): absent field or explicit JSON `null` ⇒ all plugins; explicit `[]` ⇒ no plugins; non-empty array ⇒ exactly that subset (before dependency expansion).
- Intersection semantics: `pluginSelection` operates **first**, narrowing which plugins are eligible for a session. The existing per-marketplace `SelectionFilter` (`skills`/`agents` allow-lists nested in `marketplaces[].{skills,agents}`) then further narrows *within* the plugins that survived the `pluginSelection` narrowing — it never independently re-widens a plugin `pluginSelection` excluded.
- An unknown `{marketplace, plugin}` id in `pluginSelection` MUST reject the **whole** create request with `400` **before any side effects** (no container, no DB row, no mount). This exactly mirrors the existing `bad_request_unknown_aliases` / `bad_request_unknown_items` ordering already in `create_sandbox`.
- The create-sandbox response reports `pluginResolution: { supported, requested, effective, failed }`. `failed` stays `[]` until a concrete "valid-but-unresolvable" mode exists; this plan introduces exactly one such mode (a dependency that cannot be resolved) as its own task.
- `GET /api/v1/marketplaces/preview` gains `capabilities.pluginFiltering: bool`. A gateway that predates this feature omits the field entirely; per the design spec, an app-side consumer MUST treat a **missing** field as `false` (fail closed) — this is an app-repo behavior, but the gateway MUST emit the field truthfully (`true`) once this plan lands so the app can rely on its presence.
- Persistence follows the existing `skill_allow` precedent verbatim: a new nullable TEXT column on `session_mounts`, added via an `ALTER TABLE ... ADD COLUMN` appended to the existing migration list in `db.rs`, with the same "swallow only the duplicate-column error" pattern, and `#[serde(default)]` grandfathering on `MountRecord` for rows persisted before the column existed.
- Command filtering and MCP-server locate/filter wiring are **real, currently-unaddressed gaps**, not hypothetical: `crates/mcp-gateway/src/discovery/orchestrate.rs:264-269` contains an explicit code comment stating commands are not allow-list filterable today; `PluginsManager::all_mcp_servers()` (`plugins.rs:501`) has **zero production call sites** (grep-verified — only referenced from its own test module at lines 2893/2916). Both must be designed and implemented, not deferred.
- Dependency resolution is **new** functionality (no `dependencies` field exists today on `PluginJson`/`CatalogEntry`/`PluginInfo`) and is a **separately reviewable task** (Task 10) — not folded into plugin-selection resolution.
- No existing "official-style marketplace allowlist" schema was found anywhere in the crate (confirmed via full-crate grep). Cross-marketplace dependency trust is therefore defined as a **new, minimal** `AppConfig` field in this plan (`trusted_dependency_marketplaces: Vec<String>`), not hand-waved.
- Test-run command: `cargo test -p mcp-gateway`. Every task's tests must pass with this exact command (optionally scoped with `--test <name>` or `-- <test_fn>` for a single RED/GREEN cycle, per step).
- Prefer modifying existing files over creating new ones. Only two new files are created in this whole plan: `tests/plugin_selection_api.rs` (integration tests, Task 11) and its fixture plugins under `tests/fixtures/marketplaces/` (Task 10/11).
- The final task (Task 12) requires the gateway to already be built, deployed, and reachable; it uses **placeholder environment variables** (`$GATEWAY_URL`, `$GATEWAY_TOKEN`) — never a real hostname or secret.

---

## File Structure

Files touched by this plan, in the order tasks modify them:

- `crates/mcp-gateway/src/session.rs` — add `PluginRef` (structured plugin identity) and `MountRecord.plugin_allow: Option<AllowList>` (Tasks 1, 6).
- `crates/mcp-gateway/src/volume_spec.rs` — add the `pluginSelection` wire field + `normalize_plugin_selection()` on `CreateSandboxRequest` (Task 1).
- `crates/mcp-gateway/src/plugins.rs` — add `known_plugin_refs()`, `resolve_plugin_selection()` (Task 2); extend `get_marketplace_mounts()` for `plugin_allow` (Task 6); extend `get_skill_infos`/`get_agent_discoveries` filtering (Task 7); add a `commands: AllowList` filter pass to `get_command_discoveries` (Task 8); add `get_mcp_servers()` filtered accessor + `CatalogMcpServer` projection (Task 9); add `dependencies: Vec<PluginRef>` to `PluginJson`/`PluginInfo` + `resolve_plugin_dependencies()` (Task 10).
- `crates/mcp-gateway/src/api/sandboxes.rs` — add `bad_request_unknown_plugins()`, wire plugin resolution into `create_sandbox`, add `PluginResolution` + `CreateSandboxResponse.plugin_resolution` (Tasks 3, 4, 10).
- `crates/mcp-gateway/src/api/catalog.rs` — add `capabilities.pluginFiltering` to `CatalogResponse` (Task 5).
- `crates/mcp-gateway/src/db.rs` — add the `plugin_allow` column + migration + read/write wiring in `create_session_with_policy`/`list_sessions` (Task 6).
- `crates/mcp-gateway/src/discovery/orchestrate.rs` — thread the plugin allow-list into `run_marketplace_agent_scan` for agents and commands (Tasks 7, 8).
- `crates/mcp-gateway/src/lib.rs` — add `AppConfig.trusted_dependency_marketplaces` field + `AppState::gateway_skills` plugin-filter wiring + new `AppState::session_mcp_servers` (Tasks 7, 9, 10).
- `crates/mcp-gateway/src/main.rs` — parse `TRUSTED_DEPENDENCY_MARKETPLACES` env var (Task 10).
- `crates/mcp-gateway/tests/fixtures/marketplaces/` — new plugin fixtures with `.mcp.json`, `commands/`, and (Task 10) `dependencies` in `plugin.json`, to support integration tests (Tasks 9, 10, 11).
- `crates/mcp-gateway/tests/plugin_selection_api.rs` — new integration test file mirroring `tests/catalog_api.rs` conventions (Task 11).

---

### Task 1: Wire types — `PluginRef` and the `pluginSelection` tri-state field

**Files:**
- Modify: `crates/mcp-gateway/src/session.rs:142-153` (insert `PluginRef` just above `SelectionFilter`)
- Modify: `crates/mcp-gateway/src/volume_spec.rs:1-20` (imports), `:227-290` (`CreateSandboxRequest`), `:358-375` (add `normalize_plugin_selection` next to `normalize_marketplaces`)
- Test: `crates/mcp-gateway/src/volume_spec.rs` (inline `#[cfg(test)] mod tests`, alongside the existing `normalize_marketplaces_*` tests at line ~1756)

**Interfaces:**
- Consumes: `session::AllowList` (existing, `session.rs:55-140`) — reused verbatim as the value type inside the new `PluginSelection` enum's serde impl.
- Produces: `pub struct PluginRef { pub marketplace: String, pub plugin: String }` (session.rs) — the structured identity every later task (2, 3, 4, 8, 9, 10) uses for requested/effective/failed/dependency lists. `pub enum PluginSelection { All, Only(Vec<PluginRef>) }` with `is_all()` (mirrors `AllowList::is_all`). `CreateSandboxRequest.plugin_selection: PluginSelection` (serde name `pluginSelection`). `pub fn CreateSandboxRequest::normalize_plugin_selection(&self) -> &PluginSelection` (trivial accessor — kept as a method for symmetry with `normalize_marketplaces()`, since later tasks call it the same way).

- [ ] **Step 1: Write the failing test for `PluginRef`/`PluginSelection` wire round-trip**

Add to `crates/mcp-gateway/src/session.rs`, inside the existing `#[cfg(test)] mod tests` block (search for `mod tests` in that file; add near the existing `AllowList` serde tests):

```rust
#[test]
fn plugin_selection_absent_is_all() {
    #[derive(serde::Deserialize)]
    struct Wrapper {
        #[serde(default)]
        plugin_selection: PluginSelection,
    }
    let w: Wrapper = serde_json::from_str("{}").unwrap();
    assert_eq!(w.plugin_selection, PluginSelection::All);
}

#[test]
fn plugin_selection_null_is_all() {
    #[derive(serde::Deserialize)]
    struct Wrapper {
        #[serde(default)]
        plugin_selection: PluginSelection,
    }
    let w: Wrapper = serde_json::from_str(r#"{"plugin_selection":null}"#).unwrap();
    assert_eq!(w.plugin_selection, PluginSelection::All);
}

#[test]
fn plugin_selection_empty_array_is_only_empty() {
    #[derive(serde::Deserialize)]
    struct Wrapper {
        #[serde(default)]
        plugin_selection: PluginSelection,
    }
    let w: Wrapper = serde_json::from_str(r#"{"plugin_selection":[]}"#).unwrap();
    assert_eq!(w.plugin_selection, PluginSelection::Only(vec![]));
}

#[test]
fn plugin_selection_pairs_round_trip() {
    #[derive(serde::Deserialize)]
    struct Wrapper {
        #[serde(default)]
        plugin_selection: PluginSelection,
    }
    let w: Wrapper = serde_json::from_str(
        r#"{"plugin_selection":[{"marketplace":"official","plugin":"gh"}]}"#,
    )
    .unwrap();
    assert_eq!(
        w.plugin_selection,
        PluginSelection::Only(vec![PluginRef {
            marketplace: "official".to_string(),
            plugin: "gh".to_string(),
        }])
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib plugin_selection_absent_is_all`
Expected: FAIL with `error[E0433]: failed to resolve: use of undeclared type PluginSelection` (or similar "cannot find type/struct `PluginSelection`/`PluginRef`").

- [ ] **Step 3: Implement `PluginRef` and `PluginSelection` in `session.rs`**

Insert immediately above `pub struct SelectionFilter` (currently `session.rs:142-153`):

```rust
/// Structured plugin identity: `{marketplace, plugin}`. Plugin names are not
/// unique across marketplaces, so every plugin-selection surface (request,
/// response, dependency graph) uses this pair rather than a bare name.
#[derive(Debug, Clone, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize)]
pub struct PluginRef {
    pub marketplace: String,
    pub plugin: String,
}

/// Session-wide plugin selection (tri-state), analogous to [`AllowList`] but
/// keyed by structured [`PluginRef`] identity instead of a bare string.
///
/// Wire/selection semantics:
/// - field absent or explicit `null` ⇒ [`PluginSelection::All`] (every plugin
///   in the selected marketplaces is eligible)
/// - `[]`                             ⇒ `Only(vec![])` (no plugin is eligible)
/// - `[{...}, {...}]`                 ⇒ `Only([...])` (exactly those, before
///   dependency expansion — see `plugins::resolve_plugin_dependencies`)
///
/// This narrows which plugins are eligible; the existing per-marketplace
/// `SelectionFilter` (`skills`/`agents`) then further narrows WITHIN the
/// plugins that survive this selection — it never independently re-admits a
/// plugin this selection excluded.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub enum PluginSelection {
    #[default]
    All,
    Only(Vec<PluginRef>),
}

impl PluginSelection {
    pub fn is_all(&self) -> bool {
        matches!(self, PluginSelection::All)
    }
}

impl serde::Serialize for PluginSelection {
    fn serialize<S>(&self, serializer: S) -> Result<S::Ok, S::Error>
    where
        S: serde::Serializer,
    {
        match self {
            PluginSelection::All => serializer.serialize_none(),
            PluginSelection::Only(v) => v.serialize(serializer),
        }
    }
}

impl<'de> serde::Deserialize<'de> for PluginSelection {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: serde::Deserializer<'de>,
    {
        let opt: Option<Vec<PluginRef>> = Option::deserialize(deserializer)?;
        Ok(match opt {
            None => PluginSelection::All,
            Some(v) => PluginSelection::Only(v),
        })
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib plugin_selection_`
Expected: 4 tests pass (`plugin_selection_absent_is_all`, `plugin_selection_null_is_all`, `plugin_selection_empty_array_is_only_empty`, `plugin_selection_pairs_round_trip`).

- [ ] **Step 5: Write the failing test for the `CreateSandboxRequest.pluginSelection` field**

Add to `crates/mcp-gateway/src/volume_spec.rs`, near the existing `normalize_marketplaces_*` tests (search `normalize_marketplaces_absent_is_none` around line 1781):

```rust
#[test]
fn plugin_selection_field_deserializes_and_normalizes() {
    let req: CreateSandboxRequest = serde_json::from_str(
        r#"{"app":{"id":"a"},"pluginSelection":[{"marketplace":"official","plugin":"gh"}]}"#,
    )
    .unwrap();
    assert_eq!(
        req.normalize_plugin_selection(),
        &crate::session::PluginSelection::Only(vec![crate::session::PluginRef {
            marketplace: "official".to_string(),
            plugin: "gh".to_string(),
        }])
    );
}

#[test]
fn plugin_selection_field_absent_is_all() {
    let req: CreateSandboxRequest = serde_json::from_str(r#"{"app":{"id":"a"}}"#).unwrap();
    assert!(req.normalize_plugin_selection().is_all());
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib plugin_selection_field_`
Expected: FAIL with "no field `pluginSelection`" or "no method named `normalize_plugin_selection`".

- [ ] **Step 7: Add the field and accessor to `CreateSandboxRequest`**

In `crates/mcp-gateway/src/volume_spec.rs`, add the import at the top (near the existing `use crate::session::{...}` line):

```rust
use crate::session::{AllowList, MountKind, MountOrigin, PluginRef, PluginSelection, SelectionFilter};
```

Add the field to `CreateSandboxRequest` (`volume_spec.rs:229-290`), directly after the existing `marketplaces` field (currently ending at line 250):

```rust
    /// Session-wide plugin narrowing within the selected marketplaces (see
    /// `PluginSelection`'s doc for the tri-state rule). Distinct from the
    /// legacy `plugins`/`mounts.plugins` fields above, which are HOST MOUNT
    /// DATA relative to `PLUGINS_BASE_PATH` — this field selects catalog
    /// plugin identities, never mount paths.
    #[serde(default, rename = "pluginSelection")]
    pub plugin_selection: PluginSelection,
```

Add the accessor next to `normalize_marketplaces` (`volume_spec.rs:358-375`):

```rust
    pub fn normalize_plugin_selection(&self) -> &PluginSelection {
        &self.plugin_selection
    }
```

- [ ] **Step 8: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib plugin_selection_field_`
Expected: both tests pass.

- [ ] **Step 9: Commit**

```bash
git add crates/mcp-gateway/src/session.rs crates/mcp-gateway/src/volume_spec.rs
git commit -m "feat(gateway): add PluginRef/PluginSelection tri-state wire type"
```

---

### Task 2: Catalog-based plugin identity resolution

**Files:**
- Modify: `crates/mcp-gateway/src/plugins.rs:1375-1400` (insert new methods after `resolve_selection`)
- Test: `crates/mcp-gateway/src/plugins.rs` (inline `#[cfg(test)] mod tests`)

**Interfaces:**
- Consumes: `session::PluginRef`, `session::PluginSelection` (Task 1); `PluginsManager::get_all()`/`canonical_marketplaces()` (existing).
- Produces: `pub async fn PluginsManager::known_plugin_refs(&self, selected: &[String]) -> HashSet<PluginRef>`. `pub async fn PluginsManager::resolve_plugin_selection(&self, selection: &PluginSelection, selected_marketplaces: &[String]) -> Result<Vec<PluginRef>, Vec<PluginRef>>` — `Ok(effective_before_deps)` lists every plugin ref admitted (in catalog iteration order, deduplicated); `Err(unknown)` lists every `{marketplace, plugin}` pair from the request that is not in the catalog for the given marketplaces. Task 3 calls this directly; Task 10's dependency resolver consumes its `Ok` output as input.

- [ ] **Step 1: Write the failing test**

Add near the existing `resolve_selection` tests in `plugins.rs`'s test module (search `mod tests` — there are existing tests exercising `PluginsManager` against the fixture marketplaces under `tests/fixtures/marketplaces`, but this unit test builds its own `PluginsManager` inline, matching the existing pattern used for `all_mcp_servers` tests around line 2893):

```rust
#[tokio::test]
async fn resolve_plugin_selection_rejects_unknown_pair() {
    let dir = tempfile::tempdir().unwrap();
    let marketplace = dir.path().join("official");
    let plugin_dir = marketplace.join("gh");
    std::fs::create_dir_all(plugin_dir.join(".claude-plugin")).unwrap();
    std::fs::write(
        plugin_dir.join(".claude-plugin/plugin.json"),
        r#"{"name":"gh","description":"gh plugin"}"#,
    )
    .unwrap();

    let mgr = PluginsManager::new(
        vec![MarketplaceDir {
            alias: "official".to_string(),
            path: marketplace,
        }],
        300,
    );

    let selection = crate::session::PluginSelection::Only(vec![crate::session::PluginRef {
        marketplace: "official".to_string(),
        plugin: "does-not-exist".to_string(),
    }]);
    let result = mgr
        .resolve_plugin_selection(&selection, &["official".to_string()])
        .await;
    assert_eq!(
        result,
        Err(vec![crate::session::PluginRef {
            marketplace: "official".to_string(),
            plugin: "does-not-exist".to_string(),
        }])
    );
}

#[tokio::test]
async fn resolve_plugin_selection_all_returns_every_known_plugin() {
    let dir = tempfile::tempdir().unwrap();
    let marketplace = dir.path().join("official");
    let plugin_dir = marketplace.join("gh");
    std::fs::create_dir_all(plugin_dir.join(".claude-plugin")).unwrap();
    std::fs::write(
        plugin_dir.join(".claude-plugin/plugin.json"),
        r#"{"name":"gh","description":"gh plugin"}"#,
    )
    .unwrap();

    let mgr = PluginsManager::new(
        vec![MarketplaceDir {
            alias: "official".to_string(),
            path: marketplace,
        }],
        300,
    );

    let result = mgr
        .resolve_plugin_selection(
            &crate::session::PluginSelection::All,
            &["official".to_string()],
        )
        .await
        .unwrap();
    assert_eq!(
        result,
        vec![crate::session::PluginRef {
            marketplace: "official".to_string(),
            plugin: "gh".to_string(),
        }]
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib resolve_plugin_selection_`
Expected: FAIL with "no method named `resolve_plugin_selection` found for struct `PluginsManager`".

- [ ] **Step 3: Implement `known_plugin_refs` and `resolve_plugin_selection`**

Insert into `impl PluginsManager` in `plugins.rs`, directly after `resolve_selection` (currently ending at `plugins.rs:1400`):

```rust
    /// Every `{marketplace, plugin}` pair known to the catalog, restricted to
    /// the given (already-alias-validated) canonical marketplace aliases.
    pub async fn known_plugin_refs(&self, selected: &[String]) -> std::collections::HashSet<crate::session::PluginRef> {
        let canonical = self.canonical_marketplaces();
        let results = self.get_all().await;
        canonical
            .iter()
            .zip(results)
            .filter(|((alias, _), _)| selected.iter().any(|s| s == alias))
            .flat_map(|((alias, _), result)| {
                result
                    .plugins
                    .into_iter()
                    .map(move |p| crate::session::PluginRef {
                        marketplace: alias.clone(),
                        plugin: p.name,
                    })
            })
            .collect()
    }

    /// Resolve a [`crate::session::PluginSelection`] against the catalog for
    /// the already-validated `selected_marketplaces`.
    ///
    /// - `All` ⇒ every known plugin in the selected marketplaces (catalog
    ///   iteration order).
    /// - `Only(refs)` ⇒ validate every ref exists in the catalog; `Err` lists
    ///   every unknown ref (in request order) when any do not resolve, else
    ///   `Ok` echoes the requested refs (deduplicated, catalog order is not
    ///   imposed here — dependency resolution in a later task establishes the
    ///   deterministic effective order).
    ///
    /// Does not itself expand dependencies — see `resolve_plugin_dependencies`
    /// (a separate, independently reviewable step).
    pub async fn resolve_plugin_selection(
        &self,
        selection: &crate::session::PluginSelection,
        selected_marketplaces: &[String],
    ) -> Result<Vec<crate::session::PluginRef>, Vec<crate::session::PluginRef>> {
        let known = self.known_plugin_refs(selected_marketplaces).await;
        match selection {
            crate::session::PluginSelection::All => {
                let canonical = self.canonical_marketplaces();
                let results = self.get_all().await;
                Ok(canonical
                    .iter()
                    .zip(results)
                    .filter(|((alias, _), _)| selected_marketplaces.iter().any(|s| s == alias))
                    .flat_map(|((alias, _), result)| {
                        result
                            .plugins
                            .into_iter()
                            .map(move |p| crate::session::PluginRef {
                                marketplace: alias.clone(),
                                plugin: p.name,
                            })
                    })
                    .collect())
            }
            crate::session::PluginSelection::Only(refs) => {
                let unknown: Vec<crate::session::PluginRef> = refs
                    .iter()
                    .filter(|r| !known.contains(r))
                    .cloned()
                    .collect();
                if unknown.is_empty() {
                    Ok(refs.clone())
                } else {
                    Err(unknown)
                }
            }
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib resolve_plugin_selection_`
Expected: both tests pass.

- [ ] **Step 5: Commit**

```bash
git add crates/mcp-gateway/src/plugins.rs
git commit -m "feat(gateway): resolve plugin selection against the catalog"
```

---

### Task 3: Reject unknown plugin ids with a 400 before any side effects

**Files:**
- Modify: `crates/mcp-gateway/src/api/sandboxes.rs:193-215` (add `bad_request_unknown_plugins` next to `bad_request_unknown_items`), `:519-570` (wire into `create_sandbox`, right after the existing item-selection validation)
- Test: `crates/mcp-gateway/tests/plugin_selection_api.rs` (new file — created here, extended by Tasks 4/5/9/11)

**Interfaces:**
- Consumes: `PluginsManager::resolve_plugin_selection` (Task 2); `CreateSandboxRequest::normalize_plugin_selection()` (Task 1); the handler's existing `selected: Vec<String>` (validated marketplace aliases, already computed at `sandboxes.rs:530-539`).
- Produces: `pub(crate) fn bad_request_unknown_plugins(unknown: Vec<PluginRef>, available: Vec<PluginRef>) -> (StatusCode, Json<serde_json::Value>)`. Both arrays are sorted deterministically by `(marketplace, plugin)` before serialization — `available` in particular comes from a `HashSet` (`known_plugin_refs`) whose iteration order is otherwise unstable across runs. The 400 envelope shape `{ "error": ..., "code": 400, "unknown": [{"marketplace":..,"plugin":..}], "available": [...] }` is what Task 11's integration test asserts against. `create_sandbox` gains a local `plugin_refs_before_deps: Vec<PluginRef>` binding that Task 4 turns into the response body and Task 6 turns into per-marketplace `AllowList`s.

- [ ] **Step 1: Write the failing integration test (new file)**

Create `crates/mcp-gateway/tests/plugin_selection_api.rs`:

```rust
//! Integration tests for sandbox-create plugin selection (gateway-side,
//! session-create scope). Mirrors `tests/catalog_api.rs`'s conventions: a real
//! Axum server (no Docker) driven with `reqwest` against the committed sample
//! marketplaces under `tests/fixtures/marketplaces/`.

mod common;

use common::TestGateway;
use std::path::PathBuf;

fn fixtures_root() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("tests/fixtures/marketplaces")
}

fn official_only() -> Vec<(String, PathBuf)> {
    vec![("official".to_string(), fixtures_root().join("official"))]
}

#[tokio::test]
async fn create_rejects_unknown_plugin_before_any_side_effects() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": [
                {"marketplace": "official", "plugin": "zzz-unknown"},
                {"marketplace": "official", "plugin": "does-not-exist"}
            ]
        }))
        .send()
        .await
        .unwrap();

    assert_eq!(resp.status(), 400);
    let body: serde_json::Value = resp.json().await.unwrap();
    assert_eq!(body["code"], 400);
    // `unknown` is sorted deterministically by (marketplace, plugin), not
    // request order — "does-not-exist" < "zzz-unknown" lexicographically.
    assert_eq!(
        body["unknown"],
        serde_json::json!([
            {"marketplace": "official", "plugin": "does-not-exist"},
            {"marketplace": "official", "plugin": "zzz-unknown"}
        ])
    );
    // `available` is sorted the same way, independent of the HashSet's
    // internal iteration order (official/gh and official/superpowers — see
    // tests/fixtures/marketplaces/official).
    assert_eq!(
        body["available"],
        serde_json::json!([
            {"marketplace": "official", "plugin": "gh"},
            {"marketplace": "official", "plugin": "superpowers"}
        ])
    );

    // No side effects: the sandboxes list must stay empty.
    let list: serde_json::Value = gw
        .client
        .get(gw.url("/api/v1/sandboxes"))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();
    assert_eq!(list["sandboxes"].as_array().unwrap().len(), 0);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --test plugin_selection_api create_rejects_unknown_plugin_before_any_side_effects`
Expected: FAIL — request currently succeeds with `201` (or the field is silently ignored since `pluginSelection` isn't wired into the handler yet), so the `assert_eq!(resp.status(), 400)` fails.

- [ ] **Step 3: Add the 400 helper**

In `crates/mcp-gateway/src/api/sandboxes.rs`, directly after `bad_request_unknown_items` (currently ending at line 215):

```rust
/// 400 for an unknown-plugin-id selection (`pluginSelection`). Mirrors
/// [`bad_request_unknown_items`]: same `{error, code}` envelope plus a
/// structured `unknown`/`available` array of `{marketplace, plugin}` pairs.
///
/// Both arrays are sorted deterministically by `(marketplace, plugin)` before
/// being placed in the response — `available` in particular is built from a
/// `HashSet` (see `known_plugin_refs`), whose iteration order is otherwise
/// unstable across runs/versions, which would make the response
/// non-reproducible and hard to assert on in tests.
pub(crate) fn bad_request_unknown_plugins(
    mut unknown: Vec<crate::session::PluginRef>,
    mut available: Vec<crate::session::PluginRef>,
) -> (StatusCode, Json<serde_json::Value>) {
    let sort_key = |r: &crate::session::PluginRef| (r.marketplace.clone(), r.plugin.clone());
    unknown.sort_by_key(sort_key);
    available.sort_by_key(sort_key);
    let summary = unknown
        .iter()
        .map(|r| format!("{}/{}", r.marketplace, r.plugin))
        .collect::<Vec<_>>()
        .join(", ");
    (
        StatusCode::BAD_REQUEST,
        Json(serde_json::json!({
            "error": format!("unknown plugin(s): {summary}"),
            "code": 400,
            "unknown": unknown,
            "available": available,
        })),
    )
}
```

- [ ] **Step 4: Wire resolution into `create_sandbox`**

In `crates/mcp-gateway/src/api/sandboxes.rs`, directly after the existing item-selection validation block (currently `sandboxes.rs:541-546`, right before the `skill_allows` construction), insert:

```rust
    // Resolve the plugin selection (issue: sandbox-plugin-selection) against
    // the catalog for the already-validated marketplace `selected` set,
    // rejecting an unknown `{marketplace, plugin}` id with a 400 BEFORE any
    // backend work — mirrors the alias/item validation immediately above.
    let plugin_selection = req.normalize_plugin_selection().clone();
    let plugin_refs_before_deps = match state
        .plugins
        .resolve_plugin_selection(&plugin_selection, &selected)
        .await
    {
        Ok(refs) => refs,
        Err(unknown) => {
            let available = state.plugins.known_plugin_refs(&selected).await.into_iter().collect();
            return bad_request_unknown_plugins(unknown, available).into_response();
        }
    };
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --test plugin_selection_api create_rejects_unknown_plugin_before_any_side_effects`
Expected: PASS.

- [ ] **Step 6: Run the full gateway test suite to confirm no regressions**

Run: `cargo test -p mcp-gateway`
Expected: all existing tests still pass (in particular `tests/catalog_api.rs` and the existing sandbox-create tests in `api/sandboxes.rs`'s inline test module, since `plugin_selection` defaults to `PluginSelection::All` and is a no-op for every request that omits it).

- [ ] **Step 7: Commit**

```bash
git add crates/mcp-gateway/src/api/sandboxes.rs crates/mcp-gateway/tests/plugin_selection_api.rs
git commit -m "feat(gateway): reject unknown pluginSelection ids with 400 before side effects"
```

---

### Task 4: `pluginResolution` response block

**Files:**
- Modify: `crates/mcp-gateway/src/api/sandboxes.rs:107-120` (add `PluginResolution` struct next to `CreateSandboxResponse`), `:689-702` (populate it)
- Test: `crates/mcp-gateway/tests/plugin_selection_api.rs`

**Interfaces:**
- Consumes: `plugin_refs_before_deps: Vec<PluginRef>` (Task 3, local binding in `create_sandbox`); `plugin_selection: PluginSelection` (Task 3).
- Produces: `pub struct PluginResolution { pub supported: bool, pub requested: Option<Vec<PluginRef>>, pub effective: Vec<PluginRef>, pub failed: Vec<PluginRef> }` — `requested` is nullable: `None` for `PluginSelection::All` (nothing finite to echo), `Some(vec![])` for an explicit empty selection, `Some(refs)` for an explicit subset; `effective`/`failed` stay concrete lists always. `CreateSandboxResponse.plugin_resolution: PluginResolution`, annotated `#[serde(rename = "pluginResolution")]` (the struct is not `rename_all`-annotated, so this field needs an explicit rename to produce camelCase — see Step 3). Task 10 is the only later task that changes what feeds `effective`/`failed` (dependency expansion); it must not change this struct's shape.

- [ ] **Step 1: Write the failing test**

Add to `crates/mcp-gateway/tests/plugin_selection_api.rs`:

```rust
#[tokio::test]
async fn create_reports_requested_and_effective_plugins() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": [{"marketplace": "official", "plugin": "gh"}]
        }))
        .send()
        .await
        .unwrap();

    assert_eq!(resp.status(), 201);
    let body: serde_json::Value = resp.json().await.unwrap();
    assert_eq!(body["pluginResolution"]["supported"], true);
    assert_eq!(
        body["pluginResolution"]["requested"],
        serde_json::json!([{"marketplace": "official", "plugin": "gh"}])
    );
    assert_eq!(
        body["pluginResolution"]["effective"],
        serde_json::json!([{"marketplace": "official", "plugin": "gh"}])
    );
    assert_eq!(body["pluginResolution"]["failed"], serde_json::json!([]));
}

#[tokio::test]
async fn create_without_plugin_selection_reports_all_effective() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"]
        }))
        .send()
        .await
        .unwrap();

    assert_eq!(resp.status(), 201);
    let body: serde_json::Value = resp.json().await.unwrap();
    assert_eq!(body["pluginResolution"]["requested"], serde_json::json!(null));
    let effective = body["pluginResolution"]["effective"].as_array().unwrap();
    // official/gh and official/superpowers (see tests/fixtures/marketplaces/official).
    assert_eq!(effective.len(), 2);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --test plugin_selection_api create_reports_requested_and_effective_plugins`
Expected: FAIL with a JSON-index panic (`body["pluginResolution"]` is `null`).

- [ ] **Step 3: Add `PluginResolution` and populate it**

In `crates/mcp-gateway/src/api/sandboxes.rs`, directly after `CreateSandboxResponse` (currently ending at line 120):

```rust
/// Plugin selection resolution reported alongside a `201 Created` sandbox
/// response (`pluginSelection`, sandbox-plugin-selection design). `requested`
/// mirrors the request's tri-state exactly, not collapsed to a list: `None`
/// (serializes as JSON `null`) when `pluginSelection` was absent/null — "all",
/// there is no finite requested set to echo; `Some(vec![])` when the caller
/// explicitly selected zero plugins; `Some(refs)` for an explicit subset.
/// `effective` is always a concrete list — the actual resolved plugin set for
/// the session (after dependency expansion — see
/// `plugins::resolve_plugin_dependencies`); `failed` is reserved for a
/// concrete valid-but-unresolvable dependency and is `[]` until then.
#[derive(Debug, Serialize)]
pub struct PluginResolution {
    /// Always `true` once the gateway understands `pluginSelection` — lets a
    /// caller detect an older gateway that silently ignored the field (it
    /// would simply omit `pluginResolution` from the response entirely).
    pub supported: bool,
    pub requested: Option<Vec<crate::session::PluginRef>>,
    pub effective: Vec<crate::session::PluginRef>,
    pub failed: Vec<crate::session::PluginRef>,
}
```

Add the field to `CreateSandboxResponse` (directly after `proxy_token`). `CreateSandboxResponse` is
not `rename_all`-annotated (its other fields, e.g. `session_id`, serialize as snake_case), so the
new field needs an explicit per-field rename to produce the camelCase `pluginResolution` key the
app DTO and every test in this plan expect:

```rust
    #[serde(rename = "pluginResolution")]
    pub plugin_resolution: PluginResolution,
```

Update the response construction (`sandboxes.rs:689-702`) to populate it — `requested` mirrors the
tri-state exactly: `None` for `All` (absent/null request — nothing finite to echo), `Some(refs)`
(including `Some(vec![])` for an explicit empty selection) for `Only`:

```rust
    let requested_plugin_refs: Option<Vec<crate::session::PluginRef>> = match &plugin_selection {
        crate::session::PluginSelection::All => None,
        crate::session::PluginSelection::Only(refs) => Some(refs.clone()),
    };
    let response = CreateSandboxResponse {
        session_id,
        container_id,
        volumes,
        app_id: policy.app.id.clone(),
        policy: SandboxPolicySummary {
            network_rules: policy.network.rules.len(),
            auth_providers: policy.auth_providers.len(),
            host_overrides: policy.host_overrides.len(),
        },
        proxy_token,
        plugin_resolution: PluginResolution {
            supported: true,
            requested: requested_plugin_refs,
            effective: plugin_refs_before_deps,
            failed: Vec::new(),
        },
    };
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --test plugin_selection_api create_reports_requested_and_effective_plugins create_without_plugin_selection_reports_all_effective`
Expected: both PASS.

- [ ] **Step 5: Commit**

```bash
git add crates/mcp-gateway/src/api/sandboxes.rs crates/mcp-gateway/tests/plugin_selection_api.rs
git commit -m "feat(gateway): report pluginResolution on sandbox create"
```

---

### Task 5: `capabilities.pluginFiltering` on the marketplace preview

**Files:**
- Modify: `crates/mcp-gateway/src/api/catalog.rs:35-50` (add `capabilities` to `CatalogResponse`), `:53-95` (populate it)
- Test: `crates/mcp-gateway/tests/catalog_api.rs` (extend existing file — same fixtures/harness already used there)

**Interfaces:**
- Consumes: nothing new — this is a pure additive constant-shaped field.
- Produces: `pub struct Capabilities { #[serde(rename = "pluginFiltering")] pub plugin_filtering: bool }`, `CatalogResponse.capabilities: Capabilities`. Any later task must NOT remove this field or rename `pluginFiltering` — the app repo's fail-closed check depends on this exact JSON key existing.

- [ ] **Step 1: Write the failing test**

Add to `crates/mcp-gateway/tests/catalog_api.rs`:

```rust
#[tokio::test]
async fn preview_reports_plugin_filtering_capability() {
    let gw = TestGateway::start_with_marketplaces(both_marketplaces(), None).await;

    let body: serde_json::Value = gw
        .client
        .get(gw.url("/api/v1/marketplaces/preview?marketplaces=official"))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    assert_eq!(body["capabilities"]["pluginFiltering"], true);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --test catalog_api preview_reports_plugin_filtering_capability`
Expected: FAIL (`body["capabilities"]` is `null`).

- [ ] **Step 3: Add `Capabilities` and populate it**

In `crates/mcp-gateway/src/api/catalog.rs`, directly above `CatalogResponse` (currently at line 35):

```rust
/// Gateway-advertised feature flags for the pre-creation catalog (issue:
/// sandbox-plugin-selection). A gateway predating this field omits
/// `capabilities` entirely — callers MUST treat a missing `pluginFiltering`
/// as `false` (fail closed); this gateway always reports `true`.
#[derive(Debug, Serialize)]
pub struct Capabilities {
    #[serde(rename = "pluginFiltering")]
    pub plugin_filtering: bool,
}
```

Add the field to `CatalogResponse`:

```rust
    pub capabilities: Capabilities,
```

In `preview_catalog` (currently `catalog.rs:53-95`), update the final response construction to include:

```rust
    Json(CatalogResponse {
        selected,
        marketplaces,
        capabilities: Capabilities {
            plugin_filtering: true,
        },
    })
```

(Match the exact existing construction — the handler currently builds `CatalogResponse { selected, marketplaces }` with a trailing `Cache-Control` header attached via the response wrapper; only the struct literal changes.)

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --test catalog_api preview_reports_plugin_filtering_capability`
Expected: PASS.

- [ ] **Step 5: Run the full catalog test file to confirm no regressions**

Run: `cargo test -p mcp-gateway --test catalog_api`
Expected: all tests pass, including the pre-existing ones that assert on the full response body shape (verify none of them use a strict `assert_eq!(body, json!({...}))` full-object comparison that this new field would break — inspect first; the existing tests read specific keys, e.g. `body["selected"]`, so an additive field is safe).

- [ ] **Step 6: Commit**

```bash
git add crates/mcp-gateway/src/api/catalog.rs crates/mcp-gateway/tests/catalog_api.rs
git commit -m "feat(gateway): advertise capabilities.pluginFiltering on marketplace preview"
```

---

### Task 6: Persist the plugin allow-list (restart-durable)

**Files:**
- Modify: `crates/mcp-gateway/src/session.rs:154-186` (add `MountRecord.plugin_allow`)
- Modify: `crates/mcp-gateway/src/db.rs:174-191` (schema column), `:268-285` (migration), `:402-488` (`create_session_with_policy` write path), `:660-820` (`list_sessions` read path — read the exact `skill_allow` read block first, see Step 3)
- Modify: `crates/mcp-gateway/src/plugins.rs:1254-1294` (`get_marketplace_mounts` signature)
- Modify: `crates/mcp-gateway/src/api/sandboxes.rs:548-570` (build `plugin_allows` map, pass to `get_marketplace_mounts`)
- Test: `crates/mcp-gateway/src/db.rs` (inline, mirroring the existing `skill_allow` round-trip test at `db.rs:1606-1710`)

**Interfaces:**
- Consumes: `session::AllowList` (existing); `plugin_refs_before_deps: Vec<PluginRef>` and `selected: Vec<String>` (Task 3).
- Produces: `MountRecord.plugin_allow: Option<AllowList>` (JSON-encoded plugin-NAME allow-list, scoped per marketplace mount — exactly mirrors `skill_allow`'s shape and precedent). `PluginsManager::get_marketplace_mounts(&self, selected: &[String], skill_allows: &HashMap<String, AllowList>, plugin_allows: &HashMap<String, AllowList>) -> Vec<VolumeMount>` (signature grows by one parameter — Task 7 and Task 9 both read `MountRecord.plugin_allow` back out via `AppState.sessions`/`state.db`, so this exact field name is load-bearing).

- [ ] **Step 1: Write the failing DB round-trip test**

Add to `crates/mcp-gateway/src/db.rs`'s test module, directly after the existing `skill_allow` round-trip test (search for the test around line 1606, e.g. `list_sessions_rejects_mount_id_above_javascript_safe_integer` / the skill_allow round-trip test just before it):

```rust
#[tokio::test]
async fn plugin_allow_round_trips_through_sqlite() {
    let db = Database::open(":memory:").unwrap();
    let mounts = vec![MountRecord {
        relative_path: Some("official".to_string()),
        container_path: "/marketplaces/official".to_string(),
        read_only: true,
        kind: MountKind::Marketplace,
        skill_allow: None,
        plugin_allow: Some(AllowList::Only(vec!["gh".to_string()])),
        origin: MountOrigin::Global,
        id: None,
    }];
    db.create_session_with_policy(
        "sess-1",
        "container-1",
        "image:latest",
        chrono::Utc::now(),
        None,
        &mounts,
        None,
        None,
        None,
        None,
    )
    .await
    .unwrap();

    let sessions = db.list_sessions().await.unwrap();
    let mount = &sessions[0].mounts[0];
    assert_eq!(
        mount.plugin_allow,
        Some(AllowList::Only(vec!["gh".to_string()])),
        "plugin_allow must round-trip through SQLite"
    );
}

#[tokio::test]
async fn plugin_allow_null_reads_back_as_none() {
    let db = Database::open(":memory:").unwrap();
    let mounts = vec![MountRecord {
        relative_path: Some("official".to_string()),
        container_path: "/marketplaces/official".to_string(),
        read_only: true,
        kind: MountKind::Marketplace,
        skill_allow: None,
        plugin_allow: None,
        origin: MountOrigin::Global,
        id: None,
    }];
    db.create_session_with_policy(
        "sess-2",
        "container-2",
        "image:latest",
        chrono::Utc::now(),
        None,
        &mounts,
        None,
        None,
        None,
        None,
    )
    .await
    .unwrap();

    let sessions = db.list_sessions().await.unwrap();
    assert!(sessions[0].mounts[0].plugin_allow.is_none());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib plugin_allow_round_trips_through_sqlite`
Expected: FAIL with "no field `plugin_allow` on type `MountRecord`" (compile error).

- [ ] **Step 3: Read the exact `skill_allow` restore block before editing (do not skip)**

Run (read-only, no edit yet): open `crates/mcp-gateway/src/db.rs` around lines 761-815 (the `list_sessions` `SELECT ... skill_allow ...` and the subsequent `let skill_allow = match skill_allow_json { ... }` deserialization block) to copy its exact structure for the new column — the column must be added to both the `SELECT` column list and the row-mapping closure in the same statement, in the same relative position as `skill_allow`.

- [ ] **Step 4: Add the column, migration, and `MountRecord` field**

In `crates/mcp-gateway/src/session.rs`, add to `MountRecord` (directly after `skill_allow`, currently ending at line 179):

```rust
    /// Per-marketplace plugin-name allow-list for `Marketplace` mounts
    /// (sandbox-plugin-selection). `None` for non-marketplace mounts, an
    /// unfiltered ("all plugins") marketplace, and rows persisted before this
    /// field existed. Mirrors `skill_allow` exactly: JSON-encoded
    /// `Option<AllowList>`, restart-durable, re-applied live on every
    /// skill/agent/command/MCP-server surface (see `plugins.rs`).
    #[serde(default)]
    pub plugin_allow: Option<AllowList>,
```

In `crates/mcp-gateway/src/db.rs`, add the column to the `CREATE TABLE session_mounts` DDL (`db.rs:174-191`), directly after `skill_allow TEXT,`:

```sql
                    -- Per-marketplace plugin-name allow-list
                    -- (sandbox-plugin-selection), JSON-encoded
                    -- `Option<AllowList>`; NULL ⇒ None. Mirrors skill_allow.
                    plugin_allow   TEXT,
```

Append to the migration list (`db.rs:268-285`), as the new final entry:

```rust
                // Per-marketplace plugin-name allow-list (sandbox-plugin-selection).
                "ALTER TABLE session_mounts ADD COLUMN plugin_allow TEXT",
```

In `create_session_with_policy` (`db.rs:402-488`), directly after the existing `skill_allow_json` computation, add the analogous `plugin_allow_json` and extend the INSERT:

```rust
                let plugin_allow_json = match &mount.plugin_allow {
                    Some(allow) => Some(
                        serde_json::to_string(allow)
                            .context("Failed to serialize mount plugin_allow")?,
                    ),
                    None => None,
                };
                tx.execute(
                    "INSERT INTO session_mounts
                         (session_id, relative_path, container_path, read_only, kind, skill_allow, origin, plugin_allow)
                     VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
                    params![
                        session_id,
                        mount.relative_path,
                        mount.container_path,
                        mount.read_only as i32,
                        kind_str,
                        skill_allow_json,
                        mount_origin_to_str(&mount.origin),
                        plugin_allow_json,
                    ],
                )
                .context("Failed to insert session mount")?;
```

In `list_sessions`'s row-mapping (the block found in Step 3), add `plugin_allow` to the `SELECT` column list and the row closure the same way `skill_allow` is read — deserialize the `TEXT` column back into `Option<AllowList>` with `serde_json::from_str`, mirroring the existing `skill_allow` deserialization exactly, and populate `MountRecord { ..., plugin_allow, ... }`.

- [ ] **Step 5: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib plugin_allow_round_trips_through_sqlite plugin_allow_null_reads_back_as_none`
Expected: both PASS.

- [ ] **Step 6: Extend `get_marketplace_mounts` and wire `create_sandbox`**

In `crates/mcp-gateway/src/plugins.rs`, change `get_marketplace_mounts`'s signature (`plugins.rs:1266-1270`) to accept `plugin_allows: &HashMap<String, AllowList>`, and set `plugin_allow: plugin_allows.get(alias).cloned()` on each emitted `VolumeMount` (directly alongside the existing `skill_allow: skill_allows.get(alias).cloned()` at line 1290). This requires `VolumeMount` (defined nearby in `plugins.rs` or `volume_spec.rs` — confirm its definition site before editing) to also grow a `plugin_allow: Option<AllowList>` field, and `resolve_mounts` in `volume_spec.rs` (which turns a `VolumeMount` into a `MountRecord`) to copy it through — mirror exactly how `skill_allow` is copied there today.

In `crates/mcp-gateway/src/api/sandboxes.rs`, directly after the existing `skill_allows` construction (`sandboxes.rs:559-563`), build the analogous plugin map from `plugin_refs_before_deps` (Task 3) grouped by marketplace, keeping only marketplaces where the plugin set is a **strict subset** of that marketplace's known plugins (an "every known plugin explicitly named" marketplace still normalizes to `All`, exactly matching the `skill_allows` "only restrictive lists are kept" rule):

```rust
    let mut plugin_refs_by_marketplace: std::collections::HashMap<String, Vec<String>> =
        std::collections::HashMap::new();
    for r in &plugin_refs_before_deps {
        plugin_refs_by_marketplace
            .entry(r.marketplace.clone())
            .or_default()
            .push(r.plugin.clone());
    }
    let mut plugin_allows: std::collections::HashMap<String, crate::session::AllowList> =
        std::collections::HashMap::new();
    if matches!(plugin_selection, crate::session::PluginSelection::Only(_)) {
        // Load the known-plugin catalog for every selected marketplace ONCE,
        // before the loop below — `known_plugin_refs` scans the full plugin
        // catalog internally, so calling it per-alias inside the loop would
        // re-scan the catalog once per marketplace instead of once per
        // request.
        let known_refs = state.plugins.known_plugin_refs(&selected).await;
        let mut known_counts_by_marketplace: std::collections::HashMap<String, usize> =
            std::collections::HashMap::new();
        for r in &known_refs {
            *known_counts_by_marketplace
                .entry(r.marketplace.clone())
                .or_insert(0) += 1;
        }
        for alias in &selected {
            let names = plugin_refs_by_marketplace
                .get(alias)
                .cloned()
                .unwrap_or_default();
            let known_for_alias = known_counts_by_marketplace
                .get(alias)
                .copied()
                .unwrap_or(0);
            if names.len() < known_for_alias || known_for_alias == 0 {
                plugin_allows.insert(alias.clone(), crate::session::AllowList::Only(names));
            }
        }
    }
```

Update the `get_marketplace_mounts` call site (`sandboxes.rs:568-570`) to pass `&plugin_allows`:

```rust
    let server_mounts = state
        .plugins
        .get_marketplace_mounts(&selected, &skill_allows, &plugin_allows);
```

- [ ] **Step 7: Run the full suite to confirm every existing `get_marketplace_mounts` caller/test compiles with the new parameter**

Run: `cargo test -p mcp-gateway`
Expected: compile succeeds after updating every call site the compiler flags (existing unit tests in `plugins.rs` that call `get_marketplace_mounts` directly must pass an empty `&HashMap::new()` for the new `plugin_allows` parameter); all tests pass.

- [ ] **Step 8: Commit**

```bash
git add crates/mcp-gateway/src/session.rs crates/mcp-gateway/src/db.rs crates/mcp-gateway/src/plugins.rs crates/mcp-gateway/src/volume_spec.rs crates/mcp-gateway/src/api/sandboxes.rs
git commit -m "feat(gateway): persist a restart-durable per-marketplace plugin allow-list"
```

---

### Task 7: Filter skills and agents by the selected plugins (live + restart-durable)

**Files:**
- Modify: `crates/mcp-gateway/src/plugins.rs:639-667` (`get_skill_infos` gains a plugin filter pass), `:829-` (`get_agent_discoveries` gains a plugin filter pass)
- Modify: `crates/mcp-gateway/src/lib.rs:586-` (`AppState::gateway_skills` — read `mount.plugin_allow`, mirror the existing `mount.skill_allow` read)
- Modify: `crates/mcp-gateway/src/discovery/orchestrate.rs:233-284` (`run_marketplace_agent_scan` — pass the plugin allow-list through)
- Test: `crates/mcp-gateway/src/plugins.rs` (inline)

**Interfaces:**
- Consumes: `MountRecord.plugin_allow` (Task 6); `session::AllowList::allows` (existing).
- Produces: `PluginsManager::get_skill_infos(&self, selected: &[String], filters: &HashMap<String, SelectionFilter>, plugin_allows: &HashMap<String, AllowList>) -> Vec<SkillInfo>` and the analogous new parameter on `get_agent_discoveries` — both signatures grow by one parameter that Task 8 (commands) and Task 9 (MCP servers) copy verbatim for consistency.

- [ ] **Step 1: Write the failing test**

Add to `plugins.rs`'s test module, alongside the existing `get_skill_infos`-oriented tests:

```rust
#[tokio::test]
async fn get_skill_infos_excludes_plugins_not_in_allow_list() {
    let dir = tempfile::tempdir().unwrap();
    let marketplace = dir.path().join("official");
    for (plugin, skill) in [("gh", "gh-skill"), ("superpowers", "sp-skill")] {
        let skill_dir = marketplace.join(plugin).join("skills").join(skill);
        std::fs::create_dir_all(&skill_dir).unwrap();
        std::fs::write(skill_dir.join("SKILL.md"), "---\
description: d\
---\
body").unwrap();
        std::fs::create_dir_all(marketplace.join(plugin).join(".claude-plugin")).unwrap();
        std::fs::write(
            marketplace.join(plugin).join(".claude-plugin/plugin.json"),
            format!(r#"{{"name":"{plugin}","description":"d"}}"#),
        )
        .unwrap();
    }

    let mgr = PluginsManager::new(
        vec![MarketplaceDir {
            alias: "official".to_string(),
            path: marketplace,
        }],
        300,
    );

    let mut plugin_allows = HashMap::new();
    plugin_allows.insert(
        "official".to_string(),
        AllowList::Only(vec!["gh".to_string()]),
    );

    let infos = mgr
        .get_skill_infos(&["official".to_string()], &HashMap::new(), &plugin_allows)
        .await;
    let names: Vec<&str> = infos.iter().map(|s| s.plugin.as_deref().unwrap()).collect();
    assert_eq!(names, vec!["gh"], "superpowers must be excluded by the plugin allow-list");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib get_skill_infos_excludes_plugins_not_in_allow_list`
Expected: FAIL — compile error, `get_skill_infos` takes 2 args, 3 supplied.

- [ ] **Step 3: Add the `plugin_allows` parameter to `get_skill_infos`**

In `plugins.rs`, change the signature (`plugins.rs:639-643`) and add a third filter stage (directly after the existing `s.marketplace` and item-filter `.filter` calls, before `.collect()`):

```rust
    pub async fn get_skill_infos(
        &self,
        selected: &[String],
        filters: &HashMap<String, SelectionFilter>,
        plugin_allows: &HashMap<String, AllowList>,
    ) -> Vec<crate::skills::SkillInfo> {
        self.all_skill_infos()
            .await
            .into_iter()
            .filter(|s| {
                s.marketplace
                    .as_deref()
                    .is_some_and(|a| selected.iter().any(|x| x == a))
            })
            .filter(|s| {
                match s.marketplace.as_deref().and_then(|a| plugin_allows.get(a)) {
                    Some(allow) => s.plugin.as_deref().is_some_and(|p| allow.allows(p)),
                    None => true,
                }
            })
            .filter(|s| {
                match s.marketplace.as_deref().and_then(|a| filters.get(a)) {
                    Some(filter) => filter.skills.allows_qualified(s.plugin.as_deref(), &s.name),
                    None => true,
                }
            })
            .collect()
    }
```

Update every existing call site the compiler flags: `catalog()` (`plugins.rs:1177`, pass `&HashMap::new()`), `validate_item_selection` (`plugins.rs:1324`, pass `&HashMap::new()` — item-name validation is intentionally plugin-selection-agnostic since it validates names that already exist in the full catalog), and `AppState::gateway_skills` in `lib.rs` (Step 4 below).

- [ ] **Step 4: Add the analogous parameter to `get_agent_discoveries`, and wire `gateway_skills`**

Apply the identical pattern to `get_agent_discoveries` (`plugins.rs:829-`): add `plugin_allows: &HashMap<String, AllowList>` as a new parameter, and add a `.filter(|g| match plugin_allows.get(&g.alias) { Some(allow) => allow.allows(&g.plugin), None => true })` stage before the existing item-filter stage (`g.plugin` is already available on `GroupedAgent`, confirmed in the struct at `plugins.rs:286-291`).

In `crates/mcp-gateway/src/lib.rs`, in `AppState::gateway_skills` (starting at line 586), directly alongside the existing `if let Some(skill_allow) = &m.skill_allow { filters.insert(...) }` block (lines 601-610), add the analogous plugin-allow collection:

```rust
        let mut plugin_allows: std::collections::HashMap<String, session::AllowList> =
            std::collections::HashMap::new();
        // ... inside the same `for m in info.mounts.iter().filter(...)` loop:
        if let Some(plugin_allow) = &m.plugin_allow {
            plugin_allows.insert(alias.clone(), plugin_allow.clone());
        }
```

Update the trailing call to `self.plugins.get_skill_infos(&selected, &filters, &plugin_allows).await` (previously `get_skill_infos(&selected, &filters)`), and any other in-crate call site the compiler now flags for both functions.

- [ ] **Step 5: Wire `run_marketplace_agent_scan`**

In `crates/mcp-gateway/src/discovery/orchestrate.rs`, `run_marketplace_agent_scan` (`orchestrate.rs:233-284`) currently derives `selected` from `Marketplace` mounts (lines 247-252). Directly after that block, add:

```rust
    let plugin_allows: std::collections::HashMap<String, crate::session::AllowList> = session
        .mounts
        .iter()
        .filter(|m| m.kind == MountKind::Marketplace)
        .filter_map(|m| {
            m.relative_path
                .clone()
                .zip(m.plugin_allow.clone())
        })
        .collect();
```

Update the `get_agent_discoveries` call (line 260-263) to `state.plugins.get_agent_discoveries(&selected, &item_filters, &plugin_allows).await`.

- [ ] **Step 6: Run tests to verify everything passes**

Run: `cargo test -p mcp-gateway`
Expected: `get_skill_infos_excludes_plugins_not_in_allow_list` PASSES; all previously-passing tests (including `tests/catalog_api.rs::preview_skill_set_matches_get_skill_infos`, which calls `get_skill_infos` directly and must be updated to pass `&HashMap::new()` as the new third argument) still PASS.

- [ ] **Step 7: Commit**

```bash
git add crates/mcp-gateway/src/plugins.rs crates/mcp-gateway/src/lib.rs crates/mcp-gateway/src/discovery/orchestrate.rs crates/mcp-gateway/tests/catalog_api.rs
git commit -m "feat(gateway): filter skills and agents by the persisted plugin allow-list"
```

---

### Task 8: Command filtering (closes the confirmed `discovery/orchestrate.rs:264-269` gap)

**Files:**
- Modify: `crates/mcp-gateway/src/session.rs:147-152` (add `commands: AllowList` to `SelectionFilter`)
- Modify: `crates/mcp-gateway/src/plugins.rs:1011-1021` (`get_command_discoveries` gains filter parameters)
- Modify: `crates/mcp-gateway/src/discovery/orchestrate.rs:258-270` (pass filters through, remove the stale comment)
- Test: `crates/mcp-gateway/src/plugins.rs` (inline)

**Interfaces:**
- Consumes: `SelectionFilter` (existing struct, extended here); `MountRecord.plugin_allow` (Task 6).
- Produces: `SelectionFilter.commands: AllowList` (new field, wire key `commands`, added to `MarketplaceItem`'s `ALLOWED` key list in `volume_spec.rs`). `PluginsManager::get_command_discoveries(&self, selected: &[String], filters: &HashMap<String, SelectionFilter>, plugin_allows: &HashMap<String, AllowList>) -> Vec<Discovery>` (signature grows from 1 to 3 parameters — every existing call site must be updated).

- [ ] **Step 1: Write the failing test**

Add to `plugins.rs`'s test module:

```rust
#[tokio::test]
async fn get_command_discoveries_respects_commands_allow_list() {
    let dir = tempfile::tempdir().unwrap();
    let marketplace = dir.path().join("official");
    for (plugin, command) in [("gh", "gh-quick-pr"), ("gh", "gh-slow-pr")] {
        let cmd_dir = marketplace.join(plugin).join("commands");
        std::fs::create_dir_all(&cmd_dir).unwrap();
        std::fs::write(cmd_dir.join(format!("{command}.md")), "prompt body").unwrap();
    }
    std::fs::create_dir_all(marketplace.join("gh").join(".claude-plugin")).unwrap();
    std::fs::write(
        marketplace.join("gh").join(".claude-plugin/plugin.json"),
        r#"{"name":"gh","description":"d"}"#,
    )
    .unwrap();

    let mgr = PluginsManager::new(
        vec![MarketplaceDir {
            alias: "official".to_string(),
            path: marketplace,
        }],
        300,
    );

    let mut filters = HashMap::new();
    filters.insert(
        "official".to_string(),
        SelectionFilter {
            skills: AllowList::All,
            agents: AllowList::All,
            commands: AllowList::Only(vec!["gh-quick-pr".to_string()]),
        },
    );

    let discoveries = mgr
        .get_command_discoveries(&["official".to_string()], &filters, &HashMap::new())
        .await;
    let names: Vec<_> = discoveries.iter().map(|d| d.name.clone().unwrap()).collect();
    assert_eq!(names, vec!["gh-quick-pr"]);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib get_command_discoveries_respects_commands_allow_list`
Expected: FAIL — compile error (`SelectionFilter` has no field `commands`; `get_command_discoveries` takes 2 args, 3 supplied).

- [ ] **Step 3: Add `commands` to `SelectionFilter`**

In `crates/mcp-gateway/src/session.rs`, extend `SelectionFilter` (`session.rs:147-152`):

```rust
#[derive(Debug, Clone, Default, PartialEq, Eq, serde::Serialize, serde::Deserialize)]
pub struct SelectionFilter {
    #[serde(default, skip_serializing_if = "AllowList::is_all")]
    pub skills: AllowList,
    #[serde(default, skip_serializing_if = "AllowList::is_all")]
    pub agents: AllowList,
    #[serde(default, skip_serializing_if = "AllowList::is_all")]
    pub commands: AllowList,
}
```

In `crates/mcp-gateway/src/volume_spec.rs`, update `MarketplaceItem`'s deserializer `ALLOWED` constant (`volume_spec.rs:164`) to admit the new key:

```rust
                const ALLOWED: [&str; 4] = ["alias", "skills", "agents", "commands"];
```

- [ ] **Step 4: Extend `get_command_discoveries` with plugin/item filtering**

In `plugins.rs`, change the signature and body (`plugins.rs:1011-1021`):

```rust
    pub async fn get_command_discoveries(
        &self,
        selected: &[String],
        filters: &HashMap<String, SelectionFilter>,
        plugin_allows: &HashMap<String, AllowList>,
    ) -> Vec<context_walk::Discovery> {
        self.all_command_discoveries_grouped()
            .await
            .into_iter()
            .filter(|g| selected.contains(&g.alias))
            .filter(|g| match plugin_allows.get(&g.alias) {
                Some(allow) => allow.allows(&g.plugin),
                None => true,
            })
            .filter(|g| match filters.get(&g.alias) {
                Some(filter) => filter
                    .commands
                    .allows_qualified(Some(&g.plugin), g.discovery.name.as_deref().unwrap_or_default()),
                None => true,
            })
            .map(|g| g.discovery)
            .collect()
    }
```

- [ ] **Step 5: Update `run_marketplace_agent_scan` and remove the stale gap comment**

In `crates/mcp-gateway/src/discovery/orchestrate.rs`, replace the block at lines 264-270 (the comment documenting the gap plus the unfiltered call) with:

```rust
    // Plugin commands (closes the previously-unfiltered gap: commands now
    // carry their own `commands` AllowList in `SelectionFilter`, mirroring
    // agents, plus the same `plugin_allows` narrowing applied to skills and
    // agents above).
    discoveries.extend(
        state
            .plugins
            .get_command_discoveries(&selected, &item_filters, &plugin_allows)
            .await,
    );
```

- [ ] **Step 6: Run tests to verify everything passes**

Run: `cargo test -p mcp-gateway`
Expected: `get_command_discoveries_respects_commands_allow_list` PASSES; update any other call site the compiler flags (e.g. `catalog()` in `plugins.rs:1217`, pass `&HashMap::new()`/`&HashMap::new()`); all previously-passing tests still PASS.

- [ ] **Step 7: Commit**

```bash
git add crates/mcp-gateway/src/session.rs crates/mcp-gateway/src/volume_spec.rs crates/mcp-gateway/src/plugins.rs crates/mcp-gateway/src/discovery/orchestrate.rs
git commit -m "feat(gateway): add command allow-list filtering (closes issue #88 gap)"
```

---

### Task 9: MCP-server locate/filter surface (closes the confirmed `all_mcp_servers` zero-call-site gap)

**Files:**
- Modify: `crates/mcp-gateway/src/plugins.rs:255-262` (add `CatalogMcpServer`), `:1163-1252` (`catalog()` gains an `mcp_servers` field per `CatalogPlugin`), `:501-517` (`all_mcp_servers` grows a filtered sibling)
- Modify: `crates/mcp-gateway/src/lib.rs` (new `AppState::session_mcp_servers`)
- Test: `crates/mcp-gateway/tests/plugin_selection_api.rs`, `crates/mcp-gateway/src/plugins.rs` (inline)

**Interfaces:**
- Consumes: `MountRecord.plugin_allow` (Task 6); `PluginInfo.mcp_servers` (existing).
- Produces: `PluginsManager::get_mcp_servers(&self, selected: &[String], plugin_allows: &HashMap<String, AllowList>) -> Vec<McpServerEntry>` — the filtered, session-scoped sibling of the existing unfiltered `all_mcp_servers()` (kept for backward compatibility with any external caller, though none currently exists). `CatalogPlugin.mcp_servers: Vec<CatalogMcpServer>` (name + server type only — no secrets/env/headers, matching the existing redacting `Debug` policy for `McpServerConfig`). `AppState::session_mcp_servers(&self, session_id: &str) -> Vec<McpServerEntry>` — the per-session accessor a future backend-wiring task (out of scope here) would call to actually spawn/inject these servers; this task makes the set locatable and filterable, which today does not exist at all.

- [ ] **Step 1: Write the failing test for the filtered accessor**

Add to `plugins.rs`'s test module:

```rust
#[tokio::test]
async fn get_mcp_servers_excludes_plugins_not_in_allow_list() {
    let dir = tempfile::tempdir().unwrap();
    let marketplace = dir.path().join("official");
    for plugin in ["gh", "superpowers"] {
        let plugin_dir = marketplace.join(plugin);
        std::fs::create_dir_all(plugin_dir.join(".claude-plugin")).unwrap();
        std::fs::write(
            plugin_dir.join(".claude-plugin/plugin.json"),
            format!(r#"{{"name":"{plugin}","description":"d"}}"#),
        )
        .unwrap();
        std::fs::write(
            plugin_dir.join(".mcp.json"),
            r#"{"mcpServers":{"srv":{"type":"stdio","command":"echo"}}}"#,
        )
        .unwrap();
    }

    let mgr = PluginsManager::new(
        vec![MarketplaceDir {
            alias: "official".to_string(),
            path: marketplace,
        }],
        300,
    );

    let mut plugin_allows = HashMap::new();
    plugin_allows.insert(
        "official".to_string(),
        AllowList::Only(vec!["gh".to_string()]),
    );

    let servers = mgr
        .get_mcp_servers(&["official".to_string()], &plugin_allows)
        .await;
    assert_eq!(servers.len(), 1);
    assert_eq!(servers[0].plugin_name, "gh");
}
```

(Note: `.mcp.json`'s exact wire key — `"mcpServers"` vs. `"mcp_servers"` — must match whatever `McpJsonFile`'s existing `#[serde(rename...)]` already declares; check `plugins.rs:90-93` before writing this fixture, and use the confirmed key.)

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib get_mcp_servers_excludes_plugins_not_in_allow_list`
Expected: FAIL — compile error, no method `get_mcp_servers`.

- [ ] **Step 3: Add the filtered accessor**

In `plugins.rs`, directly after `all_mcp_servers` (`plugins.rs:501-517`):

```rust
    /// Filtered, session-scoped sibling of [`Self::all_mcp_servers`]: only
    /// entries whose owning plugin is admitted by `plugin_allows` for its
    /// marketplace. Closes the gap where MCP servers had no per-session
    /// locate/filter path at all (`all_mcp_servers` has no production caller
    /// — every consumer needs THIS narrowed, session-aware view instead).
    pub async fn get_mcp_servers(
        &self,
        selected: &[String],
        plugin_allows: &HashMap<String, AllowList>,
    ) -> Vec<McpServerEntry> {
        let canonical = self.canonical_marketplaces();
        let results = self.get_all().await;
        canonical
            .iter()
            .zip(results)
            .filter(|((alias, _), _)| selected.iter().any(|s| s == alias))
            .flat_map(|((alias, _), result)| {
                let alias = alias.clone();
                let plugin_allows = plugin_allows.clone();
                result.plugins.into_iter().flat_map(move |plugin| {
                    let admitted = match plugin_allows.get(&alias) {
                        Some(allow) => allow.allows(&plugin.name),
                        None => true,
                    };
                    let alias = alias.clone();
                    let plugin_name = plugin.name.clone();
                    plugin
                        .mcp_servers
                        .into_iter()
                        .filter(move |_| admitted)
                        .map(move |(server_name, config)| McpServerEntry {
                            marketplace_alias: alias.clone(),
                            plugin_name: plugin_name.clone(),
                            server_name,
                            config,
                        })
                })
            })
            .collect()
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib get_mcp_servers_excludes_plugins_not_in_allow_list`
Expected: PASS.

- [ ] **Step 5: Add the catalog projection**

In `plugins.rs`, add `CatalogMcpServer` directly after `CatalogCommand` (`plugins.rs:218-229`):

```rust
/// One MCP-server node in the marketplace catalog — advertise-only: name and
/// transport type, never `env`/`headers`/credentials (mirrors `McpServerConfig`'s
/// redacting `Debug` policy).
#[derive(Debug, Clone, Serialize, PartialEq, Eq)]
pub struct CatalogMcpServer {
    pub name: String,
    pub server_type: String,
    pub plugin: String,
    pub marketplace: String,
}
```

Add `pub mcp_servers: Vec<CatalogMcpServer>` to `CatalogPlugin` (`plugins.rs:234-241`), and populate it inside `catalog()` (`plugins.rs:1163-1252`) the same way `skills_by`/`agents_by`/`commands_by` are grouped — add an `mcp_servers_by: HashMap<(String, String), Vec<CatalogMcpServer>>` built from `self.get_mcp_servers(selected, &HashMap::new()).await` (no plugin filtering in the marketplace-granular preview, matching the existing "no per-item filter" comment on the skills grouping at line 1176), and thread it into `catalog_marketplace_from(...)`'s call (which will need a new parameter — update its signature accordingly, mirroring how `skills_by`/`agents_by`/`commands_by` are already threaded).

- [ ] **Step 6: Add `AppState::session_mcp_servers`**

In `crates/mcp-gateway/src/lib.rs`, directly after `gateway_skills` (which ends where the `None => self.plugins...` branch closes, just past line 620), add:

```rust
    /// MCP servers declared by the plugins visible to `session_id`, narrowed
    /// by that session's persisted plugin allow-list. The locate/filter
    /// surface a future backend-wiring step uses to actually build/inject
    /// these servers into the running sandbox — today nothing calls
    /// `PluginsManager::all_mcp_servers` in production, so this closes that
    /// gap at the location/filtering layer.
    pub async fn session_mcp_servers(&self, session_id: &str) -> Vec<plugins::McpServerEntry> {
        let Some(info) = self.sessions.get_session(session_id).await else {
            return Vec::new();
        };
        let mut selected = Vec::new();
        let mut plugin_allows: std::collections::HashMap<String, session::AllowList> =
            std::collections::HashMap::new();
        for m in info
            .mounts
            .iter()
            .filter(|m| m.kind == session::MountKind::Marketplace)
        {
            if let Some(alias) = m.relative_path.clone() {
                if let Some(allow) = &m.plugin_allow {
                    plugin_allows.insert(alias.clone(), allow.clone());
                }
                selected.push(alias);
            }
        }
        self.plugins.get_mcp_servers(&selected, &plugin_allows).await
    }
```

- [ ] **Step 7: Write and run an integration test proving locate/filter parity end-to-end**

Add to `crates/mcp-gateway/tests/plugin_selection_api.rs`:

```rust
#[tokio::test]
async fn session_mcp_servers_are_narrowed_by_plugin_selection() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp: serde_json::Value = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": [{"marketplace": "official", "plugin": "gh"}]
        }))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    let session_id = resp["session_id"].as_str().unwrap();
    let servers = gw.state.session_mcp_servers(session_id).await;
    assert!(
        servers.iter().all(|s| s.plugin_name == "gh"),
        "only gh's MCP servers may be locatable for this session"
    );
}
```

Run: `cargo test -p mcp-gateway --test plugin_selection_api session_mcp_servers_are_narrowed_by_plugin_selection`
Expected: PASS (adjust `TestGateway`'s field name for the shared `AppState` handle if `tests/common` exposes it under a different name — confirm via `tests/common/mod.rs` before writing this step).

- [ ] **Step 8: Commit**

```bash
git add crates/mcp-gateway/src/plugins.rs crates/mcp-gateway/src/lib.rs crates/mcp-gateway/tests/plugin_selection_api.rs
git commit -m "feat(gateway): add a filtered, session-scoped MCP-server locate surface"
```

---

### Task 10: Dependency manifest and deterministic transitive resolution (separately reviewable)

**Files:**
- Modify: `crates/mcp-gateway/src/plugins.rs:400-405` (`PluginJson` gains `dependencies`), `:148-162` (`PluginInfo` gains `dependencies`), `:1893-1904` (`load_plugin` threads it through), new `resolve_plugin_dependencies` function
- Modify: `crates/mcp-gateway/src/lib.rs:228-229` area (new `AppConfig.trusted_dependency_marketplaces` field + its `Default` entry near line 361)
- Modify: `crates/mcp-gateway/src/main.rs:976-985` area (parse `TRUSTED_DEPENDENCY_MARKETPLACES`)
- Modify: `crates/mcp-gateway/src/api/sandboxes.rs` (wire the dependency resolver into `create_sandbox`, after Task 3's `plugin_refs_before_deps` and before Task 6's `plugin_allows` construction, updating both to use the dependency-expanded set)
- Create fixtures: `crates/mcp-gateway/tests/fixtures/marketplaces/official/gh/.claude-plugin/plugin.json` (extend with a `dependencies` array), new `crates/mcp-gateway/tests/fixtures/marketplaces/custom/devtools/.claude-plugin/plugin.json` (add if not already present, extend with a cross-marketplace dependency on `official/gh`)
- Test: `crates/mcp-gateway/src/plugins.rs` (inline, unit-level cycle/missing/ordering/trust tests)

**Interfaces:**
- Consumes: `PluginRef` (Task 1); `plugin_refs_before_deps: Vec<PluginRef>` and `selected: Vec<String>` (Task 3); `PluginInfo` (existing, extended here).
- Produces: `PluginInfo.dependencies: Vec<PluginRef>` (new, `#[serde(default)]`). `pub enum DependencyResolutionError { MissingDependency { of: PluginRef, missing: PluginRef }, Cycle(Vec<PluginRef>), UntrustedCrossMarketplace { of: PluginRef, dependency: PluginRef } }`. `pub fn resolve_plugin_dependencies(requested: &[PluginRef], catalog: &HashMap<PluginRef, PluginInfo>, trusted_dependency_marketplaces: &[String]) -> Result<Vec<PluginRef>, DependencyResolutionError>` — a **pure, synchronous** function (no `&self`, no I/O) so its cycle/ordering logic is unit-testable in isolation; `create_sandbox` (Task 3/4/6) builds the `catalog` map once per request from `state.plugins.get_all()` and calls this. `AppConfig.trusted_dependency_marketplaces: Vec<String>` (env `TRUSTED_DEPENDENCY_MARKETPLACES`, comma-separated canonical aliases, default empty ⇒ no cross-marketplace dependency is trusted).

- [ ] **Step 1: Write the failing unit tests for the resolver (cycles, missing deps, ordering, trust)**

Add to `plugins.rs`'s test module:

```rust
fn plugin_ref(marketplace: &str, plugin: &str) -> crate::session::PluginRef {
    crate::session::PluginRef {
        marketplace: marketplace.to_string(),
        plugin: plugin.to_string(),
    }
}

fn info_with_deps(deps: Vec<crate::session::PluginRef>) -> PluginInfo {
    PluginInfo {
        name: "unused".to_string(),
        description: String::new(),
        version: None,
        author: None,
        source: PluginSource::Local(".".to_string()),
        category: None,
        tags: vec![],
        plugin_dir: PathBuf::from("."),
        mcp_servers: HashMap::new(),
        skills: vec![],
        dependencies: deps,
    }
}

#[test]
fn resolve_plugin_dependencies_expands_transitively_in_deterministic_order() {
    let mut catalog = HashMap::new();
    catalog.insert(
        plugin_ref("official", "gh"),
        info_with_deps(vec![plugin_ref("official", "git-core")]),
    );
    catalog.insert(plugin_ref("official", "git-core"), info_with_deps(vec![]));

    let effective =
        resolve_plugin_dependencies(&[plugin_ref("official", "gh")], &catalog, &[]).unwrap();

    assert_eq!(
        effective,
        vec![plugin_ref("official", "git-core"), plugin_ref("official", "gh")],
        "dependencies must precede the plugin that requires them, deterministically"
    );
}

#[test]
fn resolve_plugin_dependencies_detects_cycles() {
    let mut catalog = HashMap::new();
    catalog.insert(
        plugin_ref("official", "a"),
        info_with_deps(vec![plugin_ref("official", "b")]),
    );
    catalog.insert(
        plugin_ref("official", "b"),
        info_with_deps(vec![plugin_ref("official", "a")]),
    );

    let err = resolve_plugin_dependencies(&[plugin_ref("official", "a")], &catalog, &[]).unwrap_err();
    assert!(matches!(err, DependencyResolutionError::Cycle(_)));
}

#[test]
fn resolve_plugin_dependencies_detects_missing_dependency() {
    let mut catalog = HashMap::new();
    catalog.insert(
        plugin_ref("official", "gh"),
        info_with_deps(vec![plugin_ref("official", "ghost")]),
    );

    let err = resolve_plugin_dependencies(&[plugin_ref("official", "gh")], &catalog, &[]).unwrap_err();
    assert_eq!(
        err,
        DependencyResolutionError::MissingDependency {
            of: plugin_ref("official", "gh"),
            missing: plugin_ref("official", "ghost"),
        }
    );
}

#[test]
fn resolve_plugin_dependencies_rejects_untrusted_cross_marketplace_dependency() {
    let mut catalog = HashMap::new();
    catalog.insert(
        plugin_ref("custom", "devtools"),
        info_with_deps(vec![plugin_ref("official", "gh")]),
    );
    catalog.insert(plugin_ref("official", "gh"), info_with_deps(vec![]));

    let err =
        resolve_plugin_dependencies(&[plugin_ref("custom", "devtools")], &catalog, &[]).unwrap_err();
    assert_eq!(
        err,
        DependencyResolutionError::UntrustedCrossMarketplace {
            of: plugin_ref("custom", "devtools"),
            dependency: plugin_ref("official", "gh"),
        }
    );

    // Trusting "official" as a dependency source permits it.
    let ok = resolve_plugin_dependencies(
        &[plugin_ref("custom", "devtools")],
        &catalog,
        &["official".to_string()],
    )
    .unwrap();
    assert_eq!(
        ok,
        vec![plugin_ref("official", "gh"), plugin_ref("custom", "devtools")]
    );
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cargo test -p mcp-gateway --lib resolve_plugin_dependencies_`
Expected: FAIL — compile errors (`PluginInfo` has no field `dependencies`; `resolve_plugin_dependencies`/`DependencyResolutionError` do not exist).

- [ ] **Step 3: Add `dependencies` to `PluginJson`/`PluginInfo` and thread it through `load_plugin`**

In `plugins.rs`, extend `PluginJson` (`plugins.rs:398-405`):

```rust
/// Contents of `.claude-plugin/plugin.json`.
#[derive(Debug, Deserialize)]
struct PluginJson {
    name: String,
    description: Option<String>,
    author: Option<PluginAuthor>,
    version: Option<String>,
    /// Plugins this one depends on (sandbox-plugin-selection). Not merged
    /// with `catalog_entry` (marketplace.json entries never declare
    /// dependencies) — `plugin.json` is authoritative.
    #[serde(default)]
    dependencies: Vec<crate::session::PluginRef>,
}
```

Extend `PluginInfo` (`plugins.rs:148-162`), adding directly after `skills`:

```rust
    /// Plugins this plugin depends on — see `resolve_plugin_dependencies`.
    #[serde(default)]
    pub dependencies: Vec<crate::session::PluginRef>,
```

In `load_plugin` (`plugins.rs:1893-1904`), add `dependencies: plugin_json.dependencies.clone(),` to the constructed `PluginInfo` (before the `plugin_json` binding is moved/dropped — capture it before the `name`/`description` destructuring consumes `plugin_json` by value, or clone earlier; verify against the exact existing merge block at lines 1817-1846 to avoid a move-after-use compile error).

- [ ] **Step 4: Implement `DependencyResolutionError` and `resolve_plugin_dependencies`**

Add as a free function in `plugins.rs` (module-level, not inside `impl PluginsManager` — it takes an explicit `catalog` parameter rather than `&self`, per the Interfaces contract, so it stays independently unit-testable):

```rust
/// Failure modes for [`resolve_plugin_dependencies`]. `Cycle` carries the
/// cyclic path in detection order (first repeated ref last) for diagnostics.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum DependencyResolutionError {
    MissingDependency {
        of: crate::session::PluginRef,
        missing: crate::session::PluginRef,
    },
    Cycle(Vec<crate::session::PluginRef>),
    UntrustedCrossMarketplace {
        of: crate::session::PluginRef,
        dependency: crate::session::PluginRef,
    },
}

/// Deterministically expand `requested` plugin refs into their full
/// transitive dependency set, using a DFS post-order walk (dependencies
/// visited, and therefore emitted, before the plugin that needs them) over
/// each plugin's `dependencies` array in declared order. `requested` plugins
/// are walked in the given order, so the same input always produces the same
/// output order — this is the "deterministic ordering" the design spec
/// requires.
///
/// A same-marketplace dependency is always permitted. A cross-marketplace
/// dependency is permitted only when the DEPENDENCY's marketplace alias is in
/// `trusted_dependency_marketplaces` (an "official-style" trust allowlist —
/// there is no pre-existing schema for this in the gateway, so this plan
/// defines it fresh as `AppConfig.trusted_dependency_marketplaces`).
pub fn resolve_plugin_dependencies(
    requested: &[crate::session::PluginRef],
    catalog: &HashMap<crate::session::PluginRef, PluginInfo>,
    trusted_dependency_marketplaces: &[String],
) -> Result<Vec<crate::session::PluginRef>, DependencyResolutionError> {
    let mut effective: Vec<crate::session::PluginRef> = Vec::new();
    let mut visited: std::collections::HashSet<crate::session::PluginRef> =
        std::collections::HashSet::new();

    fn visit(
        current: &crate::session::PluginRef,
        catalog: &HashMap<crate::session::PluginRef, PluginInfo>,
        trusted: &[String],
        visiting: &mut Vec<crate::session::PluginRef>,
        visited: &mut std::collections::HashSet<crate::session::PluginRef>,
        effective: &mut Vec<crate::session::PluginRef>,
    ) -> Result<(), DependencyResolutionError> {
        if visited.contains(current) {
            return Ok(());
        }
        if visiting.contains(current) {
            let mut cycle = visiting.clone();
            cycle.push(current.clone());
            return Err(DependencyResolutionError::Cycle(cycle));
        }
        visiting.push(current.clone());

        let Some(info) = catalog.get(current) else {
            // The plugin itself is unknown — resolve_plugin_selection (an
            // earlier gate) already rejects an unknown top-level request, so
            // reaching here means a DEPENDENCY (not the top-level request)
            // named an unknown plugin.
            return Err(DependencyResolutionError::MissingDependency {
                of: visiting[visiting.len().saturating_sub(2)].clone(),
                missing: current.clone(),
            });
        };

        for dep in &info.dependencies {
            if dep.marketplace != current.marketplace && !trusted.iter().any(|t| t == &dep.marketplace) {
                return Err(DependencyResolutionError::UntrustedCrossMarketplace {
                    of: current.clone(),
                    dependency: dep.clone(),
                });
            }
            if !catalog.contains_key(dep) {
                return Err(DependencyResolutionError::MissingDependency {
                    of: current.clone(),
                    missing: dep.clone(),
                });
            }
            visit(dep, catalog, trusted, visiting, visited, effective)?;
        }

        visiting.pop();
        visited.insert(current.clone());
        effective.push(current.clone());
        Ok(())
    }

    let mut visiting = Vec::new();
    for r in requested {
        visit(
            r,
            catalog,
            trusted_dependency_marketplaces,
            &mut visiting,
            &mut visited,
            &mut effective,
        )?;
    }
    Ok(effective)
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `cargo test -p mcp-gateway --lib resolve_plugin_dependencies_`
Expected: all 4 tests pass.

- [ ] **Step 6: Add `AppConfig.trusted_dependency_marketplaces` and its env parsing**

In `crates/mcp-gateway/src/lib.rs`, add the field directly after `default_marketplaces` (line 228):

```rust
    /// Marketplace aliases that may be depended upon FROM a different
    /// marketplace (sandbox-plugin-selection dependency resolution). A
    /// same-marketplace dependency is always allowed regardless of this list.
    /// Empty (default) ⇒ no cross-marketplace dependency is trusted. Set via
    /// `TRUSTED_DEPENDENCY_MARKETPLACES` as a comma-separated list.
    pub trusted_dependency_marketplaces: Vec<String>,
```

Add its `Default` entry near line 361 (alongside `default_marketplaces: None,`):

```rust
            trusted_dependency_marketplaces: Vec::new(),
```

In `crates/mcp-gateway/src/main.rs`, directly after the `default_marketplaces` env-parsing block (`main.rs:976-985`), add:

```rust
        trusted_dependency_marketplaces: parse_csv_env("TRUSTED_DEPENDENCY_MARKETPLACES"),
```

- [ ] **Step 7: Wire the resolver into `create_sandbox`**

In `crates/mcp-gateway/src/api/sandboxes.rs`, directly after Task 3's `plugin_refs_before_deps` binding and before Task 6's `plugin_refs_by_marketplace`/`plugin_allows` construction, insert:

```rust
    let plugin_catalog: std::collections::HashMap<crate::session::PluginRef, crate::plugins::PluginInfo> =
        {
            let mut map = std::collections::HashMap::new();
            for result in state.plugins.get_all().await {
                for p in result.plugins {
                    map.insert(
                        crate::session::PluginRef {
                            marketplace: result.alias.clone(),
                            plugin: p.name.clone(),
                        },
                        p,
                    );
                }
            }
            map
        };
    let plugin_refs_before_deps = match crate::plugins::resolve_plugin_dependencies(
        &plugin_refs_before_deps,
        &plugin_catalog,
        &state.config.trusted_dependency_marketplaces,
    ) {
        Ok(effective) => effective,
        Err(crate::plugins::DependencyResolutionError::MissingDependency { of, missing }) => {
            return bad_request(format!(
                "plugin {}/{} depends on unknown plugin {}/{}",
                of.marketplace, of.plugin, missing.marketplace, missing.plugin
            ))
            .into_response();
        }
        Err(crate::plugins::DependencyResolutionError::Cycle(cycle)) => {
            let path = cycle
                .iter()
                .map(|r| format!("{}/{}", r.marketplace, r.plugin))
                .collect::<Vec<_>>()
                .join(" -> ");
            return bad_request(format!("plugin dependency cycle detected: {path}")).into_response();
        }
        Err(crate::plugins::DependencyResolutionError::UntrustedCrossMarketplace { of, dependency }) => {
            return bad_request(format!(
                "plugin {}/{} depends on untrusted cross-marketplace plugin {}/{}",
                of.marketplace, of.plugin, dependency.marketplace, dependency.plugin
            ))
            .into_response();
        }
    };
```

This shadows the earlier `plugin_refs_before_deps` with the dependency-expanded set, so Task 4's `pluginResolution.effective` and Task 6's `plugin_allows` both automatically pick up the expanded set with no further changes — but `requested_plugin_refs` (built in Task 4, Step 3, as `Option<Vec<PluginRef>>` from the raw `plugin_selection` before this expansion) must NOT be reassigned here, preserving the "requested = exactly what the caller sent, including the All → `null` distinction" contract.

- [ ] **Step 8: Add fixture plugins with dependencies for the integration-level test**

Add a `dependencies` array to `crates/mcp-gateway/tests/fixtures/marketplaces/official/gh/.claude-plugin/plugin.json` if that file does not already declare one (create the `.claude-plugin/` dir and `plugin.json` if the fixture currently relies on `marketplace.json` catalog entries only — check first), for example depending on nothing (keep `gh` dependency-free) and instead add a **new** small fixture plugin, `official/git-core`, with an empty `plugin.json`, then update `official/gh/.claude-plugin/plugin.json` to declare `"dependencies": [{"marketplace":"official","plugin":"git-core"}]`.

- [ ] **Step 9: Write the failing integration test for dependency expansion in the response**

Add to `crates/mcp-gateway/tests/plugin_selection_api.rs`:

```rust
#[tokio::test]
async fn create_expands_dependencies_into_effective_set() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp: serde_json::Value = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": [{"marketplace": "official", "plugin": "gh"}]
        }))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    let effective = resp["pluginResolution"]["effective"].as_array().unwrap();
    let names: Vec<&str> = effective
        .iter()
        .map(|r| r["plugin"].as_str().unwrap())
        .collect();
    assert_eq!(
        names,
        vec!["git-core", "gh"],
        "git-core (gh's dependency) must precede gh in the effective set"
    );
}
```

- [ ] **Step 10: Run test to verify it fails, then implement fixtures/wiring, then verify it passes**

Run: `cargo test -p mcp-gateway --test plugin_selection_api create_expands_dependencies_into_effective_set`
Expected first: FAIL (effective is just `["gh"]`, no dependency expansion, until Step 8's fixture exists). After adding the fixture files from Step 8:
Run: `cargo test -p mcp-gateway --test plugin_selection_api create_expands_dependencies_into_effective_set`
Expected: PASS.

- [ ] **Step 11: Run the full suite**

Run: `cargo test -p mcp-gateway`
Expected: all tests pass, including every earlier task's tests (verify none of Tasks 3/4/6's tests assumed `effective == requested` for a dependency-free plugin — they should, since the fixtures used there declare no dependencies).

- [ ] **Step 12: Commit**

```bash
git add crates/mcp-gateway/src/plugins.rs crates/mcp-gateway/src/lib.rs crates/mcp-gateway/src/main.rs crates/mcp-gateway/src/api/sandboxes.rs crates/mcp-gateway/tests/fixtures crates/mcp-gateway/tests/plugin_selection_api.rs
git commit -m "feat(gateway): deterministic transitive plugin dependency resolution"
```

---

### Task 11: End-to-end integration test sweep (catalog/session parity for plugin selection)

**Files:**
- Modify: `crates/mcp-gateway/tests/plugin_selection_api.rs` (add the remaining spec-12 gateway test bullets not yet covered by Tasks 3/4/9/10)
- Modify: `crates/mcp-gateway/tests/catalog_api.rs` (extend the existing `preview_skill_set_matches_get_skill_infos` parity test to also cover a plugin-narrowed selection)

**Interfaces:**
- Consumes: every wire type and endpoint from Tasks 1-10.
- Produces: nothing new — this task is pure test coverage closing the design spec's Section 12 gateway test list.

- [ ] **Step 1: Write the failing tests for the remaining spec-12 bullets**

Add to `crates/mcp-gateway/tests/plugin_selection_api.rs`:

```rust
#[tokio::test]
async fn plugins_null_is_legacy_all_behavior() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp: serde_json::Value = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": null
        }))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    assert_eq!(resp["pluginResolution"]["requested"], serde_json::json!(null));
    assert!(!resp["pluginResolution"]["effective"].as_array().unwrap().is_empty());
}

#[tokio::test]
async fn plugin_selection_empty_array_yields_zero_effective_plugins() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    let resp: serde_json::Value = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"],
            "pluginSelection": []
        }))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    // Explicit empty selection: `requested` is `[]` (a concrete, empty list),
    // not `null` — only an absent/null `pluginSelection` (PluginSelection::All)
    // produces a `null` `requested`.
    assert_eq!(resp["pluginResolution"]["requested"], serde_json::json!([]));
    assert_eq!(resp["pluginResolution"]["effective"], serde_json::json!([]));
}

#[tokio::test]
async fn old_format_request_without_plugin_selection_field_is_unaffected() {
    let gw = TestGateway::start_with_marketplaces(official_only(), None).await;

    // No `pluginSelection` key at all in the body — an old-format client.
    let resp = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official"]
        }))
        .send()
        .await
        .unwrap();

    assert_eq!(resp.status(), 201);
}
```

- [ ] **Step 2: Run tests to verify they fail (or pass — confirm each is a real gap, not a duplicate)**

Run: `cargo test -p mcp-gateway --test plugin_selection_api plugins_null_is_legacy_all_behavior plugin_selection_empty_array_yields_zero_effective_plugins old_format_request_without_plugin_selection_field_is_unaffected`
Expected: `old_format_request_without_plugin_selection_field_is_unaffected` likely already PASSES (Tasks 1-10 are additive); `plugins_null_is_legacy_all_behavior` and `plugin_selection_empty_array_yields_zero_effective_plugins` should already PASS too if Tasks 1-10 were implemented correctly — this step's purpose is to confirm that, not to drive new production code. If either FAILS, that indicates a defect introduced in an earlier task; fix the earlier task's code (not this test) before proceeding.

- [ ] **Step 3: Extend the catalog/session parity test for a plugin-narrowed selection**

Add to `crates/mcp-gateway/tests/catalog_api.rs`, directly after `preview_skill_set_matches_get_skill_infos`:

```rust
#[tokio::test]
async fn session_created_with_plugin_selection_exposes_exactly_that_plugins_skills() {
    let gw = TestGateway::start_with_marketplaces(both_marketplaces(), None).await;

    let resp: serde_json::Value = gw
        .client
        .post(gw.url("/api/v1/sandboxes"))
        .json(&serde_json::json!({
            "app": {"id": "test-app"},
            "marketplaces": ["official", "custom"],
            "pluginSelection": [{"marketplace": "official", "plugin": "gh"}]
        }))
        .send()
        .await
        .unwrap()
        .json()
        .await
        .unwrap();

    let session_id = resp["session_id"].as_str().unwrap();
    let skills = gw.state.gateway_skills(Some(session_id)).await;
    assert!(
        skills.iter().all(|s| s.plugin.as_deref() == Some("gh")),
        "only gh's skills may surface for a session scoped to official/gh"
    );
}
```

Run: `cargo test -p mcp-gateway --test catalog_api session_created_with_plugin_selection_exposes_exactly_that_plugins_skills`
Expected: PASS (this exercises the exact Task 7 wiring end-to-end through the real create-sandbox path, not a direct `PluginsManager` call, closing the design spec's "catalog↔session parity" guarantee for the new plugin dimension).

- [ ] **Step 4: Run the complete crate test suite one final time**

Run: `cargo test -p mcp-gateway`
Expected: every test in the crate passes — this is the gate before the live-verification task.

- [ ] **Step 5: Commit**

```bash
git add crates/mcp-gateway/tests/plugin_selection_api.rs crates/mcp-gateway/tests/catalog_api.rs
git commit -m "test(gateway): close out spec-12 gateway test coverage for plugin selection"
```

---

### Task 12: Deploy and verify against a real running gateway (rollout-order gate)

**Files:** none (verification only — no code changes in this task).

**Interfaces:**
- Consumes: the deployed gateway's `POST /api/v1/sandboxes` and `GET /api/v1/marketplaces/preview` endpoints from Tasks 1-11.
- Produces: a verification record (curl transcript) that the app-repo PR (out of scope) can be unblocked by, per the design spec's stated rollout order ("Gateway repo ships first... deploy, and verify against a real running gateway before the app repo PR merges").

- [ ] **Step 1: Confirm the build is clean and ready to deploy**

Run: `cargo build -p mcp-gateway --release`
Expected: builds with no errors or warnings introduced by this plan.

- [ ] **Step 2: Deploy the gateway per the operator's existing deploy process (outside this plan's scope) and set placeholder environment variables for verification**

This step is manual/operator-driven; record the deployed base URL and a valid app bearer credential as shell variables (never hard-code them):

```bash
export GATEWAY_URL="https://your-deployed-gateway.example"   # placeholder — replace with the real deployment URL
export GATEWAY_TOKEN="replace-with-a-real-app-bearer-token"  # placeholder — replace with a real, non-committed credential
```

- [ ] **Step 3: Curl-probe the capability signal**

```bash
curl -sS -H "Authorization: Bearer $GATEWAY_TOKEN" \
  "$GATEWAY_URL/api/v1/marketplaces/preview" | jq '.capabilities.pluginFiltering'
```

Expected output: `true`.

- [ ] **Step 4: Curl-probe an unknown-plugin rejection (400, no side effects)**

```bash
curl -sS -o /tmp/create_response.json -w "%{http_code}\
" \
  -H "Authorization: Bearer $GATEWAY_TOKEN" -H "Content-Type: application/json" \
  -X POST "$GATEWAY_URL/api/v1/sandboxes" \
  -d '{"app":{"id":"verify-app"},"marketplaces":["official"],"pluginSelection":[{"marketplace":"official","plugin":"does-not-exist"}]}'
cat /tmp/create_response.json | jq .
```

Expected: the status line prints `400`, and the body contains `"code": 400` and an `"unknown": [{"marketplace":"official","plugin":"does-not-exist"}]` array.

- [ ] **Step 5: Curl-probe a successful, narrowed create and its `pluginResolution` block**

```bash
curl -sS -H "Authorization: Bearer $GATEWAY_TOKEN" -H "Content-Type: application/json" \
  -X POST "$GATEWAY_URL/api/v1/sandboxes" \
  -d '{"app":{"id":"verify-app"},"marketplaces":["official"],"pluginSelection":[{"marketplace":"official","plugin":"gh"}]}' \
  | jq '{status: .session_id, pluginResolution}'
```

Expected: a `201`-shaped body (non-null `session_id`) whose `pluginResolution.supported` is `true`, `pluginResolution.requested` echoes exactly `[{"marketplace":"official","plugin":"gh"}]`, and `pluginResolution.effective` includes `gh` (plus any of its declared dependencies).

- [ ] **Step 6: Curl-probe backward compatibility (an old-format request with no `pluginSelection` key at all)**

```bash
curl -sS -o /tmp/legacy_response.json -w "%{http_code}\
" \
  -H "Authorization: Bearer $GATEWAY_TOKEN" -H "Content-Type: application/json" \
  -X POST "$GATEWAY_URL/api/v1/sandboxes" \
  -d '{"app":{"id":"verify-app"},"marketplaces":["official"]}'
cat /tmp/legacy_response.json | jq '.pluginResolution'
```

Expected: status line prints `201`; `pluginResolution.requested` is `null` (no `pluginSelection` key was sent — `PluginSelection::All`) and `pluginResolution.effective` lists every plugin in `official` — confirming a pre-existing (legacy) caller is unaffected by this feature's presence.

- [ ] **Step 7: Record the verification outcome**

Summarize the five curl outcomes (Steps 3-6) in the PR description or deploy log for this change, explicitly stating: "Gateway plugin-selection verified live against `$GATEWAY_URL` on <date> — capability signal, 400-reject, resolution reporting, and backward compatibility all confirmed." This is the gate the design spec requires before the app-repo PR (out of scope here) may merge.

---

## Self-Review

**1. Spec coverage** (design spec `2026-08-11-sandbox-plugin-selection-design.md`, Sections 5.3/5.4/6/9/12 — gateway-owned items only):
- Structured `{marketplace, plugin}` identity (5.1): Task 1 (`PluginRef`).
- Tri-state `plugins` selection on create, distinct from mount data (5.3): Task 1 (`pluginSelection` field, distinct wire key), Global Constraints entry.
- `pluginResolution: {supported, requested, effective, failed}` on the response (5.3), with `requested` nullable (`null`/`[]`/list, per Section 5.3's corrected tri-state wording) and the field explicitly `#[serde(rename = "pluginResolution")]`-annotated to produce camelCase: Task 4, expanded by Task 10.
- Unknown id → whole-request 400 before side effects (5.3, 6): Task 3.
- `capabilities.pluginFiltering` on preview (5.4): Task 5.
- Component responsibility table's "reject unknown plugin id with 400" + "resolve dependencies" + "expose capability signal" (6): Tasks 3, 5, 10.
- Rollout order — gateway ships and is verified against a live instance first (9): Task 12.
- Gateway test list (12): plugins:null legacy (Task 11), plugins:[] zero-plugin (Task 11), non-empty subset with resolved deps in effective (Tasks 4, 10, 11), unknown pair → 400 (Task 3), capability signal false-when-unsupported is inherently satisfied by "the field simply doesn't exist on an old gateway" — this plan makes the field always `true` once deployed, which is the positive-case half of that requirement (Task 5); effective-inventory distinguishable from requested (Task 4); backward-compat old-format request unaffected (Tasks 3, 11).
- User's mandatory corrections: intersection semantics with existing `SelectionFilter` (Global Constraints + Task 7/8 filter ordering: plugin_allows filter runs before the skills/agents/commands allow-list filter in every one of `get_skill_infos`/`get_agent_discoveries`/`get_command_discoveries`); do-not-reuse the mount-data `plugins` field (Global Constraints, verified via direct code read of `volume_spec.rs:241-243`/`317-329`); command filtering gap (Task 8, closing the exact `orchestrate.rs:264-269` comment); MCP-server locate/filter gap (Task 9, closing the confirmed zero-call-site `all_mcp_servers`); persistence via `skill_allow`-precedent migration (Task 6, verbatim pattern match against `db.rs:268-285`/`402-488`); dependency resolution as its own reviewable task with a new minimal trust schema, cycle/missing/ordering/requested-vs-effective tests (Task 10, since no existing schema was found after an exhaustive grep); live curl verification with placeholder env vars (Task 12).

**2. Placeholder scan:** No "TBD"/"TODO"/"handle appropriately"/"similar to Task N" phrasing appears anywhere in this plan. Every code block is complete, compilable Rust (modulo the caller updating any additional call site the compiler flags after a signature change, which is explicitly called out per task rather than hand-waved — e.g., Tasks 6, 7, 8 each explicitly say "update any other call site the compiler flags" together with naming the specific known ones).

**3. Type consistency:** `PluginRef { marketplace, plugin }` is used with these exact field names in every task (1 through 11) with no drift. `PluginSelection` (`All`/`Only(Vec<PluginRef>)`) is defined once in Task 1 and never redefined. `MountRecord.plugin_allow: Option<AllowList>` (Task 6) is read with that exact name in Tasks 7, 8, 9. `get_skill_infos`/`get_agent_discoveries`/`get_command_discoveries` all grow to take `plugin_allows: &HashMap<String, AllowList>` as their final parameter, in that consistent position, across Tasks 7-8. `PluginResolution`'s four fields (`supported`, `requested: Option<Vec<PluginRef>>` — `None`/`Some(vec![])`/`Some(refs)` for the All/explicit-empty/explicit-subset cases respectively — `effective`, `failed`) are defined in Task 4 and never renamed by Task 10 (which only changes what populates `effective`, not the struct). `resolve_plugin_dependencies`'s signature (`requested`, `catalog`, `trusted_dependency_marketplaces`) is defined once in Task 10 and called with that exact argument order in Task 10's own `create_sandbox` wiring step.

---

**Plan complete and returned above** (this planning agent has no file-write access; please save it verbatim to `docs/superpowers/plans/2026-08-11-sandbox-plugin-selection-gateway.md`). **Total task count: 12** (Tasks 1-11 implementation/tests, Task 12 live verification).

Two execution options once saved:
1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in-session using `executing-plans`, batch execution with checkpoints.

### Critical Files for Implementation
- B:\sources\SandboxedOstoolsMcpServer\crates\mcp-gateway\src\plugins.rs
- B:\sources\SandboxedOstoolsMcpServer\crates\mcp-gateway\src\session.rs
- B:\sources\SandboxedOstoolsMcpServer\crates\mcp-gateway\src\api\sandboxes.rs
- B:\sources\SandboxedOstoolsMcpServer\crates\mcp-gateway\src\db.rs
- B:\sources\SandboxedOstoolsMcpServer\crates\mcp-gateway\src\volume_spec.rs