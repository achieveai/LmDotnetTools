# Design: Daemon Pooled Review Workspace (Layer 1)

- **Date:** 2026-07-05
- **Status:** Approved in brainstorming — pending spec review
- **Scope:** Layer 1 of the Code-Review Daemon workspace/memory redesign.
- **Builds on:** the merged Code-Review Daemon (PR #152) and the `achieveai/AchieveAiReviews` cross-repo store (target repos as submodules under `repos/<Repo>`, plus `Contracts/`, `KnowledgeBase/`, `PRs/`).
- **Later layers (own specs, out of scope here):** Layer 2 — knowledge-base extraction; Layer 3 — judge improvement / prompt-evolution loop.

## 1. Motivation

The current daemon reviews each PR against an **ephemeral, per-run** checkout and treats the agent as **strictly read-only**:

- **Cloning every run is expensive.** `DaemonReviewStageExecutor.FetchContextAsync` clones `AchieveAiReviews` (or the target) into a fresh sandbox per run. Git clone + submodule init dominate wall-clock and cost.
- **The reviewer can't take notes.** The agent has no `Write`/`Bash` (`ReadOnlyToolFilter` keeps only `Read/Grep/Glob/Skill`). It cannot record PR-specific findings, cannot do file-level `git diff`, and has no memory to carry into a re-review.
- **The review branch is ephemeral.** `ReviewBotRepoManager.PublishAsync` creates `review/<...>/<pr>`, commits, **immediately fast-forwards it into `main`, and deletes it** — so per-PR artifacts never persist as a working surface across re-reviews.

Layer 1 replaces this with a **warm pool of host-side checkouts**, a **scoped-writable reviewer** that takes PR-specific notes, and a **persistent per-PR branch** that carries memory across re-reviews and is merged-or-deleted when the PR closes.

## 2. Goals / Non-goals

**Goals**
- G1. Avoid re-cloning: a warm pool of `AchieveAiReviews` checkouts, leased serially.
- G2. Let the reviewer **write PR-specific notes** and use **Bash** (for file-level diffs), while a compromised/injected agent stays boxed in (scoped write + no egress + commit gate).
- G3. Persist per-PR notes on a **long-lived PR branch** so a re-review reads its own prior notes first (PR-specific memory).
- G4. React to PR lifecycle: **merge** the PR branch into store `main` on PR close (merged); **delete** it on abandon (closed unmerged).

**Non-goals (Layer 1)**
- Knowledge-base extraction at completion (Layer 2).
- Judge "what did we miss" notes + prompt-evolution loop (Layer 3).
- Concurrent/parallel PR reviews (kept serial; the pool exists for warm reuse, not parallelism — but lease/return is designed so parallelism can be enabled later without redesign).

## 3. Approved decisions

- **D1 — Security posture: scoped write + Bash.** Reviewed code (`repos/<Repo>`), `Contracts/`, `KnowledgeBase/` are **read-only** to the agent; only the PR notes dir + an ephemeral scratchpad are writable; Bash has **no egress and cannot push**; the write credential never enters the agent session; the daemon commits **only** the PR notes dir (the commit gate). See §6.
- **D2 — Pool: warm reuse, serial.** A small, configurable number `N` of warm checkouts; reviews run one at a time. Lease → switch branch → review → commit → return.
- **D3 — Notes layout: one dir per PR, accumulating.** `PRs/<provider>/<slug>/<pr>/` persists on the PR branch across re-reviews; each re-review reads its prior notes first, reviews the new head, and appends/updates; per-commit output lives as sections/files inside the one PR dir.
- **D4 — Checkout + git move host-side.** All deterministic git (clone/fetch/checkout/submodule/commit/push/merge) runs in the daemon process (privileged, with the write credential). The sandbox only mounts a prepared slot for the read+notes phase.

## 4. Architecture

Four phases per review, plus lifecycle handling. Two execution identities throughout (§5).

### 4.1 The slot pool (`ReviewSlotPool`, host-side)

- The daemon manages `N` **slots** under a host root, e.g. `…/review-pool/slot-{i}/`. Each slot dir is **mounted at `/workspace`** and contains two children: `store/` — a full `AchieveAiReviews` clone (created lazily on first use), and `scratch/` — the ephemeral scratchpad (§4.6). So the store git tree is `/workspace/store` and scratch is `/workspace/scratch`, a sibling *outside* the git tree. `N` is a config knob (`CodeReviewDaemonOptions.ReviewPoolSize`, default 2).
- A slot is a value: `(int Index, string HostPath, string? LeasedToRunId)`. The pool is a free-list guarded by a simple async gate; `Lease(run)` returns a free slot (creating/cloning one if none exist and `N` not reached, else awaiting a return); `Return(slot)` marks it free after cleanup.
- Because reviews are serial (D2), at most one slot is leased at a time in Layer 1; the free-list/gate exists so parallelism is a later flag flip, not a redesign.

### 4.2 Lease + prepare (privileged prep phase)

On lease, the daemon prepares the slot **host-side**, with the write credential:
1. `git fetch origin` (store remote).
2. Resolve the PR branch `review/<provider>/<slug>/<pr>`: check it out if it exists on the remote (carries prior notes), else create it from store `main`.
3. Advance the target submodule to the PR head: `submodule update --init repos/<Repo>` (allow-list scoped, as today), then fetch base+head and `checkout --force <head>` inside `repos/<Repo>`.
4. Wipe the ephemeral scratchpad (`/workspace/scratch` inside the slot mount — see §4.6).
5. Set permissions so the sandbox user sees **code read-only, `PRs/<pr>/` + scratch read-write** (§5, enforcement TBD by §9-R1).

### 4.3 Agent phase (scoped, in-sandbox)

- The daemon opens a gateway session **bind-mounting the leased slot** and hands the agent the scoped tool surface: `Read/Grep/Glob/Skill` + `Write/Edit/Bash`, path-scoped per §6.
- The review prompt instructs the agent to: **read its prior notes** for this PR first (`PRs/<...>/<pr>/`) as memory; ground findings in the code via `Read`/subdir `Grep`/`Glob` and `Bash` file-level `git diff/log/blame`; use `/workspace/scratch` freely as throwaway; write the review + any durable per-PR notes into `PRs/<...>/<pr>/`. All PR/file content remains untrusted (injection framing unchanged).

### 4.4 Commit phase (host-side, the gate)

Back on the host, the daemon:
1. Stages **only** `PRs/<provider>/<slug>/<pr>/…` (never the code submodule pointer, never scratch, never anything the agent wrote elsewhere).
2. Commits onto the PR branch and pushes it, so the notes persist for the next re-review. The branch is **not** merged to `main` here.
3. Returns the slot to the pool (scratch wiped; branch left on the remote).

Collect-only default is preserved for *posting to the PR* (unchanged `ReviewPoster`/`EnableCommentPosting`); the notes-branch push is separate host-side retention and gated on a configured store URL exactly as today.

### 4.5 PR-lifecycle transitions (poller responsibility)

The poller already tracks reviewed PRs (`ReviewStore`). It gains a **lifecycle sweep** over PRs it has a branch for, acting on GitHub state:
- **open + new head** → re-review (lease → the branch already carries prior notes → memory carries forward).
- **closed + merged** → host-side: **merge the PR branch into store `main`** (fast-forward if possible, else a merge commit) and push, then delete the branch. *(This is the trigger point Layers 2/3 will extend — knowledge extraction, judge notes — but Layer 1 only does the merge + branch delete.)*
- **closed + not merged (abandoned)** → **delete the PR branch** (local + remote); no merge.

State comes from the provider (`GitHubPrProvider` — extended to read `state` / `merged_at`; today it only fetches open PRs). Transitions are idempotent (re-running the sweep is safe: already-merged/deleted branches are no-ops).

### 4.6 Scratchpad

- A single ephemeral directory the agent may write to freely, **never committed**: `/workspace/scratch` — a sibling of the store clone `/workspace/store` (§4.1), so it is structurally outside the git tree and cannot be staged. Wiped on every lease-prepare (§4.2 step 4) so no bleed between PRs.

### 4.7 Two execution identities

- **Daemon git (privileged):** clone/fetch/checkout/submodule/commit/push/merge run in the daemon process, host-side, directly on the slot's host path with the write credential (the existing `HostGitCommandRunner`). This is the only identity that can mutate the branch or the store remote. The only step that may need a container-side privileged escalation (`docker exec -u root <slot-container>`) is applying the RO/RW permissions in §4.2 step 5 — and only if §9-R1 lands on the `chmod` path rather than the RO-mount path.
- **Agent tools (restricted):** run as the `sandbox` user via the gateway MCP, over the scoped surface, seeing code RO / notes+scratch RW, no egress, no credential.

## 5. Security model (defense-in-depth)

1. **Filesystem permissions (primary):** code + `Contracts/` + `KnowledgeBase/` RO to `sandbox`; `PRs/<pr>/` + `/workspace/scratch` RW. Kernel-enforced against Write/Edit/Bash alike.
2. **Egress policy:** the agent's Bash gets no egress (or a tight allow-list) — can run local git read commands, cannot push/`curl`/exfiltrate.
3. **No write credential in-session:** the GitHub write token exists only in the daemon's host-side phase.
4. **Commit gate:** the daemon commits only `PRs/<pr>/…`; scratch is ephemeral; the submodule pointer moves only when the daemon advances it — so nothing the agent did outside the notes dir can reach git.
5. **(Optional) `Write`/`Edit` path wrapper:** a `ScopedToolFilter` that rejects any `file_path` outside the notes/scratch roots before the call reaches the gateway — belt-and-suspenders if OS perms don't enforce (§9-R1).

## 6. Components (units + interfaces)

New / changed, each with one clear purpose:

- **`ReviewSlotPool` (new).** Owns the `N` host-side slots + free-list. `Task<ReviewSlot> LeaseAsync(ReviewRun, CancellationToken)`, `Task ReturnAsync(ReviewSlot, CancellationToken)`. Depends on host git + filesystem. Replaces `ReviewSessionProvisioner`'s per-run host-dir role; the gateway-session part is reused (a session is opened over the leased slot).
- **`ReviewSlotPreparer` (new).** The privileged prep phase (§4.2): fetch, branch resolve, submodule advance, scratch wipe, permission set. Pure host-git/fs orchestration → unit-testable against a fake runner.
- **`ScopedToolFilter` (new, replaces/extends `ReadOnlyToolFilter`).** Builds the agent's registry: keeps `Read/Grep/Glob/Skill`, adds `Write/Edit/Bash`, and (optional layer 5) wraps `Write/Edit` with a path allow-list.
- **`PrLifecycleSweeper` (new).** The §4.5 sweep: reads reviewed-PR state from the provider + `ReviewStore`, and drives merge-on-close / delete-on-abandon via the host git. Terminal transitions are idempotent.
- **`ReviewBranchManager` (refactor of `ReviewBotRepoManager`).** Split the current "commit + immediately merge + delete" into two operations: `CommitNotesAsync(branch, notesFiles)` (persist notes on the PR branch, push, keep the branch) used per-review, and `MergeToDefaultAsync(branch)` / `DeleteBranchAsync(branch)` used by the sweeper. The one-commit-per-review retention semantics and rebase-retry push are retained.
- **`DaemonReviewStageExecutor` (changed).** `FetchContextAsync`: checkout/diff/manifest move into the prep phase against the leased slot (host-side) rather than an in-sandbox clone. `ReviewAsync`: the tool context uses `ScopedToolFilter`; the review input tells the agent where the notes dir + scratch are. Add a commit-notes step. The terminal-stage session teardown becomes slot return.
- **`CodeReviewDaemonOptions` (added):** `ReviewPoolSize` (default 2), `ReviewPoolHostRoot`, scratch path knob; scoped-tool allow-list additions (`Write`,`Edit`,`Bash`) gated behind the same tool-assisted flag.
- **`GitHubPrProvider` (extended):** expose PR `state` + `merged_at` so the sweeper can classify open / merged / abandoned.

## 7. Data flow (end-to-end, one PR)

1. Poller discovers PR (open) → `ReviewRun`.
2. `ReviewSlotPool.LeaseAsync` → a warm slot.
3. `ReviewSlotPreparer`: fetch, checkout `review/<...>/<pr>` (new-from-main or existing-with-notes), advance submodule to head, wipe scratch, set RO/RW perms.
4. Daemon computes diff + `git ls-files` manifest (host-side) → context artifact.
5. Gateway session over the slot; agent (scoped) reads prior notes + code, does file-level diffs via Bash, writes review + notes into `PRs/<...>/<pr>/`, scratch as needed.
6. Daemon commits **only** `PRs/<...>/<pr>/…` onto the PR branch, pushes it.
7. Slot returned to the pool.
8. Later, poller's lifecycle sweep sees the PR merged → merge its branch into store `main`, push, delete branch; or abandoned → delete branch.

## 8. Error handling & degradation

- **No free slot / pool disabled / disk guard:** degrade to a single-lease-or-fallback path (never fail the stage); the existing disk guard applies to the pool root.
- **Prep failure (fetch/checkout/submodule):** surface (throw) so the stage retries; no partial artifact persisted.
- **Commit/push failure:** keep the branch, record non-terminal (mirrors today's `GitSyncFailed` + reconcile), so notes are not lost and the next sweep/retry re-pushes.
- **Merge-on-close conflict** (store `main` advanced): rebase-retry (as today) then, if still conflicting, leave the branch and log for manual/reconcile — never force-push `main`.
- **Agent misbehavior:** bounded by §5; a hijacked agent cannot push, exfiltrate, or land anything outside the notes dir.

## 9. Open items / research (first steps of the plan)

- **R1 — RO enforcement mechanism.** Verify whether the gateway supports a **per-path read-only bind mount** (kernel-enforced) for `repos/<Repo>` while `PRs/<pr>/`+scratch stay RW. If yes, that is the primary boundary. If no (Docker-Desktop/NTFS `chmod` may not enforce), fall back to egress-block + no-credential + commit-gate + the `ScopedToolFilter` `Write` wrapper. This decides §5.1 vs §5.5 as primary. **Resolve before building §4.2 step 5.**
- **R2 — Slot ↔ gateway-session mapping.** Confirm the cleanest way to mount an already-populated host slot into a gateway session (the store review already proves a populated host dir mounts fine; confirm it for the pool root path shape).
- **R3 — Bash egress default.** Confirm the gateway can present the agent's Bash with egress denied while the daemon's separate host-side git retains push access.

## 10. Testing strategy

- **Unit (no gateway):** `ReviewSlotPool` (lease/return/free-list, cap, degrade), `ReviewSlotPreparer` (git command sequence: fetch → branch resolve new-vs-existing → submodule advance → scratch wipe → perms) against a fake runner, `ScopedToolFilter` (kept/added/wrapped tools + path allow-list), `ReviewBranchManager` split ops (commit-keeps-branch; merge; delete), `PrLifecycleSweeper` (open→re-review, merged→merge+delete, abandoned→delete; idempotency), executor wiring.
- **Integration/live (opt-in):** one live pass — lease a warm slot, scoped agent reads prior notes + writes new notes, daemon commits to the PR branch; then simulate close → branch merged to `main`; abandon → branch deleted. Assert the security bounds (agent write outside notes denied; no push from agent).
- **Follow the existing daemon test patterns** (`FakeSandboxCommandRunner`, `FakeSandboxFileSystem`, `DaemonReviewStageExecutorTests`, `CrossRepoCheckoutTests`).

## 11. Out of scope (future layers)

- **Layer 2 — Knowledge base:** at PR-close (the §4.5 merged transition), extract durable knowledge into queryable markdown + JSONL under `KnowledgeBase/`; not every PR contributes; readable by future reviews.
- **Layer 3 — Judge improvement loop:** at completion, the judge records "what did we miss" (marking important KB points) and accumulates its own learnings that periodically evolve the review prompt.

Both hang off the same PR-close trigger Layer 1 establishes.
