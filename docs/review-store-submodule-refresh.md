# Review store: submodule gitlinks never advance

**Status:** open, not started
**Store affected:** `github.com/gautamb_microsoft/NOVA_reviews` (profile `appsettings.nova.json`)
**Observed:** 2026-08-07
**Component:** `samples/CodeReviewDaemon.Sample`

---

## 1. Problem

The five submodules under `repos/` in the review store are pinned to the commits they
had at store creation. They have not moved since. Reviews merge into `main` continuously,
but `repos/` stays frozen, so GitHub shows the same "last updated" date for every
submodule regardless of how much review activity has landed.

The expectation was that this refresh is automatic. It is not — see §2.

### Evidence

`repos/` has been touched by exactly one commit in the store's entire history:

```
dc5cc82  2026-08-06 12:55:16 -0700  Initialize space-efficient review store
```

Top-level path churn across all 89 commits on `main`:

| Path | Commits touching it |
|---|---|
| `PRs` | 176 |
| `KnowledgeBase` | 50 |
| **`repos`** | **5** (all five gitlinks, all in `dc5cc82`) |
| `README.md` | 1 |
| `.gitmodules` | 1 |

Current pins on `main`:

```
160000 commit 5e22900b50ed12506c97decec43040ff01970c21  repos/Astra
160000 commit be06616f1e8790f4f2f91542a55c9d7b7f30a3bc  repos/MODISService
160000 commit a18d048094df8b02f6e1d1777cef0bd59dc84a54  repos/Nova
160000 commit 6ce2dfefaed01e4a978821d92d2c4b53658b472e  repos/NovaClient
160000 commit 99ba4faf73a2c246a2f233cbb0f64103db6b365d  repos/WeveNova
```

`git submodule status` in the sweeper's store checkout — the leading `-` means
*registered but never initialized*:

```
-5e22900b50ed12506c97decec43040ff01970c21 repos/Astra
-be06616f1e8790f4f2f91542a55c9d7b7f30a3bc repos/MODISService
-a18d048094df8b02f6e1d1777cef0bd59dc84a54 repos/Nova
-6ce2dfefaed01e4a978821d92d2c4b53658b472e repos/NovaClient
-99ba4faf73a2c246a2f233cbb0f64103db6b365d repos/WeveNova
```

---

## 2. Root cause

**No code path in the daemon advances a superproject gitlink.** The feature does not
exist — this is not a broken implementation.

Git never auto-advances a gitlink on its own. Something must explicitly run
`git add repos/<X>` against a submodule worktree whose HEAD has moved, and commit the
result in the superproject. Nothing in the daemon does this.

Two supporting facts make it structurally impossible today, even by accident:

1. **Review branches only write notes.** `ReviewBranchManager.CommitNotesAsync`
   (`Workspace/Git/ReviewBranchManager.cs:98`) stages either explicit note paths
   (`add -- <path>`, line 144) or `add -A` (line 157). Sample diff of
   `review/nova-5505117` against `main`: 9 files, all under `PRs/nova-5505117/`.

2. **The store's submodules are never initialized**, so the `add -A` at line 157 has no
   submodule worktree to observe a moved HEAD in. It cannot produce a gitlink change.

---

## 3. Why reviews still work correctly

The reviewed source is **not read through the gitlink**. Live slot layout:

```
<workspace>/review-…-nova-…/slot-0/
  ├── notes     → worktree on branch review/nova-<pr>
  ├── repo      → detached worktree at the PR head SHA
  └── scratch
```

Measured 2026-08-07:

| | commit | date |
|---|---|---|
| `slot-0/repo` HEAD (what is reviewed) | `023db74222c` | 2026-08-07 11:23 |
| `repos/Nova` gitlink (what is pinned) | `a18d048094d` | 2026-08-06 12:55 |

`ReviewSlotPreparer.EnsureSharedSubmodulesAsync` (`Workspace/ReviewSlotPreparer.cs:342`)
initializes a **shared** submodule object database per repo; each slot then adds a
worktree checked out at the PR head, fetched fresh per review.

**The gitlink is a bootstrap seed for that object database, not a tracking pointer.**
It determines how much history the first `submodule update --init` pulls — and with
`shallow = true` set on all five entries, that seed is shallow regardless.

So: correctness is unaffected. Do not treat this as a review-accuracy bug.

---

## 4. What the drift actually costs

- **Fetch cost creeps.** Every review fetches the delta from the pin to the PR head. The
  pin is static, so that delta grows every day. Over weeks this becomes the dominant
  cost of slot preparation.
- **Store is misleading to a human reader.** `repos/` on GitHub advertises Aug 6 as the
  state of the world. Anyone opening the store to see "what code was this reviewed
  against" gets a stale answer.
- **Shallow re-clone gets worse.** If a slot is ever re-cloned (`SlotHygiene` reclone
  path), it re-seeds from the same stale pin and pays the full accumulated delta.

---

## 5. Blocking input needed

**Which branch should each submodule track?**

`.gitmodules` declares no `branch =` key for any of the five entries:

```
[submodule "repos/Nova"]
	path = repos/Nova
	url = https://dev.azure.com/O365Exchange/Weve_DA/_git/Nova
	shallow = true
```

Without that, there is no declared "dev branch" to advance to. This must be decided
before implementation. Options:

- **(a)** Add `branch = <name>` per entry in `.gitmodules`, then use
  `git submodule update --remote`, which reads that key. Most idiomatic; makes the
  intent visible in the store itself.
- **(b)** Add a `SubmoduleTrackingBranches` map to `CodeReviewDaemonOptions` keyed by
  repo name. Keeps the store dumb, but the config and the store can silently disagree.
- **(c)** Resolve each submodule's remote default branch at sweep time
  (`git remote show origin` / `ls-remote --symref origin HEAD`). Zero config, but "the
  default branch" is not necessarily the branch these teams develop on.

**Recommendation: (a).** It is the only option where the store is self-describing, and it
is what `--remote` already expects. Note the five repos may not share a branch name —
confirm per repo, do not assume `main`.

---

## 6. Proposed implementation

### Where

**`PrLifecycleSweeper`** (`Orchestration/PrLifecycleSweeper.cs:84`), invoked from
`Program.cs:820`, running every `MaintenanceSweepIntervalSeconds` (default 900 —
`Configuration/CodeReviewDaemonOptions.cs:446`).

It is the right home because it already:

- holds a store checkout on `main` via `ReviewBotCheckout.EnsureCheckoutAsync`
  (`Workspace/ReviewBot/ReviewBotCheckout.cs:14`)
- runs on a timer, decoupled from any individual review
- commits and pushes `main` (`ReviewBranchManager.MergeToDefaultAsync`, line 244)
- has a `GitRunner` carrying ADO credentials (`Program.cs:657`, `hostGit`) — required,
  since the submodule URLs are ADO while the store itself is GitHub

### Do NOT put it on the review notes branch

85 concurrent `review/*` branches each bumping the same five gitlinks would conflict on
essentially every merge. One commit per sweep, on `main`, is the correct granularity.

### Sketch

Add a step to `SweepAsync` (`PrLifecycleSweeper.cs:159`), after branch resolution and
before the final push:

1. `git submodule update --init --remote --depth 1 -- repos/<X>` for each entry
   (`--remote` reads the `branch =` key from §5(a)).
2. `git add -- repos/<X>` for each entry whose gitlink moved.
3. If anything is staged, one commit: `chore(store): advance submodule pins` — list the
   moved submodules and short SHAs in the body.
4. Push `main`.

### Ordering constraint

Run this **after** notes-branch merges in the same sweep, not before. A gitlink commit
landing between a `checkout main` and a `merge --ff-only` turns a fast-forward into a
merge commit unnecessarily.

---

## 7. Edge cases to handle

| Case | Required behaviour |
|---|---|
| Submodule fetch fails (auth expiry, ADO outage) | Log warning, skip **that** submodule, continue the sweep. Never abort the whole sweep — notes merging must not depend on pin refresh. |
| No submodule moved | Emit **no** commit. Do not create empty commits every 15 minutes. |
| Push races another daemon instance | Reuse the existing retry/idempotency posture in `MergeToDefaultAsync`; a rejected push should retry next sweep, not wedge. |
| Store checkout has a dirty index | `MergeToDefaultAsync` already does `reset --hard` on entry (line ~290). The new step must run *after* that guard, not before. |
| `shallow = true` + `--depth 1` | Confirm the shallow submodule can fast-forward to the new tip. If `--depth 1` re-fetch orphans the old pin, the object DB seeding assumption in §3 changes — verify before shipping. |
| A submodule's tracking branch is deleted upstream | Log warning once (follow the `warnedOrphanBranches` pattern at `Program.cs:~818` — warn once, not every sweep), skip the entry. |

---

## 8. Acceptance criteria

1. After one sweep against a store whose submodule upstreams have moved, `main` carries
   exactly one new commit touching `repos/`, and the gitlinks match the tracked branch
   tips.
2. A second sweep with no upstream movement produces **zero** new commits.
3. A submodule whose fetch fails does not prevent the other four from advancing, and
   does not prevent notes-branch merges in the same sweep.
4. `git log --oneline -- repos/` on the store shows a growing history rather than a
   single init commit.
5. Slot preparation fetch time for a new review does not regress.

## 9. Test plan

- Unit: seam-level test over `PrLifecycleSweeper` with a fake `GitRunner` asserting
  no-op when no gitlink moves, one commit when ≥1 moves, and per-submodule failure
  isolation.
- Integration: mirror the existing `CrossRepoCheckoutTests` style — that suite already
  pins the URL-spelling equivalences (`SubmoduleTargetsRepo_pairs_every_url_spelling_of_the_same_repo`)
  and is the natural place for a pin-advance case.
- Manual: run the nova profile against the live store, confirm one `chore(store)` commit
  appears and `repos/` timestamps update on GitHub.

---

## 10. Out of scope

- Comment posting (`EnableCommentPosting` is deliberately `false` on this profile).
- The 85 unmerged `review/*` branches — those are open PRs, left untouched by design
  (`PrLifecycleSweeper.cs:71-72`). Not a defect.
- `review_run.pr_lifecycle_state` staleness. The sweeper resolves lifecycle live from the
  provider and never writes that column back, so it holds the review-time snapshot
  (all rows read `Open`, including the 36 whose notes already merged). Worth a separate
  ticket if it ever gets used as a decision input; harmless today.
