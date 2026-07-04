# Design — Skill + Sub-agent-enabled, Cross-repo Code-Review Daemon (P3)

Date: 2026-07-04
Component: `samples/CodeReviewDaemon.Sample` (with small shared fixes in `src/LmAgentInfra`)
Status: Approved design — ready for implementation planning.

## 1. Goal

Turn the Code-Review Daemon's review agent from a **diff-only text reviewer** into a **tool-using,
`code-reviewer`-skill-driven, cross-repo reviewer** that can invoke the real `code-reviewer` sub-agents from
the `gb-plugins` marketplace, read across the connected repos and their shared `Contracts/`, and still
preserve the daemon's invariant that **the daemon — not the agent — owns all posting**.

Today (`Agents/LiveReviewAgentLoopFactory.cs`) the review loop is built with an empty `FunctionRegistry`
and no sub-agent options: it has no tools, no skills, and no sub-agents. It reasons over the diff text the
executor supplies (`Orchestration/DaemonReviewStageExecutor.BuildReviewInput`) and nothing else.

## 2. Verified facts this design stands on

- **Skill tool:** the gateway's `Skill` tool returns the real `SKILL.md` body (a "learn this skill"
  prompt). It appears in `tools/list` when the session has a marketplace selected. gb-plugins is already
  mounted in the daemon's sessions.
- **Sub-agent bodies are on the wire.** Live check of the running gateway
  (`GET /api/v1/sandboxes/{id}/discovered`) returns `{session_id, discovered:[...]}`; `code-reviewer:*`
  appear as `kind==subagent` items carrying the **full real markdown body inline** in `content` (~27 KB),
  plus `qualified_name` (e.g. `code-reviewer:architecture-review`), `plugin`, and a `/marketplaces/...`
  `path`. `/api/v1/marketplaces/preview` is metadata-only (no body).
- **The C# client currently drops those bodies** (the only reason marketplace sub-agents look "hollow"):
  - `SandboxSessionRegistry.ListDiscoveredAsync` deserializes `{items:[...]}`, but the gateway sends
    `{discovered:[...]}` → binds to null → returns `[]`.
  - `SandboxSessionRegistry.DiscoveredItem` has no `content` field.
  - `WorkspaceSubAgentLoader.LoadOneAsync` reads the body from a **workspace file**, which does not exist
    for a `/marketplaces/...` path → skipped. LmStreaming therefore falls back to
    `MarketplaceSubAgentLoader`'s **persona stub** (name/description), not the real methodology.
- **Security enforcement point:** `AuthWebhookController` (the injector for sandbox git egress) gates only
  on shared-secret + `OAuthProviderHosts.IsAllowed(providerId, host)`. It does **not** consult the daemon's
  `OperationPolicy`. `OperationPolicy`'s fetch-vs-push discrimination is enforced only on the daemon's own
  provider HTTP (`OperationPolicyHandler` on `PolicyEnforcedHttpClientFactory`) and the submodule pre-flight
  (`SubmoduleInitializer`), never on the git transport.
- **The sandbox command runner is a boot-lifetime singleton** (`Program.cs` registers `SandboxOrchestrator`
  with a session id fixed from `CRD_SANDBOX_SESSION` at construction); it cannot rebind to a per-run
  session.

## 3. Shared fix in `src/LmAgentInfra` (unblocks real sub-agent bodies)

Three edits, each additive/backward-compatible; they also fix a real latent bug in LmStreaming (marketplace
sub-agents currently spawn as stubs):

1. `SandboxSessionRegistry.DiscoveredItemsResponse` — accept the `discovered` envelope (the gateway's
   current wire shape) instead of `items`.
2. `SandboxSessionRegistry.DiscoveredItem` — add `content` and `qualified_name` fields.
3. `WorkspaceSubAgentLoader.LoadOneAsync` — **content-first**: when `item.Content` is non-empty, parse it
   directly with `SubAgentMarkdownParser` and skip the workspace-file read (which only applies to real
   workspace `.claude/agents/*.md` whose `path` is workspace-relative). Keep the file-read as the fallback.
   Templates are keyed by `qualified_name` to avoid unqualified-name collisions across plugins.

## 4. Components (new and changed)

| Unit | Type | Responsibility |
|---|---|---|
| `SandboxSessionRegistry` (+ §3 edits) + `SandboxGatewayLifetime` | wire in + fix | Adopt the pre-running Docker gateway (`AutoSpawn=false`, `BaseUrl=http://127.0.0.1:3000`, egress-proxy unset); create the per-run session (egress + auth + marketplaces derived from the already-registered OAuth providers + config); `ListDiscoveredAsync` now returns real sub-agent `content`. |
| `ReviewSessionProvisioner` | new | Per review: ensure the host workspace dir exists; `GetOrCreateLiveSessionAsync(WorkspaceRef(id=repo#pr#head_sha, marketplaces=[gb-plugins]))` → `(sessionId, hostPath)`; own the per-run session + workspace-dir lifecycle (create → use → destroy + rmdir). |
| Per-run sandbox command runner | change (was singleton) | Replace the boot-lifetime `SandboxOrchestrator` singleton with a per-run instance constructed from the provisioned `sessionId`, so the daemon's deterministic **read/checkout** git AND the agent's tools address the SAME session/container. (The retention push is host-side per §6 Risk A, not through this runner.) |
| `SubAgentTemplateSource` build (reuse the fixed `WorkspaceSubAgentLoader`, content-first) | new wiring | After create, pull `/discovered`, filter `kind==subagent` for the `code-reviewer` plugin, parse each item's `content` → `SubAgentTemplate[]` keyed by `qualified_name`, build a `MutableSubAgentTemplateSource`. |
| `LiveReviewAgentLoopFactory` | change | Populate the review loop's `FunctionRegistry` via `FunctionRegistry.AddMcpClientsAsync({gateway}/mcp, X-Session-ID, omitServerPrefix:true)` filtered to a **read-only** allow-list (`Read/Grep/Glob` + `Skill`; no `Write/Edit`); pass `SubAgentOptions` + the template source into `MultiTurnAgentLoop`. Own + dispose the `McpClient` (mirrors the shared agent's lifetime). |
| Cross-repo checkout (`DaemonReviewStageExecutor`) | change | In the **read-scoped sandbox session**: clone `AchieveAiReviews`; checkout the PR head in the affected submodule; **populate the run's submodule allow-list** (`DaemonOperationPolicy.BuildForRun`) with the store's submodule remotes so `SubmoduleInitializer` permits them (for the same-trust-domain case — see §6 Risk B). The agent reads this checkout; it is never pushed from here. |
| Retention write (`ReviewBotRepoManager` / `PublishToReviewBotAsync`) | change (move host-side) | In the daemon's **host-side deterministic write phase** with the WRITE credential (not the sandbox): write `PRs/.../review.md` + KnowledgeBase into a host-side clone of `AchieveAiReviews` and push to a per-PR review branch. Keeps all writes off the read-only sandbox (§6 Risk A). |
| Review prompt (`Agents/DaemonAgentFactory`) | change | Instruct: load the `code-reviewer` skill via `Skill`; use its sub-agents; read across `repos/<Repo>` + `Contracts/`. Add untrusted-content / prompt-injection framing. |
| `CodeReviewDaemonOptions` + config | change | `EnableToolAssistedReview` (default off), workspace host root, `Marketplaces=gb-plugins`, read-only tool allow-list, bumped `ReviewMaxTokens`/effort, `Auth.Github.ClientId` placeholder (required so `BuildAuthProviders` emits the github egress rule). |

## 5. Data flow (per PR, tool-assisted path)

1. Poller detects a new `head_sha` → `ReviewSessionProvisioner` ensures the workspace dir and creates a
   per-run session (egress + auth + gb-plugins).
2. Capability probe (`tools/list` for `Skill`; `/discovered` for `code-reviewer:*`). Missing capabilities →
   **degrade** (skill-only or diff-only), never fail the daemon.
3. Deterministic checkout: clone `AchieveAiReviews`; init the store's allow-listed submodules; checkout the
   PR head in the affected submodule. For a public/fork target PR, **do not** co-locate the sibling private
   submodule (see §6 Risk B).
4. Build sub-agent templates from `/discovered.content`; build the read-only tool registry; construct the
   `MultiTurnAgentLoop` with tools + `SubAgentOptions`.
5. Agent turn (read-scoped session): `Skill("code-reviewer:...")` to load the methodology; `Read/Grep/Glob`
   across `repos/<Repo>` + `Contracts/`; `Agent(subagent_type:"code-reviewer:architecture-review", ...)` etc.
   The agent returns review text; its injected credential is read-scoped, so even with `Bash` it cannot
   push or post (see §6 Risk A).
6. Daemon host-side deterministic write phase (WRITE credential, no sandbox): post the review comment (own
   HTTP, `OperationPolicy`-gated) and push the artifact to the per-PR review branch on a host-side clone of
   `AchieveAiReviews`.
7. Session destroyed; host workspace dir removed.

## 6. Security model — two distinct risks, both closed

**Risk A — integrity / push.** An agent with `Bash` and a write-capable github credential could `git push`.
Because `AuthWebhookController` injects whatever token the provider yields for any allowed host and cannot
distinguish the daemon's git from the agent's `Bash` in the same session (same token, no actor identity),
we do **not** rely on a per-run policy at the webhook or on phase-flipping the token inside one session
(a still-alive agent shell would race the flip). Instead we split by **credential and location**:
- **The sandbox review session is injected with a READ-scoped github credential** (contents:read).
  The agent may clone/fetch and run `Bash` (including build/test sub-agents), but the injected token
  cannot push or write the API. This is the hard boundary — it holds even if the agent runs arbitrary git.
- **All daemon writes happen outside that session, in the daemon process, with a separate WRITE-scoped
  credential the sandbox never receives:** the comment post already runs host-side via the gated
  `PolicyEnforcedHttpClient` (unchanged); the retention push **moves host-side** — the daemon writes the
  artifact into a host-side clone of `AchieveAiReviews` and pushes it directly (no sandbox, no agent).
- The agent's tool set also excludes `Write/Edit`.

Mental model: **sandbox = read-only review; host/daemon = all writes.** This requires two github
credentials (read-scoped for sandbox egress, write-scoped for the daemon's host-side post + retention);
provisioning them is an implementation-plan detail. A simpler fallback (omit `Bash` from the agent so no
git can run in-sandbox at all) is recorded as a non-goal in §11 — it avoids the second credential but
disables build/test sub-agents, so the read-scoped-credential approach is preferred.

**Risk B — confidentiality / exfil.** Co-locating the other private submodule beside an untrusted PR lets a
prompt-injected agent `Read` it and surface it in the review the daemon posts. Mitigation:
- **Trust-gate cross-repo co-location:** co-locate the sibling submodule only for same-trust-domain PRs
  (same private org, non-fork). For a public repo or a fork PR, check out target + `Contracts/` only
  (`Contracts/` is the shared, low-sensitivity layer).
- Add prompt-injection framing to the review prompt.
- (Same-org private→private posting is within one trust boundary; the exfil risk is material only when the
  target PR is public or from an untrusted fork.)

## 7. Discovery, degradation, lifecycle

- **Discovery = boot-time PULL** via the fixed `ListDiscoveredAsync` (GET). `CreateSessionAsync` still
  registers a discovery webhook the daemon has no route for; the daemon does not use it — omit the discovery
  block for the daemon's create request (or accept/ACK the resulting 404s). No live-webhook controller, no
  pool.
- **Degrade, don't fail:** capability gaps fall back to skill-only or diff-only and log; a per-run failure
  marks `RetryPending` (existing `PrPollingService` isolation), never a process exit.
- **Lifecycle / hygiene:** per-run session created and destroyed around the run; the host workspace dir is
  removed on completion (best-effort with retry for read-only untrusted files) with a disk guard. Runs stay
  serial (the `ReviewStore` single-accessor invariant); a mid-run gateway 404 (idle eviction) is classified
  retryable so the PR re-runs.

## 8. Cost gating

Diff-only stays the default; the tool + sub-agent path is opt-in via `EnableToolAssistedReview` (per
repo/run). `ReviewMaxTokens` is raised; the adaptive-thinking `effort` is re-verified against Copilot (a
multi-turn + sub-agent loop likely wants effort above `low`, unlike the single-pass reviewer).

## 9. Testing

- **Unit** (existing `FakeSandboxCommandRunner`/`FakeReviewAgentLoopFactory` + new fakes for the provisioner
  and discovery): read-only tool filter; create-request shape (github egress rule present,
  `marketplaces=gb-plugins`); `/discovered` `content`→template parsing keyed by `qualified_name`; submodule
  allow-list permits the store's submodules and denies others; the per-run runner uses the provisioned
  session; the confidentiality gate omits the sibling submodule for fork/public PRs; degrade-to-diff-only;
  host-dir cleanup.
- **Live (RED→GREEN, proof-rigor):** a real PR reviewed with the `code-reviewer` skill loaded AND a real
  `code-reviewer:*` sub-agent (real 27 KB body, not a stub) dispatched — verified from the
  request/response dumps + SQLite; a RED proof the agent cannot push; a RED proof a fork/public PR does not
  co-locate the sibling private submodule.

## 10. Implementation sequencing (staged, each independently verifiable)

1. **`LmAgentInfra` discovery fix** (§3): envelope + `content` + content-first loader. Unblocks real
   sub-agent bodies; also fixes LmStreaming's marketplace sub-agents.
2. **Session provisioning:** per-run sandbox command runner (replace the singleton) + `ReviewSessionProvisioner`
   + workspace-dir lifecycle + adopt gateway + marketplaces.
3. **Read-only tool registry + `Skill` + prompt** on the review loop; degrade-not-fail. (Delivers the
   skill-driven reviewer on its own.)
4. **Sub-agents:** build templates from `/discovered.content` + `SubAgentOptions` → the `Agent` tool
   dispatches real `code-reviewer:*`.
5. **Cross-repo checkout:** clone `AchieveAiReviews` + submodule allow-list + confidentiality gate (read-scoped
   sandbox) + per-PR review-branch retention moved to the daemon's host-side write phase.
6. **Security hardening:** verify no agent push; verify the fork/public confidentiality gate; host-dir
   cleanup + disk guard.

## 11. Non-goals / deferred

- Live mid-run context-discovery injection (webhook + pool) — pull is sufficient for an unattended daemon.
- Wiring `OperationPolicy` into `AuthWebhookController` (Option (i)) — avoided by the §6 Risk A
  read-scoped-credential split; reserved for a future ticket if in-sandbox agent writes ever become a
  requirement.
- Omit-`Bash` fallback for Risk A (agent gets `Read/Grep/Glob/Skill` only, no git in-sandbox) — avoids the
  second credential but disables build/test sub-agents; the read-scoped-credential approach is preferred, so
  this is a documented fallback, not the plan.
- Azure DevOps parity for the tool-assisted path (GitHub first).
- Cross-repo **change sets** (a coordinated PR in each repo reviewed together) — single-repo PRs first.
