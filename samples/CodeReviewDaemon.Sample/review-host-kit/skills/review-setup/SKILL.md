---
name: review-setup
description: >
  Prepare, reset, or merge an isolated NOVA PR review slot. Setup fetches the
  latest Azure DevOps merge ref and hard-resets one nested repo worktree to
  it -- destructive, no archive, no reuse. Use to prepare a slot (Git only, or
  Git plus read-only PR/work-item/blame context), check/warm the shared pool,
  reset a finished slot (archive or discard), or merge a completed local
  review into main. Distinguish "Git setup only" (`review_git_prepare.py`)
  from "setup plus context" (`review_setup.py`) when the caller only needs a
  clean checkout.
user-invocable: true
disable-model-invocation: false
allowed-tools: Read, Bash, TaskOutput
---

# Review Setup -- Disposable NOVA PR Slots

**Setup is destructive. Read this before running it.**

Every explicit setup permanently deletes the target slot's staged, unstaged,
untracked, and ignored content. There is no archive, no preservation
checkpoint, and no dirty-reuse shortcut. A rerun for the same PR still
fetches the latest merge ref, cleans the slot, and resets it -- it never
returns a cached/reused result.

## 0. Discard-reset the slot before every setup (mandatory)

Before calling `review_git_prepare.py` or `review_setup.py` against a slot,
always discard-reset that exact slot first:

```bash
python3 scripts/review_reset.py --slot <Repo>-<N> --mode discard --confirm YES
```

Example: `python3 scripts/review_reset.py --slot Nova-0 --mode discard --confirm YES`
before `python3 scripts/review_setup.py 5648587 --repo Nova --slot 0`.

This is destructive and scoped to the **selected slot only**. It intentionally
clears stale ownership, prepared-slot manifests, and leftover worktree state
left by any earlier run against that slot, before setup fetches or resets
anything itself. Run it every time, even when the slot looks idle -- never
skip it because a prior run "should have" cleaned up.

If the slot was never prepared before (or was already fully reset), this
exits non-zero with `No managed pooled run exists for that repository slot.`
-- that is the expected, benign outcome for an unused slot, not a failure to
diagnose. Proceed straight to setup (Section 1 or 2) in that case.

This step is unrelated to archiving a finished review: archive keeps its own
unchanged, PR-specific confirmation (`--confirm {repo-slug}-{pr-number}`,
Section 5) -- never `YES`.

## Decision table

| Goal | Command |
|---|---|
| Discard-reset a slot before setup (always, mandatory) | `review_reset.py --mode discard --confirm YES` |
| Clean Git checkout only, no ADO call needed after | `review_git_prepare.py` |
| Clean checkout **and** PR/work-item/blame context | `review_setup.py` (router) |
| Pool status, warm a slot, prime history, recover cache | `review_pool_admin.py` |
| Archive or discard a finished slot | `review_reset.py` |
| Merge a completed local review into local `main` | `merge_review.py` |
| Copy outside changes and commit every review artifact locally | `review_commit.py <PR> --repo <Repo> --slot <N>` |

## Topology

One outer `nova-reviews` meta-repo worktree per slot, at
`.worktrees/{Repo}-{slot}`. Its `repos/{Repo}` entry is a **nested** worktree
of that repo's own shared bare source store
(`review-setup/source-stores/{repo-key}.git`). Example:
`.worktrees/Nova-2/repos/Nova`.

- The outer meta-repo worktree **never moves**. Its HEAD stays at its
  configured baseline.
- Only the target's **nested repo** moves: setup detaches it at the fetched
  PR merge commit.
- Setup never blindly cleans the outer worktree -- that would corrupt other
  repos mounted under `repos/*`. Only the target's own nested worktree is
  hard-reset.

Six slots (`0`-`5`) per source repo: Nova, NovaClient, Astra, WeveNova,
MODISService. Canonical slot names combine repo and slot number, e.g.
`Nova-0`, `MODISService-3`.

## 1. `review_git_prepare.py` -- Git setup only

Run the mandatory discard-reset step (Section 0) against this slot first.

```bash
python3 scripts/review_git_prepare.py <PR> --repo <Repo> --slot <N>
```

Resolves the PR (read-only ADO call), fetches its merge ref into the shared
source store, then hard-resets the target nested worktree to that commit.
Requires `--repo` explicitly -- a PR number alone does not identify its
repository without a lookup.

What it discards in the target nested worktree only: staged changes,
unstaged changes, untracked files, ignored files, and any old local review
branch/ref for that slot. It always fetches and validates the new merge
commit **before** deleting anything -- if the fetch fails, the prior slot
state is untouched.

Verified before returning ready: outer meta-repo HEAD unchanged at baseline;
target nested worktree HEAD exactly equals the fetched merge SHA and is
detached; target status (including ignored files) is clean; no in-progress
Git operation (merge/rebase/cherry-pick/revert/bisect) anywhere in the slot.

```bash
python3 scripts/review_git_prepare.py 5648587 --repo Nova --slot 0
python3 scripts/review_git_prepare.py 5648587 --repo Nova --slot 0 --org <org>
python3 scripts/review_git_prepare.py 5648587 --repo Nova --slot 0 --lock-timeout 60
python3 scripts/review_git_prepare.py 5648587 --repo Nova --slot 0 --probe-git-exe
```

Flags: `--repo` (required), `--slot` (required, int), `--root` (canonical
store root, normally inferred), `--org` (only needed with more than one ADO
org configured), `--transport {auto,gateway,cli}`, `--lock-timeout` (seconds,
**default `0`** -- a same-slot collision fails fast instead of waiting),
`--probe-git-exe` (read-only: resolves the PR, then proves `git.exe` can
reach its merge ref via `ls-remote`; exits without calling the destructive
preparation step; never used by the real fetch).

Exit codes: `4` / `PR_NOT_ACTIVE` means ADO confirms the PR is completed or abandoned: skip it; no Git preparation was attempted. `0` ready and verified clean; `1` failed (invalid target, lock
conflict, fetch failure, cleanup failure, checkout failure, or verification
failure) -- nothing is left half-cleaned.

## 2. `review_setup.py` -- Resolve -> Git setup -> Context

Run the mandatory discard-reset step (Section 0) against this slot first.

```bash
python3 scripts/review_setup.py <PR> --repo <Repo> --slot <N>
```

Thin router, in this exact order:

1. **Resolve** -- ask the ADO provider for repo, merge ref/SHA, branches,
   iteration.
2. **Git setup** -- call `review_git_prepare.prepare()` with that resolved
   payload. Same destructive contract as Section 1.
3. **Context** -- call the provider's read-only context collection against
   the now-prepared worktree.

A Git-ready workspace survives a context failure or a moved PR: once step 2
succeeds, nothing in step 3 tears it down.

```bash
python3 scripts/review_setup.py 5648587 --repo Nova --slot 0
python3 scripts/review_setup.py 5648587 --repo Nova --slot 0 --org <org>
```

Flags: `--repo` (required), `--slot` (required, int), `--org`, `--root`,
`--transport {auto,gateway,cli}`.

### Exit codes (exact)

| Code | Meaning |
|---|---|
| `0` | Complete: Git workspace ready **and** context collected without warnings. |
| `1` | Workspace failed: PR could not be resolved, or Git setup failed. Nothing usable. |
| `2` | Workspace ready, context warning: **the prepared Git worktree stays valid and usable** -- only context collection warned or failed. |
| `3` | PR moved during context collection (iteration/merge commit changed underneath it). The slot stays ready at the fetched merge SHA; re-resolve and re-prepare for a coherent report. |
| `4` | `PR_NOT_ACTIVE`: ADO confirms the PR is completed or abandoned. Skip it. Rejected before Git preparation; the existing slot is unchanged. |

Exit `2` is not a setup failure. Do not discard the workspace on exit `2`.

Exit `4` is a terminal-PR skip, not a retryable fetch failure. Do not reset or retry the slot solely for this result. Missing merge refs on active PRs, unknown statuses, and authentication/network failures are not classified as completed; normal errors remain exit `1`. The status check is a point-in-time observation; a PR completing after resolution can still produce a normal fetch failure. Callers must explicitly handle code `4`; older daemon callers may report it as a generic failure.

## 3. `review_ado.py` -- ADO provider (resolve / context)

```bash
python3 scripts/review_ado.py resolve <PR> [--repo <Repo>] [--org <org>]
python3 scripts/review_ado.py context <PR> --repo <Repo> --slot <N> [--org <org>]
```

`resolve` is read-only: maps a PR number to repo, merge ref/SHA, source and
target branch, and iteration (an `int`, not a string). Nothing is fetched or
written.

`context` reads the worktree an earlier `review_git_prepare.py` (or
`review_setup.py`) run already prepared for that exact `--repo --slot`. It
**never prepares one itself** -- if no matching manifest exists, or the
manifest names a different PR, it refuses instead of guessing. It never
mutates the worktree.

`review_context.py` is an internal implementation module `review_ado.py`
imports for context/blame/Markdown rendering. It has no CLI of its own and
is not a public entry point -- callers use `review_ado.py context` or the
router, never `review_context.py` directly. It is a compatibility detail
pending later removal, not a documented command.

## 4. `review_pool_admin.py` -- shared source-store administration

Status/warm/prime/recover only. It never prepares, resets, or merges a
review slot.

```bash
python3 scripts/review_pool_admin.py status
python3 scripts/review_pool_admin.py warm --repo Nova --slot 0 [--dry-run] [--lock-timeout SECONDS]
python3 scripts/review_pool_admin.py warm-all [--dry-run] [--lock-timeout SECONDS]
python3 scripts/review_pool_admin.py prime-history --repo Nova [--dry-run] [--baseline-depth N] [--history-timeout SECONDS] [--lock-timeout SECONDS]
python3 scripts/review_pool_admin.py recover-cache --repo Nova [--dry-run] [--lock-timeout SECONDS]
```

| Subcommand | Purpose | Mutating? |
|---|---|---|
| `status` | Report every configured repo/slot state. | No |
| `warm --repo --slot` | Warm one canonical repository slot. | Only without `--dry-run` |
| `warm-all` | Warm all configured repo/slot rows sequentially (not 30 parallel downloads). | Only without `--dry-run` |
| `prime-history --repo` | Deepen the shared baseline history without moving any slot. | Only without `--dry-run` |
| `recover-cache --repo` | Verify/resolve an owned acquisition cleanup, no fetch, no slot move. | Only without `--dry-run` |

Retired and **not** ported here or anywhere else: `--preflight`,
`--inspect-reclaim`, `--reclaim-generation`, `--recover-slot`,
`--operator-assertion`. If a caller or an old note mentions these, they no
longer exist in any parser.

`warm`/`warm-all`/`prime-history`/`recover-cache` default `--lock-timeout` to
`1200` seconds (wait budget for a busy repository-scoped lock, separate from
fetch/history time).

## 5. `review_reset.py` -- archive or discard a finished slot

```bash
python3 scripts/review_reset.py --slot Nova-2 --dry-run
python3 scripts/review_reset.py --slot Nova-2
python3 scripts/review_reset.py --slot Nova-2 --mode discard --confirm YES
python3 scripts/review_reset.py --legacy-slot 2 --dry-run
```

Flags: `--slot NAME` (e.g. `Nova-2`) or `--legacy-slot N` (mutually
exclusive), `--mode {archive,discard}` (default `archive`), `--confirm`
(required for `--mode discard`: the generic affirmative `YES` -- `yes`/`y`
also accepted, case-insensitive; **not** a PR-specific token), `--dry-run`
(no mutation), `--lock-timeout` (default `1200`, pooled slots only), `--root`,
`--verbose`.

- **Archive (default):** commits only this PR's own artifact directory plus
  the pinned submodule pointer, then renames the branch to
  `archive/review/{repo-slug}-{pr}/{run-id}`. Never pushes. Archive retains
  its PR-specific `{repo-slug}-{pr-number}` confirmation behavior; never use
  the generic `YES` token for archive.
- **Discard:** hard-resets the selected slot. It clears stale ownership and
  prepared-run state, removes staged, unstaged, untracked, and ignored slot
  content, and restores the managed baseline. This is the mandatory first
  command before every setup (Section 0), using `--confirm YES`.

**Legacy status:** `review_reset.py` still reads the original
`pool-runs`/`SourcePool` manifest exactly as before; it is not migrated to
the new `review_git_prepare.py` prepared-slot manifest. It gains one
fail-closed guard: if a new-format prepared-slot manifest also exists for
the same `{repo, slot}` and disagrees on which PR occupies it, reset refuses
instead of operating against a possibly wrong PR.

## 6. `merge_review.py` -- merge a completed local review into `main`

```bash
python3 scripts/merge_review.py --repo Nova --slot 2 --dry-run
python3 scripts/merge_review.py --repo Nova --slot 2
python3 scripts/merge_review.py --repo Nova --slot 2 --abort --confirm <repo-slug>-<pr-number>
```

Flags: `--repo`, `--slot` (required), `--root`, `--dry-run` (inspect only, no
mutation/network), `--max-depth`, `--history-timeout`, `--lock-timeout`
(default `1200`), `--abort` (abort the active merge, keep its checkpoint;
needs `--confirm`), `--recover-inspect` (print evidence for a pending
root-writer recovery), `--recover-assert FILE` (operator recovery
assertion).

Local-only: never pushes, never completes the remote PR, never comments,
never deletes the source branch. Conflict state exits with code `4` and is
preserved for manual resolution and resume.

**Legacy status:** same as `review_reset.py` -- reads the legacy manifest,
unmigrated, gains only the same fail-closed guard against a conflicting
new-format prepared-slot manifest.

## Authentication

| Path | Mechanism |
|---|---|
| Direct-host WSL, Azure DevOps REST (`resolve`/`context`) | Windows Azure CLI (`az login` on the Windows host; invoked as `python.exe -IBm azure.cli ...`, argv list, no shell, CRLF-tolerant JSON parsing). |
| Linux Git on WSL, fetching the PR ref | Windows Git Credential Manager (`git-credential-manager.exe`), applied as a per-command, URL-scoped Git config key -- never global config, never a redirect target outside the original host. |
| Inside the Gateway sandbox | The sandbox's already-injected outgoing-request authentication. No Azure CLI needed. |

`--transport {auto,gateway,cli}` selects explicitly; `auto` detects the
Gateway proxy marker (`HTTP_PROXY`/`HTTPS_PROXY`).

A bearer token or Git authorization header is never placed in argv, stdout,
logs, manifests, or exceptions. Do not print or echo a token to verify
authentication -- an exit code and the resolved JSON are sufficient proof.

## Concurrency

- **Slot lock:** held for the whole preparation of one `{repo, slot}`.
  Different explicit slots (same or different repo) may prepare
  concurrently.
- **Source-fetch lock:** held only while updating one repo's shared source
  store, so two slots of the *same* repo serialize narrowly around the fetch,
  not around cleanup/checkout/verification.
- **Repository-administration lock:** held only briefly around shared
  worktree/ref registration changes.
- A second invocation against the *same* slot fails fast with a busy result;
  it does not wait indefinitely and does not run concurrently with the
  first.
- There is **no global allocation lock** -- every command requires an
  explicit slot, so nothing needs to pick one for you.
- `--lock-timeout` default is `0` (fail fast) for `review_git_prepare.py`
  directly, and `1200` seconds everywhere else (router has none; pool admin,
  reset, and merge default to a longer wait budget for a busy shared lock).

## Safety guarantees

- No command pushes to Git or writes to Azure DevOps. Context collection is
  read-only.
- Fetch-before-delete: the new merge commit is fetched and validated before
  any destructive cleanup starts. A fetch failure leaves the prior slot
  untouched.
- All managed paths are resolved only through the canonical
  `SourcePool.slot_relative_path` helper; symlink/path traversal outside the
  managed root is refused.
- Two manifest formats coexist by design: the new
  `.git/review-setup/prepared-slots/{repo-key}-{slot}.json` (written by
  `review_git_prepare.py`/`review_setup.py`) and the legacy
  `pool-runs`/`SourcePool` manifest (still read by `review_reset.py` and
  `merge_review.py`). They are compared only for the fail-closed conflict
  guard described in Sections 5 and 6 -- reset/merge are not otherwise
  migrated onto the new manifest.

## Verification checklist after setup

After any successful `review_git_prepare.py` or `review_setup.py` run,
confirm:

- [ ] Manifest at `.git/review-setup/prepared-slots/{repo-key}-{slot}.json`
      names the requested repo, slot, PR, and fetched merge SHA.
- [ ] Target nested worktree HEAD is detached and equals that merge SHA
      exactly.
- [ ] Target nested worktree status, including ignored files, is clean.
- [ ] Outer meta-repo worktree HEAD is unchanged from its configured
      baseline.
- [ ] No leftover Git operation (merge/rebase/cherry-pick/revert/bisect) and
      no live writer for that slot.

## Commit all review artifacts locally

After the root review conversation **and all recursive subagents are idle**, run:

```bash
python3 /workspace/.claude/skills/review-setup/scripts/review_commit.py 5720427 --repo Nova --slot 5 --dry-run
python3 /workspace/.claude/skills/review-setup/scripts/review_commit.py 5720427 --repo Nova --slot 5
```

The script validates the prepared PR/merge, selected-slot ownership and review
branch. It accepts `review/<repo-slug>-<PR>` or a suffixed branch for that PR;
`--expected-branch` requires an exact branch. A detached outer slot at local
`main` may create `review/<repo-slug>-<PR>`. Wrong branches and existing staged
changes are refused, not silently fixed. Shared and selected-slot locks protect
against managed setup/reset/merge writers. The caller must separately establish
conversation/subagent idle; a Git lock does not stop an agent editing files.

**Every file** under `PRs/<repo-slug>-<PR>/` is committed, including hidden and
ignored files, raw context and receipt files. Deleted tracked artifacts are
included. Changed files outside that directory are copied into
`outside/outer/` or `outside/source/`, preserving their relative paths and
leaving originals untouched. Checkout/diff and deletion evidence is retained
in `outside/checkout-<digest>.json`; source gitlink changes are never staged.
Copy collisions, symlinks, other PR/repository changes and sensitive/dependency
paths are refused rather than overwritten or omitted. Inspect sensitive
content before committing: this command is not a general secret scanner.

`--dry-run` makes no copies, branch changes, staging or commits; it still uses
managed locks/writer bookkeeping. `--lock-timeout` defaults to `0` (fail fast).
An unchanged repeat returns `status: unchanged`, not another commit. Normal
Git hooks/signing remain enabled. No push, PR feedback, source commit, reset,
merge, cleanup or automatic retry occurs. If a hook/commit/verification fails,
inspect the preserved branch/index before retrying. Successful output includes
the branch, commit, copied paths and file count; all directory paths and file
contents must match committed HEAD before success is reported.

## Invoke by absolute path

Run scripts as `/workspace/.claude/skills/review-setup/scripts/<script>.py`
in the Gateway sandbox. `.claude/` is locally git-excluded and does not carry
into linked worktrees, so a path relative to a slot's own checkout will not
resolve.
