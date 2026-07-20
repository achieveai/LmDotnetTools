# ContextReady Slot Durability — Design

- **Date:** 2026-07-12
- **Status:** Approved (brainstorming). Ready for implementation planning.
- **Branch:** `feat/contextready-slot-durability` (off `ado-subagent-parity`)
- **Component:** `samples/CodeReviewDaemon.Sample`

---

## 1. Problem & motivation

On 2026-07-12 the achieveai daemon failed PR #186's review **~200 times over an hour** — a single loop. Root cause: a crashed git process at 09:03 left a 0-byte `index.lock` inside the **persistent** pooled review slot (`review-pool/slot-0/store/.git/modules/LmDotnetTools/index.lock`). Because the slot's store is persistent and reused verbatim, the lock never cleared, so every ~30 s re-poll re-ran `ContextReady`, re-hit `checkout --force <head>` → `exit 128: Unable to create '…/index.lock': File exists` → RetryPending → repeat.

A full map of the stage (`Orchestration/PrOrchestrator.cs`, `Orchestration/DaemonReviewStageExecutor.cs`, `Workspace/ReviewSlotPreparer.cs`, `Workspace/ReviewSlot.cs`) shows the incident is one instance of a broader class of fragility:

- The pooled slot is a **persistent, writable** store, **reused verbatim** each lease — only an empty/missing store triggers a re-clone (`ReviewSlot.cs` `LeaseAsync` ~92–100). No health check, no clean, no reset.
- `ReviewSlotPreparer.PrepareAsync` assumes a clean slot: step 3 `checkout -B <branch>` runs **without `--force`**, there is **no `git clean` anywhere**, and there is **no post-init submodule verification** (unlike the executor's store-checkout path). So a prior run's dirty tracked files, agent-created untracked files, half-inited submodule, or stale lock all break or contaminate the next lease — and because leases aren't pinned to a run, **a different PR can inherit the mess**.
- No stale-lock removal anywhere in the codebase.
- **No retry governance**: no attempt counter, no backoff, no cap, no terminal/park state. RetryPending re-runs every ~30 s while the PR stays open.
- A second silent-stall: a RetryPending run whose PR drops off the provider's open-PR page (recency/pagination) is **never** retried.

## 2. Goals / non-goals

**Goals** (scope: full fault-tolerance):
1. **Every lease begins on a pristine store.** A crash, kill, or cancellation of a prior lease can never wedge or contaminate the next one.
2. **Each review is transactional:** on success, commit the intended review notes, then strip everything else so the returned slot is pristine.
3. **Bounded retries + backoff + park.** No hot-loop; a genuinely stuck commit is parked and alerted, not retried forever.
4. **Endpoint = park + alert** (not degrade-to-diff-only). The bad *slot* is quarantined/re-cloned regardless.
5. **Commit scope = review notes / KnowledgeBase only.** The reviewed submodule working tree is read-only *context*; any agent churn there is byproduct and is cleaned.

**Non-goals:**
- **Approach B** (disposable `git worktree` per lease over a persistent object store) — structurally cleaner, but a bigger change with known git worktree+submodule sharp edges. Documented as a future evolution.
- Committing the review agent's edits to the reviewed repo (that would be a code-fixing bot, not a reviewer).
- **RetryPending scanner** (drive RetryPending runs straight from the store, independent of the provider re-listing the PR) — **deferred to a follow-up** (see §9).

## 3. Core principle

**Clean-on-entry is the durability guarantee** — it runs at the start of every prepare and therefore survives any way a prior lease ended (success, crash, kill, cancellation). **Clean-on-exit is best-effort tidiness** — correctness never depends on it.

This is safe because of the **exclusive-lease invariant**: the pool leases a slot to at most one run at a time (`SemaphoreSlim`), so a *leased* slot has **no concurrent git process** — any lock file in it is stale by definition and safe to remove.

## 4. Transactional slot lifecycle (Approach A)

### 4.1 Clean-on-entry — `SlotHygiene.EnsureCleanAsync(slot) → {Clean | NeedsReclone}`
Runs first in `ReviewSlotPreparer.PrepareAsync`, before the fetch:
1. **Clear stale locks** — delete `*.lock` under `store/.git/` and every `store/.git/modules/**/` (`index.lock`, `shallow.lock`, `HEAD.lock`, `config.lock`, `packed-refs.lock`, `ORIG_HEAD.lock`, …).
2. **Abort in-progress ops** — if `MERGE_HEAD` / `rebase-merge` / `rebase-apply` / `CHERRY_PICK_HEAD` exist, abort/remove them.
3. **Reset + clean, superproject then submodules** — `git reset --hard`, `git clean -ffdx`, then `git submodule foreach --recursive "git reset --hard && git clean -ffdx"` (locks in submodule git dirs are cleared in step 1 first).
4. **Health probe** — `git rev-parse --git-dir` (+ optional `git fsck --connectivity-only`). A structurally broken store returns **`NeedsReclone`**; otherwise **`Clean`**.

### 4.2 Prepare-sequence fixes (now safe because the tree is guaranteed clean)
- Step 3 `checkout -B` no longer throws on a dirty tree (the tree was reset/cleaned).
- Add **post-init submodule verification**: after the allow-listed submodule init, assert `outcome.InitializedPaths.Contains(submoduleRelPath)`; if absent, throw a classified-corrupt error instead of proceeding to a fetch that fails opaquely.
- `RunGitOrThrowAsync` **classifies** failures (see §5) rather than throwing a single opaque `InvalidOperationException`.

### 4.3 Clean-on-exit — `SlotHygiene.StripAsync(...)` (success path only)
Folded into the existing notes commit+push at close: commit the PR's notes under the notes dir to the persistent `review/…` branch, push, **then** `reset --hard` + `clean -ffdx` (superproject + submodules) so the returned slot is pristine.

### 4.4 Recovery ladder (escalation)
1. `EnsureCleanAsync` → `NeedsReclone` → re-clone the slot store, else proceed.
2. A git step still fails **classified corrupt** → re-clone the store in place (reuse the pool's existing partial-store wipe+clone), retry prepare **once**.
3. Still failing → throw; retry governance (§5) takes over.

Warm-reuse stays the normal path; re-clone is the on-corruption escape hatch (Approach C on demand).

## 5. Retry governance (in-memory)

Because **a restart must re-drive everything** (operator intent: "restart = retry"), governance state is **in-memory** — no schema migration, no persisted terminal state.

- **`RetryGovernor`** — a singleton holding `ConcurrentDictionary<Guid runId, RetryState { attempts, nextEligibleAtUtc, parked }>`.
  - `ShouldAttempt(runId)` → `false` while backing off or parked.
  - `RecordFailure(runId, classification)` → increments `attempts`, sets `nextEligibleAtUtc = now + backoff` (exponential w/ jitter: ~30 s → 1 m → 2 m → 4 m → 8 m, capped at `RetryBackoffCapSeconds`); after `MaxContextRetries` attempts → `parked = true` + a greppable **Error** alert `review_run PARKED …`. Returns `{Retry | Parked}`.
  - `RecordSuccess(runId)` → clears the entry.
- **`PrOrchestrator.RunAsync`** consults `ShouldAttempt` at entry (skip, leave RetryPending, if not eligible); calls `RecordFailure` on a stage failure (park → alert, no infinite rethrow); calls `RecordSuccess` on success.
- **Corruption classification** — `GitFailureClassifier.Classify(stderr, exitCode) → {Transient | Corrupt | Unknown}` (extend the existing `CloneFailureClassifier`). Transient (DNS/timeout/5xx/auth) → retry, no re-clone. Corrupt (`index.lock` / `unable to create` / `not a git repository` / `object file is empty` / dirty-tree) → drives the re-clone escalation. Unknown → transient + logged.

### Resume semantics
- **New commit (automatic, primary):** run identity is the commit tuple, so a new push → new `head_sha` → a fresh run row at `Discovered`, `attempts=0`. Park is therefore **per-commit**; the stale parked run for the old head is simply superseded. No operator action.
- **Restart (retry all):** the in-memory map is empty on boot → every previously backing-off/parked run is eligible again and retried fresh. Safe now because clean-on-entry self-heals the stuck cause on the first retry; still-broken runs re-park within the new lifetime after bounded backoff.

## 6. File-level change map

**New:**
- `Workspace/SlotHygiene.cs` — `EnsureCleanAsync`, `StripAsync`, `HygieneVerdict`.
- `Orchestration/RetryGovernor.cs` — in-memory attempts/backoff/park.
- `Workspace/Git/GitFailureClassifier.cs` — stderr/exit → `{Transient|Corrupt|Unknown}` (extend `CloneFailureClassifier`).

**Modified:**
- `Workspace/ReviewSlotPreparer.cs` — call `EnsureCleanAsync` first; `NeedsReclone` signal; post-init submodule verification; classify in `RunGitOrThrowAsync`.
- `Workspace/ReviewSlot.cs` (pool) — expose `RecloneStoreAsync(slot)` (reuse the partial-store wipe+clone).
- `Orchestration/DaemonReviewStageExecutor.cs` — recovery ladder around prepare in `TryPooledFetchContextAsync`; `StripAsync` after the success-path notes commit+push.
- `Orchestration/PrOrchestrator.cs` — gate on `RetryGovernor`, record failure/success, park alert.
- `Configuration/CodeReviewDaemonOptions.cs` — `MaxContextRetries` (5), `RetryBackoffBaseSeconds` (30), `RetryBackoffCapSeconds` (900).
- `Program.cs` — register `RetryGovernor` singleton.

## 7. Testing strategy

- **`SlotHygieneTests`** — the RED→GREEN repro of the incident: seed a store with a stale `index.lock` (+ a nested `.git/modules/**` lock), a dirty tracked file, an untracked file, a `MERGE_HEAD` → assert `EnsureCleanAsync` clears all → `Clean`; a broken `.git` → `NeedsReclone`.
- **`ReviewSlotPreparerTests`** (extend) — prepare over a dirty/locked warm slot now *succeeds*; a half-init submodule throws.
- **`RetryGovernorTests`** — attempts/backoff gating, park-after-K, success clears, transient-vs-corrupt routing.
- **`DaemonReviewStageExecutorPooledTests`** (extend) — recovery ladder (corrupt → re-clone → success; and → park after K); **cross-PR cleanliness** (lease A leaves an untracked file → return → lease B on the same slot starts clean).
- **Constraint:** `GitRunner` blocks `file://`, so real-git tests use a temp `git init` repo with the hardened runner (or a fake runner asserting issued commands); the lock-clearing (pure filesystem) path tests without git.

## 8. Config knobs

| Knob | Default | Meaning |
|---|---|---|
| `MaxContextRetries` | 5 | attempts (incl. re-clone) before a run is parked |
| `RetryBackoffBaseSeconds` | 30 | first backoff; doubles each attempt |
| `RetryBackoffCapSeconds` | 900 | backoff ceiling |

## 9. Decisions & residual gaps

- **Deferred:** the RetryPending scanner (④). Residual gap: a PR that *permanently* drops off the provider's open-PR page (past recency/pagination) with **no new commit** stalls until a daemon restart. Acceptable for v1; tracked as a follow-up.
- **Park is visible in logs, not the DB** — an in-memory-parked run shows `RetryPending` in SQLite; operators find it via the greppable `PARKED` Error line / DuckDB.
- **A crash-looping daemon** re-tries `MaxContextRetries` per restart — bounded and self-limiting; a crash-looping daemon is a separate, louder problem.

## 10. Rollout

Implement on `feat/contextready-slot-durability`; unit + integration green; then live-verify against the running achieveai daemon by seeding a stale lock into a slot and confirming the next lease self-heals (the exact incident, reproduced and auto-cleared).
